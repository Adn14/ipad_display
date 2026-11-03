using System.Net.Sockets;

namespace IPadScreenHost;

internal sealed class Options
{
    public bool List, ShowHelp, Verbose, NoInput, TestInput, NoAudio;
    public string? AudioDevice;        // null = "iPad" / "Virtual Audio" output device
    public string? Monitor;            // null = first non-primary monitor
    public string Encoder = "auto";
    public int Fps;                    // 0 = match the monitor's refresh rate (max 120)
    public int MaxLagMs = 200;         // drop frames once the iPad is this far behind (oldest unacked frame); 0 = never
    public int BitrateMbps = 30;
    public int Port = 7878;
    public string? WifiHost;           // connect over the network instead of USB
    public string? FfmpegPath;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
            int NextInt(int min, int max)
            {
                string v = Next();
                return int.TryParse(v, out int n) && n >= min && n <= max ? n : throw new ArgumentException($"{a}: expected {min}-{max}, got '{v}'");
            }
            switch (a.ToLowerInvariant())
            {
                case "-h" or "--help" or "/?": o.ShowHelp = true; break;
                case "-l" or "--list": o.List = true; break;
                case "-v" or "--verbose": o.Verbose = true; break;
                case "-m" or "--monitor": o.Monitor = Next(); break;
                case "-e" or "--encoder":
                    o.Encoder = Next().ToLowerInvariant();
                    if (o.Encoder != "auto" && !FfmpegEncoder.Names.Contains(o.Encoder))
                        throw new ArgumentException($"--encoder must be auto, {string.Join(", ", FfmpegEncoder.Names)}");
                    break;
                case "--fps": o.Fps = NextInt(10, 240); break;
                case "--max-lag": o.MaxLagMs = NextInt(0, 5000); break;
                case "-b" or "--bitrate": o.BitrateMbps = NextInt(2, 200); break;
                case "-p" or "--port": o.Port = NextInt(1, 65535); break;
                case "-w" or "--wifi": o.WifiHost = Next(); break;
                case "--ffmpeg": o.FfmpegPath = Next(); break;
                case "--no-input": o.NoInput = true; break;
                case "--test-input": o.TestInput = true; break;
                case "-a" or "--audio-device": o.AudioDevice = Next(); break;
                case "--no-audio": o.NoAudio = true; break;
                default: throw new ArgumentException($"unknown option '{a}'");
            }
        }
        return o;
    }

    public static void PrintUsage() => Console.WriteLine("""
        IPadScreenHost - use an iPad as a second monitor (with touch) over USB-C.

        Usage: IPadScreenHost [options]

          -l, --list            list monitors and exit
          -m, --monitor N       monitor to stream: index from --list or \\.\DISPLAYn
                                (default: first non-primary monitor = the virtual display)
          -e, --encoder NAME    auto | qsv (Intel) | nvenc (NVIDIA) | amf (AMD) | x264 (CPU)   [auto]
              --fps N           frames per second                    [the monitor's refresh rate]
              --max-lag MS      drop frames once the iPad is this far behind (0 = never)  [200]
          -b, --bitrate MBPS    video bitrate in Mbit/s                                    [30]
          -w, --wifi IP         connect to the iPad over Wi-Fi at IP instead of USB
          -p, --port N          TCP port the iPad app listens on                           [7878]
              --ffmpeg PATH     path to ffmpeg.exe (default: next to this exe, or on PATH)
          -a, --audio-device NAME  Windows output device whose sound plays on the iPad
                                (default: one named "iPad" or "CABLE Input")
              --no-audio        don't send sound to the iPad
              --no-input        view only; ignore touches from the iPad
              --test-input      check that touch/pen/mouse injection works (moves the cursor), then exit
          -v, --verbose         print FFmpeg output and stream statistics
        """);
}

internal static class Program
{
    private static int Main(string[] args)
    {
        Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        Options opt;
        try { opt = Options.Parse(args); }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
            Options.PrintUsage();
            return 2;
        }
        if (opt.ShowHelp) { Options.PrintUsage(); return 0; }
        Log.Verbose = opt.Verbose;

        if (opt.List)
        {
            var all = Monitors.Enumerate();
            if (all.Count == 0) Console.WriteLine("No monitors found.");
            foreach (var m in all) Console.WriteLine(m);
            Console.WriteLine("\nAudio outputs (use with --audio-device):");
            foreach (var a in AudioStreamer.ListOutputs()) Console.WriteLine($"  {a}");
            return 0;
        }

        if (opt.TestInput)
        {
            var mon = Monitors.Select(opt.Monitor) ?? Monitors.Select("0");
            if (mon is null) { Log.Error("No monitor."); return 1; }
            using var injector = new InputInjector();
            injector.SetMonitor(mon);
            Log.Info($"Testing input injection on {mon}");
            return injector.SelfTest() ? 0 : 1;
        }

