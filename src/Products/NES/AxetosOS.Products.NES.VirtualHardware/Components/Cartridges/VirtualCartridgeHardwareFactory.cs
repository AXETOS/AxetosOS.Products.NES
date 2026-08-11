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
            9 => new Mmc2Cartridge("SLOT.CARTRIDGE.MMC2"),
            10 => new Mmc4Cartridge("SLOT.CARTRIDGE.MMC4"),
            11 => new ColorDreamsCartridge("SLOT.CARTRIDGE.COLORDREAMS"),
            16 => new BandaiFcgCartridge("SLOT.CARTRIDGE.BANDAI.FCG"),
            18 => new JalecoSs88006Cartridge("SLOT.CARTRIDGE.JALECO.SS88006"),
            19 => new Namco163Cartridge("SLOT.CARTRIDGE.NAMCO.163"),
            21 or 23 or 25 => new KonamiVrc4Cartridge("SLOT.CARTRIDGE.KONAMI.VRC4"),
            24 or 26 => new KonamiVrc6Cartridge("SLOT.CARTRIDGE.KONAMI.VRC6"),
            85 => new KonamiVrc7Cartridge("SLOT.CARTRIDGE.KONAMI.VRC7"),
            34 => new Mapper34Cartridge("SLOT.CARTRIDGE.MAPPER34"),
            66 => new GxromCartridge("SLOT.CARTRIDGE.GXROM"),
            69 => new SunsoftFme7Cartridge("SLOT.CARTRIDGE.SUNSOFT.FME7"),
            71 => new CamericaCartridge("SLOT.CARTRIDGE.CAMERICA"),
            79 => new Nina0306Cartridge("SLOT.CARTRIDGE.NINA0306"),
            206 => new DxromCartridge("SLOT.CARTRIDGE.DXROM"),
            227 => new Mapper227Cartridge("SLOT.CARTRIDGE.MAPPER227"),
            _ => throw new NotSupportedException(
                $"Mapper {image.MapperNumber} is not yet implemented as replaceable cartridge hardware.")
        };
        cartridge.LoadImage(image);
        return cartridge;
    }
}
