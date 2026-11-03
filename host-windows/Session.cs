using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;

namespace IPadScreenHost;

/// <summary>One connected iPad: streams video to it and applies its input until either side goes away.</summary>
internal sealed class Session(Stream stream, MonitorInfo monitor, Options opt, string ffmpeg, InputInjector input)
{
    private static string? _workingEncoder; // remembered across reconnects
    private readonly object _writeLock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _helloReceived = new();
    private readonly FramePacer _pacer = new(opt.MaxInFlight);
    // Reusable serialization buffers: grow to the largest message once, then no per-frame allocation.
    private readonly ArrayBufferWriter<byte> _videoOut = new(1 << 20); // video thread only
    private readonly ArrayBufferWriter<byte> _audioOut = new(16 << 10); // audio capture thread only
    private volatile int _clientProto = 1;
    private long _bytesSent;
    private long _lastAuTimestamp, _maxAuGap; // video thread; stats only

    private int Fps => opt.Fps > 0 ? opt.Fps : Math.Clamp(monitor.RefreshHz, 24, 120);

    public void Run(CancellationToken outer)
    {
        using var reg = outer.Register(_cts.Cancel);
        input.SetMonitor(monitor);

        SendJson(Msg.HostHello, new { proto = Msg.ProtocolVersion, width = monitor.Width, height = monitor.Height, name = Environment.MachineName });

        // Input from the iPad: injected inline on this thread (no hand-off queue to add latency).
        var reader = new Thread(ReadLoop) { IsBackground = true, Name = "ipad-input", Priority = ThreadPriority.Highest };
        reader.Start();

        AudioStreamer? audio = opt.NoAudio ? null : StartAudio();
        try
        {
            StreamVideo();
        }
        finally
        {
            _cts.Cancel();
            audio?.Dispose();
            input.ReleaseAll();
            try { stream.Dispose(); } catch { }
            reader.Join(1000);
            _helloReceived.Dispose();
        }
    }

    // ------------------------------------------------------------------ audio

    private static bool _warnedNoAudioDevice;

    private AudioStreamer? StartAudio()
    {
        try
        {
            var streamer = AudioStreamer.TryCreate(opt.AudioDevice, SendAudio);
            if (streamer is null)
            {
                if (!_warnedNoAudioDevice)
                {
                    _warnedNoAudioDevice = true;
                    Log.Info(opt.AudioDevice is null
                        ? "Audio: no virtual speaker found, so sound stays on the laptop. Install VB-Audio Virtual Cable (see README) to get a speaker for the iPad."
                        : $"Audio: output device \"{opt.AudioDevice}\" not found. Run --list to see devices.");
                }
                return null;
            }
            SendJson(Msg.AudioFormat, new { sampleRate = streamer.SampleRate, channels = 2 });
            streamer.Start();
            Log.Info($"Audio: anything Windows plays to \"{streamer.DeviceName}\" goes to the iPad ({streamer.SampleRate} Hz).");
            return streamer;
        }
        catch (Exception e)
        {
            Log.Warn($"Audio capture failed to start: {e.Message}");
            return null;
        }
    }

    private void SendAudio(ReadOnlySpan<byte> pcm)
    {
        if (_cts.IsCancellationRequested) return;
        var w = _audioOut;
        w.ResetWrittenCount();
        Wire.WriteHeader(w, Msg.Audio, pcm.Length);
        Wire.Write(w, pcm);
        Write(w.WrittenSpan);
    }

    // ------------------------------------------------------------------ video

