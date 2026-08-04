using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Processors.Tiny8;

/// <summary>
/// A deliberately small processor used to validate the virtual-hardware
/// architecture. It fetches, reads and writes exclusively through physical
/// address/data/control pins.
/// </summary>
public sealed class Tiny8Processor : VirtualHardwareComponent
{
    private enum ExecutionPhase
    {
        FetchOpcode,
        LoadImmediate,
        ReadOperandAddressForStore,
        CommitStore,
        ReadOperandAddressForLoad,
        LoadMemory,
        Halted
    }

    public const byte LoadImmediateOpcode = 0x10;
    public const byte StoreAbsoluteOpcode = 0x20;
    public const byte LoadAbsoluteOpcode = 0x30;
    public const byte HaltOpcode = 0xFF;

    private ExecutionPhase _phase;
    private DigitalLevel _previousClock;
    private byte _operandAddress;

    public Tiny8Processor(string componentId, byte resetVector = 0x80)
        : base(componentId)
    {
        ResetVector = resetVector;

        var addressPins = new DigitalPin[8];
        var dataPins = new DigitalPin[8];
        for (var bit = 0; bit < 8; bit++)
        {
            addressPins[bit] = AddPin($"A{bit}", PinDirection.Output);
            dataPins[bit] = AddPin($"D{bit}", PinDirection.Bidirectional);
        }

        Address = new DigitalBus($"{componentId}.A", addressPins);
        Data = new DigitalBus($"{componentId}.D", dataPins);
        ReadWrite = AddPin("R/W", PinDirection.Output);
        Clock = AddPin("CLK", PinDirection.Input);
        ResetBar = AddPin("/RESET", PinDirection.Input);
    }

    public byte ResetVector { get; }
    public DigitalBus Address { get; }
    public DigitalBus Data { get; }
    public DigitalPin ReadWrite { get; }
    public DigitalPin Clock { get; }
    public DigitalPin ResetBar { get; }
    public byte ProgramCounter { get; private set; }
    public byte Accumulator { get; private set; }
    public byte CurrentOpcode { get; private set; }
    public bool IsHalted => _phase == ExecutionPhase.Halted;
    public ulong RisingEdgeCount { get; private set; }
    public ulong InstructionCount { get; private set; }

    public override void PowerOn()
    {
        ProgramCounter = ResetVector;
        Accumulator = 0;
        CurrentOpcode = 0;
        _operandAddress = 0;
        _phase = ExecutionPhase.FetchOpcode;
        _previousClock = DigitalLevel.Low;
        RisingEdgeCount = 0;
        InstructionCount = 0;
        PrepareRead(ProgramCounter);
    }

    public override void Reset() => PowerOn();

    public override void Evaluate()
    {
        if (ResetBar.SampledLevel == DigitalLevel.Low)
        {
            PowerOn();
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
        ExecuteRisingEdge();
    }

    private void ExecuteRisingEdge()
    {
        switch (_phase)
        {
            case ExecutionPhase.FetchOpcode:
                if (!TrySampleByte(out var opcode))
                {
                    return;
                }

                CurrentOpcode = opcode;
                ProgramCounter++;
                switch (opcode)
                {
                    case LoadImmediateOpcode:
                        _phase = ExecutionPhase.LoadImmediate;
                        PrepareRead(ProgramCounter);
                        break;
                    case StoreAbsoluteOpcode:
                        _phase = ExecutionPhase.ReadOperandAddressForStore;
                        PrepareRead(ProgramCounter);
                        break;
                    case LoadAbsoluteOpcode:
                        _phase = ExecutionPhase.ReadOperandAddressForLoad;
                        PrepareRead(ProgramCounter);
                        break;
                    case HaltOpcode:
                        InstructionCount++;
                        _phase = ExecutionPhase.Halted;
                        Data.Release();
                        ReadWrite.Drive(DigitalLevel.High);
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown Tiny8 opcode 0x{opcode:X2} at 0x{(byte)(ProgramCounter - 1):X2}.");
                }
                break;

            case ExecutionPhase.LoadImmediate:
                if (!TrySampleByte(out var immediate))
                {
                    return;
                }

                Accumulator = immediate;
                ProgramCounter++;
                CompleteInstruction();
                break;

            case ExecutionPhase.ReadOperandAddressForStore:
                if (!TrySampleByte(out _operandAddress))
                {
                    return;
                }

                ProgramCounter++;
                _phase = ExecutionPhase.CommitStore;
                Address.Drive(_operandAddress);
                Data.Drive(Accumulator);
                ReadWrite.Drive(DigitalLevel.Low);
                break;

            case ExecutionPhase.CommitStore:
                CompleteInstruction();
                break;

            case ExecutionPhase.ReadOperandAddressForLoad:
                if (!TrySampleByte(out _operandAddress))
                {
                    return;
                }

                ProgramCounter++;
                _phase = ExecutionPhase.LoadMemory;
                PrepareRead(_operandAddress);
                break;

            case ExecutionPhase.LoadMemory:
                if (!TrySampleByte(out var value))
                {
                    return;
                }

                Accumulator = value;
                CompleteInstruction();
                break;

            case ExecutionPhase.Halted:
                break;
        }
    }

    private bool TrySampleByte(out byte value)
    {
        if (Data.TrySample(out var raw))
        {
            value = (byte)raw;
            return true;
        }

        value = 0;
        return false;
    }

    private void CompleteInstruction()
    {
        InstructionCount++;
        _phase = ExecutionPhase.FetchOpcode;
        PrepareRead(ProgramCounter);
    }

    private void PrepareRead(byte address)
    {
        Data.Release();
        Address.Drive(address);
        ReadWrite.Drive(DigitalLevel.High);
    }
}