        string? ffmpeg = FfmpegEncoder.Locate(opt.FfmpegPath);
        if (ffmpeg is null)
        {
            Log.Error("ffmpeg.exe not found. Install it with:  winget install Gyan.FFmpeg   (then open a new terminal)\n" +
                      "  or put ffmpeg.exe next to IPadScreenHost.exe, or pass --ffmpeg C:\\path\\to\\ffmpeg.exe");
            return 1;
        }
        Log.Debug($"Using {ffmpeg}");

        // Two hosts aimed at the same iPad would keep stealing it from each other (it accepts the newest
        // connection). Hosts aimed at different targets (USB vs a Wi-Fi address/port) may coexist.
        string target = opt.WifiHost is null ? "usb" : $"wifi-{opt.WifiHost}-{opt.Port}";
        using var single = new Mutex(true, $@"Local\IPadScreenHost.Running.{target}", out bool firstInstance);
        if (!firstInstance)
        {
            Log.Error("IPadScreenHost is already running (another window, or the system tray). Close that one first.");
            return 1;
        }

        // Steady-state streaming allocates almost nothing; keep the rare collections from blocking the
        // video/input threads (no full blocking GCs unless memory is low).
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        using var input = new InputInjector();

        Log.Info($"IPadScreenHost - {(opt.WifiHost is null ? "USB-C" : $"Wi-Fi ({opt.WifiHost})")} mode. Press Ctrl+C to quit.");
        var status = new StatusOnce();

        while (!cts.IsCancellationRequested)
        {
            var monitor = Monitors.Select(opt.Monitor);
            if (monitor is null)
            {
                status.Show(opt.Monitor is null
                    ? "Waiting for a second monitor. Windows has only one display - install/enable the virtual display driver (see README), " +
                      "and make sure Settings > System > Display is set to \"Extend these displays\"."
                    : $"Monitor '{opt.Monitor}' not found. Run with --list to see monitors.");
                cts.Token.WaitHandle.WaitOne(3000);
                continue;
            }

            Stream? stream = opt.WifiHost is null ? ConnectUsb(opt, status) : ConnectWifi(opt, status);
            if (stream is null)
            {
                cts.Token.WaitHandle.WaitOne(2000);
                continue;
            }

            status.Reset();
            new Session(stream, monitor, opt, ffmpeg, input).Run(cts.Token);
            if (!cts.IsCancellationRequested) cts.Token.WaitHandle.WaitOne(1000);
        }

        Log.Info("Bye.");
        return 0;
    }

    private static Stream? ConnectUsb(Options opt, StatusOnce status)
    {
        try
        {
            var devices = Usbmux.ListDevices();
            var usb = devices.Where(d => d.ConnectionType.Equals("USB", StringComparison.OrdinalIgnoreCase)).ToList();
            if (usb.Count == 0)
            {
                status.Show(devices.Count > 0
                    ? "iPad is visible only over the network. Connect it with the USB-C cable."
                    : "Waiting for the iPad on USB... (plug it in, unlock it, and tap \"Trust\" if asked)");
                return null;
            }
            var dev = usb[0];
            var client = Usbmux.Connect(dev.DeviceId, opt.Port);
            Log.Info($"USB tunnel open to iPad {dev.Serial}.");
            return client.GetStream();
        }
        catch (Usbmux.UsbmuxException e)
        {
            status.Show(e.Message);
            return null;
        }
        catch (Exception e) when (e is IOException or SocketException)
        {
            status.Show($"USB connection error: {e.Message}");
            return null;
        }
    }

    private static Stream? ConnectWifi(Options opt, StatusOnce status)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            if (!client.ConnectAsync(opt.WifiHost!, opt.Port).Wait(3000)) throw new SocketException((int)SocketError.TimedOut);
            Log.Info($"Connected to {opt.WifiHost}:{opt.Port} over Wi-Fi.");
            return client.GetStream();
        }
        catch (Exception e) when (e is SocketException or AggregateException)
        {
            client.Dispose();
            status.Show($"Waiting for the iPad app at {opt.WifiHost}:{opt.Port} (open the app; same Wi-Fi network)...");
            return null;
        }
    }
}

/// <summary>Prints a waiting/status message only when it changes, so retry loops don't spam.</summary>
internal sealed class StatusOnce
{
    private string? _last;
    public void Show(string msg)
    {
        if (msg == _last) return;
        _last = msg;
        Log.Info(msg);
    }
    public void Reset() => _last = null;
}

internal static class Log
{
    public static bool Verbose;
    private static readonly object Gate = new();

    public static void Info(string m) => Write(m, ConsoleColor.Gray);
    public static void Warn(string m) => Write(m, ConsoleColor.Yellow);
    public static void Error(string m) => Write(m, ConsoleColor.Red);
    public static void Debug(string m) { if (Verbose) Write(m, ConsoleColor.DarkGray); }

    private static void Write(string m, ConsoleColor color)
    {
        lock (Gate)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} {m}");
            Console.ForegroundColor = old;
        }
    }
}
