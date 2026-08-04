using AxetosOS.Products.NES.Abstractions;

namespace AxetosOS.Products.NES.Hardware;

/// <summary>
/// Functional NES motherboard composition. The board owns the physical wiring
/// between the major chips and devices; hosts consume the assembled machine
/// rather than reproducing that wiring themselves.
/// </summary>
public sealed class NesMotherboard : INesHardwareModule, IHardwareCompositeModule
{
    private readonly List<INesHardwareModule> _powerOrder;
    private readonly List<HardwareComponentDescriptor> _hardwareComponents;
    private readonly List<HardwareConnectionDescriptor> _hardwareConnections;

    public NesMotherboard(
        CartridgeHardware cartridge,
        INesControllerInput controllerInput,
        AxetosOS.Products.NES.Cartridges.NametableMirroring mirroring,
        NesTimingProfile? timing = null)
    {
        ArgumentNullException.ThrowIfNull(cartridge);
        ArgumentNullException.ThrowIfNull(controllerInput);

        Timing = timing ?? NesTimingProfile.For(AxetosOS.Products.NES.Cartridges.NesTimingMode.Ntsc);
        Cartridge = cartridge;

        CpuSignals = new Rp2A03SignalLines();
        CpuBus = new CpuBus();
        PpuBus = new PpuBus();
        WorkRam = new CpuWorkRam();
        var initialMirroring = cartridge.PrgDevice is ICartridgeMirroringProvider initialMirroringProvider
            ? initialMirroringProvider.Mirroring
            : mirroring;
        CartridgeBoard = new CartridgeBoardPackage(cartridge, initialMirroring);
        CpuAddressDecoder = new NesCpuAddressDecoder(CpuBus);
        PpuAddressDecoder = new NesPpuAddressDecoder(PpuBus);
        CartridgeConnector = new NesCartridgeConnectorPackage(CpuBus, PpuBus, CartridgeBoard);
        Ciram = new CiramNametableRam(initialMirroring);
        PaletteRam = new PpuPaletteRam();

        if (cartridge.PrgDevice is ICartridgeMirroringProvider mirroringProvider)
        {
            mirroringProvider.MirroringChanged += Ciram.SetMirroring;
        }

        PpuBus.Attach(cartridge.ChrDevice);
        PpuBus.Attach(Ciram);
        PpuBus.Attach(PaletteRam);

        Ppu = new Rp2C02Ppu(PpuBus, CpuSignals.Nmi, Timing);
        Rp2C02 = new Rp2C02Package(Ppu, Ppu.SpriteEvaluator, PaletteRam, CpuSignals.Nmi);
        Controllers = new NesControllerPorts(controllerInput);
        Apu = new Rp2A03Apu();

        CpuBus.Attach(WorkRam);
        CpuBus.Attach(Ppu);
        CpuBus.Attach(cartridge.PrgDevice);
        CpuBus.Attach(Controllers);
        CpuBus.Attach(Apu);

        Cpu = new Rp2A03Cpu(CpuBus, CpuSignals);
        IrqLines = new IrqLineCombiner(SetCpuIrqLine);
        Apu.IrqLineChanged += IrqLines.CreateSource();
        if (cartridge.PrgDevice is ICartridgeIrqProvider cartridgeIrq)
            cartridgeIrq.IrqLineChanged += IrqLines.CreateSource();

        OamDma = new OamDmaController(CpuBus, Ppu, Cpu);
        OamDma.AttachDmc(Apu);
        CpuBus.Attach(OamDma);

        Clock = new NesMasterClock(Cpu, Ppu, Apu, Timing);
        ClockNetwork = new NesClockNetworkPackage(Clock);
        SignalNetwork = new Rp2A03SignalNetworkPackage(CpuSignals, IrqLines);
        Rp2A03 = new Rp2A03Package(Cpu, Apu, Controllers, OamDma, CpuSignals);
        ConsoleIo = new NesConsoleIoPackage(Ppu, Apu, Controllers, PowerOn, Reset);

        _powerOrder = [Controllers, Apu, OamDma, Ppu, Cpu];

        _hardwareComponents =
        [
            new(ModuleId, "NES Motherboard", HardwareComponentKind.Board, this),
            .. ClockNetwork.HardwareComponents,
            new(Rp2A03.ModuleId, "RP2A03 CPU/APU package", HardwareComponentKind.Chip, Rp2A03),
            new(CpuBus.ModuleId, "CPU bus", HardwareComponentKind.Bus, CpuBus),
            new(PpuBus.ModuleId, "PPU bus", HardwareComponentKind.Bus, PpuBus),
            new(CpuAddressDecoder.ModuleId, "CPU address decoder", HardwareComponentKind.Internal, CpuAddressDecoder),
            new(PpuAddressDecoder.ModuleId, "PPU address decoder", HardwareComponentKind.Internal, PpuAddressDecoder),
            .. CartridgeConnector.HardwareComponents,
            new(Cpu.ModuleId, "RP2A03 CPU core", HardwareComponentKind.Chip, Cpu),
            new(Apu.ModuleId, "RP2A03 APU", HardwareComponentKind.Chip, Apu),
            new(Rp2C02.ModuleId, "RP2C02 PPU package", HardwareComponentKind.Chip, Rp2C02),
            new(Ppu.ModuleId, "RP2C02 timing/fetch/pixel core", HardwareComponentKind.Internal, Ppu),
            new(Ppu.SpriteEvaluator.ModuleId, "RP2C02 sprite evaluator", HardwareComponentKind.Internal, Ppu.SpriteEvaluator),
            new(Rp2C02.PrimaryOam.ModuleId, "256-byte primary OAM", HardwareComponentKind.Memory, Rp2C02.PrimaryOam),
            new(Rp2C02.SecondaryOam.ModuleId, "32-byte secondary OAM", HardwareComponentKind.Memory, Rp2C02.SecondaryOam),
            new(WorkRam.ModuleId, "2 KiB CPU work RAM", HardwareComponentKind.Memory, WorkRam),
            new(Ciram.ModuleId, "2 KiB CIRAM", HardwareComponentKind.Memory, Ciram),
            new(PaletteRam.ModuleId, "PPU palette RAM", HardwareComponentKind.Memory, PaletteRam),
            new(Controllers.ModuleId, "Controller I/O registers", HardwareComponentKind.InputOutput, Controllers),
            new(Controllers.Port1.ModuleId, "Controller port 1 serial connector", HardwareComponentKind.InputOutput, Controllers.Port1),
            new(Controllers.Port2.ModuleId, "Controller port 2 serial connector", HardwareComponentKind.InputOutput, Controllers.Port2),
            new(Controllers.StrobeLine.ModuleId, "Controller OUT0 strobe line", HardwareComponentKind.SignalBundle, Controllers.StrobeLine),
            new(OamDma.ModuleId, "OAM/DMC DMA controller", HardwareComponentKind.DmaController, OamDma),
            new(OamDma.OamChannel.ModuleId, "OAM DMA channel", HardwareComponentKind.DmaController, OamDma.OamChannel),
            new(OamDma.DmcChannel.ModuleId, "DMC sample DMA channel", HardwareComponentKind.DmaController, OamDma.DmcChannel),
            new(OamDma.BusArbiter.ModuleId, "DMA bus arbiter", HardwareComponentKind.Internal, OamDma.BusArbiter),
            .. SignalNetwork.HardwareComponents,
            .. CartridgeBoard.HardwareComponents,
            .. ConsoleIo.HardwareComponents
        ];

        _hardwareConnections =
        [
            .. ClockNetwork.HardwareConnections,
            new(ClockNetwork.CpuDivider.ModuleId, Rp2A03.ModuleId, HardwareConnectionKind.Clock, "RP2A03 clock input"),
            new(Rp2A03.ModuleId, Cpu.ModuleId, HardwareConnectionKind.Internal, "CPU core"),
            new(Rp2A03.ModuleId, Apu.ModuleId, HardwareConnectionKind.Internal, "APU"),
            new(ClockNetwork.PpuDivider.ModuleId, Rp2C02.ModuleId, HardwareConnectionKind.Clock, "RP2C02 clock input"),
            new(Rp2C02.ModuleId, Ppu.ModuleId, HardwareConnectionKind.Internal, "timing/fetch/pixel core"),
            new(Ppu.ModuleId, Rp2C02.PrimaryOam.ModuleId, HardwareConnectionKind.Internal, "primary OAM address/data path"),
            new(Rp2C02.PrimaryOam.ModuleId, Ppu.SpriteEvaluator.ModuleId, HardwareConnectionKind.Internal, "sprite evaluation reads"),
            new(Ppu.SpriteEvaluator.ModuleId, Rp2C02.SecondaryOam.ModuleId, HardwareConnectionKind.Internal, "secondary OAM clear/copy"),
            new(Cpu.ModuleId, CpuBus.ModuleId, HardwareConnectionKind.CpuBus),
            new(CpuBus.ModuleId, CpuAddressDecoder.ModuleId, HardwareConnectionKind.Internal, "address/control decode"),
            new(PpuBus.ModuleId, PpuAddressDecoder.ModuleId, HardwareConnectionKind.Internal, "14-bit address decode"),
            new(CpuAddressDecoder.ModuleId, CartridgeConnector.Cpu.ModuleId, HardwareConnectionKind.CartridgeConnector, "$4020-$FFFF chip select"),
            new(PpuAddressDecoder.ModuleId, CartridgeConnector.Ppu.ModuleId, HardwareConnectionKind.CartridgeConnector, "$0000-$1FFF chip select"),
            .. CartridgeConnector.HardwareConnections,
            new(CpuBus.ModuleId, WorkRam.ModuleId, HardwareConnectionKind.CpuBus),
            new(CpuBus.ModuleId, Ppu.ModuleId, HardwareConnectionKind.CpuBus, "CPU register interface"),
            new(CpuBus.ModuleId, Apu.ModuleId, HardwareConnectionKind.CpuBus),
            new(CpuBus.ModuleId, Controllers.ModuleId, HardwareConnectionKind.CpuBus, "$4016/$4017"),
            new(Controllers.ModuleId, Controllers.StrobeLine.ModuleId, HardwareConnectionKind.Signal, "$4016 OUT0"),
            new(Controllers.StrobeLine.ModuleId, Controllers.Port1.ModuleId, HardwareConnectionKind.Signal, "latch/continuous A"),
            new(Controllers.StrobeLine.ModuleId, Controllers.Port2.ModuleId, HardwareConnectionKind.Signal, "latch/continuous A"),
            new(Controllers.Port1.ModuleId, Controllers.ModuleId, HardwareConnectionKind.Internal, "serial D0"),
            new(Controllers.Port2.ModuleId, Controllers.ModuleId, HardwareConnectionKind.Internal, "serial D0"),
            new(CpuBus.ModuleId, OamDma.ModuleId, HardwareConnectionKind.Dma),
            new(OamDma.ModuleId, OamDma.OamChannel.ModuleId, HardwareConnectionKind.Internal, "$4014 OAM channel"),
            new(OamDma.ModuleId, OamDma.DmcChannel.ModuleId, HardwareConnectionKind.Internal, "DMC sample channel"),
            new(OamDma.OamChannel.ModuleId, OamDma.BusArbiter.ModuleId, HardwareConnectionKind.Dma, "OAM bus request"),
            new(OamDma.DmcChannel.ModuleId, OamDma.BusArbiter.ModuleId, HardwareConnectionKind.Dma, "DMC bus request"),
            new(CpuBus.ModuleId, $"nes.cartridge.{cartridge.BoardId}", HardwareConnectionKind.CartridgeConnector, "PRG side"),
            new(Ppu.ModuleId, PpuBus.ModuleId, HardwareConnectionKind.PpuBus),
            new(PpuBus.ModuleId, Ciram.ModuleId, HardwareConnectionKind.PpuBus),
            new(PpuBus.ModuleId, PaletteRam.ModuleId, HardwareConnectionKind.PpuBus),
            new(PpuBus.ModuleId, $"nes.cartridge.{cartridge.BoardId}", HardwareConnectionKind.CartridgeConnector, "CHR side"),
            .. SignalNetwork.HardwareConnections,
            new(Ppu.ModuleId, SignalNetwork.Nmi.ModuleId, HardwareConnectionKind.Signal, "NMI"),
            new(Apu.ModuleId, "nes.signal.irq-combiner", HardwareConnectionKind.Signal, "APU IRQ source"),
            new($"nes.cartridge.{cartridge.BoardId}", "nes.signal.irq-combiner", HardwareConnectionKind.Signal, "cartridge IRQ source"),
            new(OamDma.BusArbiter.ModuleId, SignalNetwork.Rdy.ModuleId, HardwareConnectionKind.Signal, "RDY"),
            new(SignalNetwork.Rdy.ModuleId, Cpu.ModuleId, HardwareConnectionKind.Signal, "CPU ready input"),
            new(SignalNetwork.Nmi.ModuleId, Cpu.ModuleId, HardwareConnectionKind.Signal, "CPU NMI input"),
            new(SignalNetwork.Irq.ModuleId, Cpu.ModuleId, HardwareConnectionKind.Signal, "CPU IRQ input"),
            new(SignalNetwork.Reset.ModuleId, Cpu.ModuleId, HardwareConnectionKind.Signal, "CPU reset input"),
            .. CartridgeBoard.HardwareConnections,
            new(CpuBus.ModuleId, CartridgeConnector.Cpu.ModuleId, HardwareConnectionKind.CartridgeConnector, "CPU address/data/control"),
            new(CartridgeConnector.Cpu.ModuleId, CartridgeBoard.Prg.ModuleId, HardwareConnectionKind.CartridgeConnector, "PRG board contacts"),
            new(PpuBus.ModuleId, CartridgeConnector.Ppu.ModuleId, HardwareConnectionKind.CartridgeConnector, "PPU address/data/control"),
            new(CartridgeConnector.Ppu.ModuleId, CartridgeBoard.Chr.ModuleId, HardwareConnectionKind.CartridgeConnector, "CHR board contacts"),
            new(CartridgeBoard.Mirroring.ModuleId, Ciram.ModuleId, HardwareConnectionKind.Signal, "nametable select wiring"),
            new(CartridgeBoard.IrqOutput.ModuleId, "nes.signal.irq-combiner", HardwareConnectionKind.Signal, "IRQ"),
            .. ConsoleIo.HardwareConnections
        ];

        Inspection = new NesHardwareInspectionRegistry(this, _hardwareComponents, _hardwareConnections);
    }

