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
            _ => throw new NotSupportedException(
                $"Mapper {image.MapperNumber} is not yet implemented as replaceable cartridge hardware.")
        };
        cartridge.LoadImage(image);
        return cartridge;
    }
}
