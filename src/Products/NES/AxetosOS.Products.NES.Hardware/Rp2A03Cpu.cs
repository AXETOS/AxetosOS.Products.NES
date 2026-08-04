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
    private readonly Rp2A03SignalLines _signals;
    private int _cyclesRemaining;
    private bool _nmiPending;
    private bool _previousNmiAsserted;
    private bool _resetServicedWhileAsserted;
    private int _stallCycles;
    private readonly ScheduledCpuAction[] _scheduledActions = new ScheduledCpuAction[8];
    private int _scheduledActionCount;
    private byte _rmwOriginal;
    private byte _rmwModified;
    private bool _interruptSequenceIsNmi;
    private ushort _interruptVector;
    private byte _interruptVectorLow;

    public Rp2A03Cpu(CpuBus bus, Rp2A03SignalLines? signals = null)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _signals = signals ?? new Rp2A03SignalLines();
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
    public bool IsInstructionBoundary => _cyclesRemaining == 0 && _stallCycles == 0;
    public ulong NmiServiced { get; private set; }
    public ulong IrqServiced { get; private set; }
    public ulong BrkExecuted { get; private set; }
    public ulong RtiExecuted { get; private set; }
    public ulong ReadyStallCycles { get; private set; }
    public Rp2A03SignalLines Signals => _signals;
    public int CyclesRemaining => _cyclesRemaining;
    public int ScheduledMicroOperationCount => _scheduledActionCount;
    public int CompatibilityStallCycles => _stallCycles;
    public bool NmiPending => _nmiPending;
    public bool InterruptSequenceIsNmi => _interruptSequenceIsNmi;
    public ushort InterruptVector => _interruptVector;
    public byte InterruptVectorLow => _interruptVectorLow;

    public event Action<ulong>? StallCycle;

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
        _previousNmiAsserted = _signals.Nmi.IsAsserted;
        _resetServicedWhileAsserted = false;
        _stallCycles = 0;
        _scheduledActionCount = 0;
        _interruptSequenceIsNmi = false;
        _interruptVector = 0;
        _interruptVectorLow = 0;
        NmiServiced = 0;
        IrqServiced = 0;
        BrkExecuted = 0;
        RtiExecuted = 0;
        ReadyStallCycles = 0;
        Reset();
    }

    public void Reset()
    {
        _scheduledActionCount = 0;
        StackPointer = unchecked((byte)(StackPointer - 3));
        Status = NormalizeStatus((byte)(Status | InterruptDisableFlag));
        ProgramCounter = ReadWord(0xFFFC);
        _cyclesRemaining = 7;
    }

    public void RequestNmi() => _nmiPending = true;
    public void SetIrqLine(bool asserted)
    {
        if (asserted) _signals.Irq.Assert();
        else _signals.Irq.Release();
    }
    public void RequestDmaStall(int cycles) => _stallCycles += Math.Max(0, cycles);
    public bool IsFlagSet(byte flag) => (Status & flag) != 0;

    public void Clock()
    {
        TotalCycles++;
        _bus.SetCpuCycle(TotalCycles);
        SampleInputPins();

        if (_signals.Reset.IsAsserted)
        {
            if (!_resetServicedWhileAsserted)
            {
                _resetServicedWhileAsserted = true;
                Reset();
            }
            return;
        }

        _resetServicedWhileAsserted = false;

        // RDY is sampled on every RP2A03 clock. A released logical ready line
        // means an external bus master owns the CPU bus. The CPU retains its
        // current micro-operation while the rest of the console keeps running.
        if (!_signals.Rdy.IsAsserted)
        {
            ReadyStallCycles++;
            StallCycle?.Invoke(TotalCycles);
            return;
        }

        // Compatibility path for callers not yet migrated to explicit RDY bus
        // ownership (currently DMC DMA). This will be removed when DMC/OAM
        // arbitration is consolidated behind the shared bus controller.
        if (_stallCycles > 0)
        {
            _stallCycles--;
            StallCycle?.Invoke(TotalCycles);
            return;
        }

        if (_cyclesRemaining > 0)
        {
            AdvanceScheduledActions();
            _cyclesRemaining--;
            return;
        }

        if (_nmiPending)
        {
            _nmiPending = false;
            NmiServiced++;
            StartHardwareInterrupt(0xFFFA, isNmi: true);
            return;
        }

        if (_signals.Irq.IsAsserted && !IsFlagSet(InterruptDisableFlag))
        {
            IrqServiced++;
            StartHardwareInterrupt(0xFFFE, isNmi: false);
            return;
        }

        LastOpcode = ReadByte(ProgramCounter++);
        InstructionsExecuted++;
        _cyclesRemaining = Execute(LastOpcode) - 1;
    }


    private void SampleInputPins()
    {
        var nmiAsserted = _signals.Nmi.IsAsserted;
        if (nmiAsserted && !_previousNmiAsserted)
        {
            _nmiPending = true;
        }

        _previousNmiAsserted = nmiAsserted;
    }

    private int Execute(byte opcode) => opcode switch
    {
        // BRK / control / flags
        0x00 => Break(), 0x18 => Flag(CarryFlag, false), 0x38 => Flag(CarryFlag, true),
        0x58 => Flag(InterruptDisableFlag, false), 0x78 => Flag(InterruptDisableFlag, true),
        0xB8 => Flag(OverflowFlag, false), 0xD8 => Flag(DecimalFlag, false), 0xF8 => Flag(DecimalFlag, true),
        0xEA => 2,

        // ORA
        0x01 => ReadOp(IndexedIndirect(), CpuDataOperation.Or, 6), 0x05 => ReadOp(ZeroPage(), CpuDataOperation.Or, 3),
        0x09 => ReadOp(Immediate(), CpuDataOperation.Or, 2), 0x0D => ReadOp(Absolute(), CpuDataOperation.Or, 4),
        0x11 => ReadOp(IndirectIndexed(out var p11), CpuDataOperation.Or, 5 + Bool(p11)),
        0x15 => ReadOp(ZeroPageX(), CpuDataOperation.Or, 4), 0x19 => ReadOp(AbsoluteY(out var p19), CpuDataOperation.Or, 4 + Bool(p19)),
        0x1D => ReadOp(AbsoluteX(out var p1D), CpuDataOperation.Or, 4 + Bool(p1D)),

        // AND
        0x21 => ReadOp(IndexedIndirect(), CpuDataOperation.And, 6), 0x25 => ReadOp(ZeroPage(), CpuDataOperation.And, 3),
        0x29 => ReadOp(Immediate(), CpuDataOperation.And, 2), 0x2D => ReadOp(Absolute(), CpuDataOperation.And, 4),
        0x31 => ReadOp(IndirectIndexed(out var p31), CpuDataOperation.And, 5 + Bool(p31)),
        0x35 => ReadOp(ZeroPageX(), CpuDataOperation.And, 4), 0x39 => ReadOp(AbsoluteY(out var p39), CpuDataOperation.And, 4 + Bool(p39)),
        0x3D => ReadOp(AbsoluteX(out var p3D), CpuDataOperation.And, 4 + Bool(p3D)),

        // EOR
        0x41 => ReadOp(IndexedIndirect(), CpuDataOperation.Eor, 6), 0x45 => ReadOp(ZeroPage(), CpuDataOperation.Eor, 3),
        0x49 => ReadOp(Immediate(), CpuDataOperation.Eor, 2), 0x4D => ReadOp(Absolute(), CpuDataOperation.Eor, 4),
        0x51 => ReadOp(IndirectIndexed(out var p51), CpuDataOperation.Eor, 5 + Bool(p51)),
        0x55 => ReadOp(ZeroPageX(), CpuDataOperation.Eor, 4), 0x59 => ReadOp(AbsoluteY(out var p59), CpuDataOperation.Eor, 4 + Bool(p59)),
        0x5D => ReadOp(AbsoluteX(out var p5D), CpuDataOperation.Eor, 4 + Bool(p5D)),

        // ADC
        0x61 => ReadOp(IndexedIndirect(), CpuDataOperation.AddWithCarry, 6), 0x65 => ReadOp(ZeroPage(), CpuDataOperation.AddWithCarry, 3),
        0x69 => ReadOp(Immediate(), CpuDataOperation.AddWithCarry, 2), 0x6D => ReadOp(Absolute(), CpuDataOperation.AddWithCarry, 4),
        0x71 => ReadOp(IndirectIndexed(out var p71), CpuDataOperation.AddWithCarry, 5 + Bool(p71)),
        0x75 => ReadOp(ZeroPageX(), CpuDataOperation.AddWithCarry, 4), 0x79 => ReadOp(AbsoluteY(out var p79), CpuDataOperation.AddWithCarry, 4 + Bool(p79)),
        0x7D => ReadOp(AbsoluteX(out var p7D), CpuDataOperation.AddWithCarry, 4 + Bool(p7D)),

        // SBC
        0xE1 => ReadOp(IndexedIndirect(), CpuDataOperation.SubtractWithCarry, 6), 0xE5 => ReadOp(ZeroPage(), CpuDataOperation.SubtractWithCarry, 3),
        0xE9 => ReadOp(Immediate(), CpuDataOperation.SubtractWithCarry, 2), 0xED => ReadOp(Absolute(), CpuDataOperation.SubtractWithCarry, 4),
        0xF1 => ReadOp(IndirectIndexed(out var pF1), CpuDataOperation.SubtractWithCarry, 5 + Bool(pF1)),
        0xF5 => ReadOp(ZeroPageX(), CpuDataOperation.SubtractWithCarry, 4), 0xF9 => ReadOp(AbsoluteY(out var pF9), CpuDataOperation.SubtractWithCarry, 4 + Bool(pF9)),
        0xFD => ReadOp(AbsoluteX(out var pFD), CpuDataOperation.SubtractWithCarry, 4 + Bool(pFD)),

        // CMP / CPX / CPY
        0xC1 => ReadOp(IndexedIndirect(), CpuDataOperation.CompareAccumulator, 6),
        0xC5 => ReadOp(ZeroPage(), CpuDataOperation.CompareAccumulator, 3),
        0xC9 => ReadOp(Immediate(), CpuDataOperation.CompareAccumulator, 2),
        0xCD => ReadOp(Absolute(), CpuDataOperation.CompareAccumulator, 4),
        0xD1 => ReadOp(IndirectIndexed(out var pD1), CpuDataOperation.CompareAccumulator, 5 + Bool(pD1)),
        0xD5 => ReadOp(ZeroPageX(), CpuDataOperation.CompareAccumulator, 4),
        0xD9 => ReadOp(AbsoluteY(out var pD9), CpuDataOperation.CompareAccumulator, 4 + Bool(pD9)),
        0xDD => ReadOp(AbsoluteX(out var pDD), CpuDataOperation.CompareAccumulator, 4 + Bool(pDD)),
        0xE0 => ReadOp(Immediate(), CpuDataOperation.CompareX, 2), 0xE4 => ReadOp(ZeroPage(), CpuDataOperation.CompareX, 3),
        0xEC => ReadOp(Absolute(), CpuDataOperation.CompareX, 4),
        0xC0 => ReadOp(Immediate(), CpuDataOperation.CompareY, 2), 0xC4 => ReadOp(ZeroPage(), CpuDataOperation.CompareY, 3),
        0xCC => ReadOp(Absolute(), CpuDataOperation.CompareY, 4),

        // LDA
        0xA1 => Load(IndexedIndirect(), CpuLoadTarget.Accumulator, 6), 0xA5 => Load(ZeroPage(), CpuLoadTarget.Accumulator, 3),
        0xA9 => Load(Immediate(), CpuLoadTarget.Accumulator, 2), 0xAD => Load(Absolute(), CpuLoadTarget.Accumulator, 4),
        0xB1 => Load(IndirectIndexed(out var pB1), CpuLoadTarget.Accumulator, 5 + Bool(pB1)),
        0xB5 => Load(ZeroPageX(), CpuLoadTarget.Accumulator, 4), 0xB9 => Load(AbsoluteY(out var pB9), CpuLoadTarget.Accumulator, 4 + Bool(pB9)),
        0xBD => Load(AbsoluteX(out var pBD), CpuLoadTarget.Accumulator, 4 + Bool(pBD)),

        // LDX
        0xA2 => Load(Immediate(), CpuLoadTarget.X, 2), 0xA6 => Load(ZeroPage(), CpuLoadTarget.X, 3),
        0xAE => Load(Absolute(), CpuLoadTarget.X, 4), 0xB6 => Load(ZeroPageY(), CpuLoadTarget.X, 4),
        0xBE => Load(AbsoluteY(out var pBE), CpuLoadTarget.X, 4 + Bool(pBE)),

        // LDY
        0xA0 => Load(Immediate(), CpuLoadTarget.Y, 2), 0xA4 => Load(ZeroPage(), CpuLoadTarget.Y, 3),
        0xAC => Load(Absolute(), CpuLoadTarget.Y, 4), 0xB4 => Load(ZeroPageX(), CpuLoadTarget.Y, 4),
        0xBC => Load(AbsoluteX(out var pBC), CpuLoadTarget.Y, 4 + Bool(pBC)),

        // STA / STX / STY
        0x81 => Store(IndexedIndirect(), Accumulator, 6), 0x85 => Store(ZeroPage(), Accumulator, 3),
        0x8D => Store(Absolute(), Accumulator, 4), 0x91 => Store(IndirectIndexed(out _), Accumulator, 6),
        0x95 => Store(ZeroPageX(), Accumulator, 4), 0x99 => Store(AbsoluteY(out _), Accumulator, 5),
        0x9D => Store(AbsoluteX(out _), Accumulator, 5),
        0x86 => Store(ZeroPage(), X, 3), 0x8E => Store(Absolute(), X, 4), 0x96 => Store(ZeroPageY(), X, 4),
        0x84 => Store(ZeroPage(), Y, 3), 0x8C => Store(Absolute(), Y, 4), 0x94 => Store(ZeroPageX(), Y, 4),

        // BIT
        0x24 => ReadOp(ZeroPage(), CpuDataOperation.Bit, 3), 0x2C => ReadOp(Absolute(), CpuDataOperation.Bit, 4),

        // ASL / LSR / ROL / ROR
        0x0A => AccumulatorShift(Asl), 0x06 => Modify(ZeroPage(), CpuModifyOperation.Asl, 5), 0x0E => Modify(Absolute(), CpuModifyOperation.Asl, 6),
        0x16 => Modify(ZeroPageX(), CpuModifyOperation.Asl, 6), 0x1E => Modify(AbsoluteX(out _), CpuModifyOperation.Asl, 7),
        0x4A => AccumulatorShift(Lsr), 0x46 => Modify(ZeroPage(), CpuModifyOperation.Lsr, 5), 0x4E => Modify(Absolute(), CpuModifyOperation.Lsr, 6),
        0x56 => Modify(ZeroPageX(), CpuModifyOperation.Lsr, 6), 0x5E => Modify(AbsoluteX(out _), CpuModifyOperation.Lsr, 7),
        0x2A => AccumulatorShift(Rol), 0x26 => Modify(ZeroPage(), CpuModifyOperation.Rol, 5), 0x2E => Modify(Absolute(), CpuModifyOperation.Rol, 6),
        0x36 => Modify(ZeroPageX(), CpuModifyOperation.Rol, 6), 0x3E => Modify(AbsoluteX(out _), CpuModifyOperation.Rol, 7),
        0x6A => AccumulatorShift(Ror), 0x66 => Modify(ZeroPage(), CpuModifyOperation.Ror, 5), 0x6E => Modify(Absolute(), CpuModifyOperation.Ror, 6),
        0x76 => Modify(ZeroPageX(), CpuModifyOperation.Ror, 6), 0x7E => Modify(AbsoluteX(out _), CpuModifyOperation.Ror, 7),

        // INC / DEC
        0xE6 => Modify(ZeroPage(), CpuModifyOperation.Inc, 5), 0xEE => Modify(Absolute(), CpuModifyOperation.Inc, 6),
        0xF6 => Modify(ZeroPageX(), CpuModifyOperation.Inc, 6), 0xFE => Modify(AbsoluteX(out _), CpuModifyOperation.Inc, 7),
        0xC6 => Modify(ZeroPage(), CpuModifyOperation.Dec, 5), 0xCE => Modify(Absolute(), CpuModifyOperation.Dec, 6),
        0xD6 => Modify(ZeroPageX(), CpuModifyOperation.Dec, 6), 0xDE => Modify(AbsoluteX(out _), CpuModifyOperation.Dec, 7),

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
        0x88 => Register(CpuLoadTarget.Y, unchecked((byte)(Y - 1))),
        0x98 => Transfer(Y, CpuLoadTarget.Accumulator), 0xA8 => Transfer(Accumulator, CpuLoadTarget.Y),
        0xAA => Transfer(Accumulator, CpuLoadTarget.X), 0x8A => Transfer(X, CpuLoadTarget.Accumulator),
        0x9A => TransferToStackPointer(), 0xBA => Transfer(StackPointer, CpuLoadTarget.X),
        0xC8 => Register(CpuLoadTarget.Y, unchecked((byte)(Y + 1))),
        0xCA => Register(CpuLoadTarget.X, unchecked((byte)(X - 1))),
        0xE8 => Register(CpuLoadTarget.X, unchecked((byte)(X + 1))),

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

    private int ReadOp(ushort address, CpuDataOperation operation, int cycles)
    {
        ScheduleAtInstructionCycle(cycles, ScheduledActionKind.ReadOperation, address, operation: operation);
        return cycles;
    }

    private int Load(ushort address, CpuLoadTarget target, int cycles)
    {
        ScheduleAtInstructionCycle(cycles, ScheduledActionKind.Load, address, loadTarget: target);
        return cycles;
    }

    private int Store(ushort address, byte value, int cycles)
    {
        ScheduleAtInstructionCycle(cycles, ScheduledActionKind.Store, address, value);
        return cycles;
    }

    private int Modify(ushort address, CpuModifyOperation operation, int cycles)
    {
        // NMOS 6502 read-modify-write instructions read the operand, perform a
        // dummy write of the original value, then write the modified value on
        // three distinct CPU cycles. Mapper 1 depends on this exact ordering.
        ScheduleAtInstructionCycle(cycles - 2, ScheduledActionKind.ModifyRead, address, modifyOperation: operation);
        ScheduleAtInstructionCycle(cycles - 1, ScheduledActionKind.ModifyDummyWrite, address);
        ScheduleAtInstructionCycle(cycles, ScheduledActionKind.ModifyFinalWrite, address);
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
        BrkExecuted++;
        _interruptSequenceIsNmi = false;
        _interruptVector = 0xFFFE;

        // BRK is a real two-byte instruction. Cycle 2 fetches the padding byte
        // before the incremented return address is pushed.
        ScheduleAtInstructionCycle(2, ScheduledActionKind.BrkPaddingRead);
        ScheduleAtInstructionCycle(3, ScheduledActionKind.PushProgramCounterHigh);
        ScheduleAtInstructionCycle(4, ScheduledActionKind.PushProgramCounterLow);
        ScheduleAtInstructionCycle(5, ScheduledActionKind.PushBreakStatus);
        ScheduleInterruptVectorFetch();
        return 7;
    }

    private int ReturnFromInterrupt()
    {
        RtiExecuted++;
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

    private int Transfer(byte value, CpuLoadTarget destination)
    {
        WriteLoadTarget(destination, value);
        SetZeroAndNegativeFlags(value);
        return 2;
    }

    private int TransferToStackPointer()
    {
        StackPointer = X;
        return 2;
    }

    private int Register(CpuLoadTarget destination, byte value)
    {
        WriteLoadTarget(destination, value);
        SetZeroAndNegativeFlags(value);
        return 2;
    }

    private void WriteLoadTarget(CpuLoadTarget target, byte value)
    {
        switch (target)
        {
            case CpuLoadTarget.Accumulator:
                Accumulator = value;
                break;
            case CpuLoadTarget.X:
                X = value;
                break;
            case CpuLoadTarget.Y:
                Y = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, "A writable CPU register target is required.");
        }
    }

    private int Flag(byte flag, bool enabled)
    {
        SetFlagValue(flag, enabled);
        return 2;
    }

    private void StartHardwareInterrupt(ushort vector, bool isNmi)
    {
        _scheduledActionCount = 0;
        _interruptSequenceIsNmi = isNmi;
        _interruptVector = vector;

        // Cycle 1 is the discarded opcode fetch at the current PC. The 6502
        // then performs a second dummy read before the three stack writes.
        _ = ReadByte(ProgramCounter);
        ScheduleAtInstructionCycle(2, ScheduledActionKind.DummyReadProgramCounter);
        ScheduleAtInstructionCycle(3, ScheduledActionKind.PushProgramCounterHigh);
        ScheduleAtInstructionCycle(4, ScheduledActionKind.PushProgramCounterLow);
        ScheduleAtInstructionCycle(5, ScheduledActionKind.PushInterruptStatus);
        ScheduleInterruptVectorFetch();
        _cyclesRemaining = 6;
    }

    private void ScheduleInterruptVectorFetch()
    {
        ScheduleAtInstructionCycle(6, ScheduledActionKind.InterruptVectorLow);
        ScheduleAtInstructionCycle(7, ScheduledActionKind.InterruptVectorHigh);
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

    private void ScheduleAtInstructionCycle(
        int cycle,
        ScheduledActionKind kind,
        ushort address = 0,
        byte value = 0,
        CpuDataOperation operation = CpuDataOperation.None,
        CpuLoadTarget loadTarget = CpuLoadTarget.None,
        CpuModifyOperation modifyOperation = CpuModifyOperation.None)
    {
        if (_scheduledActionCount >= _scheduledActions.Length)
            throw new InvalidOperationException("The RP2A03 micro-operation queue overflowed.");

        _scheduledActions[_scheduledActionCount++] = new ScheduledCpuAction(
            cycle - 1, kind, address, value, operation, loadTarget, modifyOperation);
    }

    private void AdvanceScheduledActions()
    {
        var writeIndex = 0;
        for (var index = 0; index < _scheduledActionCount; index++)
        {
            var scheduled = _scheduledActions[index];
            scheduled.CyclesRemaining--;
            if (scheduled.CyclesRemaining > 0)
            {
                _scheduledActions[writeIndex++] = scheduled;
                continue;
            }

            ExecuteScheduledAction(scheduled);
        }

        _scheduledActionCount = writeIndex;
    }

    private void ExecuteScheduledAction(in ScheduledCpuAction action)
    {
        switch (action.Kind)
        {
            case ScheduledActionKind.ReadOperation:
                ApplyDataOperation(action.Operation, ReadByte(action.Address));
                break;
            case ScheduledActionKind.Load:
            {
                var value = ReadByte(action.Address);
                switch (action.LoadTarget)
                {
                    case CpuLoadTarget.Accumulator: Accumulator = value; break;
                    case CpuLoadTarget.X: X = value; break;
                    case CpuLoadTarget.Y: Y = value; break;
                    default: throw new InvalidOperationException("Invalid CPU load target.");
                }
                SetZeroAndNegativeFlags(value);
                break;
            }
            case ScheduledActionKind.Store:
                _bus.Write(action.Address, action.Value);
                break;
            case ScheduledActionKind.ModifyRead:
                _rmwOriginal = ReadByte(action.Address);
                _rmwModified = ApplyModifyOperation(action.ModifyOperation, _rmwOriginal);
                break;
            case ScheduledActionKind.ModifyDummyWrite:
                _bus.Write(action.Address, _rmwOriginal);
                break;
            case ScheduledActionKind.ModifyFinalWrite:
                _bus.Write(action.Address, _rmwModified);
                break;
            case ScheduledActionKind.BrkPaddingRead:
                _ = ReadByte(ProgramCounter);
                ProgramCounter++;
                break;
            case ScheduledActionKind.DummyReadProgramCounter:
                _ = ReadByte(ProgramCounter);
                break;
            case ScheduledActionKind.PushProgramCounterHigh:
                Push((byte)(ProgramCounter >> 8));
                break;
            case ScheduledActionKind.PushProgramCounterLow:
                Push((byte)ProgramCounter);
                break;
            case ScheduledActionKind.PushBreakStatus:
                Push((byte)(Status | BreakFlag | UnusedFlag));
                Status = NormalizeStatus((byte)(Status | InterruptDisableFlag));
                break;
            case ScheduledActionKind.PushInterruptStatus:
                Push((byte)((Status & ~BreakFlag) | UnusedFlag));
                Status = NormalizeStatus((byte)(Status | InterruptDisableFlag));
                break;
            case ScheduledActionKind.InterruptVectorLow:
                if (!_interruptSequenceIsNmi && _nmiPending)
                {
                    _nmiPending = false;
                    _interruptSequenceIsNmi = true;
                    _interruptVector = 0xFFFA;
                    NmiServiced++;
                }
                _interruptVectorLow = ReadByte(_interruptVector);
                break;
            case ScheduledActionKind.InterruptVectorHigh:
            {
                var high = ReadByte(unchecked((ushort)(_interruptVector + 1)));
                ProgramCounter = (ushort)(_interruptVectorLow | (high << 8));
                break;
            }
            default:
                throw new InvalidOperationException("Unknown RP2A03 scheduled micro-operation.");
        }
    }

    private void ApplyDataOperation(CpuDataOperation operation, byte value)
    {
        switch (operation)
        {
            case CpuDataOperation.Or: Or(value); break;
            case CpuDataOperation.And: And(value); break;
            case CpuDataOperation.Eor: Eor(value); break;
            case CpuDataOperation.AddWithCarry: AddWithCarry(value); break;
            case CpuDataOperation.SubtractWithCarry: SubtractWithCarry(value); break;
            case CpuDataOperation.CompareAccumulator: Compare(Accumulator, value); break;
            case CpuDataOperation.CompareX: Compare(X, value); break;
            case CpuDataOperation.CompareY: Compare(Y, value); break;
            case CpuDataOperation.Bit: Bit(value); break;
            default: throw new InvalidOperationException("Unknown RP2A03 data operation.");
        }
    }

    private byte ApplyModifyOperation(CpuModifyOperation operation, byte value) => operation switch
    {
        CpuModifyOperation.Asl => Asl(value),
        CpuModifyOperation.Lsr => Lsr(value),
        CpuModifyOperation.Rol => Rol(value),
        CpuModifyOperation.Ror => Ror(value),
        CpuModifyOperation.Inc => Inc(value),
        CpuModifyOperation.Dec => Dec(value),
        _ => throw new InvalidOperationException("Unknown RP2A03 modify operation.")
    };

    private enum ScheduledActionKind : byte
    {
        ReadOperation, Load, Store, ModifyRead, ModifyDummyWrite, ModifyFinalWrite,
        BrkPaddingRead, DummyReadProgramCounter, PushProgramCounterHigh, PushProgramCounterLow,
        PushBreakStatus, PushInterruptStatus, InterruptVectorLow, InterruptVectorHigh
    }

    private enum CpuDataOperation : byte
    {
        None, Or, And, Eor, AddWithCarry, SubtractWithCarry,
        CompareAccumulator, CompareX, CompareY, Bit
    }

    private enum CpuLoadTarget : byte { None, Accumulator, X, Y }
    private enum CpuModifyOperation : byte { None, Asl, Lsr, Rol, Ror, Inc, Dec }

    private struct ScheduledCpuAction(
        int cyclesRemaining,
        ScheduledActionKind kind,
        ushort address,
        byte value,
        CpuDataOperation operation,
        CpuLoadTarget loadTarget,
        CpuModifyOperation modifyOperation)
    {
        public int CyclesRemaining { get; set; } = cyclesRemaining;
        public ScheduledActionKind Kind { get; } = kind;
        public ushort Address { get; } = address;
        public byte Value { get; } = value;
        public CpuDataOperation Operation { get; } = operation;
        public CpuLoadTarget LoadTarget { get; } = loadTarget;
        public CpuModifyOperation ModifyOperation { get; } = modifyOperation;
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