    private void StreamVideo()
    {
        // The video format depends on the app's protocol version, so wait (briefly) for its hello.
        try
        {
            if (!_helloReceived.Wait(TimeSpan.FromSeconds(2), _cts.Token))
                Log.Warn("The iPad app didn't introduce itself; assuming an old version (no frame pacing).");
        }
        catch (OperationCanceledException) { return; }

        var candidates = _workingEncoder is { } w ? new List<string> { w } : FfmpegEncoder.Candidates(monitor, opt.Encoder);
        int restarts = 0;

        for (int i = 0; i < candidates.Count && !_cts.IsCancellationRequested;)
        {
            string name = candidates[i];
            int fps = Fps;
            using var enc = new FfmpegEncoder(ffmpeg, name, monitor, fps, opt.BitrateMbps, SendAccessUnit);

            // Wait for the first picture; if ffmpeg dies first, this encoder doesn't work here.
            var sw = Stopwatch.StartNew();
            while (!_cts.IsCancellationRequested && Interlocked.Read(ref enc.AccessUnits) == 0 && !enc.HasExited && sw.Elapsed < TimeSpan.FromSeconds(8))
                Thread.Sleep(20);
            if (_cts.IsCancellationRequested) return;

            if (Interlocked.Read(ref enc.AccessUnits) == 0)
            {
                Thread.Sleep(200); // let stderr drain
                Log.Warn($"Encoder '{name}' ({enc.Codec}) failed to start:\n    {enc.StderrTail}");
                if (_workingEncoder == name) { _workingEncoder = null; candidates = FfmpegEncoder.Candidates(monitor, opt.Encoder); i = 0; continue; }
                i++;
                continue;
            }

            Log.Info($"Encoder: {name} ({enc.Codec}), {monitor.Width}x{monitor.Height} @ {fps} fps, {opt.BitrateMbps} Mbps" +
                     (_pacer.Enabled ? $", pacing at {opt.MaxInFlight} frames in flight" : ""));
            _workingEncoder = name;
            SendJson(Msg.HostInfo, new { encoder = enc.Codec });

            // Stream until the iPad disconnects or ffmpeg exits (e.g. display mode changed).
            var statsTimer = Stopwatch.StartNew();
            var geometryTimer = Stopwatch.StartNew();
            long lastBytes = 0, lastAus = 0, lastDropped = _pacer.Dropped;
            bool resized = false;
            while (!_cts.IsCancellationRequested && !enc.HasExited)
            {
                Thread.Sleep(100);
                if (geometryTimer.ElapsedMilliseconds >= 500)
                {
                    geometryTimer.Restart();
                    var change = RefreshMonitor();
                    if (change == GeometryChange.Gone) return;          // main loop picks the display again
                    if (change == GeometryChange.Resized) { resized = true; break; }
                }
                if (opt.Verbose && statsTimer.ElapsedMilliseconds >= 5000)
                {
                    long b = Interlocked.Read(ref _bytesSent), a = Interlocked.Read(ref enc.AccessUnits), d = _pacer.Dropped;
                    double secs = statsTimer.Elapsed.TotalSeconds;
                    double maxGapMs = Interlocked.Exchange(ref _maxAuGap, 0) * 1000.0 / Stopwatch.Frequency;
                    Log.Debug($"{(a - lastAus) / secs:F1} fps encoded (max gap {maxGapMs:F0} ms), {(b - lastBytes) * 8 / secs / 1e6:F1} Mbps" +
                              (_pacer.Enabled ? $", {(d - lastDropped) / secs:F1} dropped/s, {_pacer.InFlight} in flight" : ""));
                    lastBytes = b; lastAus = a; lastDropped = d;
                    statsTimer.Restart();
                }
            }
            if (_cts.IsCancellationRequested) return;
            // A mode change usually ends ffmpeg by itself; pick up the new geometry before restarting.
            if (!resized)
            {
                var change = RefreshMonitor();
                if (change == GeometryChange.Gone) return;
                resized = change == GeometryChange.Resized;
            }
            if (resized)
            {
                Log.Info($"{monitor.DeviceName} is now {monitor.Width}x{monitor.Height} @ {monitor.RefreshHz} Hz; restarting video.");
                Thread.Sleep(300);
                continue; // new encoder at the new size (the using disposes the old one)
            }

            Log.Warn($"ffmpeg stopped unexpectedly:\n    {enc.StderrTail}");
            if (++restarts > 5) { Log.Error("Encoder keeps failing; dropping this connection."); return; }
            Thread.Sleep(500);
            // loop again with the same encoder (i unchanged)
        }

        if (!_cts.IsCancellationRequested)
            Log.Error("No working H.264 encoder. Run with --verbose to see FFmpeg's output, or try --encoder x264.");
    }

