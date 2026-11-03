import AVFoundation
import Foundation

/// Owns the listening socket, the current host connection, and the video renderer.
/// Thread-safe; all callbacks are delivered on the main thread.
final class StreamController {
    enum State: Equatable {
        case waiting
        case connected(host: String)
        case streaming(host: String)
    }

    let renderer: VideoRenderer
    let audio = AudioPlayer()

    var onStateChange: ((State) -> Void)?
    var onHostInfo: (([String: Any]) -> Void)?

    private let server: SocketServer
    private let lock = NSLock()
    private var connection: ClientConnection?
    private var hostName = "laptop"
    private var helloPayload: [String: Any] = [:]

    init(port: UInt16, displayLayer: AVSampleBufferDisplayLayer) {
        server = SocketServer(port: port)
        renderer = VideoRenderer(displayLayer: displayLayer)
        renderer.onFirstFrame = { [weak self] in
            guard let self else { return }
            self.onStateChange?(.streaming(host: self.currentHostName()))
        }
    }

    /// Info about this iPad sent to the host when it connects.
    func setHello(_ hello: [String: Any]) {
        lock.lock()
        helloPayload = hello
        lock.unlock()
    }

    func start() {
        server.onAccept = { [weak self] fd in self?.accept(fd) }
        server.start()
    }

    /// Sends a message to the connected host (no-op when disconnected). Any thread.
    func send(_ data: Data) {
        lock.lock()
        let c = connection
        lock.unlock()
        c?.send(data)
    }

    var isConnected: Bool {
        lock.lock()
        defer { lock.unlock() }
        return connection != nil
    }

    private func currentHostName() -> String {
        lock.lock()
        defer { lock.unlock() }
        return hostName
    }

    /// Per-connection state touched only on that connection's reader thread.
    private final class Link {
        var hostProtocol = 1
    }

    private func accept(_ fd: Int32) {
        let conn = ClientConnection(fd: fd)
        let link = Link()
        lock.lock()
        let previous = connection
        connection = conn
        hostName = "laptop"
        var hello = helloPayload
        lock.unlock()
        hello["proto"] = AppConfig.protocolVersion
        hello["acks"] = true // we acknowledge frames, so the host can pace the stream

        previous?.disconnect() // newest host wins
        renderer.reset()

        conn.onVideo = { [weak self, weak conn] block, length in
            guard let self else { return }
            let frameId = self.renderer.handleVideo(block, length: length, hasFrameId: link.hostProtocol >= 2)
            if let frameId, let conn {
                var ack = ByteBuffer()
                ack.u32(frameId)
                conn.send(Message.frame(.frameAck, ack.data))
            }
        }
        conn.onMessage = { [weak self] type, payload in
            self?.handle(type: type, payload: payload, link: link)
        }
        conn.onClose = { [weak self, weak conn] in
            guard let self else { return }
            self.lock.lock()
            let wasCurrent = conn != nil && self.connection === conn
            if wasCurrent { self.connection = nil }
            self.lock.unlock()
            if wasCurrent {
                self.audio.stop()
                DispatchQueue.main.async { self.onStateChange?(.waiting) }
            }
        }
        DispatchQueue.main.async { [weak self] in self?.onStateChange?(.connected(host: "laptop")) }
        conn.start()
        conn.send(Message.json(.clientHello, hello))
    }

    // Reader thread. `payload` is borrowed: valid only during this call.
    private func handle(type: UInt8, payload: UnsafeRawBufferPointer, link: Link) {
        switch MsgType(rawValue: type) {
        case .audio:
            audio.enqueue(payload)
        case .audioFormat:
            let obj = Self.json(payload)
            let rate = (obj["sampleRate"] as? NSNumber)?.doubleValue ?? 48000
            let channels = (obj["channels"] as? NSNumber)?.intValue ?? 2
            audio.configure(sampleRate: rate, channels: channels)
        case .hostHello:
            let obj = Self.json(payload)
            let name = obj["name"] as? String ?? "laptop"
            link.hostProtocol = (obj["proto"] as? NSNumber)?.intValue ?? 1
            lock.lock()
            hostName = name
            lock.unlock()
            DispatchQueue.main.async { [weak self] in self?.onStateChange?(.connected(host: name)) }
        case .hostInfo:
            let obj = Self.json(payload)
            DispatchQueue.main.async { [weak self] in self?.onHostInfo?(obj) }
        default:
            break
        }
    }

    /// Control messages are rare and small; copying them out is fine.
    private static func json(_ payload: UnsafeRawBufferPointer) -> [String: Any] {
        (try? JSONSerialization.jsonObject(with: Data(payload))) as? [String: Any] ?? [:]
    }
}
