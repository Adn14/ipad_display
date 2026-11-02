import Foundation
import Darwin

/// TCP listener on all interfaces. Over USB-C the Windows host reaches it through Apple's usbmux
/// tunnel (arriving as a loopback connection); in Wi-Fi mode it connects directly.
/// Plain BSD sockets: simple, and known to work with usbmux tunnels.
final class SocketServer {
    let port: UInt16
    var onAccept: ((Int32) -> Void)?

    private var listenFD: Int32 = -1
    private var running = false
    private let queue = DispatchQueue(label: "secondscreen.accept", qos: .userInitiated)

    init(port: UInt16) { self.port = port }

    func start() {
        guard !running else { return }
        running = true
        queue.async { [weak self] in self?.acceptLoop() }
    }

    private func acceptLoop() {
        while running {
            if listenFD < 0 {
                listenFD = makeListener()
                if listenFD < 0 { sleep(1); continue }
            }
            let fd = accept(listenFD, nil, nil)
            if fd < 0 {
                if errno == EINTR { continue }
                // The socket can be invalidated while the app is suspended; recreate it.
                close(listenFD)
                listenFD = -1
                usleep(300_000)
                continue
            }
            configure(fd)
            onAccept?(fd)
        }
    }

    private func makeListener() -> Int32 {
        let fd = socket(AF_INET, SOCK_STREAM, 0)
        guard fd >= 0 else { return -1 }
        var yes: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &yes, socklen_t(MemoryLayout<Int32>.size))

        var addr = sockaddr_in()
        addr.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = port.bigEndian
        addr.sin_addr = in_addr(s_addr: 0) // INADDR_ANY

        let bound = withUnsafePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard bound == 0, listen(fd, 4) == 0 else {
            close(fd)
            return -1
        }
        return fd
    }

    private func configure(_ fd: Int32) {
        var yes: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &yes, socklen_t(MemoryLayout<Int32>.size))
        setsockopt(fd, Int32(IPPROTO_TCP), TCP_NODELAY, &yes, socklen_t(MemoryLayout<Int32>.size))
        var rcvbuf: Int32 = 2 * 1024 * 1024
        setsockopt(fd, SOL_SOCKET, SO_RCVBUF, &rcvbuf, socklen_t(MemoryLayout<Int32>.size))
    }
}

/// One connected host. Reads framed messages on a dedicated thread; writes on a serial queue.
final class ClientConnection {
    /// Called on the reader thread.
    var onMessage: ((UInt8, Data) -> Void)?
    /// Called once, on the reader thread, after the socket is closed.
    var onClose: (() -> Void)?

    private let fd: Int32
    private let sendQueue = DispatchQueue(label: "secondscreen.send", qos: .userInteractive)
    private let stateLock = NSLock()
    private var isShutDown = false

    init(fd: Int32) { self.fd = fd }

    func start() {
        let thread = Thread { [self] in readLoop() }
        thread.name = "secondscreen.reader"
        thread.qualityOfService = .userInteractive
        thread.start()
    }

    func send(_ data: Data) {
        sendQueue.async { [self] in
            stateLock.lock()
            let down = isShutDown
            stateLock.unlock()
            if down { return }
            data.withUnsafeBytes { (buf: UnsafeRawBufferPointer) in
                guard let base = buf.baseAddress else { return }
                var sent = 0
                while sent < buf.count {
                    let r = Darwin.send(fd, base + sent, buf.count - sent, 0)
                    if r > 0 { sent += r }
                    else if r < 0 && errno == EINTR { continue }
                    else { break }
                }
            }
        }
    }

    /// Ends the connection; the reader thread then closes the descriptor and reports onClose.
    func disconnect() {
        stateLock.lock()
        defer { stateLock.unlock() }
        guard !isShutDown else { return }
        isShutDown = true
        shutdown(fd, SHUT_RDWR)
    }

    private func readLoop() {
        var header = [UInt8](repeating: 0, count: 5)
        while true {
            let gotHeader = header.withUnsafeMutableBytes { readExact($0.baseAddress!, 5) }
            if !gotHeader { break }
            let type = header[0]
            let len = Int(UInt32(header[1]) | UInt32(header[2]) << 8 | UInt32(header[3]) << 16 | UInt32(header[4]) << 24)
            if len > 64 * 1024 * 1024 { break }
            var payload = Data(count: len)
            if len > 0 {
                let ok = payload.withUnsafeMutableBytes { readExact($0.baseAddress!, len) }
                if !ok { break }
            }
            onMessage?(type, payload)
        }
        disconnect()
        sendQueue.sync { } // let pending writes finish before the fd number can be reused
        close(fd)
        onClose?()
    }

    private func readExact(_ ptr: UnsafeMutableRawPointer, _ count: Int) -> Bool {
        var got = 0
        while got < count {
            let r = read(fd, ptr + got, count - got)
            if r > 0 { got += r }
            else if r < 0 && errno == EINTR { continue }
            else { return false }
        }
        return true
    }
}

enum NetworkInfo {
    /// IPv4 addresses of active Wi-Fi/Ethernet interfaces, for the Wi-Fi fallback mode.
    static func ipv4Addresses() -> [String] {
        var result: [String] = []
        var ifaddr: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&ifaddr) == 0, let first = ifaddr else { return [] }
        defer { freeifaddrs(ifaddr) }
        for ptr in sequence(first: first, next: { $0.pointee.ifa_next }) {
            let ifa = ptr.pointee
            guard let addr = ifa.ifa_addr, addr.pointee.sa_family == UInt8(AF_INET) else { continue }
            let flags = Int32(truncatingIfNeeded: ifa.ifa_flags)
            guard flags & IFF_UP != 0, flags & IFF_LOOPBACK == 0 else { continue }
            let name = String(cString: ifa.ifa_name)
            guard name.hasPrefix("en") else { continue }
            var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            if getnameinfo(addr, socklen_t(addr.pointee.sa_len), &host, socklen_t(host.count), nil, 0, NI_NUMERICHOST) == 0 {
                result.append(String(cString: host))
            }
        }
        return result
    }
}
