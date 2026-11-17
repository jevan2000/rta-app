using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Rta.Audio;

/// <summary>
/// WASAPI shared-mode capture (fallback when no ASIO driver). Runs at the device's Windows
/// format; set 48/96 kHz in Windows Sound settings → device → Advanced. Disable "audio
/// enhancements" on the input, or levels will be wrong.
/// </summary>
public sealed class WasapiInput : IAudioInput
{
    private readonly string _deviceId;
    private readonly int _requestedRate;
    private WasapiCapture? _capture;
    private float[] _buffer = [];
    private WaveFormat? _format;
    private static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    public string DeviceName { get; private set; } = "";
    public int ChannelCount { get; private set; }
    public double SampleRate { get; private set; }

    public event SamplesAvailableHandler? SamplesAvailable;
    public event Action<string>? Faulted;

    public WasapiInput(string deviceId, int sampleRate)
    {
        _deviceId = deviceId;
        _requestedRate = sampleRate;
    }

    public void Start()
    {
        if (_capture is not null) return;
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDevice(_deviceId);
        DeviceName = device.FriendlyName;
        var capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 20);
        _format = capture.WaveFormat;
        ChannelCount = _format.Channels;
        SampleRate = _format.SampleRate;
        if (_format.SampleRate != _requestedRate)
            Faulted?.Invoke($"Windows has '{DeviceName}' set to {_format.SampleRate} Hz (requested {_requestedRate} Hz). Change it in Sound settings → Advanced.");
        capture.DataAvailable += OnData;
        capture.RecordingStopped += (_, e) => { if (e.Exception is not null) Faulted?.Invoke(e.Exception.Message); };
        capture.StartRecording();
        _capture = capture;
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var fmt = _format!;
        var bytesPerSample = fmt.BitsPerSample / 8;
        var samples = e.BytesRecorded / bytesPerSample;
        if (_buffer.Length < samples) _buffer = new float[samples];
        var src = e.Buffer.AsSpan(0, e.BytesRecorded);
        var isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                      (fmt is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubtype);

        if (isFloat && bytesPerSample == 4)
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(src).CopyTo(_buffer);
        else if (bytesPerSample == 2)
            for (var i = 0; i < samples; i++) _buffer[i] = BitConverter.ToInt16(src.Slice(i * 2, 2)) / 32768f;
        else if (bytesPerSample == 3)
            for (var i = 0; i < samples; i++)
            {
                var o = i * 3;
                var v = (src[o] | (src[o + 1] << 8) | ((sbyte)src[o + 2] << 16));
                _buffer[i] = v / 8388608f;
            }
        else if (bytesPerSample == 4)
            for (var i = 0; i < samples; i++) _buffer[i] = BitConverter.ToInt32(src.Slice(i * 4, 4)) / 2147483648f;
        else
        {
            Faulted?.Invoke($"Unsupported capture format {fmt}");
            return;
        }

        try { SamplesAvailable?.Invoke(_buffer.AsSpan(0, samples), fmt.Channels); }
        catch (Exception ex) { Faulted?.Invoke(ex.Message); }
    }

    public void Stop()
    {
        var c = _capture;
        _capture = null;
        if (c is null) return;
        try { c.StopRecording(); } catch { /* ignore */ }
        c.Dispose();
    }

    public void Dispose() => Stop();

    public static IReadOnlyList<AudioDeviceInfo> List()
    {
        var list = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                try
                {
                    var mix = d.AudioClient.MixFormat;
                    list.Add(new AudioDeviceInfo(AudioBackend.Wasapi, d.ID, d.FriendlyName, mix.Channels,
                        Enumerable.Range(1, mix.Channels).Select(i => $"Input {i}").ToList(),
                        [mix.SampleRate],
                        "Sample rate is set in Windows Sound settings."));
                }
                catch { /* device vanished */ }
                finally { d.Dispose(); }
            }
        }
        catch { /* no audio subsystem */ }
        return list;
    }
}
