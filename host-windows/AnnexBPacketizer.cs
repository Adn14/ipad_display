using System.Buffers;
using System.IO.Pipelines;

namespace IPadScreenHost;

/// <summary>One NAL unit of an access unit: offset/length relative to <see cref="AccessUnit.Buffer"/>.</summary>
internal readonly record struct NalRange(int Start, int Length, byte Header)
{
    public int Type => Header & 0x1F;
    public bool IsPicture => Type is 1 or 5;
    /// <summary>nal_ref_idc != 0: later frames may predict from this one.</summary>
    public bool IsReference => (Header & 0x60) != 0;
}

/// <summary>
/// A complete H.264 picture, borrowed from the pipe. Valid only during the sink call: the bytes are
/// released (and reused) once the sink returns, so a sink must copy what it keeps.
/// </summary>
internal readonly ref struct AccessUnit(ReadOnlySequence<byte> buffer, ReadOnlySpan<NalRange> nals)
{
    public readonly ReadOnlySequence<byte> Buffer = buffer;
    public readonly ReadOnlySpan<NalRange> Nals = nals;

    public bool IsIdr
    {
        get { foreach (var n in Nals) if (n.Type == 5) return true; return false; }
    }

    public bool HasParameterSets
    {
        get { foreach (var n in Nals) if (n.Type is 7 or 8) return true; return false; }
    }

    /// <summary>No slice of this picture is a reference, so it can be skipped without corrupting later frames.</summary>
    public bool IsDisposable
    {
        get
        {
            bool anyPicture = false;
            foreach (var n in Nals)
            {
                if (!n.IsPicture) continue;
                if (n.IsReference) return false;
                anyPicture = true;
            }
            return anyPicture;
        }
    }

    /// <summary>Bytes on the wire as AVCC: each NAL prefixed by a 4-byte length.</summary>
    public int AvccLength
    {
        get { int total = 0; foreach (var n in Nals) total += 4 + n.Length; return total; }
    }
}

internal delegate void AccessUnitSink(in AccessUnit au);

/// <summary>
/// Splits FFmpeg's raw H.264 output (Annex-B) into access units, straight out of the pipe's
/// <see cref="ReadOnlySequence{T}"/>: no per-frame managed allocations, no copies until the sink
/// serializes the picture for the socket.
///
/// Encoders may emit many slices per picture and VideoToolbox wants one whole picture per sample,
/// so slices are grouped (H.264 7.4.1.2.3). A picture is emitted as soon as the first two bytes of
/// the next one arrive. Unconsumed bytes stay in the pipe until their access unit is complete, so
/// the pipe's pause threshold must exceed the largest keyframe (see <see cref="CreatePipe"/>).
/// </summary>
internal sealed class AnnexBPacketizer(AccessUnitSink sink)
{
    private static ReadOnlySpan<byte> StartCode => [0, 0, 1];

    private NalRange[] _nals = new NalRange[64];
    private int _nalCount;
    private bool _auHasPicture;
    // Offsets relative to the start of the current (unconsumed) buffer, which is the access unit's start.
    private long _nalStart = -1;
    private bool _headerChecked;
    private long _scan;

    /// <summary>
    /// The pipe between FFmpeg's stdout and the parser. Reader continuations run inline on the thread
    /// that flushes (the stdout reader), so read, parse and send happen on one thread with no hand-offs.
    /// </summary>
    public static Pipe CreatePipe() => new(new PipeOptions(
        pauseWriterThreshold: 32 * 1024 * 1024,   // default (64 KB) would deadlock on the first keyframe
        resumeWriterThreshold: 16 * 1024 * 1024,
        minimumSegmentSize: 64 * 1024,
        readerScheduler: PipeScheduler.Inline,
        writerScheduler: PipeScheduler.Inline,
        useSynchronizationContext: false));

    public async Task RunAsync(PipeReader reader)
    {
        try
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync().ConfigureAwait(false);
                ReadOnlySequence<byte> remaining = Process(result.Buffer);
                reader.AdvanceTo(remaining.Start, remaining.End);
                if (result.IsCompleted || result.IsCanceled) break;
            }
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Parses as far as possible; returns the part that must stay in the pipe.</summary>
    private ReadOnlySequence<byte> Process(ReadOnlySequence<byte> buffer)
    {
        while (true)
        {
            if (_nalStart < 0)
            {
                long sc = Find(buffer, _scan);
                if (sc < 0)
                {
                    // No start code yet; nothing before one can belong to a picture.
                    long keep = Math.Max(0, buffer.Length - 2);
                    buffer = buffer.Slice(keep);
                    _scan = 0;
                    return buffer;
                }
                buffer = buffer.Slice(sc); // the access unit starts at its first start code
                _nalStart = 3;
                _headerChecked = false;
                _scan = _nalStart;
            }

            if (!_headerChecked)
            {
                if (buffer.Length < _nalStart + 2) return buffer;
                Span<byte> head = stackalloc byte[2];
                buffer.Slice(_nalStart, 2).CopyTo(head);
                if (_auHasPicture && StartsNewAccessUnit(head[0], head[1]))
                {
                    long boundary = _nalStart - 3; // where this NAL's start code begins
                    Emit(buffer.Slice(0, boundary));
                    buffer = buffer.Slice(boundary);
                    _nalStart -= boundary;
                }
                _headerChecked = true;
                _scan = _nalStart;
            }

            long next = Find(buffer, _scan);
            if (next < 0)
            {
                _scan = Math.Max(_nalStart, buffer.Length - 2);
                return buffer;
            }

            long end = next;
            while (end > _nalStart && ByteAt(buffer, end - 1) == 0) end--; // zero_byte / trailing zeros
            if (end > _nalStart) AddNal((int)_nalStart, (int)(end - _nalStart), ByteAt(buffer, _nalStart));

            _nalStart = next + 3;
            _headerChecked = false;
            _scan = _nalStart;
        }
    }