    public string ModuleId => "nes.board.motherboard";
    public NesTimingProfile Timing { get; }
    public CartridgeHardware Cartridge { get; }
    public CartridgeBoardPackage CartridgeBoard { get; }
    public Rp2A03SignalLines CpuSignals { get; }
    public Rp2A03Package Rp2A03 { get; }
    public Rp2C02Package Rp2C02 { get; }
    public CpuBus CpuBus { get; }
    public PpuBus PpuBus { get; }
    public NesCpuAddressDecoder CpuAddressDecoder { get; }
    public NesPpuAddressDecoder PpuAddressDecoder { get; }
    public NesCartridgeConnectorPackage CartridgeConnector { get; }
    public CpuWorkRam WorkRam { get; }
    public CiramNametableRam Ciram { get; }
    public PpuPaletteRam PaletteRam { get; }
    public Rp2C02PrimaryOamMemory PrimaryOam => Rp2C02.PrimaryOam;
    public Rp2C02SecondaryOamMemory SecondaryOam => Rp2C02.SecondaryOam;
    public NesControllerPorts Controllers { get; }
    public Rp2A03Apu Apu { get; }
    public Rp2A03Cpu Cpu { get; }
    public Rp2C02Ppu Ppu { get; }
    public OamDmaController OamDma { get; }
    public IrqLineCombiner IrqLines { get; }
    public NesMasterClock Clock { get; }
    public NesClockNetworkPackage ClockNetwork { get; }
    public Rp2A03SignalNetworkPackage SignalNetwork { get; }
    public NesConsoleIoPackage ConsoleIo { get; }
    public NesHardwareInspectionRegistry Inspection { get; }

    public IReadOnlyList<INesHardwareModule> Components => _powerOrder;
    public IReadOnlyList<HardwareComponentDescriptor> HardwareComponents => _hardwareComponents;
    public IReadOnlyList<HardwareConnectionDescriptor> HardwareConnections => _hardwareConnections;

    public void PowerOn()
    {
        ConsoleIo.PowerSwitch.MarkPowered();
        foreach (var component in _powerOrder)
            component.PowerOn();
    }

    public void Reset()
    {
        foreach (var component in _powerOrder)
            component.Reset();
    }

    private void SetCpuIrqLine(bool asserted)
    {
        if (asserted) CpuSignals.Irq.Assert();
        else CpuSignals.Irq.Release();
    }
}
