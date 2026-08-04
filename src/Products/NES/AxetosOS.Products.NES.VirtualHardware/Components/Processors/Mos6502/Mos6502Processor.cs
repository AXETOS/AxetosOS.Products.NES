using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Processors.Mos6502;

/// <summary>
/// Pin-driven 6502-family processor foundation. Every external bus operation is
/// expressed by address, data and control pins; the component has no reference
/// to RAM, ROM, a motherboard, or any NES-specific bus implementation.
/// </summary>
public sealed class Mos6502Processor : VirtualHardwareComponent
{
    private enum CycleState
    {
        ResetVectorLow,
        ResetVectorHigh,
        FetchOpcode,
        ReadImmediate,
        Halted
    }

    private CycleState _state;
    private DigitalLevel _previousClock;
    private byte _resetVectorLow;

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
    public byte Accumulator { get; private set; }
    public byte CurrentOpcode { get; private set; }
    public bool IsHalted => _state == CycleState.Halted;
    public ulong RisingEdgeCount { get; private set; }
    public ulong CompletedInstructionCount { get; private set; }
    public ulong ReadyStallCount { get; private set; }

    public override void PowerOn()
    {
        ProgramCounter = 0;
        Accumulator = 0;
        CurrentOpcode = 0;
        _resetVectorLow = 0;
        RisingEdgeCount = 0;
        CompletedInstructionCount = 0;
        ReadyStallCount = 0;
        _previousClock = DigitalLevel.Low;
        BeginResetSequence();
    }

    public override void Reset() => BeginResetSequence();

    public override void Evaluate()
    {
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
        if (Ready.SampledLevel == DigitalLevel.Low)
        {
            ReadyStallCount++;
            return;
        }

        ExecuteBusCycle();
    }

    private void BeginResetSequence()
    {
        _state = CycleState.ResetVectorLow;
        Data.Release();
        ReadWrite.Drive(DigitalLevel.High);
        Sync.Drive(DigitalLevel.Low);
        Address.Drive(0xFFFC);
    }

    private void ExecuteBusCycle()
    {
        switch (_state)
        {
            case CycleState.ResetVectorLow:
                if (!TrySampleData(out _resetVectorLow))
                {
                    return;
                }

                _state = CycleState.ResetVectorHigh;
                Address.Drive(0xFFFD);
                break;

            case CycleState.ResetVectorHigh:
                if (!TrySampleData(out var vectorHigh))
                {
                    return;
                }

                ProgramCounter = (ushort)(_resetVectorLow | (vectorHigh << 8));
                BeginOpcodeFetch();
                break;

            case CycleState.FetchOpcode:
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
                        Address.Drive(ProgramCounter);
                        break;
                    case 0x00: // BRK is a temporary stop marker for this foundation stage.
                        CompletedInstructionCount++;
                        _state = CycleState.Halted;
                        Data.Release();
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
                ProgramCounter++;
                CompletedInstructionCount++;
                BeginOpcodeFetch();
                break;

            case CycleState.Halted:
                break;
        }
    }

    private void BeginOpcodeFetch()
    {
        _state = CycleState.FetchOpcode;
        Data.Release();
        ReadWrite.Drive(DigitalLevel.High);
        Sync.Drive(DigitalLevel.High);
        Address.Drive(ProgramCounter);
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
