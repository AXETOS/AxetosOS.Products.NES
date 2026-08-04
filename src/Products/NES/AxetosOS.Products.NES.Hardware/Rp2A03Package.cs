using AxetosOS.Products.NES.Abstractions;

namespace AxetosOS.Products.NES.Hardware;

/// <summary>
/// Inspectable functional composition of the RP2A03 package. Runtime behavior
/// continues to be provided by the already validated CPU, APU, controller I/O,
/// DMA, and signal-line components; this package defines their physical ownership
/// and internal connections without adding a second execution path.
/// </summary>
public sealed class Rp2A03Package : IHardwareCompositeModule
{
    private readonly HardwareComponentDescriptor[] _components;
    private readonly HardwareConnectionDescriptor[] _connections;

    public Rp2A03Package(
        Rp2A03Cpu cpu,
        Rp2A03Apu apu,
        NesControllerPorts controllerIo,
        OamDmaController dmaController,
        Rp2A03SignalLines signals)
    {
        Cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));
        Apu = apu ?? throw new ArgumentNullException(nameof(apu));
        ControllerIo = controllerIo ?? throw new ArgumentNullException(nameof(controllerIo));
        DmaController = dmaController ?? throw new ArgumentNullException(nameof(dmaController));
        Signals = signals ?? throw new ArgumentNullException(nameof(signals));
        RegisterFile = new Rp2A03CpuRegisterFileComponent(Cpu);
        ExecutionUnit = new Rp2A03CpuExecutionUnitComponent(Cpu);
        InterruptController = new Rp2A03CpuInterruptControllerComponent(Cpu);

        _components =
        [
            new(ModuleId, "RP2A03 package", HardwareComponentKind.Chip, this),
            new(Cpu.ModuleId, "6502-derived CPU core", HardwareComponentKind.Chip, Cpu),
            new(RegisterFile.ModuleId, "CPU register file", HardwareComponentKind.Internal, RegisterFile),
            new(ExecutionUnit.ModuleId, "CPU execution unit and microsequencer", HardwareComponentKind.Internal, ExecutionUnit),
            new(InterruptController.ModuleId, "CPU interrupt controller", HardwareComponentKind.Internal, InterruptController),
            new(Apu.ModuleId, "Audio processing unit", HardwareComponentKind.Chip, Apu),
            new(Apu.FrameSequencer.ModuleId, "APU frame sequencer", HardwareComponentKind.Internal, Apu.FrameSequencer),
            new(Apu.Pulse1.ModuleId, "Pulse channel 1", HardwareComponentKind.Internal, Apu.Pulse1),
            new(Apu.Pulse2.ModuleId, "Pulse channel 2", HardwareComponentKind.Internal, Apu.Pulse2),
            new(Apu.Triangle.ModuleId, "Triangle channel", HardwareComponentKind.Internal, Apu.Triangle),
            new(Apu.Noise.ModuleId, "Noise channel", HardwareComponentKind.Internal, Apu.Noise),
            new(Apu.Dmc.ModuleId, "DMC channel", HardwareComponentKind.Internal, Apu.Dmc),
            new(ControllerIo.ModuleId, "Controller I/O registers", HardwareComponentKind.InputOutput, ControllerIo),
            new(ControllerIo.Port1.ModuleId, "Controller port 1", HardwareComponentKind.InputOutput, ControllerIo.Port1),
            new(ControllerIo.Port2.ModuleId, "Controller port 2", HardwareComponentKind.InputOutput, ControllerIo.Port2),
            new(ControllerIo.StrobeLine.ModuleId, "Controller strobe line", HardwareComponentKind.SignalBundle, ControllerIo.StrobeLine),
            new(DmaController.ModuleId, "OAM/DMC DMA unit", HardwareComponentKind.DmaController, DmaController),
            new(DmaController.OamChannel.ModuleId, "OAM DMA channel", HardwareComponentKind.DmaController, DmaController.OamChannel),
            new(DmaController.DmcChannel.ModuleId, "DMC sample DMA channel", HardwareComponentKind.DmaController, DmaController.DmcChannel),
            new(DmaController.BusArbiter.ModuleId, "DMA bus arbiter", HardwareComponentKind.Internal, DmaController.BusArbiter),
            new(SignalBundleId, "RP2A03 external signal pins", HardwareComponentKind.SignalBundle, Signals)
        ];

        _connections =
        [
            new(ModuleId, Cpu.ModuleId, HardwareConnectionKind.Internal, "CPU core"),
            new(Cpu.ModuleId, RegisterFile.ModuleId, HardwareConnectionKind.Internal, "architectural registers"),
            new(RegisterFile.ModuleId, ExecutionUnit.ModuleId, HardwareConnectionKind.Internal, "operand/result path"),
            new(ExecutionUnit.ModuleId, Cpu.ModuleId, HardwareConnectionKind.Internal, "bus micro-operations"),
            new(InterruptController.ModuleId, ExecutionUnit.ModuleId, HardwareConnectionKind.Signal, "interrupt entry request"),
            new(Cpu.ModuleId, InterruptController.ModuleId, HardwareConnectionKind.Internal, "interrupt state"),
            new(ModuleId, Apu.ModuleId, HardwareConnectionKind.Internal, "APU"),
            new(Apu.ModuleId, Apu.FrameSequencer.ModuleId, HardwareConnectionKind.Internal, "quarter/half-frame clocks"),
            new(Apu.FrameSequencer.ModuleId, Apu.Pulse1.ModuleId, HardwareConnectionKind.Clock, "envelope/length/sweep"),
            new(Apu.FrameSequencer.ModuleId, Apu.Pulse2.ModuleId, HardwareConnectionKind.Clock, "envelope/length/sweep"),
            new(Apu.FrameSequencer.ModuleId, Apu.Triangle.ModuleId, HardwareConnectionKind.Clock, "linear/length counters"),
            new(Apu.FrameSequencer.ModuleId, Apu.Noise.ModuleId, HardwareConnectionKind.Clock, "envelope/length counter"),
            new(Apu.Pulse1.ModuleId, Apu.ModuleId, HardwareConnectionKind.Internal, "pulse mixer input"),
            new(Apu.Pulse2.ModuleId, Apu.ModuleId, HardwareConnectionKind.Internal, "pulse mixer input"),
            new(Apu.Triangle.ModuleId, Apu.ModuleId, HardwareConnectionKind.Internal, "TND mixer input"),
            new(Apu.Noise.ModuleId, Apu.ModuleId, HardwareConnectionKind.Internal, "TND mixer input"),
            new(Apu.Dmc.ModuleId, Apu.ModuleId, HardwareConnectionKind.Internal, "TND mixer input"),
            new(ModuleId, ControllerIo.ModuleId, HardwareConnectionKind.Internal, "controller I/O"),
            new(ControllerIo.ModuleId, ControllerIo.StrobeLine.ModuleId, HardwareConnectionKind.Signal, "$4016 OUT0"),
            new(ControllerIo.StrobeLine.ModuleId, ControllerIo.Port1.ModuleId, HardwareConnectionKind.Signal, "port 1 latch"),
            new(ControllerIo.StrobeLine.ModuleId, ControllerIo.Port2.ModuleId, HardwareConnectionKind.Signal, "port 2 latch"),
            new(ControllerIo.Port1.ModuleId, ControllerIo.ModuleId, HardwareConnectionKind.Internal, "serial D0"),
            new(ControllerIo.Port2.ModuleId, ControllerIo.ModuleId, HardwareConnectionKind.Internal, "serial D0"),
            new(ModuleId, DmaController.ModuleId, HardwareConnectionKind.Internal, "DMA"),
            new(DmaController.ModuleId, DmaController.OamChannel.ModuleId, HardwareConnectionKind.Internal, "OAM DMA channel"),
            new(DmaController.ModuleId, DmaController.DmcChannel.ModuleId, HardwareConnectionKind.Internal, "DMC DMA channel"),
            new(DmaController.OamChannel.ModuleId, DmaController.BusArbiter.ModuleId, HardwareConnectionKind.Dma, "OAM bus request"),
            new(DmaController.DmcChannel.ModuleId, DmaController.BusArbiter.ModuleId, HardwareConnectionKind.Dma, "DMC bus request"),
            new(Cpu.ModuleId, SignalBundleId, HardwareConnectionKind.Signal, "NMI/IRQ/RESET/RDY"),
            new(Apu.ModuleId, SignalBundleId, HardwareConnectionKind.Signal, "IRQ"),
            new(DmaController.ModuleId, SignalBundleId, HardwareConnectionKind.Signal, "RDY"),
            new(DmaController.ModuleId, Apu.ModuleId, HardwareConnectionKind.Internal, "DMC arbitration")
        ];
    }

    public string ModuleId => "nes.chip.rp2a03";
    public string SignalBundleId => "nes.signal.rp2a03";
    public Rp2A03Cpu Cpu { get; }
    public Rp2A03CpuRegisterFileComponent RegisterFile { get; }
    public Rp2A03CpuExecutionUnitComponent ExecutionUnit { get; }
    public Rp2A03CpuInterruptControllerComponent InterruptController { get; }
    public Rp2A03Apu Apu { get; }
    public NesControllerPorts ControllerIo { get; }
    public OamDmaController DmaController { get; }
    public Rp2A03SignalLines Signals { get; }
    public IReadOnlyList<HardwareComponentDescriptor> HardwareComponents => _components;
    public IReadOnlyList<HardwareConnectionDescriptor> HardwareConnections => _connections;
}
