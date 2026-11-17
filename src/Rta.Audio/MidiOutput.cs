using NAudio.Midi;

namespace Rta.Audio;

/// <summary>A MIDI output port (Windows MIDI API via NAudio), for level CCs and alert notes.</summary>
public sealed class MidiOutput : IDisposable
{
    private readonly MidiOut _out;
    public string DeviceName { get; }

    private MidiOutput(MidiOut o, string name) { _out = o; DeviceName = name; }

    public static IReadOnlyList<string> Devices()
    {
        try { return Enumerable.Range(0, MidiOut.NumberOfDevices).Select(i => MidiOut.DeviceInfo(i).ProductName).ToList(); }
        catch { return []; }
    }

    public static MidiOutput Open(string deviceName)
    {
        for (var i = 0; i < MidiOut.NumberOfDevices; i++)
            if (string.Equals(MidiOut.DeviceInfo(i).ProductName, deviceName, StringComparison.OrdinalIgnoreCase))
                return new MidiOutput(new MidiOut(i), deviceName);
        throw new InvalidOperationException($"MIDI output '{deviceName}' was not found.");
    }

    /// <summary>Channel 1–16.</summary>
    public void ControlChange(int channel, int cc, int value) =>
        _out.Send(MidiMessage.ChangeControl(cc, Math.Clamp(value, 0, 127), channel).RawData);

    public void NoteOn(int channel, int note, int velocity = 127) =>
        _out.Send(MidiMessage.StartNote(note, Math.Clamp(velocity, 1, 127), channel).RawData);

    public void NoteOff(int channel, int note) =>
        _out.Send(MidiMessage.StopNote(note, 0, channel).RawData);

    public void Dispose() => _out.Dispose();
}
