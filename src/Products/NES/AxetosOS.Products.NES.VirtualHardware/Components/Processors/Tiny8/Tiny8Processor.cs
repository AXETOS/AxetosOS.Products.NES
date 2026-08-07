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
    private byte _operandAddress;
    private readonly ulong _clockInputMask;
    private readonly ulong _resetInputMask;
    private bool _resetAsserted;

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
        Clock = AddPin("CLK", PinDirection.Input, DigitalInputActivation.RisingEdge);
        ResetBar = AddPin("/RESET", PinDirection.Input);
        _clockInputMask = Clock.InputChangeMask;
        _resetInputMask = ResetBar.InputChangeMask;
    
        InitializePackageState();
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

    private void InitializePackageState()
    {
        ProgramCounter = ResetVector;
        Accumulator = 0;
        CurrentOpcode = 0;
        _operandAddress = 0;
        _phase = ExecutionPhase.FetchOpcode;
        RisingEdgeCount = 0;
        InstructionCount = 0;
        PrepareRead(ProgramCounter);
    }

    protected override void OnInputChanges(ulong changedInputMask)
    {
        var resetChanged = (changedInputMask & _resetInputMask) != 0;
        var clockRising = (changedInputMask & _clockInputMask) != 0;
        if (!resetChanged && !clockRising) return;

        if (ResetBar.SampledLevel == DigitalLevel.Low)
        {
            if (!_resetAsserted) InitializePackageState();
            _resetAsserted = true;
            return;
        }

        _resetAsserted = false;
        if (!clockRising || Clock.SampledLevel != DigitalLevel.High) return;

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
