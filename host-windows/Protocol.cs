namespace IPadScreenHost;

/// <summary>
/// Wire protocol shared with the iPad app (see docs/PROTOCOL.md).
/// Every message: [u8 type][u32 little-endian payload length][payload].
/// </summary>
internal static class Msg
{
    // host -> iPad
    public const byte HostHello = 0x01;   // UTF-8 JSON
    public const byte Video = 0x02;       // one H.264 access unit: repeated [u32 BIG-endian NAL length][NAL]
    public const byte HostInfo = 0x03;    // UTF-8 JSON (encoder name etc.)
    public const byte AudioFormat = 0x04; // UTF-8 JSON {"sampleRate":48000,"channels":2}
    public const byte Audio = 0x05;       // interleaved 16-bit little-endian PCM

    // iPad -> host
    public const byte ClientHello = 0x10; // UTF-8 JSON
    public const byte Touch = 0x11;       // [u8 count] count x { u8 id, u8 phase, f32 x, f32 y, f32 pressure }
    public const byte Pen = 0x12;         // u8 phase, f32 x, f32 y, f32 pressure, f32 tiltX, f32 tiltY
    public const byte Mouse = 0x13;       // u8 action, f32 x, f32 y, f32 dx, f32 dy

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

/// <summary>
/// Splits an H.264 Annex-B byte stream (as written by ffmpeg -f h264) into access units (one
/// complete picture each). Encoders may emit many slices per picture, and VideoToolbox on the iPad
/// wants a whole picture per sample, so grouping happens here.
/// An access unit is emitted as soon as the first header bytes of the *next* one are seen.
/// </summary>
internal sealed class AnnexBSplitter(Action<List<ArraySegment<byte>>> onAccessUnit)
{
    private byte[] _buf = new byte[1 << 20];
    private int _len;          // valid bytes in _buf
    private int _nalStart = -1; // first byte (NAL header) of the NAL currently being received
    private bool _headerChecked;
    private int _scan;         // where to resume looking for the next start code
    private readonly List<ArraySegment<byte>> _au = new();
    private bool _auHasVcl;

    public void Feed(ReadOnlySpan<byte> data)
    {
        EnsureFree(data.Length);
        data.CopyTo(_buf.AsSpan(_len));
        _len += data.Length;

        while (true)
        {
            if (_nalStart < 0)
            {
                int sc = FindStartCode(Math.Max(_scan, 0));
                if (sc < 0) { _scan = Math.Max(0, _len - 2); return; }
                _nalStart = sc + 3;
                _headerChecked = false;
                _scan = _nalStart;
            }

            if (!_headerChecked)
            {
                if (_len < _nalStart + 2) return;
                if (StartsNewAccessUnit(_buf[_nalStart], _buf[_nalStart + 1]) && _auHasVcl)
                    EmitAccessUnit();
                _headerChecked = true;
            }

            int next = FindStartCode(_scan);
            if (next < 0)
            {
                _scan = Math.Max(_nalStart, _len - 2);
                Compact();
                return;
            }

            int end = next;
            while (end > _nalStart && _buf[end - 1] == 0) end--; // drop zero_byte / trailing zeros
            if (end > _nalStart)
            {
                int type = _buf[_nalStart] & 0x1F;
                _au.Add(new ArraySegment<byte>(_buf.AsSpan(_nalStart, end - _nalStart).ToArray()));
                if (type is 1 or 5) _auHasVcl = true;
            }
            _nalStart = next + 3;
            _headerChecked = false;
            _scan = _nalStart;
        }
    }

    /// <summary>H.264 7.4.1.2.3: which NAL units begin a new access unit (given a picture is pending).</summary>
    private static bool StartsNewAccessUnit(byte header, byte firstPayloadByte)
    {
        int type = header & 0x1F;
        return type switch
        {
            9 or 7 or 8 or 6 or (>= 14 and <= 18) => true,     // AUD, SPS, PPS, SEI, reserved
            1 or 5 => (firstPayloadByte & 0x80) != 0,          // first_mb_in_slice == 0 (ue(v) "1")
            _ => false,
        };
    }

    private void EmitAccessUnit()
    {
        var au = new List<ArraySegment<byte>>(_au);
        _au.Clear();
        _auHasVcl = false;
        onAccessUnit(au);
    }

    private int FindStartCode(int from)
    {
        var span = _buf.AsSpan(0, _len);
        for (int i = Math.Max(from, 0); i + 2 < span.Length; i++)
        {
            int j = span.Slice(i, span.Length - i).IndexOf((byte)0);
            if (j < 0) return -1;
            i += j;
            if (i + 2 < span.Length && span[i + 1] == 0 && span[i + 2] == 1) return i;
        }
        return -1;
    }

    private void Compact()
    {
        // Keep the NAL in progress; discard everything before it once it's a sizeable chunk.
        int keepFrom = _nalStart;
        if (keepFrom < _buf.Length / 2) return;
        Buffer.BlockCopy(_buf, keepFrom, _buf, 0, _len - keepFrom);
        _len -= keepFrom;
        _scan -= keepFrom;
        _nalStart = 0;
    }

    private void EnsureFree(int extra)
    {
        if (_len + extra <= _buf.Length) return;
        if (_nalStart > 0)
        {
            int shift = _nalStart;
            Buffer.BlockCopy(_buf, shift, _buf, 0, _len - shift);
            _len -= shift;
            _scan -= shift;
            _nalStart = 0;
        }
        if (_len + extra > _buf.Length)
            Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + extra));
    }
}
