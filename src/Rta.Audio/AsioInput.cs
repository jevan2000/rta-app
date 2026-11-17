using NAudio.Wave;

namespace Rta.Audio;

/// <summary>
/// ASIO capture (Focusrite, Behringer and most pro interfaces). The driver is owned by a
/// dedicated STA thread because many ASIO drivers expect create/use/dispose on one thread.
/// </summary>
public sealed class AsioInput : IAudioInput
{
    private readonly string _driverName;
    private readonly int _requestedRate;
    private Thread? _thread;
    private readonly ManualResetEventSlim _stop = new(false);
    private float[] _buffer = [];

    public string DeviceName => _driverName;
    public int ChannelCount { get; private set; }
    public double SampleRate { get; private set; }

    public event SamplesAvailableHandler? SamplesAvailable;
    public event Action<string>? Faulted;

    public AsioInput(string driverName, int sampleRate)
    {
        _driverName = driverName;
        _requestedRate = sampleRate;
    }

    public void Start()
    {
        if (_thread is not null) return;
        _stop.Reset();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() => Run(started)) { IsBackground = true, Name = "ASIO " + _driverName };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        try { started.Task.Wait(TimeSpan.FromSeconds(10)); }
        catch (AggregateException ex) { _thread = null; throw ex.InnerException ?? ex; }
        if (!started.Task.IsCompleted) throw new TimeoutException($"ASIO driver '{_driverName}' did not start.");
    }

    private void Run(TaskCompletionSource started)
    {
        AsioOut? asio = null;
        try
        {
            asio = new AsioOut(_driverName);
            if (!asio.IsSampleRateSupported(_requestedRate))
                throw new NotSupportedException($"'{_driverName}' does not support {_requestedRate} Hz.");
            ChannelCount = asio.DriverInputChannelCount;
            if (ChannelCount == 0) throw new InvalidOperationException($"'{_driverName}' has no input channels.");
            asio.InputChannelOffset = 0;
            asio.InitRecordAndPlayback(null, ChannelCount, _requestedRate);
            SampleRate = _requestedRate;
            asio.AudioAvailable += OnAudio;
            asio.Play();
            started.TrySetResult();
            _stop.Wait();
        }
        catch (Exception ex)
        {
            if (!started.TrySetException(ex)) Faulted?.Invoke(ex.Message);
        }
        finally
        {
            try { asio?.Stop(); } catch { /* driver already gone */ }
            try { asio?.Dispose(); } catch { /* ignore */ }
        }
    }

    private void OnAudio(object? sender, AsioAudioAvailableEventArgs e)
    {
        var needed = e.SamplesPerBuffer * e.InputBuffers.Length;
        if (_buffer.Length != needed) _buffer = new float[needed];
        var n = e.GetAsInterleavedSamples(_buffer);
        try { SamplesAvailable?.Invoke(_buffer.AsSpan(0, n), e.InputBuffers.Length); }
        catch (Exception ex) { Faulted?.Invoke(ex.Message); }
    }

    public void Stop()
    {
        if (_thread is null) return;
        _stop.Set();
        _thread.Join(TimeSpan.FromSeconds(5));
        _thread = null;
    }

    public void Dispose() => Stop();

    public static IReadOnlyList<string> DriverNames()
    {
        try { return AsioOut.GetDriverNames(); }
        catch { return []; }
    }

    /// <summary>Opens a driver briefly to read channel names and supported rates. Runs on an STA thread.</summary>
    public static AudioDeviceInfo Describe(string driverName)
    {
        AudioDeviceInfo? info = null;
        var t = new Thread(() =>
        {
            try
            {
                using var asio = new AsioOut(driverName);
                var count = asio.DriverInputChannelCount;
                var names = Enumerable.Range(0, count).Select(i => SafeName(asio, i)).ToList();
                var rates = new[] { 44100, 48000, 88200, 96000 }.Where(r => Supports(asio, r)).ToList();
                info = new AudioDeviceInfo(AudioBackend.Asio, driverName, driverName, count, names, rates);
            }
            catch (Exception ex)
            {
                info = new AudioDeviceInfo(AudioBackend.Asio, driverName, driverName, 0, [], [], "Could not open driver: " + ex.Message);
            }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        if (!t.Join(TimeSpan.FromSeconds(10)))
            return new AudioDeviceInfo(AudioBackend.Asio, driverName, driverName, 0, [], [], "Driver did not respond (is another app using it?)");
        return info!;
    }

    private static string SafeName(AsioOut asio, int i)
    {
        try { return asio.AsioInputChannelName(i); } catch { return $"Input {i + 1}"; }
    }

    private static bool Supports(AsioOut asio, int rate)
    {
        try { return asio.IsSampleRateSupported(rate); } catch { return false; }
    }
}
