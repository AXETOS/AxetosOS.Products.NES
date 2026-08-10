using AxetosOS.Products.NES.VirtualHardware.Loading;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;

/// <summary>
/// ROM/cartridge composition boundary. Mapper metadata is interpreted here to
/// choose physical cartridge hardware; the selected motherboard and the generic
/// hardware compiler never receive mapper/product semantics.
/// </summary>
public static class VirtualCartridgeHardwareFactory
{
    public static IReplaceableCartridgeHardware Create(VirtualHardwareNesRomImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        IReplaceableCartridgeHardware cartridge = image.MapperNumber switch
        {
            0 => new NromCartridge("SLOT.CARTRIDGE.NROM"),
            1 => new Mmc1Cartridge("SLOT.CARTRIDGE.MMC1"),
            2 => new UxromCartridge("SLOT.CARTRIDGE.UXROM"),
            3 => new CnromCartridge("SLOT.CARTRIDGE.CNROM"),
            4 => new Mmc3Cartridge("SLOT.CARTRIDGE.MMC3"),
            7 => new AxromCartridge("SLOT.CARTRIDGE.AXROM"),
            11 => new ColorDreamsCartridge("SLOT.CARTRIDGE.COLORDREAMS"),
            _ => throw new NotSupportedException(
                $"Mapper {image.MapperNumber} is not yet implemented as replaceable cartridge hardware.")
        };
        cartridge.LoadImage(image);
        return cartridge;
    }
}
