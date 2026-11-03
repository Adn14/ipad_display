using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace IPadScreenHost;

/// <summary>Receives 16-bit stereo PCM; the span is only valid during the call.</summary>
internal delegate void PcmSink(ReadOnlySpan<byte> pcm);

/// <summary>
/// Captures everything Windows plays to one output device (WASAPI loopback) and hands it on as
/// 16-bit stereo PCM. Point Windows (or a single app) at the virtual speaker and the sound plays on the iPad.
/// </summary>
internal sealed class AudioStreamer : IDisposable
{
    /// <summary>Output devices tried in order when --audio-device isn't given (name contains).</summary>
    /// "CABLE Input" is VB-Audio Virtual Cable's speaker; rename any of them to "iPad" in Windows to be explicit.
    private static readonly string[] DefaultNames = ["iPad", "CABLE Input", "Virtual Audio Driver"];

    /// <summary>KSDATAFORMAT_SUBTYPE_IEEE_FLOAT: WASAPI's usual 32-bit float mix format.</summary>
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly WasapiLoopbackCapture _capture;
    private readonly MMDevice _device;
    private readonly PcmSink _onPcm;
    private byte[] _pcm = new byte[16 * 1024]; // reused for every chunk (capture thread only)

    public string DeviceName => _device.FriendlyName;
    public int SampleRate => _capture.WaveFormat.SampleRate;

    private AudioStreamer(MMDevice device, PcmSink onPcm)
    {
        _device = device;
        _onPcm = onPcm;
        _capture = new WasapiLoopbackCapture(device);
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, e) =>
        {
            if (_disposed) return;
            // Windows can stop loopback capture (device reset, format change); pick it back up.
            Log.Warn($"Audio capture stopped{(e.Exception != null ? $": {e.Exception.Message}" : "")} - restarting.");
            Task.Delay(1000).ContinueWith(_ =>
            {
                if (_disposed) return;
                try { _capture.StartRecording(); }
                catch (Exception ex) { Log.Warn($"Audio capture could not restart: {ex.Message}"); }
            });
        };
    }

    private volatile bool _disposed;

    /// <summary>Prepares capture (call <see cref="Start"/>), or returns null if the output device isn't present.</summary>
    public static AudioStreamer? TryCreate(string? deviceName, PcmSink onPcm)
    {
        var device = Find(deviceName);
        return device is null ? null : new AudioStreamer(device, onPcm);
    }

    public void Start() => _capture.StartRecording();

    public static List<string> ListOutputs()
    {
        using var en = new MMDeviceEnumerator();
        return en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).Select(d => d.FriendlyName).ToList();
    }

    private static MMDevice? Find(string? name)
    {
        using var en = new MMDeviceEnumerator();
        var devices = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        foreach (var pattern in name is null ? DefaultNames : [name])
        {
            var d = devices.FirstOrDefault(x => x.FriendlyName.Contains(pattern, StringComparison.OrdinalIgnoreCase));
            if (d != null) return d;
        }
        return null;
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;
        var wf = _capture.WaveFormat;
        int channels = wf.Channels;
        int bytesPerSample = wf.BitsPerSample / 8;
        int frames = e.BytesRecorded / (bytesPerSample * channels);
        bool isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (wf is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubFormat);

        if (_pcm.Length < frames * 4) _pcm = new byte[frames * 8];
        var output = _pcm.AsSpan(0, frames * 4); // stereo s16le
        var src = e.Buffer.AsSpan(0, e.BytesRecorded);
        for (int f = 0; f < frames; f++)
        {
            int frameOffset = f * channels * bytesPerSample;
            short l = Sample(src, frameOffset, bytesPerSample, isFloat);
            short r = channels > 1 ? Sample(src, frameOffset + bytesPerSample, bytesPerSample, isFloat) : l;
            BitConverter.TryWriteBytes(output.Slice(f * 4), l);
            BitConverter.TryWriteBytes(output.Slice(f * 4 + 2), r);
        }
        _onPcm(output);
    }

    private static short Sample(ReadOnlySpan<byte> s, int o, int bytes, bool isFloat)
    {
        if (isFloat && bytes == 4)
            return (short)Math.Clamp(BitConverter.ToSingle(s.Slice(o, 4)) * 32767f, -32768f, 32767f);
        return bytes switch
        {
            2 => BitConverter.ToInt16(s.Slice(o, 2)),
            3 => (short)((s[o + 1]) | (s[o + 2] << 8)),
            4 => (short)(BitConverter.ToInt32(s.Slice(o, 4)) >> 16),
            _ => 0,
        };
    }

    public void Dispose()
    {
        _disposed = true;
        try { _capture.StopRecording(); } catch { }
        _capture.Dispose();
        _device.Dispose();
    }
}
