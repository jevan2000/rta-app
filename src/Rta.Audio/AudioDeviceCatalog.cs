namespace Rta.Audio;

public static class AudioDeviceCatalog
{
    /// <summary>Lists ASIO drivers (opened briefly to read channels), WASAPI capture devices, and the test signal.</summary>
    public static IReadOnlyList<AudioDeviceInfo> List()
    {
        var list = new List<AudioDeviceInfo>();
        foreach (var name in AsioInput.DriverNames()) list.Add(AsioInput.Describe(name));
        list.AddRange(WasapiInput.List());
        list.Add(TestSignalInput.Info);
        list.Add(TestSignalInput.SecondInfo);
        return list;
    }
}
