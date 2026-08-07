using AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;
using AxetosOS.Products.NES.VirtualHardware.Machines.Nes;

namespace AxetosOS.Products.NES.VirtualHardware.Loading;

/// <summary>
/// A ROM image attached to the physical regional virtual-hardware console.
/// The selected NES/Famicom motherboard contains only real board-level packages
/// and traces; CPU/APU/DMA and PPU internals remain inside their physical ICs.
/// </summary>
public sealed record VirtualHardwareNesMachine(
    VirtualHardwareNesRomImage Cartridge,
    NesResolvedRegion RegionSelection,
    RegionalNesVirtualMachine Hardware)
{
    public ActiveNesMotherboard ActiveMotherboard => Hardware.ActiveMotherboard;
    public object ActiveBoard => Hardware.ActiveBoard
        ?? throw new InvalidOperationException("No physical motherboard is selected.");
    public NromCartridge CartridgeBoard => Hardware.Slot.Cartridge
        ?? throw new InvalidOperationException("No physical cartridge board is attached.");
}

/// <summary>
/// Software composition boundary for launching the pin-level physical hardware
/// model from a ROM. This factory no longer constructs the retired synthetic
/// NES CPU/PPU helper motherboard; it always selects the same physical Famicom,
/// NTSC NES or PAL NES board architecture used by the runtime boot host.
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

        var hardware = new RegionalNesVirtualMachine();
        hardware.InsertRom(image, fileName, regionSelection);
        var resolved = hardware.Slot.ResolvedRegion
            ?? throw new InvalidOperationException("Inserted ROM did not resolve to a physical NES region.");

        return new VirtualHardwareNesMachine(image, resolved, hardware);
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
