using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;
using AxetosOS.Products.NES.VirtualHardware.Loading;

namespace AxetosOS.Products.NES.VirtualHardware.Machines.Nes;

/// <summary>
/// One normalized digital ROM insertion point shared by all regional
/// motherboard assemblies. Physical 60-pin and 72-pin connector differences
/// deliberately do not exist at this host boundary.
/// </summary>
public sealed class SharedVirtualRomSlot
{
    public const int CpuAddressWidth = 16;
    public const int CpuDataWidth = 8;
    public const int PpuAddressWidth = 14;
    public const int PpuDataWidth = 8;

    public VirtualHardwareNesRomImage? InsertedImage { get; private set; }
    public string? SourceName { get; private set; }
    public NesResolvedRegion? ResolvedRegion { get; private set; }
    public PalCicVariant PalCicVariant { get; private set; } = PalCicVariant.PalA3195;
    public bool IsOccupied => InsertedImage is not null;
    public ulong InsertCount { get; private set; }
    public ulong EjectCount { get; private set; }

    public void Insert(
        VirtualHardwareNesRomImage image,
        string? sourceName = null,
        NesRegionSelection regionSelection = NesRegionSelection.Auto,
        PalCicVariant palCicVariant = PalCicVariant.PalA3195)
    {
        ArgumentNullException.ThrowIfNull(image);
        InsertedImage = image;
        SourceName = sourceName;
        ResolvedRegion = NesHardwareRegionResolver.Resolve(image, sourceName, regionSelection);
        PalCicVariant = palCicVariant;
        InsertCount++;
    }

    public void Eject()
    {
        if (!IsOccupied)
            return;

        InsertedImage = null;
        SourceName = null;
        ResolvedRegion = null;
        EjectCount++;
    }
}
