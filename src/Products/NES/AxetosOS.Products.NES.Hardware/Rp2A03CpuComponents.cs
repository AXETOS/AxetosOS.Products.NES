using AxetosOS.Products.NES.Abstractions;

namespace AxetosOS.Products.NES.Hardware;

/// <summary>
/// Passive, allocation-free views over the live RP2A03 CPU core. The CPU remains
/// the sole owner of execution state; these components establish inspectable
/// functional boundaries for later chip-level extraction.
/// </summary>
public sealed class Rp2A03CpuRegisterFileComponent : INesHardwareModule
{
    private readonly Rp2A03Cpu _cpu;

    internal Rp2A03CpuRegisterFileComponent(Rp2A03Cpu cpu) =>
        _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));

    public string ModuleId => "nes.chip.rp2a03.cpu.register-file";
    public byte Accumulator => _cpu.Accumulator;
    public byte X => _cpu.X;
    public byte Y => _cpu.Y;
    public byte StackPointer => _cpu.StackPointer;
    public byte Status => _cpu.Status;
    public ushort ProgramCounter => _cpu.ProgramCounter;
    public void PowerOn() { }
    public void Reset() { }
}

public sealed class Rp2A03CpuExecutionUnitComponent : INesHardwareModule
{
    private readonly Rp2A03Cpu _cpu;

    internal Rp2A03CpuExecutionUnitComponent(Rp2A03Cpu cpu) =>
        _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));

    public string ModuleId => "nes.chip.rp2a03.cpu.execution-unit";
    public byte LastOpcode => _cpu.LastOpcode;
    public ulong InstructionsExecuted => _cpu.InstructionsExecuted;
    public ulong TotalCycles => _cpu.TotalCycles;
    public int CyclesRemaining => _cpu.CyclesRemaining;
    public int PendingMicroOperations => _cpu.ScheduledMicroOperationCount;
    public bool IsInstructionBoundary => _cpu.IsInstructionBoundary;
    public void PowerOn() { }
    public void Reset() { }
}

public sealed class Rp2A03CpuInterruptControllerComponent : INesHardwareModule
{
    private readonly Rp2A03Cpu _cpu;

    internal Rp2A03CpuInterruptControllerComponent(Rp2A03Cpu cpu) =>
        _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));

    public string ModuleId => "nes.chip.rp2a03.cpu.interrupt-controller";
    public bool NmiPending => _cpu.NmiPending;
    public bool NmiPinAsserted => _cpu.Signals.Nmi.IsAsserted;
    public bool IrqPinAsserted => _cpu.Signals.Irq.IsAsserted;
    public bool ResetPinAsserted => _cpu.Signals.Reset.IsAsserted;
    public bool ReadyPinAsserted => _cpu.Signals.Rdy.IsAsserted;
    public bool InterruptSequenceIsNmi => _cpu.InterruptSequenceIsNmi;
    public ushort InterruptVector => _cpu.InterruptVector;
    public byte InterruptVectorLow => _cpu.InterruptVectorLow;
    public ulong NmiServiced => _cpu.NmiServiced;
    public ulong IrqServiced => _cpu.IrqServiced;
    public ulong BrkExecuted => _cpu.BrkExecuted;
    public ulong RtiExecuted => _cpu.RtiExecuted;
    public ulong ReadyStallCycles => _cpu.ReadyStallCycles;
    public void PowerOn() { }
    public void Reset() { }
}
