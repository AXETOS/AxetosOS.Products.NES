using AxetosOS.Products.NES.Abstractions;
using AxetosOS.Products.NES.Cartridges;

namespace AxetosOS.Products.NES.Hardware;

/// <summary>
/// Inspectable physical cartridge-board composition over the existing live PRG,
/// CHR and mapper devices. The package never performs execution on behalf of the
/// cartridge; CPU and PPU bus traffic continues through the original devices.
/// </summary>
public sealed class CartridgeBoardPackage : INesHardwareModule, IHardwareCompositeModule
{
    private readonly IReadOnlyList<HardwareComponentDescriptor> _components;
    private readonly IReadOnlyList<HardwareConnectionDescriptor> _connections;

    public CartridgeBoardPackage(CartridgeHardware hardware, NametableMirroring initialMirroring)
    {
        ArgumentNullException.ThrowIfNull(hardware);

        Hardware = hardware;
        Prg = new CartridgePrgWindowComponent(hardware.PrgDevice, hardware.BoardId);
        Chr = new CartridgeChrWindowComponent(hardware.ChrDevice, hardware.BoardId);
        Mapper = new CartridgeMapperLogicComponent(hardware.PrgDevice, hardware.ChrDevice, hardware.BoardId);
        Mirroring = new CartridgeMirroringWiringComponent(hardware.PrgDevice, hardware.BoardId, initialMirroring);
        IrqOutput = new CartridgeIrqOutputComponent(hardware.PrgDevice, hardware.BoardId);

        _components =
        [
            new(ModuleId, $"Cartridge board: {hardware.BoardId}", HardwareComponentKind.Cartridge, this),
            new(Prg.ModuleId, "PRG CPU-side memory window", HardwareComponentKind.Memory, Prg),
            new(Chr.ModuleId, "CHR PPU-side memory window", HardwareComponentKind.Memory, Chr),
            new(Mapper.ModuleId, "Mapper and address-decoding logic", HardwareComponentKind.Chip, Mapper),
            new(Mirroring.ModuleId, "Nametable mirroring wiring", HardwareComponentKind.Internal, Mirroring),
            new(IrqOutput.ModuleId, "Cartridge IRQ output", HardwareComponentKind.SignalBundle, IrqOutput)
        ];

        _connections =
        [
            new(ModuleId, Mapper.ModuleId, HardwareConnectionKind.Internal, "board mapper logic"),
            new(Mapper.ModuleId, Prg.ModuleId, HardwareConnectionKind.Internal, "PRG bank/address selection"),
            new(Mapper.ModuleId, Chr.ModuleId, HardwareConnectionKind.Internal, "CHR bank/address selection"),
            new(Mapper.ModuleId, Mirroring.ModuleId, HardwareConnectionKind.Signal, "mirroring control"),
            new(Mapper.ModuleId, IrqOutput.ModuleId, HardwareConnectionKind.Signal, "IRQ control")
        ];
    }

    public string ModuleId => $"nes.cartridge.{Hardware.BoardId}";
    public CartridgeHardware Hardware { get; }
    public CartridgePrgWindowComponent Prg { get; }
    public CartridgeChrWindowComponent Chr { get; }
    public CartridgeMapperLogicComponent Mapper { get; }
    public CartridgeMirroringWiringComponent Mirroring { get; }
    public CartridgeIrqOutputComponent IrqOutput { get; }
    public IReadOnlyList<HardwareComponentDescriptor> HardwareComponents => _components;
    public IReadOnlyList<HardwareConnectionDescriptor> HardwareConnections => _connections;

    public void PowerOn() { }
    public void Reset() { }
}

public sealed class CartridgePrgWindowComponent : INesHardwareModule
{
    private readonly ICpuBusDevice _device;

    internal CartridgePrgWindowComponent(ICpuBusDevice device, string boardId)
    {
        _device = device;
        ModuleId = $"nes.cartridge.{boardId}.prg";
    }

    public string ModuleId { get; }
    public ICpuBusDevice LiveDevice => _device;
    public bool HasBatteryBackedMemory => _device is IBatteryBackedMemory battery && battery.HasBattery;
    public int PersistentSize => _device is IBatteryBackedMemory battery ? battery.PersistentSize : 0;
    public bool HandlesAddress(ushort address) => _device.HandlesCpuAddress(address);
    public byte ReadMappedByte(ushort address) => _device.CpuRead(address);
    public void PowerOn() { }
    public void Reset() { }
}

public sealed class CartridgeChrWindowComponent : INesHardwareModule
{
    private readonly IPpuBusDevice _device;

    internal CartridgeChrWindowComponent(IPpuBusDevice device, string boardId)
    {
        _device = device;
        ModuleId = $"nes.cartridge.{boardId}.chr";
    }

    public string ModuleId { get; }
    public IPpuBusDevice LiveDevice => _device;
    public bool HandlesAddress(ushort address) => _device.HandlesPpuAddress(address);
    public byte ReadMappedByte(ushort address) => _device.PpuRead(address);
    public void PowerOn() { }
    public void Reset() { }
}

public sealed class CartridgeMapperLogicComponent : INesHardwareModule
{
    internal CartridgeMapperLogicComponent(ICpuBusDevice prg, IPpuBusDevice chr, string boardId)
    {
        PrgDevice = prg;
        ChrDevice = chr;
        ModuleId = $"nes.cartridge.{boardId}.mapper";
    }

    public string ModuleId { get; }
    public ICpuBusDevice PrgDevice { get; }
    public IPpuBusDevice ChrDevice { get; }
    public string ImplementationModuleId => (PrgDevice as INesHardwareModule)?.ModuleId
        ?? (ChrDevice as INesHardwareModule)?.ModuleId
        ?? PrgDevice.GetType().Name;
    public bool SharedCpuAndPpuDevice => ReferenceEquals(PrgDevice, ChrDevice);
    public bool ProvidesIrq => PrgDevice is ICartridgeIrqProvider;
    public bool ControlsMirroring => PrgDevice is ICartridgeMirroringProvider;
    public void PowerOn() { }
    public void Reset() { }
}

public sealed class CartridgeMirroringWiringComponent : INesHardwareModule
{
    private readonly ICartridgeMirroringProvider? _provider;
    private readonly NametableMirroring _fixedMirroring;

    internal CartridgeMirroringWiringComponent(ICpuBusDevice device, string boardId, NametableMirroring fixedMirroring)
    {
        _provider = device as ICartridgeMirroringProvider;
        _fixedMirroring = fixedMirroring;
        ModuleId = $"nes.cartridge.{boardId}.mirroring";
    }

    public string ModuleId { get; }
    public NametableMirroring Current => _provider?.Mirroring ?? _fixedMirroring;
    public bool IsMapperControlled => _provider is not null;
    public void PowerOn() { }
    public void Reset() { }
}

public sealed class CartridgeIrqOutputComponent : INesHardwareModule
{
    private readonly ICartridgeIrqProvider? _provider;

    internal CartridgeIrqOutputComponent(ICpuBusDevice device, string boardId)
    {
        _provider = device as ICartridgeIrqProvider;
        ModuleId = $"nes.cartridge.{boardId}.irq";
    }

    public string ModuleId { get; }
    public bool IsConnected => _provider is not null;
    public bool IsAsserted => _provider?.IrqAsserted ?? false;
    public void PowerOn() { }
    public void Reset() { }
}
