using AxetosOS.Products.NES.Abstractions;
using AxetosOS.Products.NES.Cartridges;

namespace AxetosOS.Products.NES.Hardware;

public sealed record CartridgeHardware(ICpuBusDevice PrgDevice, IPpuBusDevice ChrDevice, string BoardId);

public static class CartridgeHardwareFactory
{
    public static CartridgeHardware Create(NesRomImage image, CartridgeBoardDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Mapper != image.MapperNumber)
            throw new InvalidDataException($"Board {definition.Id} targets mapper {definition.Mapper}, not {image.MapperNumber}.");

        return definition.Mapper switch
        {
            0 => new CartridgeHardware(new NromPrgRom(image), new NromChrMemory(image), definition.Id),
            2 => new CartridgeHardware(new UxRomPrgRom(image), new NromChrMemory(image), definition.Id),
            _ => throw new UnsupportedMapperException(image.MapperNumber, image.SubmapperNumber)
        };
    }
}
