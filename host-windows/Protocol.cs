using System.Buffers;
using System.Buffers.Binary;

namespace IPadScreenHost;

/// <summary>
/// Wire protocol shared with the iPad app (see docs/PROTOCOL.md).
/// Every message: [u8 type][u32 little-endian payload length][payload].
/// </summary>
internal static class Msg
{
    public const int ProtocolVersion = 2;

    // host -> iPad
    public const byte HostHello = 0x01;   // UTF-8 JSON
    public const byte Video = 0x02;       // v2: [u32 LE frame id] then one H.264 access unit as repeated [u32 BIG-endian NAL length][NAL]
                                          // v1: the access unit only
    public const byte HostInfo = 0x03;    // UTF-8 JSON (encoder name etc.)
    public const byte AudioFormat = 0x04; // UTF-8 JSON {"sampleRate":48000,"channels":2}
    public const byte Audio = 0x05;       // interleaved 16-bit little-endian PCM

    // iPad -> host
    public const byte ClientHello = 0x10; // UTF-8 JSON
    public const byte Touch = 0x11;       // [u8 count] count x { u8 id, u8 phase, f32 x, f32 y, f32 pressure }
    public const byte Pen = 0x12;         // u8 phase, f32 x, f32 y, f32 pressure, f32 tiltX, f32 tiltY
    public const byte Mouse = 0x13;       // u8 action, f32 x, f32 y, f32 dx, f32 dy
    public const byte FrameAck = 0x14;    // v2: u32 LE frame id, sent once the frame is handed to the decoder

    public const int HeaderSize = 5;
    public const int MaxPayload = 32 * 1024 * 1024;
}

internal enum TouchPhase : byte { Down = 0, Move = 1, Up = 2, Cancel = 3 }

internal enum MouseAction : byte
{
    Move = 0, LeftDown = 1, LeftUp = 2, RightDown = 3, RightUp = 4,
    Scroll = 5,  // dx/dy in wheel units (120 = one notch)
    Zoom = 6,    // dy in wheel units, sent with Ctrl held
    MiddleClick = 7,
}

/// <summary>Allocation-free serialization of protocol messages into a reusable buffer writer.</summary>
internal static class Wire
{
    public static void WriteHeader(IBufferWriter<byte> w, byte type, int payloadLength)
    {
        Span<byte> s = w.GetSpan(Msg.HeaderSize);
        s[0] = type;
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(1), payloadLength);
        w.Advance(Msg.HeaderSize);
    }

    public static void WriteUInt32LittleEndian(IBufferWriter<byte> w, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(w.GetSpan(4), value);
        w.Advance(4);
    }

    public static void WriteUInt32BigEndian(IBufferWriter<byte> w, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(w.GetSpan(4), value);
        w.Advance(4);
    }

    public static void Write(IBufferWriter<byte> w, in ReadOnlySequence<byte> source)
    {
        foreach (var segment in source)
        {
            segment.Span.CopyTo(w.GetSpan(segment.Length));
            w.Advance(segment.Length);
        }
    }

    public static void Write(IBufferWriter<byte> w, ReadOnlySpan<byte> source)
    {
        source.CopyTo(w.GetSpan(source.Length));
        w.Advance(source.Length);
    }
}
