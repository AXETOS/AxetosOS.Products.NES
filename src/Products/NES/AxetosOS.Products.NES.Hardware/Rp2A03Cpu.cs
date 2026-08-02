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

    public Rp2A03Cpu(CpuBus bus) => _bus = bus ?? throw new ArgumentNullException(nameof(bus));

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
        StackPointer = 0;
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
            EnterInterrupt(0xFFFA, false);
            _cyclesRemaining = 6;
            return;
        }

        if (_irqLine && !IsFlagSet(InterruptDisableFlag))
        {
            EnterInterrupt(0xFFFE, false);
            _cyclesRemaining = 6;
            return;
        }

        LastOpcode = ReadByte(ProgramCounter++);
        InstructionsExecuted++;
        _cyclesRemaining = Execute(LastOpcode) - 1;
    }

    private int Execute(byte opcode) => opcode switch
    {
        // BRK / control / flags
        0x00 => Break(), 0x18 => Flag(CarryFlag, false), 0x38 => Flag(CarryFlag, true),
        0x58 => Flag(InterruptDisableFlag, false), 0x78 => Flag(InterruptDisableFlag, true),
        0xB8 => Flag(OverflowFlag, false), 0xD8 => Flag(DecimalFlag, false), 0xF8 => Flag(DecimalFlag, true),
        0xEA => 2,

        // ORA
        0x01 => ReadOp(IndexedIndirect(), Or, 6), 0x05 => ReadOp(ZeroPage(), Or, 3),
        0x09 => ReadOp(Immediate(), Or, 2), 0x0D => ReadOp(Absolute(), Or, 4),
        0x11 => ReadOp(IndirectIndexed(out var p11), Or, 5 + Bool(p11)),
        0x15 => ReadOp(ZeroPageX(), Or, 4), 0x19 => ReadOp(AbsoluteY(out var p19), Or, 4 + Bool(p19)),
        0x1D => ReadOp(AbsoluteX(out var p1D), Or, 4 + Bool(p1D)),

        // AND
        0x21 => ReadOp(IndexedIndirect(), And, 6), 0x25 => ReadOp(ZeroPage(), And, 3),
        0x29 => ReadOp(Immediate(), And, 2), 0x2D => ReadOp(Absolute(), And, 4),
        0x31 => ReadOp(IndirectIndexed(out var p31), And, 5 + Bool(p31)),
        0x35 => ReadOp(ZeroPageX(), And, 4), 0x39 => ReadOp(AbsoluteY(out var p39), And, 4 + Bool(p39)),
        0x3D => ReadOp(AbsoluteX(out var p3D), And, 4 + Bool(p3D)),

        // EOR
        0x41 => ReadOp(IndexedIndirect(), Eor, 6), 0x45 => ReadOp(ZeroPage(), Eor, 3),
        0x49 => ReadOp(Immediate(), Eor, 2), 0x4D => ReadOp(Absolute(), Eor, 4),
        0x51 => ReadOp(IndirectIndexed(out var p51), Eor, 5 + Bool(p51)),
        0x55 => ReadOp(ZeroPageX(), Eor, 4), 0x59 => ReadOp(AbsoluteY(out var p59), Eor, 4 + Bool(p59)),
        0x5D => ReadOp(AbsoluteX(out var p5D), Eor, 4 + Bool(p5D)),

        // ADC
        0x61 => ReadOp(IndexedIndirect(), AddWithCarry, 6), 0x65 => ReadOp(ZeroPage(), AddWithCarry, 3),
        0x69 => ReadOp(Immediate(), AddWithCarry, 2), 0x6D => ReadOp(Absolute(), AddWithCarry, 4),
        0x71 => ReadOp(IndirectIndexed(out var p71), AddWithCarry, 5 + Bool(p71)),
        0x75 => ReadOp(ZeroPageX(), AddWithCarry, 4), 0x79 => ReadOp(AbsoluteY(out var p79), AddWithCarry, 4 + Bool(p79)),
        0x7D => ReadOp(AbsoluteX(out var p7D), AddWithCarry, 4 + Bool(p7D)),

        // SBC
        0xE1 => ReadOp(IndexedIndirect(), SubtractWithCarry, 6), 0xE5 => ReadOp(ZeroPage(), SubtractWithCarry, 3),
        0xE9 => ReadOp(Immediate(), SubtractWithCarry, 2), 0xED => ReadOp(Absolute(), SubtractWithCarry, 4),
        0xF1 => ReadOp(IndirectIndexed(out var pF1), SubtractWithCarry, 5 + Bool(pF1)),
        0xF5 => ReadOp(ZeroPageX(), SubtractWithCarry, 4), 0xF9 => ReadOp(AbsoluteY(out var pF9), SubtractWithCarry, 4 + Bool(pF9)),
        0xFD => ReadOp(AbsoluteX(out var pFD), SubtractWithCarry, 4 + Bool(pFD)),

        // CMP / CPX / CPY
        0xC1 => ReadOp(IndexedIndirect(), value => Compare(Accumulator, value), 6),
        0xC5 => ReadOp(ZeroPage(), value => Compare(Accumulator, value), 3),
        0xC9 => ReadOp(Immediate(), value => Compare(Accumulator, value), 2),
        0xCD => ReadOp(Absolute(), value => Compare(Accumulator, value), 4),
        0xD1 => ReadOp(IndirectIndexed(out var pD1), value => Compare(Accumulator, value), 5 + Bool(pD1)),
        0xD5 => ReadOp(ZeroPageX(), value => Compare(Accumulator, value), 4),
        0xD9 => ReadOp(AbsoluteY(out var pD9), value => Compare(Accumulator, value), 4 + Bool(pD9)),
        0xDD => ReadOp(AbsoluteX(out var pDD), value => Compare(Accumulator, value), 4 + Bool(pDD)),
        0xE0 => ReadOp(Immediate(), value => Compare(X, value), 2), 0xE4 => ReadOp(ZeroPage(), value => Compare(X, value), 3),
        0xEC => ReadOp(Absolute(), value => Compare(X, value), 4),
        0xC0 => ReadOp(Immediate(), value => Compare(Y, value), 2), 0xC4 => ReadOp(ZeroPage(), value => Compare(Y, value), 3),
        0xCC => ReadOp(Absolute(), value => Compare(Y, value), 4),

        // LDA
        0xA1 => Load(IndexedIndirect(), value => Accumulator = value, 6), 0xA5 => Load(ZeroPage(), value => Accumulator = value, 3),
        0xA9 => Load(Immediate(), value => Accumulator = value, 2), 0xAD => Load(Absolute(), value => Accumulator = value, 4),
        0xB1 => Load(IndirectIndexed(out var pB1), value => Accumulator = value, 5 + Bool(pB1)),
        0xB5 => Load(ZeroPageX(), value => Accumulator = value, 4), 0xB9 => Load(AbsoluteY(out var pB9), value => Accumulator = value, 4 + Bool(pB9)),
        0xBD => Load(AbsoluteX(out var pBD), value => Accumulator = value, 4 + Bool(pBD)),

        // LDX
        0xA2 => Load(Immediate(), value => X = value, 2), 0xA6 => Load(ZeroPage(), value => X = value, 3),
        0xAE => Load(Absolute(), value => X = value, 4), 0xB6 => Load(ZeroPageY(), value => X = value, 4),
        0xBE => Load(AbsoluteY(out var pBE), value => X = value, 4 + Bool(pBE)),

        // LDY
        0xA0 => Load(Immediate(), value => Y = value, 2), 0xA4 => Load(ZeroPage(), value => Y = value, 3),
        0xAC => Load(Absolute(), value => Y = value, 4), 0xB4 => Load(ZeroPageX(), value => Y = value, 4),
        0xBC => Load(AbsoluteX(out var pBC), value => Y = value, 4 + Bool(pBC)),

        // STA / STX / STY
        0x81 => Store(IndexedIndirect(), Accumulator, 6), 0x85 => Store(ZeroPage(), Accumulator, 3),
        0x8D => Store(Absolute(), Accumulator, 4), 0x91 => Store(IndirectIndexed(out _), Accumulator, 6),
        0x95 => Store(ZeroPageX(), Accumulator, 4), 0x99 => Store(AbsoluteY(out _), Accumulator, 5),
        0x9D => Store(AbsoluteX(out _), Accumulator, 5),
        0x86 => Store(ZeroPage(), X, 3), 0x8E => Store(Absolute(), X, 4), 0x96 => Store(ZeroPageY(), X, 4),
        0x84 => Store(ZeroPage(), Y, 3), 0x8C => Store(Absolute(), Y, 4), 0x94 => Store(ZeroPageX(), Y, 4),

        // BIT
        0x24 => ReadOp(ZeroPage(), Bit, 3), 0x2C => ReadOp(Absolute(), Bit, 4),

        // ASL / LSR / ROL / ROR
        0x0A => AccumulatorShift(Asl), 0x06 => Modify(ZeroPage(), Asl, 5), 0x0E => Modify(Absolute(), Asl, 6),
        0x16 => Modify(ZeroPageX(), Asl, 6), 0x1E => Modify(AbsoluteX(out _), Asl, 7),
        0x4A => AccumulatorShift(Lsr), 0x46 => Modify(ZeroPage(), Lsr, 5), 0x4E => Modify(Absolute(), Lsr, 6),
        0x56 => Modify(ZeroPageX(), Lsr, 6), 0x5E => Modify(AbsoluteX(out _), Lsr, 7),
        0x2A => AccumulatorShift(Rol), 0x26 => Modify(ZeroPage(), Rol, 5), 0x2E => Modify(Absolute(), Rol, 6),
        0x36 => Modify(ZeroPageX(), Rol, 6), 0x3E => Modify(AbsoluteX(out _), Rol, 7),
        0x6A => AccumulatorShift(Ror), 0x66 => Modify(ZeroPage(), Ror, 5), 0x6E => Modify(Absolute(), Ror, 6),
        0x76 => Modify(ZeroPageX(), Ror, 6), 0x7E => Modify(AbsoluteX(out _), Ror, 7),

        // INC / DEC
        0xE6 => Modify(ZeroPage(), Inc, 5), 0xEE => Modify(Absolute(), Inc, 6),
        0xF6 => Modify(ZeroPageX(), Inc, 6), 0xFE => Modify(AbsoluteX(out _), Inc, 7),
        0xC6 => Modify(ZeroPage(), Dec, 5), 0xCE => Modify(Absolute(), Dec, 6),
        0xD6 => Modify(ZeroPageX(), Dec, 6), 0xDE => Modify(AbsoluteX(out _), Dec, 7),

        // Branches
        0x10 => Branch(!IsFlagSet(NegativeFlag)), 0x30 => Branch(IsFlagSet(NegativeFlag)),
        0x50 => Branch(!IsFlagSet(OverflowFlag)), 0x70 => Branch(IsFlagSet(OverflowFlag)),
        0x90 => Branch(!IsFlagSet(CarryFlag)), 0xB0 => Branch(IsFlagSet(CarryFlag)),
        0xD0 => Branch(!IsFlagSet(ZeroFlag)), 0xF0 => Branch(IsFlagSet(ZeroFlag)),

        // Jumps / calls / returns
        0x20 => JumpToSubroutine(), 0x40 => ReturnFromInterrupt(), 0x4C => JumpAbsolute(),
        0x60 => ReturnFromSubroutine(), 0x6C => JumpIndirect(),

        // Stack
        0x08 => PushStatus(), 0x28 => PullStatus(), 0x48 => PushAccumulator(), 0x68 => PullAccumulator(),

        // Transfers and register increments/decrements
        0x88 => Register(value => Y = value, unchecked((byte)(Y - 1))),
        0x98 => Transfer(Y, value => Accumulator = value), 0xA8 => Transfer(Accumulator, value => Y = value),
        0xAA => Transfer(Accumulator, value => X = value), 0x8A => Transfer(X, value => Accumulator = value),
        0x9A => TransferToStackPointer(), 0xBA => Transfer(StackPointer, value => X = value),
        0xC8 => Register(value => Y = value, unchecked((byte)(Y + 1))),
        0xCA => Register(value => X = value, unchecked((byte)(X - 1))),
        0xE8 => Register(value => X = value, unchecked((byte)(X + 1))),

        _ => throw new UnsupportedCpuOpcodeException(opcode, unchecked((ushort)(ProgramCounter - 1)))
    };

    private ushort Immediate() => ProgramCounter++;
    private ushort ZeroPage() => ReadByte(ProgramCounter++);
    private ushort ZeroPageX() => unchecked((byte)(ReadByte(ProgramCounter++) + X));
    private ushort ZeroPageY() => unchecked((byte)(ReadByte(ProgramCounter++) + Y));

    private ushort Absolute()
    {
        var address = ReadWord(ProgramCounter);
        ProgramCounter += 2;
        return address;
    }

    private ushort AbsoluteX(out bool pageCrossed)
    {
        var baseAddress = Absolute();
        var address = unchecked((ushort)(baseAddress + X));
        pageCrossed = PageCrossed(baseAddress, address);
        return address;
    }

    private ushort AbsoluteY(out bool pageCrossed)
    {
        var baseAddress = Absolute();
        var address = unchecked((ushort)(baseAddress + Y));
        pageCrossed = PageCrossed(baseAddress, address);
        return address;
    }

    private ushort IndexedIndirect()
    {
        var pointer = unchecked((byte)(ReadByte(ProgramCounter++) + X));
        return ReadZeroPageWord(pointer);
    }

    private ushort IndirectIndexed(out bool pageCrossed)
    {
        var pointer = ReadByte(ProgramCounter++);
        var baseAddress = ReadZeroPageWord(pointer);
        var address = unchecked((ushort)(baseAddress + Y));
        pageCrossed = PageCrossed(baseAddress, address);
        return address;
    }

    private int ReadOp(ushort address, Action<byte> operation, int cycles)
    {
        operation(ReadByte(address));
        return cycles;
    }

    private int Load(ushort address, Action<byte> destination, int cycles)
    {
        var value = ReadByte(address);
        destination(value);
        SetZeroAndNegativeFlags(value);
        return cycles;
    }

    private int Store(ushort address, byte value, int cycles)
    {
        _bus.Write(address, value);
        return cycles;
    }

    private int Modify(ushort address, Func<byte, byte> operation, int cycles)
    {
        _bus.Write(address, operation(ReadByte(address)));
        return cycles;
    }

    private int AccumulatorShift(Func<byte, byte> operation)
    {
        Accumulator = operation(Accumulator);
        return 2;
    }

    private void Or(byte value)
    {
        Accumulator |= value;
        SetZeroAndNegativeFlags(Accumulator);
    }

    private void And(byte value)
    {
        Accumulator &= value;
        SetZeroAndNegativeFlags(Accumulator);
    }

    private void Eor(byte value)
    {
        Accumulator ^= value;
        SetZeroAndNegativeFlags(Accumulator);
    }

    private void AddWithCarry(byte value)
    {
        var carry = IsFlagSet(CarryFlag) ? 1 : 0;
        var sum = Accumulator + value + carry;
        var result = (byte)sum;
        SetFlagValue(CarryFlag, sum > 0xFF);
        SetFlagValue(OverflowFlag, (~(Accumulator ^ value) & (Accumulator ^ result) & 0x80) != 0);
        Accumulator = result;
        SetZeroAndNegativeFlags(result);
    }

    private void SubtractWithCarry(byte value) => AddWithCarry((byte)~value);

    private void Compare(byte register, byte value)
    {
        var result = unchecked((byte)(register - value));
        SetFlagValue(CarryFlag, register >= value);
        SetZeroAndNegativeFlags(result);
    }

    private void Bit(byte value)
    {
        SetFlagValue(ZeroFlag, (Accumulator & value) == 0);
        SetFlagValue(OverflowFlag, (value & OverflowFlag) != 0);
        SetFlagValue(NegativeFlag, (value & NegativeFlag) != 0);
    }

    private byte Asl(byte value)
    {
        SetFlagValue(CarryFlag, (value & 0x80) != 0);
        var result = unchecked((byte)(value << 1));
        SetZeroAndNegativeFlags(result);
        return result;
    }

    private byte Lsr(byte value)
    {
        SetFlagValue(CarryFlag, (value & 0x01) != 0);
        var result = (byte)(value >> 1);
        SetZeroAndNegativeFlags(result);
        return result;
    }

    private byte Rol(byte value)
    {
        var carryIn = IsFlagSet(CarryFlag) ? 1 : 0;
        SetFlagValue(CarryFlag, (value & 0x80) != 0);
        var result = unchecked((byte)((value << 1) | carryIn));
        SetZeroAndNegativeFlags(result);
        return result;
    }

    private byte Ror(byte value)
    {
        var carryIn = IsFlagSet(CarryFlag) ? 0x80 : 0;
        SetFlagValue(CarryFlag, (value & 0x01) != 0);
        var result = (byte)((value >> 1) | carryIn);
        SetZeroAndNegativeFlags(result);
        return result;
    }

    private byte Inc(byte value)
    {
        var result = unchecked((byte)(value + 1));
        SetZeroAndNegativeFlags(result);
        return result;
    }

    private byte Dec(byte value)
    {
        var result = unchecked((byte)(value - 1));
        SetZeroAndNegativeFlags(result);
        return result;
    }

    private int Branch(bool condition)
    {
        var offset = unchecked((sbyte)ReadByte(ProgramCounter++));
        if (!condition) return 2;
        var previous = ProgramCounter;
        ProgramCounter = unchecked((ushort)(ProgramCounter + offset));
        return PageCrossed(previous, ProgramCounter) ? 4 : 3;
    }

    private int JumpAbsolute()
    {
        ProgramCounter = Absolute();
        return 3;
    }

    private int JumpIndirect()
    {
        var pointer = Absolute();
        var low = ReadByte(pointer);
        var highAddress = (ushort)((pointer & 0xFF00) | ((pointer + 1) & 0x00FF));
        ProgramCounter = (ushort)(low | (ReadByte(highAddress) << 8));
        return 5;
    }

    private int JumpToSubroutine()
    {
        var destination = Absolute();
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
        EnterInterrupt(0xFFFE, true);
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

    private int PushStatus()
    {
        Push((byte)(Status | BreakFlag | UnusedFlag));
        return 3;
    }

    private int PullStatus()
    {
        Status = NormalizeStatus(Pop());
        return 4;
    }

    private int PushAccumulator()
    {
        Push(Accumulator);
        return 3;
    }

    private int PullAccumulator()
    {
        Accumulator = Pop();
        SetZeroAndNegativeFlags(Accumulator);
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

    private int Register(Action<byte> destination, byte value)
    {
        destination(value);
        SetZeroAndNegativeFlags(value);
        return 2;
    }

    private int Flag(byte flag, bool enabled)
    {
        SetFlagValue(flag, enabled);
        return 2;
    }

    private void EnterInterrupt(ushort vector, bool breakFlag)
    {
        Push((byte)(ProgramCounter >> 8));
        Push((byte)ProgramCounter);
        Push(breakFlag ? (byte)(Status | BreakFlag | UnusedFlag) : (byte)((Status & ~BreakFlag) | UnusedFlag));
        Status = NormalizeStatus((byte)(Status | InterruptDisableFlag));
        ProgramCounter = ReadWord(vector);
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

    private ushort ReadZeroPageWord(byte address)
    {
        var low = ReadByte(address);
        var high = ReadByte(unchecked((byte)(address + 1)));
        return (ushort)(low | (high << 8));
    }

    private static bool PageCrossed(ushort first, ushort second) => (first & 0xFF00) != (second & 0xFF00);
    private static int Bool(bool value) => value ? 1 : 0;
    private static byte NormalizeStatus(byte status) => (byte)((status | UnusedFlag) & ~BreakFlag);
}

public sealed class UnsupportedCpuOpcodeException(byte opcode, ushort address)
    : NotSupportedException($"CPU opcode ${opcode:X2} at ${address:X4} is not implemented yet.")
{
    public byte Opcode { get; } = opcode;
    public ushort Address { get; } = address;
}
