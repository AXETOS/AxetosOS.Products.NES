using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Processors.Mos6502;

/// <summary>
/// Pin-driven 6502-family processor foundation. Every external bus operation is
/// expressed by address, data and control pins; the component has no reference
/// to RAM, ROM, a motherboard, or any NES-specific bus implementation.
/// </summary>
public sealed class Mos6502Processor : VirtualHardwareComponent
{
    private const byte ZeroFlag = 1 << 1;
    private const byte InterruptDisableFlag = 1 << 2;
    private const byte BreakFlag = 1 << 4;
    private const byte UnusedFlag = 1 << 5;
    private const byte NegativeFlag = 1 << 7;

    private enum CycleState
    {
        ResetDummyRead1,
        ResetDummyRead2,
        ResetStackRead1,
        ResetStackRead2,
        ResetStackRead3,
        ResetVectorLow,
        ResetVectorHigh,
        FetchOpcode,
        ReadImmediate,
        InterruptDummyRead,
        InterruptPushProgramCounterHigh,
        InterruptPushProgramCounterLow,
        InterruptPushStatus,
        InterruptVectorLow,
        InterruptVectorHigh,
        Halted
    }

    private enum InterruptKind
    {
        None,
        Irq,
        Nmi
    }

    private CycleState _state;
    private InterruptKind _activeInterrupt;
    private DigitalLevel _previousClock;
    private DigitalLevel _previousNmi;
    private byte _vectorLow;
    private bool _nmiPending;

    public Mos6502Processor(string componentId)
        : base(componentId)
    {
        var addressPins = new DigitalPin[16];
        var dataPins = new DigitalPin[8];
        for (var bit = 0; bit < addressPins.Length; bit++)
        {
            addressPins[bit] = AddPin($"A{bit}", PinDirection.Output);
        }

        for (var bit = 0; bit < dataPins.Length; bit++)
        {
            dataPins[bit] = AddPin($"D{bit}", PinDirection.Bidirectional);
        }

        Address = new DigitalBus($"{componentId}.A", addressPins);
        Data = new DigitalBus($"{componentId}.D", dataPins);
        ReadWrite = AddPin("R/W", PinDirection.Output);
        Sync = AddPin("SYNC", PinDirection.Output);
        Clock = AddPin("PHI2", PinDirection.Input);
        ResetBar = AddPin("/RESET", PinDirection.Input);
        IrqBar = AddPin("/IRQ", PinDirection.Input);
        NmiBar = AddPin("/NMI", PinDirection.Input);
        Ready = AddPin("RDY", PinDirection.Input);
    }

    public DigitalBus Address { get; }
    public DigitalBus Data { get; }
    public DigitalPin ReadWrite { get; }
    public DigitalPin Sync { get; }
    public DigitalPin Clock { get; }
    public DigitalPin ResetBar { get; }
    public DigitalPin IrqBar { get; }
    public DigitalPin NmiBar { get; }
    public DigitalPin Ready { get; }

    public ushort ProgramCounter { get; private set; }
    public byte StackPointer { get; private set; }
    public byte Status { get; private set; }
    public byte Accumulator { get; private set; }
    public byte CurrentOpcode { get; private set; }
    public bool IsHalted => _state == CycleState.Halted;
    public bool InterruptDisable => (Status & InterruptDisableFlag) != 0;
    public bool NmiPending => _nmiPending;
    public ulong RisingEdgeCount { get; private set; }
    public ulong CompletedInstructionCount { get; private set; }
    public ulong CompletedInterruptCount { get; private set; }
    public ulong ReadyStallCount { get; private set; }

    public override void PowerOn()
    {
        ProgramCounter = 0;
        StackPointer = 0x00;
        Status = InterruptDisableFlag | UnusedFlag;
        Accumulator = 0;
        CurrentOpcode = 0;
        _vectorLow = 0;
        _activeInterrupt = InterruptKind.None;
        _nmiPending = false;
        RisingEdgeCount = 0;
        CompletedInstructionCount = 0;
        CompletedInterruptCount = 0;
        ReadyStallCount = 0;
        _previousClock = DigitalLevel.Low;
        _previousNmi = DigitalLevel.High;
        BeginResetSequence();
    }

