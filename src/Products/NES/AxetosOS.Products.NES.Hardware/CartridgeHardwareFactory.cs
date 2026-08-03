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
            1 => CreateMmc1(image, definition.Id),
            2 => new CartridgeHardware(new UxRomPrgRom(image), new NromChrMemory(image), definition.Id),
            3 or 7 or 11 or 66 or 71 or 79 => CreateDiscrete(image, definition.Id),
            4 => CreateMmc3(image, definition.Id, hasIrq: true),
            206 => CreateMmc3(image, definition.Id, hasIrq: false),
            _ => throw new UnsupportedMapperException(image.MapperNumber, image.SubmapperNumber)
        };
    }

    private static CartridgeHardware CreateDiscrete(NesRomImage image, string boardId)
    {
        var memory = new DiscreteMapperCartridgeMemory(image);
        return new CartridgeHardware(memory, memory, boardId);
    }

    private static CartridgeHardware CreateMmc3(NesRomImage image, string boardId, bool hasIrq)
    {
        var memory = new Mmc3CartridgeMemory(image, hasIrq);
        return new CartridgeHardware(memory, memory, boardId);
    }

    private static CartridgeHardware CreateMmc1(NesRomImage image, string boardId)
    {
        var memory = new Mmc1CartridgeMemory(image);
        return new CartridgeHardware(memory, memory, boardId);
    }
}
