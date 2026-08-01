using AxetosOS.Products.NES.Abstractions;

namespace AxetosOS.Products.NES.Hardware;

public sealed class Rp2A03Cpu : INesHardwareModule, IClockedHardwareModule
{
    public const byte CarryFlag = 1 << 0;
    public const byte ZeroFlag = 1 << 1;
    public const byte InterruptDisableFlag = 1 << 2;
    public const byte DecimalFlag = 1 << 3;
    public const byte BreakFlag = 1 << 4;
    public const byte UnusedFlag = 1 << 5;
    public const byte OverflowFlag = 1 << 6;
    public const byte NegativeFlag = 1 << 7;

    private readonly CpuBus _bus;
    private int _cyclesRemaining;
    private bool _nmiPending;
    private bool _irqLine;

    public Rp2A03Cpu(CpuBus bus)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
    }

    public string ModuleId => "nes.chip.rp2a03.cpu";
    public byte Accumulator { get; private set; }
    public byte X { get; private set; }
    public byte Y { get; private set; }
    public byte StackPointer { get; private set; }
    public byte Status { get; private set; }
    public ushort ProgramCounter { get; private set; }
    public ulong TotalCycles { get; private set; }
    public ulong InstructionsExecuted { get; private set; }
    public byte LastOpcode { get; private set; }
    public bool IsInstructionBoundary => _cyclesRemaining == 0;

    public void PowerOn()
    {
        Accumulator = 0;
        X = 0;
        Y = 0;
        StackPointer = 0x00;
        Status = InterruptDisableFlag | UnusedFlag;
        ProgramCounter = 0;
        TotalCycles = 0;
        InstructionsExecuted = 0;
        LastOpcode = 0;
        _cyclesRemaining = 0;
        _nmiPending = false;
        _irqLine = false;
        Reset();
    }

    public void Reset()
    {
        StackPointer = unchecked((byte)(StackPointer - 3));
        Status = NormalizeStatus((byte)(Status | InterruptDisableFlag));
        ProgramCounter = ReadWord(0xFFFC);
        _cyclesRemaining = 7;
    }

    public void RequestNmi() => _nmiPending = true;

    public void SetIrqLine(bool asserted) => _irqLine = asserted;

    public bool IsFlagSet(byte flag) => (Status & flag) != 0;

    public void Clock()
    {
        TotalCycles++;

        if (_cyclesRemaining > 0)
        {
            _cyclesRemaining--;
            return;
        }

        if (_nmiPending)
        {
            _nmiPending = false;
            EnterInterrupt(0xFFFA, breakFlag: false);
            _cyclesRemaining = 6;
            return;
        }

        if (_irqLine && !IsFlagSet(InterruptDisableFlag))
        {
            EnterInterrupt(0xFFFE, breakFlag: false);
            _cyclesRemaining = 6;
            return;
        }

        LastOpcode = ReadByte(ProgramCounter++);
        InstructionsExecuted++;
        _cyclesRemaining = Execute(LastOpcode) - 1;
    }

    private int Execute(byte opcode) => opcode switch
    {
        0x00 => Break(),
        0x18 => SetFlag(CarryFlag, false, 2),
        0x20 => JumpToSubroutine(),
        0x38 => SetFlag(CarryFlag, true, 2),
        0x40 => ReturnFromInterrupt(),
        0x4C => JumpAbsolute(),
        0x58 => SetFlag(InterruptDisableFlag, false, 2),
        0x60 => ReturnFromSubroutine(),
        0x69 => AddWithCarryImmediate(),
        0x78 => SetFlag(InterruptDisableFlag, true, 2),
        0x84 => StoreZeroPage(Y),
        0x85 => StoreZeroPage(Accumulator),
        0x86 => StoreZeroPage(X),
        0x88 => DecrementY(),
        0x8A => Transfer(X, value => Accumulator = value),
        0x8C => StoreAbsolute(Y),
        0x8D => StoreAbsolute(Accumulator),
        0x8E => StoreAbsolute(X),
        0x98 => Transfer(Y, value => Accumulator = value),
        0x9A => TransferToStackPointer(),
        0xA0 => LoadImmediate(value => Y = value),
        0xA2 => LoadImmediate(value => X = value),
        0xA8 => Transfer(Accumulator, value => Y = value),
        0xA9 => LoadImmediate(value => Accumulator = value),
        0xAA => Transfer(Accumulator, value => X = value),
        0xB8 => SetFlag(OverflowFlag, false, 2),
        0xBA => Transfer(StackPointer, value => X = value),
        0xC8 => IncrementY(),
        0xCA => DecrementX(),
        0xD0 => Branch(!IsFlagSet(ZeroFlag)),
        0xD8 => SetFlag(DecimalFlag, false, 2),
        0xE8 => IncrementX(),
        0xE9 => SubtractWithCarryImmediate(),
        0xEA => 2,
        0xF0 => Branch(IsFlagSet(ZeroFlag)),
        0xF8 => SetFlag(DecimalFlag, true, 2), // Accepted but decimal arithmetic remains disabled on RP2A03.
        _ => throw new UnsupportedCpuOpcodeException(opcode, unchecked((ushort)(ProgramCounter - 1)))
    };

    private int LoadImmediate(Action<byte> destination)
    {
        var value = ReadByte(ProgramCounter++);
        destination(value);
        SetZeroAndNegativeFlags(value);
        return 2;
    }

    private int StoreZeroPage(byte value)
    {
        var address = ReadByte(ProgramCounter++);
        _bus.Write(address, value);
        return 3;
    }

    private int StoreAbsolute(byte value)
    {
        var address = ReadWord(ProgramCounter);
        ProgramCounter += 2;
        _bus.Write(address, value);
        return 4;
    }

    private int Transfer(byte value, Action<byte> destination)
    {
        destination(value);
        SetZeroAndNegativeFlags(value);
        return 2;
    }

    private int TransferToStackPointer()
    {
        StackPointer = X;
        return 2;
    }

    private int IncrementX()
    {
        X++;
        SetZeroAndNegativeFlags(X);
        return 2;
    }

    private int IncrementY()
    {
        Y++;
        SetZeroAndNegativeFlags(Y);
        return 2;
    }

    private int DecrementX()
    {
        X--;
        SetZeroAndNegativeFlags(X);
        return 2;
    }

    private int DecrementY()
    {
        Y--;
        SetZeroAndNegativeFlags(Y);
        return 2;
    }

    private int AddWithCarryImmediate()
    {
        var value = ReadByte(ProgramCounter++);
        var carryIn = IsFlagSet(CarryFlag) ? 1 : 0;
        var sum = Accumulator + value + carryIn;
        var result = (byte)sum;

        SetFlagValue(CarryFlag, sum > 0xFF);
        SetFlagValue(OverflowFlag, (~(Accumulator ^ value) & (Accumulator ^ result) & 0x80) != 0);
        Accumulator = result;
        SetZeroAndNegativeFlags(Accumulator);
        return 2;
    }

    private int SubtractWithCarryImmediate()
    {
        var value = ReadByte(ProgramCounter++);
        var borrow = IsFlagSet(CarryFlag) ? 0 : 1;
        var difference = Accumulator - value - borrow;
        var result = (byte)difference;

        SetFlagValue(CarryFlag, difference >= 0);
        SetFlagValue(OverflowFlag, ((Accumulator ^ result) & (Accumulator ^ value) & 0x80) != 0);
        Accumulator = result;
        SetZeroAndNegativeFlags(Accumulator);
        return 2;
    }

    private int Branch(bool condition)
    {
        var offset = unchecked((sbyte)ReadByte(ProgramCounter++));
        if (!condition)
        {
            return 2;
        }

        var previous = ProgramCounter;
        ProgramCounter = unchecked((ushort)(ProgramCounter + offset));
        return (previous & 0xFF00) != (ProgramCounter & 0xFF00) ? 4 : 3;
    }

    private int JumpAbsolute()
    {
        ProgramCounter = ReadWord(ProgramCounter);
        return 3;
    }

    private int JumpToSubroutine()
    {
        var destination = ReadWord(ProgramCounter);
        ProgramCounter += 2;
        var returnAddress = unchecked((ushort)(ProgramCounter - 1));
        Push((byte)(returnAddress >> 8));
        Push((byte)returnAddress);
        ProgramCounter = destination;
        return 6;
    }

    private int ReturnFromSubroutine()
    {
        var low = Pop();
        var high = Pop();
        ProgramCounter = unchecked((ushort)(((high << 8) | low) + 1));
        return 6;
    }

    private int Break()
    {
        ProgramCounter++;
        EnterInterrupt(0xFFFE, breakFlag: true);
        return 7;
    }

    private int ReturnFromInterrupt()
    {
        Status = NormalizeStatus((byte)(Pop() & ~BreakFlag));
        var low = Pop();
        var high = Pop();
        ProgramCounter = (ushort)(low | (high << 8));
        return 6;
    }

    private void EnterInterrupt(ushort vector, bool breakFlag)
    {
        Push((byte)(ProgramCounter >> 8));
        Push((byte)ProgramCounter);
        var pushedStatus = breakFlag
            ? (byte)(Status | BreakFlag | UnusedFlag)
            : (byte)((Status & ~BreakFlag) | UnusedFlag);
        Push(pushedStatus);
        Status = NormalizeStatus((byte)(Status | InterruptDisableFlag));
        ProgramCounter = ReadWord(vector);
    }

    private int SetFlag(byte flag, bool enabled, int cycles)
    {
        SetFlagValue(flag, enabled);
        return cycles;
    }

    private void SetFlagValue(byte flag, bool enabled)
    {
        Status = enabled ? (byte)(Status | flag) : (byte)(Status & ~flag);
        Status = NormalizeStatus(Status);
    }

    private void SetZeroAndNegativeFlags(byte value)
    {
        SetFlagValue(ZeroFlag, value == 0);
        SetFlagValue(NegativeFlag, (value & 0x80) != 0);
    }

    private void Push(byte value)
    {
        _bus.Write((ushort)(0x0100 | StackPointer), value);
        StackPointer--;
    }

    private byte Pop()
    {
        StackPointer++;
        return ReadByte((ushort)(0x0100 | StackPointer));
    }

    private byte ReadByte(ushort address) => _bus.Read(address);

    private ushort ReadWord(ushort address)
    {
        var low = ReadByte(address);
        var high = ReadByte(unchecked((ushort)(address + 1)));
        return (ushort)(low | (high << 8));
    }

    private static byte NormalizeStatus(byte status) => (byte)((status | UnusedFlag) & ~BreakFlag);
}

public sealed class UnsupportedCpuOpcodeException(byte opcode, ushort address)
    : NotSupportedException($"CPU opcode ${opcode:X2} at ${address:X4} is not implemented yet.")
{
    public byte Opcode { get; } = opcode;
    public ushort Address { get; } = address;
}