    public override void Reset() => BeginResetSequence();

    public override void Evaluate()
    {
        SampleNmiEdge();

        if (ResetBar.SampledLevel == DigitalLevel.Low)
        {
            BeginResetSequence();
            _previousClock = Clock.SampledLevel;
            return;
        }

        var clock = Clock.SampledLevel;
        var risingEdge = _previousClock == DigitalLevel.Low && clock == DigitalLevel.High;
        _previousClock = clock;
        if (!risingEdge || ResetBar.SampledLevel != DigitalLevel.High)
        {
            return;
        }

        RisingEdgeCount++;
        if (IsReadCycle() && Ready.SampledLevel == DigitalLevel.Low)
        {
            ReadyStallCount++;
            return;
        }

        ExecuteBusCycle();
    }

    private void SampleNmiEdge()
    {
        var current = NmiBar.SampledLevel;
        if (_previousNmi == DigitalLevel.High && current == DigitalLevel.Low)
        {
            _nmiPending = true;
        }

        if (current is DigitalLevel.High or DigitalLevel.Low)
        {
            _previousNmi = current;
        }
    }

    private void BeginResetSequence()
    {
        _state = CycleState.ResetDummyRead1;
        _activeInterrupt = InterruptKind.None;
        _nmiPending = false;
        StackPointer = 0x00;
        Status = InterruptDisableFlag | UnusedFlag;
        BeginRead(ProgramCounter, sync: false);
    }

