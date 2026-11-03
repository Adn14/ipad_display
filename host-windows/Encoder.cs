using System.Diagnostics;
using System.IO.Pipelines;

namespace IPadScreenHost;

/// <summary>
/// Runs FFmpeg to capture one monitor (Desktop Duplication via the ddagrab filter, cursor included)
/// and encode it to low-latency H.264, delivering complete access units to a callback.
/// </summary>
internal sealed class FfmpegEncoder : IDisposable
{
    public static readonly string[] Names = ["qsv", "nvenc", "amf", "x264"];

    private readonly Process _proc;
    private readonly Thread _stdoutThread, _stderrThread;
    private readonly Queue<string> _stderrTail = new();
    private readonly Task _parse;
    private volatile bool _disposed;

    public string Name { get; }
    public string Codec { get; }
    public long AccessUnits;   // count of AUs delivered
    public bool HasExited => _proc.HasExited;

    private static readonly Lazy<IntPtr> Job = new(CreateKillOnCloseJob);

    public FfmpegEncoder(string ffmpegPath, string name, MonitorInfo mon, int fps, int bitrateMbps,
                         AccessUnitSink onAccessUnit)
    {
        Name = name;
        int kbps = bitrateMbps * 1000;
        // Keyframes only when needed. A periodic keyframe redraws the whole picture at a lower quality
        // than the frames that refine it, so still areas (the taskbar) visibly "pulse" each time one
        // arrives. The stream starts with one; recovery (dropped frames, or the iPad's decoder asking)
        // restarts the encoder for a fresh one. -g is only a safety net, as long as every encoder allows
        // (QSV's GOP size is 16-bit): 9-18 minutes. A shorter one showed up as a blink each time it fired
        // during motion - the keyframe can't get enough bits then.
        const int gop = 65535;
        // Half a second of rate-control buffer: keyframes get the bits they need to match the frames
        // around them. They're rare now, so a large one costs at most a one-off hitch.
        int vbv = kbps / 2;
        bool nativeNvidia = mon.VendorId == 0x10DE, nativeAmd = mon.VendorId is 0x1002 or 0x1022;

        // Filter chain after ddagrab, and encoder arguments, per encoder.
        (string filterTail, string codec, string codecArgs) = name switch
        {
            "qsv" => (",hwmap=derive_device=qsv,format=qsv", "h264_qsv",
                      $"-preset veryfast -async_depth 1 -bf 0 -g {gop} -forced_idr 1 -b:v {kbps}k -maxrate {kbps}k -bufsize {vbv}k"),
            "nvenc" => (nativeNvidia ? "" : ",hwdownload,format=bgra", "h264_nvenc",
                      $"-preset p1 -tune ull -zerolatency 1 -rc cbr -bf 0 -g {gop} -forced-idr 1 -b:v {kbps}k -bufsize {vbv}k"),
            "amf" => (nativeAmd ? "" : ",hwdownload,format=bgra,format=nv12", "h264_amf",
                      $"-usage ultralowlatency -quality speed -rc cbr -bf 0 -g {gop} -b:v {kbps}k -bufsize {vbv}k"),
            "x264" => (",hwdownload,format=bgra,format=yuv420p", "libx264",
                      $"-preset ultrafast -tune zerolatency -bf 0 -g {gop} -forced-idr 1 -b:v {kbps}k -maxrate {kbps}k -bufsize {vbv}k"),
            _ => throw new ArgumentException($"unknown encoder '{name}' (use {string.Join(", ", Names)})"),
        };
        Codec = codec;

        // Pick the capture GPU by vendor, not index: the DXGI adapter order changes once CUDA is loaded
        // in the process (e.g. NVIDIA becomes adapter 0 on hybrid laptops).
        string captureDevice = mon.VendorId != 0 ? $"d3d11va=dx:,vendor_id=0x{mon.VendorId:X4}" : $"d3d11va=dx:{mon.AdapterIndex}";
        // NVENC on another GPU than the one capturing: give the encoder its own CUDA device, otherwise
        // FFmpeg hands it the capture GPU's D3D11 device and NVENC fails with "no encode device".
        string encodeDevice = name == "nvenc" && !nativeNvidia ? "-init_hw_device cuda=cu " : "";

        // Pass capture timing straight through: ddagrab itself repeats the last frame on a steady timer
        // while the screen is still, whereas FFmpeg's constant-rate conversion invents duplicates in
        // bursts. Microsecond timestamps: at 1/fps, two updates within one tick collide and the muxer
        // discards one - and losing a P-frame corrupts every frame after it.
        string args =
            "-hide_banner -loglevel error -nostdin " +
            $"-init_hw_device \"{captureDevice}\" {encodeDevice}-filter_hw_device dx " +
            $"-filter_complex \"ddagrab=output_idx={mon.OutputIndex}:framerate={fps}:draw_mouse=1{filterTail}\" " +
            $"-fps_mode passthrough -enc_time_base 1:1000000 " +
            $"-an -c:v {codec} {codecArgs} -flush_packets 1 -f h264 pipe:1";

        Log.Debug($"ffmpeg {args}");
        _proc = new Process
        {
            StartInfo = new ProcessStartInfo(ffmpegPath, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
            },
        };
        _proc.Start();
        try { Native.AssignProcessToJobObject(Job.Value, _proc.Handle); } catch { /* best effort */ }
        try { _proc.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }

        var packetizer = new AnnexBPacketizer((in AccessUnit au) =>
        {
            Interlocked.Increment(ref AccessUnits);
            onAccessUnit(in au);
        });
        var pipe = AnnexBPacketizer.CreatePipe();
        _parse = packetizer.RunAsync(pipe.Reader); // suspends at its first ReadAsync; resumes inline below

        // Reads FFmpeg's stdout into the pipe. Because the reader scheduler is Inline, every flush runs
        // the parser - and the sink's socket write - right here: read, parse and send on one thread.
        _stdoutThread = new Thread(() =>
        {
            using var mmcss = MmcssScope.Enter("Playback");
            var stream = _proc.StandardOutput.BaseStream;
            var writer = pipe.Writer;
            try
            {
                while (!_disposed)
                {
                    int n = stream.Read(writer.GetSpan(64 * 1024));
                    if (n <= 0) break;
                    writer.Advance(n);
                    ValueTask<FlushResult> flush = writer.FlushAsync();
                    FlushResult r = flush.IsCompletedSuccessfully ? flush.Result : flush.AsTask().GetAwaiter().GetResult();
                    if (r.IsCompleted) break; // parser stopped
                }
            }
            catch (Exception e) when (_disposed || e is IOException or ObjectDisposedException) { }
            catch (Exception e) { Log.Warn($"Video pipeline error: {e.Message}"); }
            finally
            {
                writer.Complete(); // lets the parser finish and return its pooled buffers
                // If the parser died first, FFmpeg would block forever on a full stdout pipe; end it so
                // the session notices (HasExited) and restarts the encoder.
                if (!_disposed) try { _proc.Kill(entireProcessTree: true); } catch { }
            }
        }) { IsBackground = true, Name = "ffmpeg-video", Priority = ThreadPriority.Highest };

        _stderrThread = new Thread(() =>
        {
            try
            {
                string? line;
                while ((line = _proc.StandardError.ReadLine()) != null)
                {
                    lock (_stderrTail)
                    {
                        _stderrTail.Enqueue(line);
                        while (_stderrTail.Count > 15) _stderrTail.Dequeue();
                    }
                    Log.Debug($"ffmpeg: {line}");
                }
            }
            catch { }
        }) { IsBackground = true, Name = "ffmpeg-stderr" };

        _stdoutThread.Start();
        _stderrThread.Start();
    }

