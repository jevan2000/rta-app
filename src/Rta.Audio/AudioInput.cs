namespace Rta.Audio;

public enum AudioBackend { Asio, Wasapi, TestSignal }

public sealed record AudioDeviceInfo(
    AudioBackend Backend,
    string Id,
    string Name,
    int InputChannels,
    IReadOnlyList<string> ChannelNames,
    IReadOnlyList<int> SampleRates,
    string? Note = null);

/// <summary>Interleaved float32 frames from an input device.</summary>
public delegate void SamplesAvailableHandler(ReadOnlySpan<float> interleaved, int channels);

/// <summary>A running capture from one device. Events fire on a driver thread; handlers must be quick.</summary>
public interface IAudioInput : IDisposable
{
    string DeviceName { get; }
    int ChannelCount { get; }
    double SampleRate { get; }
    event SamplesAvailableHandler? SamplesAvailable;
    event Action<string>? Faulted;
    void Start();
    void Stop();
}

public static class AudioInputFactory
{
    public static IAudioInput Create(AudioBackend backend, string deviceId, int sampleRate) => backend switch
    {
        AudioBackend.Asio => new AsioInput(deviceId, sampleRate),
        AudioBackend.Wasapi => new WasapiInput(deviceId, sampleRate),
        AudioBackend.TestSignal => new TestSignalInput(sampleRate, deviceId),
        _ => throw new ArgumentOutOfRangeException(nameof(backend)),
    };
}
