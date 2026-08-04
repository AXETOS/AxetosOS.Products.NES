using AxetosOS.Products.NES.Abstractions;

namespace AxetosOS.Products.NES.Hardware;

public enum NesCpuAddressRegion
{
    None,
    WorkRam,
    PpuRegisters,
    ApuAndIo,
    Expansion,
    Cartridge
}

public enum NesPpuAddressRegion
{
    None,
    PatternTable,
    Nametable,
    Palette
}

/// <summary>
/// Inspectable view of the motherboard CPU address decoder. It observes the
/// live CPU bus transaction and reports the physical decode region; it does
/// not perform a second lookup or participate in execution.
/// </summary>
public sealed class NesCpuAddressDecoder : INesHardwareModule
{
    private readonly CpuBus _bus;

    public NesCpuAddressDecoder(CpuBus bus)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
    }

    public string ModuleId => "nes.decoder.cpu-address";
    public ushort Address => _bus.LastTransaction.Address;
    public byte Data => _bus.LastTransaction.Data;
    public BusAccessDirection Direction => _bus.LastTransaction.Direction;
    public object? SelectedDevice => _bus.LastTransaction.PrimaryDevice;
    public int ParticipantCount => _bus.LastTransaction.ParticipantCount;
    public NesCpuAddressRegion Region => Decode(Address, Direction);

    public static NesCpuAddressRegion Decode(ushort address, BusAccessDirection direction = BusAccessDirection.Read)
    {
        if (direction == BusAccessDirection.None)
            return NesCpuAddressRegion.None;
        if (address <= 0x1FFF)
            return NesCpuAddressRegion.WorkRam;
        if (address <= 0x3FFF)
            return NesCpuAddressRegion.PpuRegisters;
        if (address <= 0x401F)
            return NesCpuAddressRegion.ApuAndIo;
        if (address <= 0x5FFF)
            return NesCpuAddressRegion.Expansion;
        return NesCpuAddressRegion.Cartridge;
    }

    public void PowerOn() { }
    public void Reset() { }
}

/// <summary>
/// Inspectable view of the RP2C02 14-bit address decoder. Addresses are taken
/// from the live normalized PPU bus transaction.
/// </summary>
public sealed class NesPpuAddressDecoder : INesHardwareModule
{
    private readonly PpuBus _bus;

    public NesPpuAddressDecoder(PpuBus bus)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
    }

    public string ModuleId => "nes.decoder.ppu-address";
    public ushort Address => _bus.LastTransaction.Address;
    public byte Data => _bus.LastTransaction.Data;
    public BusAccessDirection Direction => _bus.LastTransaction.Direction;
    public object? SelectedDevice => _bus.LastTransaction.PrimaryDevice;
    public NesPpuAddressRegion Region => Decode(Address, Direction);

    public static NesPpuAddressRegion Decode(ushort address, BusAccessDirection direction = BusAccessDirection.Read)
    {
        if (direction == BusAccessDirection.None)
            return NesPpuAddressRegion.None;

        address &= 0x3FFF;
        if (address <= 0x1FFF)
            return NesPpuAddressRegion.PatternTable;
        if (address <= 0x3EFF)
            return NesPpuAddressRegion.Nametable;
        return NesPpuAddressRegion.Palette;
    }

    public void PowerOn() { }
    public void Reset() { }
}

public sealed class NesCartridgeConnectorPackage : INesHardwareModule, IHardwareCompositeModule
{
    public NesCartridgeConnectorPackage(CpuBus cpuBus, PpuBus ppuBus, CartridgeBoardPackage board)
    {
        ArgumentNullException.ThrowIfNull(cpuBus);
        ArgumentNullException.ThrowIfNull(ppuBus);
        ArgumentNullException.ThrowIfNull(board);

        Cpu = new NesCpuCartridgeConnector(cpuBus, board);
        Ppu = new NesPpuCartridgeConnector(ppuBus, board);
        HardwareComponents =
        [
            new(ModuleId, "NES cartridge edge connector", HardwareComponentKind.InputOutput, this),
            new(Cpu.ModuleId, "Cartridge CPU/PRG connector", HardwareComponentKind.InputOutput, Cpu),
            new(Ppu.ModuleId, "Cartridge PPU/CHR connector", HardwareComponentKind.InputOutput, Ppu)
        ];
        HardwareConnections =
        [
            new(ModuleId, Cpu.ModuleId, HardwareConnectionKind.Internal, "CPU-side contacts"),
            new(ModuleId, Ppu.ModuleId, HardwareConnectionKind.Internal, "PPU-side contacts")
        ];
    }

    public string ModuleId => "nes.connector.cartridge";
    public NesCpuCartridgeConnector Cpu { get; }
    public NesPpuCartridgeConnector Ppu { get; }
    public IReadOnlyList<HardwareComponentDescriptor> HardwareComponents { get; }
    public IReadOnlyList<HardwareConnectionDescriptor> HardwareConnections { get; }
    public void PowerOn() { }
    public void Reset() { }
}

public sealed class NesCpuCartridgeConnector : INesHardwareModule
{
    private readonly CpuBus _bus;

    internal NesCpuCartridgeConnector(CpuBus bus, CartridgeBoardPackage board)
    {
        _bus = bus;
        InsertedBoard = board;
    }

    public string ModuleId => "nes.connector.cartridge.cpu";
    public CartridgeBoardPackage InsertedBoard { get; }
    public ushort Address => _bus.LastTransaction.Address;
    public byte Data => _bus.LastTransaction.Data;
    public BusAccessDirection Direction => _bus.LastTransaction.Direction;
    public bool CartridgeSelected => _bus.LastTransaction.Direction != BusAccessDirection.None && Address >= 0x4020;
    public void PowerOn() { }
    public void Reset() { }
}

public sealed class NesPpuCartridgeConnector : INesHardwareModule
{
    private readonly PpuBus _bus;

    internal NesPpuCartridgeConnector(PpuBus bus, CartridgeBoardPackage board)
    {
        _bus = bus;
        InsertedBoard = board;
    }

    public string ModuleId => "nes.connector.cartridge.ppu";
    public CartridgeBoardPackage InsertedBoard { get; }
    public ushort Address => _bus.LastTransaction.Address;
    public byte Data => _bus.LastTransaction.Data;
    public BusAccessDirection Direction => _bus.LastTransaction.Direction;
    public bool CartridgeSelected => _bus.LastTransaction.Direction != BusAccessDirection.None && Address <= 0x1FFF;
    public void PowerOn() { }
    public void Reset() { }
}
