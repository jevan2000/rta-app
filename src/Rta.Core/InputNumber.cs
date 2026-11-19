namespace Rta.Core;

/// <summary>
/// Input numbers on a node with several audio interfaces. Interface slot s has inputs s·100 + 0…99,
/// so the first interface keeps plain 0, 1, 2… (and every mic's id stays the same as before
/// multiple interfaces existed). Shown as "3" for the first interface and "2·3" for the second.
/// </summary>
public static class InputNumber
{
    public const int PerDevice = 100;
    public const int MaxDevices = 10;

    public static int Slot(int channelIndex) => channelIndex / PerDevice;
    public static int DeviceChannel(int channelIndex) => channelIndex % PerDevice;
    public static int Of(int slot, int deviceChannel) => slot * PerDevice + deviceChannel;

    public static string Label(int channelIndex) =>
        Slot(channelIndex) == 0 ? $"{channelIndex + 1}" : $"{Slot(channelIndex) + 1}·{DeviceChannel(channelIndex) + 1}";
}