    private enum GeometryChange { None, Moved, Resized, Gone }

    /// <summary>
    /// Re-reads the streamed monitor's position, size and refresh rate. Touch input must map onto the
    /// monitor's *current* rectangle: changing resolution or rearranging displays while connected would
    /// otherwise make every touch land in the wrong place.
    /// </summary>
    private GeometryChange RefreshMonitor()
    {
        var now = Monitors.Enumerate().FirstOrDefault(m => m.DeviceName == monitor.DeviceName);
        if (now is null)
        {
            Log.Info($"{monitor.DeviceName} went away; looking for the display again.");
            return GeometryChange.Gone;
        }
        if (now.Left == monitor.Left && now.Top == monitor.Top && now.Width == monitor.Width && now.Height == monitor.Height
            && now.OutputIndex == monitor.OutputIndex && now.VendorId == monitor.VendorId && now.RefreshHz == monitor.RefreshHz)
            return GeometryChange.None;

        bool restartVideo = now.Width != monitor.Width || now.Height != monitor.Height
                            || now.OutputIndex != monitor.OutputIndex || now.VendorId != monitor.VendorId
                            || (opt.Fps == 0 && now.RefreshHz != monitor.RefreshHz);
        monitor = now;
        input.SetMonitor(now);
        Log.Debug($"{now.DeviceName} geometry: {now.Width}x{now.Height} @ {now.RefreshHz} Hz at ({now.Left},{now.Top})");
        return restartVideo ? GeometryChange.Resized : GeometryChange.Moved;
    }

    /// <summary>
    /// Video thread (inline from the pipe). Decides whether the picture goes out, then serializes it
    /// straight from the pipe's buffer into the reusable writer: the only copy on the host side.
    /// </summary>
    private void SendAccessUnit(in AccessUnit au)
    {
        if (_cts.IsCancellationRequested) return;
        long now = Stopwatch.GetTimestamp();
        if (_lastAuTimestamp != 0)
        {
            long gap = now - _lastAuTimestamp;
            if (gap > _maxAuGap) _maxAuGap = gap; // read (racily, for stats only) by the stats loop
        }
        _lastAuTimestamp = now;
        if (!_pacer.TryAdmit(in au, out uint frameId)) return; // dropped before it touches the socket

        bool withFrameId = _clientProto >= 2;
        var w = _videoOut;
        w.ResetWrittenCount();
        Wire.WriteHeader(w, Msg.Video, (withFrameId ? 4 : 0) + au.AvccLength);
        if (withFrameId) Wire.WriteUInt32LittleEndian(w, frameId);
        foreach (var nal in au.Nals)
        {
            Wire.WriteUInt32BigEndian(w, (uint)nal.Length);
            Wire.Write(w, au.Buffer.Slice(nal.Start, nal.Length));
        }
        Write(w.WrittenSpan);
    }