    private void ExecuteBusCycle()
    {
        switch (_state)
        {
            case CycleState.ResetDummyRead1:
                _state = CycleState.ResetDummyRead2;
                BeginRead(ProgramCounter, sync: false);
                break;

            case CycleState.ResetDummyRead2:
                _state = CycleState.ResetStackRead1;
                BeginRead(StackAddress, sync: false);
                break;

            case CycleState.ResetStackRead1:
                StackPointer--;
                _state = CycleState.ResetStackRead2;
                BeginRead(StackAddress, sync: false);
                break;

            case CycleState.ResetStackRead2:
                StackPointer--;
                _state = CycleState.ResetStackRead3;
                BeginRead(StackAddress, sync: false);
                break;

            case CycleState.ResetStackRead3:
                StackPointer--;
                _state = CycleState.ResetVectorLow;
                BeginRead(0xFFFC, sync: false);
                break;

            case CycleState.ResetVectorLow:
                if (!TrySampleData(out _vectorLow))
                {
                    return;
                }

                _state = CycleState.ResetVectorHigh;
                BeginRead(0xFFFD, sync: false);
                break;

            case CycleState.ResetVectorHigh:
                if (!TrySampleData(out var resetVectorHigh))
                {
                    return;
                }

                ProgramCounter = (ushort)(_vectorLow | (resetVectorHigh << 8));
                BeginOpcodeFetch();
                break;

            case CycleState.FetchOpcode:
                if (TryBeginPendingInterrupt())
                {
                    break;
                }

                if (!TrySampleData(out var opcode))
                {
                    return;
                }

                CurrentOpcode = opcode;
                ProgramCounter++;
                Sync.Drive(DigitalLevel.Low);
                switch (opcode)
                {
                    case 0xEA: // NOP
                        CompletedInstructionCount++;
                        BeginOpcodeFetch();
                        break;
                    case 0xA9: // LDA #immediate
                        _state = CycleState.ReadImmediate;
                        BeginRead(ProgramCounter, sync: false);
                        break;
                    case 0x58: // CLI
                        Status = (byte)((Status & ~InterruptDisableFlag) | UnusedFlag);
                        CompletedInstructionCount++;
                        BeginOpcodeFetch();
                        break;
                    case 0x78: // SEI
                        Status = (byte)(Status | InterruptDisableFlag | UnusedFlag);
                        CompletedInstructionCount++;
                        BeginOpcodeFetch();
                        break;
                    case 0x00: // BRK remains a temporary stop marker at this foundation stage.
                        CompletedInstructionCount++;
                        _state = CycleState.Halted;
                        Data.Release();
                        Sync.Drive(DigitalLevel.Low);
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"MOS6502 foundation encountered unsupported opcode 0x{opcode:X2} at 0x{(ushort)(ProgramCounter - 1):X4}.");
                }
                break;

            case CycleState.ReadImmediate:
                if (!TrySampleData(out var immediate))
                {
                    return;
                }

                Accumulator = immediate;
                SetZeroAndNegativeFlags(immediate);
                ProgramCounter++;
                CompletedInstructionCount++;
                BeginOpcodeFetch();
                break;

            case CycleState.InterruptDummyRead:
                _state = CycleState.InterruptPushProgramCounterHigh;
                BeginWrite(StackAddress, (byte)(ProgramCounter >> 8));
                break;

            case CycleState.InterruptPushProgramCounterHigh:
                StackPointer--;
                _state = CycleState.InterruptPushProgramCounterLow;
                BeginWrite(StackAddress, (byte)ProgramCounter);
                break;

            case CycleState.InterruptPushProgramCounterLow:
                StackPointer--;
                _state = CycleState.InterruptPushStatus;
                BeginWrite(StackAddress, StatusForHardwareInterrupt);
                break;

            case CycleState.InterruptPushStatus:
                StackPointer--;
                _state = CycleState.InterruptVectorLow;
                BeginRead(VectorAddress, sync: false);
                break;

            case CycleState.InterruptVectorLow:
                if (!TrySampleData(out _vectorLow))
                {
                    return;
                }

                _state = CycleState.InterruptVectorHigh;
                BeginRead((ushort)(VectorAddress + 1), sync: false);
                break;

            case CycleState.InterruptVectorHigh:
                if (!TrySampleData(out var interruptVectorHigh))
                {
                    return;
                }

                ProgramCounter = (ushort)(_vectorLow | (interruptVectorHigh << 8));
                _activeInterrupt = InterruptKind.None;
                CompletedInterruptCount++;
                BeginOpcodeFetch();
                break;

            case CycleState.Halted:
                break;
        }
    }

    private bool TryBeginPendingInterrupt()
    {
        if (_nmiPending)
        {
            _nmiPending = false;
            BeginInterrupt(InterruptKind.Nmi);
            return true;
        }

        if (IrqBar.SampledLevel == DigitalLevel.Low && !InterruptDisable)
        {
            BeginInterrupt(InterruptKind.Irq);
            return true;
        }

        return false;
    }

    private void BeginInterrupt(InterruptKind kind)
    {
        _activeInterrupt = kind;
        Status = (byte)(Status | InterruptDisableFlag | UnusedFlag);
        _state = CycleState.InterruptDummyRead;
        BeginRead(ProgramCounter, sync: false);
    }

    private void BeginOpcodeFetch()
    {
        _state = CycleState.FetchOpcode;
        BeginRead(ProgramCounter, sync: true);
    }

    private void BeginRead(ushort address, bool sync)
    {
        Data.Release();
        ReadWrite.Drive(DigitalLevel.High);
        Sync.Drive(sync ? DigitalLevel.High : DigitalLevel.Low);
        Address.Drive(address);
    }

    private void BeginWrite(ushort address, byte value)
    {
        Sync.Drive(DigitalLevel.Low);
        Address.Drive(address);
        Data.Drive(value);
        ReadWrite.Drive(DigitalLevel.Low);
    }

    private bool IsReadCycle() => ReadWrite.DriveLevel != DigitalLevel.Low;

    private ushort StackAddress => (ushort)(0x0100 | StackPointer);

    private ushort VectorAddress => _activeInterrupt == InterruptKind.Nmi ? (ushort)0xFFFA : (ushort)0xFFFE;

    private byte StatusForHardwareInterrupt => (byte)((Status | UnusedFlag) & ~BreakFlag);

    private void SetZeroAndNegativeFlags(byte value)
    {
        Status = value == 0 ? (byte)(Status | ZeroFlag) : (byte)(Status & ~ZeroFlag);
        Status = (value & 0x80) != 0 ? (byte)(Status | NegativeFlag) : (byte)(Status & ~NegativeFlag);
        Status |= UnusedFlag;
    }

    private bool TrySampleData(out byte value)
    {
        if (Data.TrySample(out var raw))
        {
            value = (byte)raw;
            return true;
        }

        value = 0;
        return false;
    }
}