    public string StderrTail
    {
        get { lock (_stderrTail) return string.Join("\n    ", _stderrTail); }
    }

    /// <summary>Encoders to try, best first, for the GPU that owns the monitor.</summary>
    public static List<string> Candidates(MonitorInfo mon, string requested)
    {
        if (requested != "auto") return [requested];
        var list = new List<string>();
        switch (mon.VendorId)
        {
            case 0x8086: list.Add("qsv"); break;
            case 0x10DE: list.Add("nvenc"); break;
            case 0x1002 or 0x1022: list.Add("amf"); break;
        }
        // A discrete NVIDIA GPU can still encode a display drawn by another GPU (frames copied over).
        if (!list.Contains("nvenc") && Monitors.HasNvidiaGpu()) list.Add("nvenc");
        list.Add("x264");
        return list;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { }
        try { _proc.WaitForExit(2000); } catch { }
        _stdoutThread.Join(2000); // completes the pipe writer, which ends the parser
        try { _parse.Wait(1000); } catch (AggregateException e) { Log.Debug($"parser: {e.InnerException?.Message}"); }
        _proc.Dispose();
    }

    private static unsafe IntPtr CreateKillOnCloseJob()
    {
        IntPtr job = Native.CreateJobObjectW(IntPtr.Zero, null);
        var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        Native.SetInformationJobObject(job, Native.JobObjectExtendedLimitInformation, &info,
            (uint)sizeof(Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
        return job;
    }

    /// <summary>Finds ffmpeg.exe: explicit path, next to this exe, ./ffmpeg/, or on PATH.</summary>
    public static string? Locate(string? explicitPath)
    {
        if (explicitPath != null) return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;
        string baseDir = AppContext.BaseDirectory;
        foreach (var p in new[] { Path.Combine(baseDir, "ffmpeg.exe"), Path.Combine(baseDir, "ffmpeg", "bin", "ffmpeg.exe"), Path.Combine(baseDir, "ffmpeg", "ffmpeg.exe") })
            if (File.Exists(p)) return p;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var p = Path.Combine(dir.Trim('"'), "ffmpeg.exe");
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }
}