    /// <summary>Rare control messages; allocation is fine here.</summary>
    private void SendJson(byte type, object payload)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload);
        var w = new ArrayBufferWriter<byte>(Msg.HeaderSize + json.Length);
        Wire.WriteHeader(w, type, json.Length);
        Wire.Write(w, json);
        Write(w.WrittenSpan);
    }

    private void Write(ReadOnlySpan<byte> message)
    {
        try
        {
            lock (_writeLock) stream.Write(message);
            Interlocked.Add(ref _bytesSent, message.Length);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            if (!_cts.IsCancellationRequested) Log.Info("iPad disconnected.");
            _cts.Cancel();
        }
    }

    // ------------------------------------------------------------------ input from iPad

    private void ReadLoop()
    {
        using var mmcss = MmcssScope.Enter("Games");
        Span<byte> header = stackalloc byte[Msg.HeaderSize];
        var payload = new byte[4096];
        var touches = new TouchPoint[16];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                stream.ReadExactly(header);
                int len = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(1));
                if (len < 0 || len > Msg.MaxPayload) throw new InvalidDataException($"bad message length {len}");
                if (payload.Length < len) payload = new byte[len];
                stream.ReadExactly(payload, 0, len);
                var p = payload.AsSpan(0, len);
                byte type = header[0];

                switch (type)
                {
                    case Msg.ClientHello:
                        HandleHello(p);
                        break;
                    case Msg.FrameAck when len >= 4:
                        _pacer.Ack(BinaryPrimitives.ReadUInt32LittleEndian(p));
                        break;
                    case Msg.Touch when len >= 1 && !opt.NoInput:
                    {
                        int count = Math.Min(p[0], touches.Length);
                        int n = 0;
                        for (int i = 0; i < count && 1 + (i + 1) * 14 <= len; i++)
                        {
                            var t = p.Slice(1 + i * 14, 14);
                            touches[n++] = new TouchPoint(t[0], (TouchPhase)t[1], F(t, 2), F(t, 6), F(t, 10));
                        }
                        input.TouchFrame(touches.AsSpan(0, n));
                        break;
                    }
                    case Msg.Pen when len >= 21 && !opt.NoInput:
                        input.Pen((TouchPhase)p[0], F(p, 1), F(p, 5), F(p, 9), F(p, 13), F(p, 17));
                        break;
                    case Msg.Mouse when len >= 17 && !opt.NoInput:
                        input.Mouse((MouseAction)p[0], F(p, 1), F(p, 5), F(p, 9), F(p, 13));
                        break;
                    case Msg.Touch or Msg.Pen or Msg.Mouse:
                        break; // --no-input, or truncated
                    default:
                        Log.Debug($"ignoring message 0x{type:X2} ({len} bytes)");
                        break;
                }
            }
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or ObjectDisposedException or InvalidDataException)
        {
            if (!_cts.IsCancellationRequested) Log.Info("iPad disconnected.");
        }
        finally
        {
            _cts.Cancel();
        }
    }

    private static float F(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadSingleLittleEndian(s.Slice(offset, 4));

    private void HandleHello(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            using var doc = JsonDocument.ParseValue(ref reader);
            var r = doc.RootElement;
            int w = r.TryGetProperty("widthPx", out var wp) ? wp.GetInt32() : 0;
            int h = r.TryGetProperty("heightPx", out var hp) ? hp.GetInt32() : 0;
            string dev = r.TryGetProperty("device", out var dp) ? dp.GetString() ?? "iPad" : "iPad";
            int proto = r.TryGetProperty("proto", out var pp) && pp.TryGetInt32(out int pv) ? pv : 1;
            bool acks = proto >= 2 && r.TryGetProperty("acks", out var ap) && ap.ValueKind == JsonValueKind.True;
            _clientProto = proto;
            _pacer.Enabled = acks && opt.MaxInFlight > 0;

            Log.Info($"Connected to {dev} ({w}x{h} px screen, protocol {proto}). Streaming {monitor.DeviceName} ({monitor.Width}x{monitor.Height}).");
            if (proto < 2) Log.Info("This iPad app is an older version: update it for frame pacing (smoother on slow links).");
            if (w > 0 && h > 0)
            {
                double a1 = (double)Math.Max(w, h) / Math.Min(w, h), a2 = (double)monitor.Width / monitor.Height;
                bool sameShape = Math.Abs(a1 - a2) < 0.02 || Math.Abs(1 / a1 - a2) < 0.02;
                if (!sameShape)
                    Log.Warn($"The virtual monitor's shape ({monitor.Width}x{monitor.Height}) doesn't match the iPad ({w}x{h}); " +
                             $"you'll see black bars. Set it to {Math.Max(w, h)}x{Math.Min(w, h)} (or {Math.Max(w, h) / 2}x{Math.Min(w, h) / 2}) - see README.");
            }
        }
        catch (JsonException) { }
        finally
        {
            try { _helloReceived.Set(); } catch (ObjectDisposedException) { }
        }
    }
}
