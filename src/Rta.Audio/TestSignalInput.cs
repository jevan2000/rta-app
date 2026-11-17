using System.Diagnostics;
using Rta.Core.Signals;

namespace Rta.Audio;

/// <summary>
/// Virtual 4-channel device for trying the app without hardware.
/// 1: pink noise around -30 dBFS, slowly swelling ±6 dB (a "service").
/// 2: same programme 5 dB quieter with less high end (a mic further back).
/// 3: 1 kHz sine at -26 dBFS (a 94 dB calibrator when offset = 120 dB).
/// 4: low-level white noise (room noise).
/// </summary>
public sealed class TestSignalInput : IAudioInput
{
    public const string DeviceId = "test-signal";
    private const int Channels = 4;

    private readonly int _rate;
    private Thread? _thread;
    private volatile bool _running;

    public string DeviceName => "Test signal (virtual)";
    public int ChannelCount => Channels;
    public double SampleRate => _rate;

    public event SamplesAvailableHandler? SamplesAvailable;
    public event Action<string>? Faulted;

    public TestSignalInput(int sampleRate) => _rate = sampleRate;

    public static AudioDeviceInfo Info => new(AudioBackend.TestSignal, DeviceId, "Test signal (virtual)", Channels,
        ["Pink (front)", "Pink (rear, darker)", "1 kHz calibrator tone", "Room noise"], [44100, 48000, 96000],
        "Generated signals for trying the app without hardware.");

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "Test signal" };
        _thread.Start();
    }

    private void Run()
    {
        try
        {
            var gens = new[]
            {
                new SignalGenerator(_rate, TestSignalKind.PinkNoise, 1) { LevelDbfs = -30 },
                new SignalGenerator(_rate, TestSignalKind.PinkNoise, 2) { LevelDbfs = -35 },
                new SignalGenerator(_rate, TestSignalKind.Sine, 3) { FrequencyHz = 1000, LevelDbfs = -26 },
                new SignalGenerator(_rate, TestSignalKind.WhiteNoise, 4) { LevelDbfs = -70 },
            };
            var frames = _rate / 100; // 10 ms
            var mono = new float[frames];
            var inter = new float[frames * Channels];
            double lp = 0; // one-pole low-pass state for channel 2
            var lpA = Math.Exp(-2 * Math.PI * 3000.0 / _rate);
            var sw = Stopwatch.StartNew();
            long produced = 0;

            while (_running)
            {
                var t = produced / (double)_rate;
                var swell = 6 * Math.Sin(2 * Math.PI * t / 20);
                gens[0].LevelDbfs = -30 + swell;
                gens[1].LevelDbfs = -33 + swell;

                for (var ch = 0; ch < Channels; ch++)
                {
                    gens[ch].Fill(mono);
                    for (var i = 0; i < frames; i++)
                    {
                        var v = mono[i];
                        if (ch == 1) { lp = (1 - lpA) * v + lpA * lp; v = (float)lp; }
                        inter[i * Channels + ch] = v;
                    }
                }
                SamplesAvailable?.Invoke(inter, Channels);
                produced += frames;

                var ahead = produced / (double)_rate - sw.Elapsed.TotalSeconds;
                if (ahead > 0.002) Thread.Sleep(TimeSpan.FromSeconds(ahead));
            }
        }
        catch (Exception ex) { Faulted?.Invoke(ex.Message); }
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    public void Dispose() => Stop();
}