    /// <summary>H.264 7.4.1.2.3: NAL units that begin a new access unit once a picture is pending.</summary>
    private static bool StartsNewAccessUnit(byte header, byte firstPayloadByte) => (header & 0x1F) switch
    {
        9 or 7 or 8 or 6 or (>= 14 and <= 18) => true,       // AUD, SPS, PPS, SEI, reserved
        1 or 5 => (firstPayloadByte & 0x80) != 0,            // first_mb_in_slice == 0 (ue(v) "1")
        _ => false,
    };

    private void AddNal(int start, int length, byte header)
    {
        if (_nalCount == _nals.Length) Array.Resize(ref _nals, _nals.Length * 2);
        var nal = new NalRange(start, length, header);
        _nals[_nalCount++] = nal;
        if (nal.IsPicture) _auHasPicture = true;
    }

    private void Emit(in ReadOnlySequence<byte> auBytes)
    {
        var au = new AccessUnit(auBytes, _nals.AsSpan(0, _nalCount));
        try { sink(in au); }
        finally
        {
            _nalCount = 0;
            _auHasPicture = false;
        }
    }

    private static long Find(in ReadOnlySequence<byte> buffer, long from)
    {
        if (from >= buffer.Length) return -1;
        var reader = new SequenceReader<byte>(buffer.Slice(from));
        return reader.TryReadTo(out ReadOnlySequence<byte> _, StartCode, advancePastDelimiter: false)
            ? from + reader.Consumed
            : -1;
    }

    private static byte ByteAt(in ReadOnlySequence<byte> buffer, long offset) => buffer.Slice(offset, 1).FirstSpan[0];
}

/// <summary>
/// Backpressure for the video stream, based on frames sent but not yet acknowledged by the iPad.
///
/// H.264 P-frames reference the frame before them, so dropping one corrupts every later frame until
/// the next keyframe. Rules, when too many frames are in flight:
///  - disposable pictures (nal_ref_idc == 0) are dropped freely;
///  - dropping a reference picture switches to "skip until IDR": everything is dropped until the next
///    keyframe, which restarts a clean reference chain;
///  - IDR pictures and parameter sets are never dropped.
/// The packetizer thread calls <see cref="TryAdmit"/>; the input thread calls <see cref="Ack"/>.
/// </summary>
internal sealed class FramePacer(int maxInFlight)
{
    private uint _issued;       // next frame id (packetizer thread only)
    private uint _acked;        // highest acknowledged frame id + 1 (written by the input thread)
    private bool _skipUntilIdr; // packetizer thread only
    private long _dropped;

    /// <summary>Only when the iPad sends acks (protocol v2); otherwise every frame is sent.</summary>
    public volatile bool Enabled;

    public long Dropped => Interlocked.Read(ref _dropped);
    public int InFlight => (int)(_issued - Volatile.Read(ref _acked));

    public bool TryAdmit(in AccessUnit au, out uint frameId)
    {
        frameId = 0;
        if (Enabled)
        {
            bool mustSend = au.IsIdr || au.HasParameterSets;
            if (_skipUntilIdr)
            {
                if (!mustSend) return Drop();
                _skipUntilIdr = false;
            }
            else if (!mustSend && InFlight >= maxInFlight)
            {
                if (!au.IsDisposable) _skipUntilIdr = true;
                return Drop();
            }
        }
        frameId = _issued;
        Volatile.Write(ref _issued, _issued + 1);
        return true;
    }

    /// <summary>Acks may arrive out of order or be repeated; only the newest counts.</summary>
    public void Ack(uint frameId)
    {
        uint next = frameId + 1;
        if ((int)(next - Volatile.Read(ref _acked)) > 0 && (int)(Volatile.Read(ref _issued) - next) >= 0)
            Volatile.Write(ref _acked, next);
    }

    private bool Drop()
    {
        Interlocked.Increment(ref _dropped);
        return false;
    }
}
