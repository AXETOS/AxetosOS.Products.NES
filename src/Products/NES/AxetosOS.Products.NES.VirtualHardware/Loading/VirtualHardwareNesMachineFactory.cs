using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;

namespace AxetosOS.Products.NES.VirtualHardware.Loading;

public sealed record VirtualHardwareNesMachine(
    VirtualHardwareNesRomImage Cartridge,
    NesResolvedRegion RegionSelection,
    NesCpuMotherboard Motherboard);

/// <summary>
/// Software composition boundary for launching VirtualHardware from a ROM.
/// It reads metadata, resolves Auto/override region policy, validates the
/// currently supported cartridge board, and constructs the selected hardware.
/// </summary>
public static class VirtualHardwareNesMachineFactory
{
    public static VirtualHardwareNesMachine Load(
        string path,
        NesRegionSelection regionSelection = NesRegionSelection.Auto)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var image = VirtualHardwareNesRomReader.ReadFile(path);
        return Create(image, path, regionSelection);
    }

    public static VirtualHardwareNesMachine Load(
        ReadOnlySpan<byte> rom,
        string? fileName = null,
        NesRegionSelection regionSelection = NesRegionSelection.Auto)
    {
        var image = VirtualHardwareNesRomReader.Read(rom);
        return Create(image, fileName, regionSelection);
    }

    public static VirtualHardwareNesMachine Create(
        VirtualHardwareNesRomImage image,
        string? fileName = null,
        NesRegionSelection regionSelection = NesRegionSelection.Auto)
    {
        ArgumentNullException.ThrowIfNull(image);
        ValidateCurrentCartridgeSupport(image);
        var resolved = NesHardwareRegionResolver.Resolve(image, fileName, regionSelection);
        var mirroring = image.Mirroring switch
        {
            VirtualHardwareNesMirroring.Horizontal => NesNametableMirroring.Horizontal,
            VirtualHardwareNesMirroring.Vertical => NesNametableMirroring.Vertical,
            VirtualHardwareNesMirroring.FourScreen => NesNametableMirroring.FourScreen,
            _ => throw new InvalidOperationException($"Unknown mirroring mode {image.Mirroring}.")
        };
        var motherboard = new NesCpuMotherboard(image.PrgRom, image.ChrRom, mirroring, resolved.Region);
        return new VirtualHardwareNesMachine(image, resolved, motherboard);
    }

    private static void ValidateCurrentCartridgeSupport(VirtualHardwareNesRomImage image)
    {
        if (image.MapperNumber != 0)
            throw new NotSupportedException($"VirtualHardware currently supports NROM mapper 0 only; ROM uses mapper {image.MapperNumber}.");

        if (image.PrgRomSizeBytes is not (16 * 1024 or 32 * 1024))
            throw new NotSupportedException($"NROM requires 16 KiB or 32 KiB PRG ROM; ROM declares {image.PrgRomSizeBytes} bytes.");

        if (image.ChrRomSizeBytes is not (0 or 8 * 1024))
            throw new NotSupportedException($"Current NROM composition supports 0 or 8 KiB CHR ROM; ROM declares {image.ChrRomSizeBytes} bytes.");
    }
}
