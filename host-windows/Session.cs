using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace IPadScreenHost;

/// <summary>One connected iPad: streams video to it and applies its input until either side goes away.</summary>
internal sealed class Session(Stream stream, MonitorInfo monitor, Options opt, string ffmpeg, InputInjector input)
{
    private static string? _workingEncoder; // remembered across reconnects
    private readonly object _writeLock = new();
    private readonly CancellationTokenSource _cts = new();
    private long _bytesSent;

    public void Run(CancellationToken outer)
    {
        using var reg = outer.Register(_cts.Cancel);
        input.SetMonitor(monitor);

        SendJson(Msg.HostHello, new { proto = 1, width = monitor.Width, height = monitor.Height, name = Environment.MachineName });

        var reader = new Thread(ReadLoop) { IsBackground = true, Name = "ipad-reader" };
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
        }
    }

    // ------------------------------------------------------------------ audio

    private static bool _warnedNoAudioDevice;

    private AudioStreamer? StartAudio()
    {
        try
        {
            var streamer = AudioStreamer.TryCreate(opt.AudioDevice, pcm =>
            {
                if (_cts.IsCancellationRequested) return;
                var msg = new byte[5 + pcm.Length];
                msg[0] = Msg.Audio;
                BinaryPrimitives.WriteUInt32LittleEndian(msg.AsSpan(1), (uint)pcm.Length);
                pcm.CopyTo(msg, 5);
                Write(msg);
            });
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

    // ------------------------------------------------------------------ video

    private void StreamVideo()
    {
        var candidates = _workingEncoder is { } w ? new List<string> { w } : FfmpegEncoder.Candidates(monitor, opt.Encoder);
        int restarts = 0;

        for (int i = 0; i < candidates.Count && !_cts.IsCancellationRequested;)
        {
            string name = candidates[i];
            using var enc = new FfmpegEncoder(ffmpeg, name, monitor, opt.Fps, opt.BitrateMbps, SendAccessUnit);

            // Wait for the first picture; if ffmpeg dies first, this encoder doesn't work here.
            var sw = Stopwatch.StartNew();
            while (!_cts.IsCancellationRequested && enc.AccessUnits == 0 && !enc.HasExited && sw.Elapsed < TimeSpan.FromSeconds(8))
                Thread.Sleep(20);
            if (_cts.IsCancellationRequested) return;

            if (enc.AccessUnits == 0)
            {
                Thread.Sleep(200); // let stderr drain
                Log.Warn($"Encoder '{name}' ({enc.Codec}) failed to start:\n    {enc.StderrTail}");
                if (_workingEncoder == name) { _workingEncoder = null; candidates = FfmpegEncoder.Candidates(monitor, opt.Encoder); i = 0; continue; }
                i++;
                continue;
            }

            if (_workingEncoder != name) Log.Info($"Encoder: {name} ({enc.Codec}), {monitor.Width}x{monitor.Height} @ {opt.Fps} fps, {opt.BitrateMbps} Mbps");
            _workingEncoder = name;
            SendJson(Msg.HostInfo, new { encoder = enc.Codec });

            // Stream until the iPad disconnects or ffmpeg exits (e.g. display mode changed).
            var statsTimer = Stopwatch.StartNew();
            var geometryTimer = Stopwatch.StartNew();
            long lastBytes = 0, lastAus = 0;
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
                    long b = Interlocked.Read(ref _bytesSent), a = Interlocked.Read(ref enc.AccessUnits);
                    double secs = statsTimer.Elapsed.TotalSeconds;
                    Log.Debug($"{(a - lastAus) / secs:F1} fps, {(b - lastBytes) * 8 / secs / 1e6:F1} Mbps");
                    lastBytes = b; lastAus = a;
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
                Log.Info($"{monitor.DeviceName} is now {monitor.Width}x{monitor.Height}; restarting video.");
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
    /// Re-reads the streamed monitor's position and size. Touch input must map onto the monitor's
    /// *current* rectangle: changing resolution or rearranging displays while connected would
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
            && now.OutputIndex == monitor.OutputIndex && now.VendorId == monitor.VendorId)
            return GeometryChange.None;

        bool resized = now.Width != monitor.Width || now.Height != monitor.Height
                       || now.OutputIndex != monitor.OutputIndex || now.VendorId != monitor.VendorId;
        monitor = now;
        input.SetMonitor(now);
        Log.Debug($"{now.DeviceName} geometry: {now.Width}x{now.Height} at ({now.Left},{now.Top})");
        return resized ? GeometryChange.Resized : GeometryChange.Moved;
    }

    private void SendAccessUnit(List<ArraySegment<byte>> nals)
    {
        if (_cts.IsCancellationRequested) return;
        int size = 0;
        foreach (var n in nals) size += 4 + n.Count;
        var msg = new byte[5 + size];
        msg[0] = Msg.Video;
        BinaryPrimitives.WriteUInt32LittleEndian(msg.AsSpan(1), (uint)size);
        int o = 5;
        foreach (var n in nals)
        {
            BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(o), (uint)n.Count);
            n.AsSpan().CopyTo(msg.AsSpan(o + 4));
            o += 4 + n.Count;
        }
        Write(msg);
    }

    private void SendJson(byte type, object payload)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload);
        var msg = new byte[5 + json.Length];
        msg[0] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(msg.AsSpan(1), (uint)json.Length);
        json.CopyTo(msg, 5);
        Write(msg);
    }

    private void Write(byte[] msg)
    {
        try
        {
            lock (_writeLock) stream.Write(msg);
            Interlocked.Add(ref _bytesSent, msg.Length);
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
        var header = new byte[5];
        var payload = new byte[4096];
        var touches = new TouchPoint[16];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                stream.ReadExactly(header);
                int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1));
                if (len < 0 || len > Msg.MaxPayload) throw new InvalidDataException($"bad message length {len}");
                if (payload.Length < len) payload = new byte[len];
                stream.ReadExactly(payload, 0, len);
                var p = payload.AsSpan(0, len);
                if (opt.NoInput && header[0] != Msg.ClientHello) continue;

                switch (header[0])
                {
                    case Msg.ClientHello:
                        HandleHello(p);
                        break;
                    case Msg.Touch when len >= 1:
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
                    case Msg.Pen when len >= 21:
                        input.Pen((TouchPhase)p[0], F(p, 1), F(p, 5), F(p, 9), F(p, 13), F(p, 17));
                        break;
                    case Msg.Mouse when len >= 17:
                        input.Mouse((MouseAction)p[0], F(p, 1), F(p, 5), F(p, 9), F(p, 13));
                        break;
                    default:
                        Log.Debug($"ignoring message 0x{header[0]:X2} ({len} bytes)");
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
            using var doc = JsonDocument.Parse(json.ToArray());
            var r = doc.RootElement;
            int w = r.TryGetProperty("widthPx", out var wp) ? wp.GetInt32() : 0;
            int h = r.TryGetProperty("heightPx", out var hp) ? hp.GetInt32() : 0;
            string dev = r.TryGetProperty("device", out var dp) ? dp.GetString() ?? "iPad" : "iPad";
            Log.Info($"Connected to {dev} ({w}x{h} px screen). Streaming {monitor.DeviceName} ({monitor.Width}x{monitor.Height}).");
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
    }
}
