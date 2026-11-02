import Foundation

/// Wire protocol shared with the Windows host (see docs/PROTOCOL.md).
/// Every message: [u8 type][u32 little-endian payload length][payload]. Numbers are little-endian.
enum MsgType: UInt8 {
    // host -> iPad
    case hostHello = 0x01   // UTF-8 JSON
    case video = 0x02       // one H.264 access unit: repeated [u32 BIG-endian NAL length][NAL]
    case hostInfo = 0x03    // UTF-8 JSON
    case audioFormat = 0x04 // UTF-8 JSON {"sampleRate":48000,"channels":2}
    case audio = 0x05       // interleaved 16-bit little-endian PCM
    // iPad -> host
    case clientHello = 0x10 // UTF-8 JSON
    case touch = 0x11       // [u8 count] count x { u8 id, u8 phase, f32 x, f32 y, f32 pressure }
    case pen = 0x12         // u8 phase, f32 x, f32 y, f32 pressure, f32 tiltX, f32 tiltY
    case mouse = 0x13       // u8 action, f32 x, f32 y, f32 dx, f32 dy
}

enum TouchPhaseCode: UInt8 {
    case down = 0, move = 1, up = 2, cancel = 3
}

enum MouseAction: UInt8 {
    case move = 0, leftDown = 1, leftUp = 2, rightDown = 3, rightUp = 4
    case scroll = 5       // dx/dy in Windows wheel units (120 = one notch)
    case zoom = 6         // dy in wheel units, applied with Ctrl held
    case middleClick = 7
}

enum AppConfig {
    static let port: UInt16 = 7878
}

/// Little-endian payload builder.
struct ByteBuffer {
    private(set) var data = Data()

    mutating func u8(_ v: UInt8) { data.append(v) }

    mutating func f32(_ v: Float) {
        var le = v.bitPattern.littleEndian
        withUnsafeBytes(of: &le) { data.append(contentsOf: $0) }
    }
}

enum Message {
    static func frame(_ type: MsgType, _ payload: Data) -> Data {
        var d = Data(capacity: payload.count + 5)
        d.append(type.rawValue)
        var len = UInt32(payload.count).littleEndian
        withUnsafeBytes(of: &len) { d.append(contentsOf: $0) }
        d.append(payload)
        return d
    }

    static func json(_ type: MsgType, _ object: [String: Any]) -> Data {
        let payload = (try? JSONSerialization.data(withJSONObject: object)) ?? Data("{}".utf8)
        return frame(type, payload)
    }
}
