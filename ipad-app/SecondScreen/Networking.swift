import CoreMedia
import Darwin
import Foundation

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
///
/// Receive path, zero-copy: a video message is read from the socket *directly* into a CMBlockBuffer
/// taken from a CMMemoryPool. The decoder takes ownership of that buffer (it decodes asynchronously,
/// so it must never be handed a pointer into a receive buffer that the next read() would overwrite).
/// One copy - kernel to block buffer - and no per-frame malloc once the pool is warm.
/// Other messages are small: they are read into one reusable scratch buffer and lent to the handler.
final class ClientConnection {
    /// Reader thread. One video message; the connection hands over its only reference to `block`.
    var onVideo: ((CMBlockBuffer, Int) -> Void)?
    /// Reader thread. Any other message. The bytes are only valid during the call: copy what you keep.
    var onMessage: ((UInt8, UnsafeRawBufferPointer) -> Void)?
    /// Called once, on the reader thread, after the socket is closed.
    var onClose: (() -> Void)?

    private let fd: Int32
    private let sendQueue = DispatchQueue(label: "secondscreen.send", qos: .userInteractive)
    private let stateLock = NSLock()
    private var isShutDown = false
    private let videoPool = CMMemoryPoolCreate(options: nil)

    private static let maxMessage = 64 * 1024 * 1024

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
        let header = UnsafeMutableRawPointer.allocate(byteCount: 8, alignment: 8)
        var scratchCapacity = 64 * 1024
        var scratch = UnsafeMutableRawPointer.allocate(byteCount: scratchCapacity, alignment: 16)
        let allocator = CMMemoryPoolGetAllocator(videoPool)
        defer {
            header.deallocate()
            scratch.deallocate()
        }

        while true {
            guard readExact(header, 5) else { break }
            let type = header.load(as: UInt8.self)
            let len = Int(UInt32(littleEndian: header.loadUnaligned(fromByteOffset: 1, as: UInt32.self)))
            guard len <= Self.maxMessage else { break }

            if type == MsgType.video.rawValue {
                guard len > 0, let target = makeBlockBuffer(length: len, allocator: allocator),
                      readExact(target.data, len) else { break }
                onVideo?(target.block, len)
            } else {
                if len > scratchCapacity {
                    scratch.deallocate()
                    scratchCapacity = len
                    scratch = UnsafeMutableRawPointer.allocate(byteCount: len, alignment: 16)
                }
                if len > 0 { guard readExact(scratch, len) else { break } }
                onMessage?(type, UnsafeRawBufferPointer(start: scratch, count: len))
            }
        }
        disconnect()
        sendQueue.sync { } // let pending writes finish before the fd number can be reused
        close(fd)
        // Pooled memory still owned by the decoder is freed when the decoder releases it.
        CMMemoryPoolInvalidate(videoPool)
        onClose?()
    }

    /// A contiguous block buffer of `length` bytes from the pool, plus a pointer to write into it.
    private func makeBlockBuffer(length: Int, allocator: CFAllocator) -> (block: CMBlockBuffer, data: UnsafeMutableRawPointer)? {
        var block: CMBlockBuffer?
        let status = CMBlockBufferCreateWithMemoryBlock(
            allocator: kCFAllocatorDefault, memoryBlock: nil, blockLength: length,
            blockAllocator: allocator, customBlockSource: nil,
            offsetToData: 0, dataLength: length,
            flags: kCMBlockBufferAssureMemoryNowFlag, blockBufferOut: &block)
        guard status == kCMBlockBufferNoErr, let block else { return nil }
        var data: UnsafeMutablePointer<CChar>?
        guard CMBlockBufferGetDataPointer(block, atOffset: 0, lengthAtOffsetOut: nil,
                                          totalLengthOut: nil, dataPointerOut: &data) == kCMBlockBufferNoErr,
              let data else { return nil }
        return (block, UnsafeMutableRawPointer(data))
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
