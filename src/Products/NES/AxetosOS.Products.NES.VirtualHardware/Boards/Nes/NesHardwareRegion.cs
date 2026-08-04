namespace AxetosOS.Products.NES.VirtualHardware.Boards.Nes;

/// <summary>
/// Physical console family selected when assembling a VirtualHardware NES.
/// The ROM image is not duplicated; the motherboard timing profile determines
/// which regional CPU, PPU and frame timing the connected chips observe.
/// </summary>
public enum NesHardwareRegion
{
    NtscNorthAmerica,
    NtscJapan,
    Pal
}
