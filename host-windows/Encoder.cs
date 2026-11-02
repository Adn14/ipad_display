using System.Diagnostics;

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
    private volatile bool _disposed;

    public string Name { get; }
    public string Codec { get; }
    public long AccessUnits;   // count of AUs delivered
    public bool HasExited => _proc.HasExited;

    private static readonly Lazy<IntPtr> Job = new(CreateKillOnCloseJob);

    public FfmpegEncoder(string ffmpegPath, string name, MonitorInfo mon, int fps, int bitrateMbps,
                         Action<List<ArraySegment<byte>>> onAccessUnit)
    {
        Name = name;
        int kbps = bitrateMbps * 1000;
        int gop = fps; // a keyframe every second: fast recovery if the iPad drops a frame
        bool nativeNvidia = mon.VendorId == 0x10DE, nativeAmd = mon.VendorId is 0x1002 or 0x1022;

        // Filter chain after ddagrab, and encoder arguments, per encoder.
        (string filterTail, string codec, string codecArgs) = name switch
        {
            "qsv" => (",hwmap=derive_device=qsv,format=qsv", "h264_qsv",
                      $"-preset veryfast -async_depth 1 -bf 0 -g {gop} -b:v {kbps}k -maxrate {kbps}k -bufsize {kbps}k"),
            "nvenc" => (nativeNvidia ? "" : ",hwdownload,format=bgra", "h264_nvenc",
                      $"-preset p1 -tune ull -zerolatency 1 -rc cbr -bf 0 -g {gop} -b:v {kbps}k -bufsize {kbps}k"),
            "amf" => (nativeAmd ? "" : ",hwdownload,format=bgra,format=nv12", "h264_amf",
                      $"-usage ultralowlatency -quality speed -rc cbr -bf 0 -g {gop} -b:v {kbps}k -bufsize {kbps}k"),
            "x264" => (",hwdownload,format=bgra,format=yuv420p", "libx264",
                      $"-preset ultrafast -tune zerolatency -bf 0 -g {gop} -b:v {kbps}k -maxrate {kbps}k -bufsize {kbps}k"),
            _ => throw new ArgumentException($"unknown encoder '{name}' (use {string.Join(", ", Names)})"),
        };
        Codec = codec;

        string args =
            "-hide_banner -loglevel error -nostdin " +
            $"-init_hw_device d3d11va=dx:{mon.AdapterIndex} -filter_hw_device dx " +
            $"-filter_complex \"ddagrab=output_idx={mon.OutputIndex}:framerate={fps}:draw_mouse=1{filterTail}\" " +
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

        var splitter = new AnnexBSplitter(au =>
        {
            Interlocked.Increment(ref AccessUnits);
            onAccessUnit(au);
        });

        _stdoutThread = new Thread(() =>
        {
            var stream = _proc.StandardOutput.BaseStream;
            var buf = new byte[256 * 1024];
            try
            {
                int n;
                while (!_disposed && (n = stream.Read(buf, 0, buf.Length)) > 0)
                    splitter.Feed(buf.AsSpan(0, n));
            }
            catch (Exception e) when (_disposed || e is IOException or ObjectDisposedException) { }
        }) { IsBackground = true, Name = "ffmpeg-stdout", Priority = ThreadPriority.AboveNormal };

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
        list.Add("x264");
        return list;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { }
        try { _proc.WaitForExit(2000); } catch { }
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
