using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Processors.Mos6502;

/// <summary>
/// Pin-driven NMOS 6502 execution core. All memory traffic is performed through
/// A0-A15, D0-D7, R/W and PHI2; the processor never references a memory object.
/// </summary>
public sealed class Mos6502Processor : VirtualHardwareComponent
{
    private const byte CarryFlag = 1 << 0;
    private const byte ZeroFlag = 1 << 1;
    private const byte InterruptDisableFlag = 1 << 2;
    private const byte DecimalFlag = 1 << 3;
    private const byte BreakFlag = 1 << 4;
    private const byte UnusedFlag = 1 << 5;
    private const byte OverflowFlag = 1 << 6;
    private const byte NegativeFlag = 1 << 7;

    private enum CycleState
    {
        ResetDummyRead1, ResetDummyRead2, ResetStackRead1, ResetStackRead2, ResetStackRead3,
        ResetVectorLow, ResetVectorHigh, FetchOpcode, ReadOperand, ReadAddressLow, ReadAddressHigh,
        ReadZeroPageIndexed, ReadAbsoluteIndexed, ReadIndirectPointerLow, ReadIndirectPointerHigh,
        JumpIndirectLow, JumpIndirectHigh,
        ReadMemory, WriteMemory, ReadModifyWriteDummy, ReadModifyWriteFinal,
        BranchOffset, BranchApply,
        JsrPushHigh, JsrPushLow, RtsDummyRead, RtsPullLow, RtsPullHigh, RtsIncrement,
        RtiDummyRead, RtiPullStatus, RtiPullLow, RtiPullHigh,
        StackPush, StackPullDummy, StackPull,
        InterruptDummyRead, InterruptPushProgramCounterHigh, InterruptPushProgramCounterLow,
        InterruptPushStatus, InterruptVectorLow, InterruptVectorHigh, Halted
    }

    private enum InterruptKind { None, Irq, Nmi }
    private enum AddressingMode { None, Immediate, ZeroPage, ZeroPageX, ZeroPageY, Absolute, AbsoluteX, AbsoluteY, IndexedIndirect, IndirectIndexed, Indirect }
    private enum Operation
    {
        None, Lda, Ldx, Ldy, Sta, Stx, Sty, And, Ora, Eor, Adc, Sbc, Cmp, Cpx, Cpy,
        Bit, Inc, Dec, Asl, Lsr, Rol, Ror, Jmp, Jsr, Pha, Php, Pla, Plp
    }

    private CycleState _state;
    private InterruptKind _activeInterrupt;
    private Operation _operation;
    private AddressingMode _addressingMode;
    private DigitalLevel _previousNmi;
    private byte _lowByte;
    private byte _operand;
    private ushort _effectiveAddress;
    private ushort _pointerAddress;
    private byte _readModifyValue;
    private bool _nmiPending;
    private bool _resetAsserted;
    private readonly ulong _clockInputMask;
    private readonly ulong _resetInputMask;
    private readonly ulong _nmiInputMask;
    private readonly ulong _busEnableInputMask;

    public Mos6502Processor(string componentId) : base(componentId)
    {
        var addressPins = new DigitalPin[16];
        var dataPins = new DigitalPin[8];
        for (var bit = 0; bit < 16; bit++) addressPins[bit] = AddPin($"A{bit}", PinDirection.Output);
        for (var bit = 0; bit < 8; bit++) dataPins[bit] = AddPin($"D{bit}", PinDirection.Bidirectional);
        Address = new DigitalBus($"{componentId}.A", addressPins);
        Data = new DigitalBus($"{componentId}.D", dataPins);
        ReadWrite = AddPin("R/W", PinDirection.Output);
        Sync = AddPin("SYNC", PinDirection.Output);
        Clock = AddPin("PHI2", PinDirection.Input, DigitalInputActivation.RisingEdge);
        ResetBar = AddPin("/RESET", PinDirection.Input);
        IrqBar = AddPin("/IRQ", PinDirection.Input);
        NmiBar = AddPin("/NMI", PinDirection.Input);
        Ready = AddPin("RDY", PinDirection.Input);
        BusEnable = AddPin("BUS_ENABLE", PinDirection.Input);
        _clockInputMask = Clock.InputChangeMask;
        _resetInputMask = ResetBar.InputChangeMask;
        _nmiInputMask = NmiBar.InputChangeMask;
        _busEnableInputMask = BusEnable.InputChangeMask;
    
        InitializePackageState();
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
    /// <summary>Internal package bus grant. Low releases A, D, R/W and SYNC for RP2A03 DMA.</summary>
    public DigitalPin BusEnable { get; }
    public ushort ProgramCounter { get; private set; }
    public byte StackPointer { get; private set; }
    public byte Status { get; private set; }
    public byte Accumulator { get; private set; }
    public byte X { get; private set; }
    public byte Y { get; private set; }
    public byte CurrentOpcode { get; private set; }
    public bool IsHalted => _state == CycleState.Halted;
    public bool InterruptDisable => IsFlagSet(InterruptDisableFlag);
    public bool NmiPending => _nmiPending;
    public ulong RisingEdgeCount { get; private set; }
    public ulong CompletedInstructionCount { get; private set; }
    public ulong CompletedInterruptCount { get; private set; }
    public ulong ReadyStallCount { get; private set; }

    private void InitializePackageState()
    {
        ProgramCounter = 0; StackPointer = 0; Status = InterruptDisableFlag | UnusedFlag;
        Accumulator = X = Y = CurrentOpcode = 0; _lowByte = _operand = 0; _effectiveAddress = 0;
        _activeInterrupt = InterruptKind.None; _operation = Operation.None; _addressingMode = AddressingMode.None; _nmiPending = false;
        RisingEdgeCount = CompletedInstructionCount = CompletedInterruptCount = ReadyStallCount = 0;
        _previousNmi = DigitalLevel.High;
        BeginResetSequence();
    }

    protected override void OnInputChanges(ulong changedInputMask)
    {
        // The data bus, IRQ and RDY pins remain electrically current, but the
        // 6502 samples them at a PHI2 boundary. Only asynchronous NMI, RESET,
        // bus grant, or an actual rising clock edge can activate package logic.
        var nmiChanged = (changedInputMask & _nmiInputMask) != 0;
        var resetChanged = (changedInputMask & _resetInputMask) != 0;
        var busEnableChanged = (changedInputMask & _busEnableInputMask) != 0;
        var clockRising = (changedInputMask & _clockInputMask) != 0;
        if (!nmiChanged && !resetChanged && !busEnableChanged && !clockRising) return;

        if (nmiChanged) SampleNmiEdge();

        if (BusEnable.SampledLevel == DigitalLevel.Low)
        {
            if (busEnableChanged)
            {
                Address.Release();
                Data.Release();
                ReadWrite.Release();
                Sync.Release();
            }
            return;
        }

        if (ResetBar.SampledLevel == DigitalLevel.Low)
        {
            if (!_resetAsserted) BeginResetSequence();
            _resetAsserted = true;
            return;
        }

        _resetAsserted = false;
        if (!clockRising || Clock.SampledLevel != DigitalLevel.High) return;

        RisingEdgeCount++;
        if (IsReadCycle() && Ready.SampledLevel == DigitalLevel.Low)
        {
            ReadyStallCount++;
            return;
        }
        ExecuteBusCycle();
    }

    private void ExecuteBusCycle()
    {
        switch (_state)
        {
            case CycleState.ResetDummyRead1: _state = CycleState.ResetDummyRead2; BeginRead(ProgramCounter); break;
            case CycleState.ResetDummyRead2: _state = CycleState.ResetStackRead1; BeginRead(StackAddress); break;
            case CycleState.ResetStackRead1: StackPointer--; _state = CycleState.ResetStackRead2; BeginRead(StackAddress); break;
            case CycleState.ResetStackRead2: StackPointer--; _state = CycleState.ResetStackRead3; BeginRead(StackAddress); break;
            case CycleState.ResetStackRead3: StackPointer--; _state = CycleState.ResetVectorLow; BeginRead(0xFFFC); break;
            case CycleState.ResetVectorLow: if (!TrySampleData(out _lowByte)) return; _state = CycleState.ResetVectorHigh; BeginRead(0xFFFD); break;
            case CycleState.ResetVectorHigh:
                if (!TrySampleData(out var resetHigh)) return;
                ProgramCounter = (ushort)(_lowByte | resetHigh << 8); BeginOpcodeFetch(); break;

            case CycleState.FetchOpcode:
                if (TryBeginPendingInterrupt()) break;
                if (!TrySampleData(out var opcode)) return;
                CurrentOpcode = opcode; ProgramCounter++; Sync.Drive(DigitalLevel.Low); DecodeOpcode(opcode); break;

            case CycleState.ReadOperand:
                if (!TrySampleData(out _operand)) return;
                ProgramCounter++;
                switch (_addressingMode)
                {
                    case AddressingMode.Immediate:
                        ExecuteReadOperation(_operand); CompleteInstruction(); break;
                    case AddressingMode.ZeroPage:
                        _effectiveAddress = _operand; BeginEffectiveOperation(); break;
                    case AddressingMode.ZeroPageX:
                    case AddressingMode.ZeroPageY:
                    case AddressingMode.IndexedIndirect:
                        _state = CycleState.ReadZeroPageIndexed; BeginRead(_operand); break;
                    case AddressingMode.IndirectIndexed:
                        _pointerAddress = _operand; _state = CycleState.ReadIndirectPointerLow; BeginRead(_pointerAddress); break;
                    default: throw new InvalidOperationException($"Invalid operand addressing mode {_addressingMode}.");
                }
                break;

            case CycleState.ReadZeroPageIndexed:
                if (_addressingMode == AddressingMode.IndexedIndirect)
                {
                    _pointerAddress = (byte)(_operand + X);
                    _state = CycleState.ReadIndirectPointerLow; BeginRead(_pointerAddress);
                }
                else
                {
                    var index = _addressingMode == AddressingMode.ZeroPageX ? X : Y;
                    _effectiveAddress = (byte)(_operand + index); BeginEffectiveOperation();
                }
                break;

            case CycleState.ReadIndirectPointerLow:
                if (!TrySampleData(out _lowByte)) return;
                _state = CycleState.ReadIndirectPointerHigh; BeginRead((byte)(_pointerAddress + 1)); break;
            case CycleState.ReadIndirectPointerHigh:
                if (!TrySampleData(out var pointerHigh)) return;
                _effectiveAddress = (ushort)(_lowByte | pointerHigh << 8);
                if (_addressingMode == AddressingMode.IndirectIndexed)
                {
                    _state = CycleState.ReadAbsoluteIndexed; BeginRead(_effectiveAddress);
                }
                else BeginEffectiveOperation();
                break;

            case CycleState.ReadAddressLow:
                if (!TrySampleData(out _lowByte)) return;
                ProgramCounter++; _state = CycleState.ReadAddressHigh; BeginRead(ProgramCounter); break;
            case CycleState.ReadAddressHigh:
                if (!TrySampleData(out var high)) return;
                ProgramCounter++; _effectiveAddress = (ushort)(_lowByte | high << 8);
                if (_operation == Operation.Jmp && _addressingMode == AddressingMode.Absolute)
                {
                    ProgramCounter = _effectiveAddress; CompleteInstruction();
                }
                else if (_operation == Operation.Jmp && _addressingMode == AddressingMode.Indirect)
                {
                    _pointerAddress = _effectiveAddress; _state = CycleState.JumpIndirectLow; BeginRead(_pointerAddress);
                }
                else if (_operation == Operation.Jsr)
                {
                    ProgramCounter--;
                    _state = CycleState.JsrPushHigh;
                    BeginWrite(StackAddress, (byte)(ProgramCounter >> 8));
                }
                else if (_addressingMode is AddressingMode.AbsoluteX or AddressingMode.AbsoluteY)
                {
                    _state = CycleState.ReadAbsoluteIndexed; BeginRead(_effectiveAddress);
                }
                else BeginEffectiveOperation();
                break;

            case CycleState.ReadAbsoluteIndexed:
                _effectiveAddress = (ushort)(_effectiveAddress + (_addressingMode is AddressingMode.AbsoluteX ? X : Y));
                BeginEffectiveOperation(); break;

            case CycleState.JumpIndirectLow:
                if (!TrySampleData(out _lowByte)) return;
                _state = CycleState.JumpIndirectHigh;
                BeginRead((ushort)((_pointerAddress & 0xFF00) | (byte)(_pointerAddress + 1)));
                break;
            case CycleState.JumpIndirectHigh:
                if (!TrySampleData(out var indirectHigh)) return;
                ProgramCounter = (ushort)(_lowByte | indirectHigh << 8); CompleteInstruction(); break;
            case CycleState.ReadMemory:
                if (!TrySampleData(out var value)) return;
                if (IsReadModifyWriteOperation(_operation))
                {
                    _readModifyValue = value; _state = CycleState.ReadModifyWriteDummy; BeginWrite(_effectiveAddress, value);
                }
                else { ExecuteReadOperation(value); CompleteInstruction(); }
                break;
            case CycleState.WriteMemory: CompleteInstruction(); break;
            case CycleState.ReadModifyWriteDummy:
                _readModifyValue = ApplyReadModifyWrite(_readModifyValue);
                _state = CycleState.ReadModifyWriteFinal; BeginWrite(_effectiveAddress, _readModifyValue); break;
            case CycleState.ReadModifyWriteFinal: CompleteInstruction(); break;

            case CycleState.BranchOffset:
                if (!TrySampleData(out var offset)) return;
                ProgramCounter++; _operand = offset;
                if (!BranchCondition(CurrentOpcode)) { CompleteInstruction(); break; }
                _state = CycleState.BranchApply; BeginRead(ProgramCounter); break;
            case CycleState.BranchApply:
                ProgramCounter = (ushort)(ProgramCounter + (sbyte)_operand); CompleteInstruction(); break;

            case CycleState.JsrPushHigh:
                StackPointer--; _state = CycleState.JsrPushLow; BeginWrite(StackAddress, (byte)ProgramCounter); break;
            case CycleState.JsrPushLow:
                StackPointer--; ProgramCounter = _effectiveAddress; CompleteInstruction(); break;

            case CycleState.RtsDummyRead:
                StackPointer++; _state = CycleState.RtsPullLow; BeginRead(StackAddress); break;
            case CycleState.RtsPullLow:
                if (!TrySampleData(out _lowByte)) return;
                StackPointer++; _state = CycleState.RtsPullHigh; BeginRead(StackAddress); break;
            case CycleState.RtsPullHigh:
                if (!TrySampleData(out var returnHigh)) return;
                ProgramCounter = (ushort)(_lowByte | returnHigh << 8); _state = CycleState.RtsIncrement; BeginRead(ProgramCounter); break;
            case CycleState.RtsIncrement:
                ProgramCounter++; CompleteInstruction(); break;

            case CycleState.RtiDummyRead:
                StackPointer++; _state = CycleState.RtiPullStatus; BeginRead(StackAddress); break;
            case CycleState.RtiPullStatus:
                if (!TrySampleData(out var interruptStatus)) return;
                Status = (byte)((interruptStatus & ~BreakFlag) | UnusedFlag);
                StackPointer++; _state = CycleState.RtiPullLow; BeginRead(StackAddress); break;
            case CycleState.RtiPullLow:
                if (!TrySampleData(out _lowByte)) return;
                StackPointer++; _state = CycleState.RtiPullHigh; BeginRead(StackAddress); break;
            case CycleState.RtiPullHigh:
                if (!TrySampleData(out var interruptHigh)) return;
                ProgramCounter = (ushort)(_lowByte | interruptHigh << 8); CompleteInstruction(); break;

            case CycleState.StackPush:
                StackPointer--; CompleteInstruction(); break;
            case CycleState.StackPullDummy:
                StackPointer++; _state = CycleState.StackPull; BeginRead(StackAddress); break;
            case CycleState.StackPull:
                if (!TrySampleData(out var pulled)) return;
                if (_operation == Operation.Pla) { Accumulator = pulled; SetZeroAndNegativeFlags(pulled); }
                else Status = (byte)((pulled & ~BreakFlag) | UnusedFlag);
                CompleteInstruction(); break;

            case CycleState.InterruptDummyRead:
                _state = CycleState.InterruptPushProgramCounterHigh; BeginWrite(StackAddress, (byte)(ProgramCounter >> 8)); break;
            case CycleState.InterruptPushProgramCounterHigh:
                StackPointer--; _state = CycleState.InterruptPushProgramCounterLow; BeginWrite(StackAddress, (byte)ProgramCounter); break;
            case CycleState.InterruptPushProgramCounterLow:
                StackPointer--; _state = CycleState.InterruptPushStatus; BeginWrite(StackAddress, StatusForHardwareInterrupt); break;
            case CycleState.InterruptPushStatus:
                StackPointer--; _state = CycleState.InterruptVectorLow; BeginRead(VectorAddress); break;
            case CycleState.InterruptVectorLow:
                if (!TrySampleData(out _lowByte)) return;
                _state = CycleState.InterruptVectorHigh; BeginRead((ushort)(VectorAddress + 1)); break;
            case CycleState.InterruptVectorHigh:
                if (!TrySampleData(out var vectorHigh)) return;
                ProgramCounter = (ushort)(_lowByte | vectorHigh << 8); _activeInterrupt = InterruptKind.None;
                CompletedInterruptCount++; BeginOpcodeFetch(); break;
            case CycleState.Halted: break;
        }
    }

    private void DecodeOpcode(byte opcode)
    {
        switch (opcode)
        {
            case 0xEA: CompleteInstruction(); return;
            case 0x00: CompletedInstructionCount++; _state = CycleState.Halted; Data.Release(); Sync.Drive(DigitalLevel.Low); return;

            case 0xA9: BeginImmediate(Operation.Lda); return; case 0xA2: BeginImmediate(Operation.Ldx); return; case 0xA0: BeginImmediate(Operation.Ldy); return;
            case 0x29: BeginImmediate(Operation.And); return; case 0x09: BeginImmediate(Operation.Ora); return; case 0x49: BeginImmediate(Operation.Eor); return;
            case 0x69: BeginImmediate(Operation.Adc); return; case 0xE9: BeginImmediate(Operation.Sbc); return;
            case 0xC9: BeginImmediate(Operation.Cmp); return; case 0xE0: BeginImmediate(Operation.Cpx); return; case 0xC0: BeginImmediate(Operation.Cpy); return;

            case 0xA5: BeginAddressed(Operation.Lda, AddressingMode.ZeroPage); return; case 0xB5: BeginAddressed(Operation.Lda, AddressingMode.ZeroPageX); return;
            case 0xAD: BeginAddressed(Operation.Lda, AddressingMode.Absolute); return; case 0xBD: BeginAddressed(Operation.Lda, AddressingMode.AbsoluteX); return;
            case 0xB9: BeginAddressed(Operation.Lda, AddressingMode.AbsoluteY); return; case 0xA1: BeginAddressed(Operation.Lda, AddressingMode.IndexedIndirect); return;
            case 0xB1: BeginAddressed(Operation.Lda, AddressingMode.IndirectIndexed); return;
            case 0xA6: BeginAddressed(Operation.Ldx, AddressingMode.ZeroPage); return; case 0xB6: BeginAddressed(Operation.Ldx, AddressingMode.ZeroPageY); return;
            case 0xAE: BeginAddressed(Operation.Ldx, AddressingMode.Absolute); return; case 0xBE: BeginAddressed(Operation.Ldx, AddressingMode.AbsoluteY); return;
            case 0xA4: BeginAddressed(Operation.Ldy, AddressingMode.ZeroPage); return; case 0xB4: BeginAddressed(Operation.Ldy, AddressingMode.ZeroPageX); return;
            case 0xAC: BeginAddressed(Operation.Ldy, AddressingMode.Absolute); return; case 0xBC: BeginAddressed(Operation.Ldy, AddressingMode.AbsoluteX); return;

            case 0x85: BeginAddressed(Operation.Sta, AddressingMode.ZeroPage); return; case 0x95: BeginAddressed(Operation.Sta, AddressingMode.ZeroPageX); return;
            case 0x8D: BeginAddressed(Operation.Sta, AddressingMode.Absolute); return; case 0x9D: BeginAddressed(Operation.Sta, AddressingMode.AbsoluteX); return;
            case 0x99: BeginAddressed(Operation.Sta, AddressingMode.AbsoluteY); return; case 0x81: BeginAddressed(Operation.Sta, AddressingMode.IndexedIndirect); return;
            case 0x91: BeginAddressed(Operation.Sta, AddressingMode.IndirectIndexed); return;
            case 0x86: BeginAddressed(Operation.Stx, AddressingMode.ZeroPage); return; case 0x96: BeginAddressed(Operation.Stx, AddressingMode.ZeroPageY); return;
            case 0x8E: BeginAddressed(Operation.Stx, AddressingMode.Absolute); return;
            case 0x84: BeginAddressed(Operation.Sty, AddressingMode.ZeroPage); return; case 0x94: BeginAddressed(Operation.Sty, AddressingMode.ZeroPageX); return;
            case 0x8C: BeginAddressed(Operation.Sty, AddressingMode.Absolute); return;

            case 0x25: BeginAddressed(Operation.And, AddressingMode.ZeroPage); return; case 0x35: BeginAddressed(Operation.And, AddressingMode.ZeroPageX); return;
            case 0x2D: BeginAddressed(Operation.And, AddressingMode.Absolute); return; case 0x3D: BeginAddressed(Operation.And, AddressingMode.AbsoluteX); return;
            case 0x39: BeginAddressed(Operation.And, AddressingMode.AbsoluteY); return; case 0x21: BeginAddressed(Operation.And, AddressingMode.IndexedIndirect); return; case 0x31: BeginAddressed(Operation.And, AddressingMode.IndirectIndexed); return;
            case 0x05: BeginAddressed(Operation.Ora, AddressingMode.ZeroPage); return; case 0x15: BeginAddressed(Operation.Ora, AddressingMode.ZeroPageX); return;
            case 0x0D: BeginAddressed(Operation.Ora, AddressingMode.Absolute); return; case 0x1D: BeginAddressed(Operation.Ora, AddressingMode.AbsoluteX); return;
            case 0x19: BeginAddressed(Operation.Ora, AddressingMode.AbsoluteY); return; case 0x01: BeginAddressed(Operation.Ora, AddressingMode.IndexedIndirect); return; case 0x11: BeginAddressed(Operation.Ora, AddressingMode.IndirectIndexed); return;
            case 0x45: BeginAddressed(Operation.Eor, AddressingMode.ZeroPage); return; case 0x55: BeginAddressed(Operation.Eor, AddressingMode.ZeroPageX); return;
            case 0x4D: BeginAddressed(Operation.Eor, AddressingMode.Absolute); return; case 0x5D: BeginAddressed(Operation.Eor, AddressingMode.AbsoluteX); return;
            case 0x59: BeginAddressed(Operation.Eor, AddressingMode.AbsoluteY); return; case 0x41: BeginAddressed(Operation.Eor, AddressingMode.IndexedIndirect); return; case 0x51: BeginAddressed(Operation.Eor, AddressingMode.IndirectIndexed); return;
            case 0x65: BeginAddressed(Operation.Adc, AddressingMode.ZeroPage); return; case 0x75: BeginAddressed(Operation.Adc, AddressingMode.ZeroPageX); return;
            case 0x6D: BeginAddressed(Operation.Adc, AddressingMode.Absolute); return; case 0x7D: BeginAddressed(Operation.Adc, AddressingMode.AbsoluteX); return;
            case 0x79: BeginAddressed(Operation.Adc, AddressingMode.AbsoluteY); return; case 0x61: BeginAddressed(Operation.Adc, AddressingMode.IndexedIndirect); return; case 0x71: BeginAddressed(Operation.Adc, AddressingMode.IndirectIndexed); return;
            case 0xE5: BeginAddressed(Operation.Sbc, AddressingMode.ZeroPage); return; case 0xF5: BeginAddressed(Operation.Sbc, AddressingMode.ZeroPageX); return;
            case 0xED: BeginAddressed(Operation.Sbc, AddressingMode.Absolute); return; case 0xFD: BeginAddressed(Operation.Sbc, AddressingMode.AbsoluteX); return;
            case 0xF9: BeginAddressed(Operation.Sbc, AddressingMode.AbsoluteY); return; case 0xE1: BeginAddressed(Operation.Sbc, AddressingMode.IndexedIndirect); return; case 0xF1: BeginAddressed(Operation.Sbc, AddressingMode.IndirectIndexed); return;
            case 0xC5: BeginAddressed(Operation.Cmp, AddressingMode.ZeroPage); return; case 0xD5: BeginAddressed(Operation.Cmp, AddressingMode.ZeroPageX); return;
            case 0xCD: BeginAddressed(Operation.Cmp, AddressingMode.Absolute); return; case 0xDD: BeginAddressed(Operation.Cmp, AddressingMode.AbsoluteX); return;
            case 0xD9: BeginAddressed(Operation.Cmp, AddressingMode.AbsoluteY); return; case 0xC1: BeginAddressed(Operation.Cmp, AddressingMode.IndexedIndirect); return; case 0xD1: BeginAddressed(Operation.Cmp, AddressingMode.IndirectIndexed); return;
            case 0xE4: BeginAddressed(Operation.Cpx, AddressingMode.ZeroPage); return; case 0xEC: BeginAddressed(Operation.Cpx, AddressingMode.Absolute); return;
            case 0xC4: BeginAddressed(Operation.Cpy, AddressingMode.ZeroPage); return; case 0xCC: BeginAddressed(Operation.Cpy, AddressingMode.Absolute); return;
            case 0x24: BeginAddressed(Operation.Bit, AddressingMode.ZeroPage); return; case 0x2C: BeginAddressed(Operation.Bit, AddressingMode.Absolute); return;

            case 0xE6: BeginAddressed(Operation.Inc, AddressingMode.ZeroPage); return; case 0xF6: BeginAddressed(Operation.Inc, AddressingMode.ZeroPageX); return;
            case 0xEE: BeginAddressed(Operation.Inc, AddressingMode.Absolute); return; case 0xFE: BeginAddressed(Operation.Inc, AddressingMode.AbsoluteX); return;
            case 0xC6: BeginAddressed(Operation.Dec, AddressingMode.ZeroPage); return; case 0xD6: BeginAddressed(Operation.Dec, AddressingMode.ZeroPageX); return;
            case 0xCE: BeginAddressed(Operation.Dec, AddressingMode.Absolute); return; case 0xDE: BeginAddressed(Operation.Dec, AddressingMode.AbsoluteX); return;
            case 0x06: BeginAddressed(Operation.Asl, AddressingMode.ZeroPage); return; case 0x16: BeginAddressed(Operation.Asl, AddressingMode.ZeroPageX); return;
            case 0x0E: BeginAddressed(Operation.Asl, AddressingMode.Absolute); return; case 0x1E: BeginAddressed(Operation.Asl, AddressingMode.AbsoluteX); return;
            case 0x46: BeginAddressed(Operation.Lsr, AddressingMode.ZeroPage); return; case 0x56: BeginAddressed(Operation.Lsr, AddressingMode.ZeroPageX); return;
            case 0x4E: BeginAddressed(Operation.Lsr, AddressingMode.Absolute); return; case 0x5E: BeginAddressed(Operation.Lsr, AddressingMode.AbsoluteX); return;
            case 0x26: BeginAddressed(Operation.Rol, AddressingMode.ZeroPage); return; case 0x36: BeginAddressed(Operation.Rol, AddressingMode.ZeroPageX); return;
            case 0x2E: BeginAddressed(Operation.Rol, AddressingMode.Absolute); return; case 0x3E: BeginAddressed(Operation.Rol, AddressingMode.AbsoluteX); return;
            case 0x66: BeginAddressed(Operation.Ror, AddressingMode.ZeroPage); return; case 0x76: BeginAddressed(Operation.Ror, AddressingMode.ZeroPageX); return;
            case 0x6E: BeginAddressed(Operation.Ror, AddressingMode.Absolute); return; case 0x7E: BeginAddressed(Operation.Ror, AddressingMode.AbsoluteX); return;
            case 0x0A: ApplyAccumulatorShift(Operation.Asl); return; case 0x4A: ApplyAccumulatorShift(Operation.Lsr); return;
            case 0x2A: ApplyAccumulatorShift(Operation.Rol); return; case 0x6A: ApplyAccumulatorShift(Operation.Ror); return;

            case 0x4C: BeginAddressed(Operation.Jmp, AddressingMode.Absolute); return;
            case 0x6C: BeginAddressed(Operation.Jmp, AddressingMode.Indirect); return;
            case 0x20: BeginAddressed(Operation.Jsr, AddressingMode.Absolute); return;
            case 0x60: _state = CycleState.RtsDummyRead; BeginRead(ProgramCounter); return;
            case 0x40: _state = CycleState.RtiDummyRead; BeginRead(ProgramCounter); return;
            case 0x48: BeginStackPush(Operation.Pha, Accumulator); return; case 0x08: BeginStackPush(Operation.Php, (byte)(Status | BreakFlag | UnusedFlag)); return;
            case 0x68: BeginStackPull(Operation.Pla); return; case 0x28: BeginStackPull(Operation.Plp); return;
            case 0x10: case 0x30: case 0x50: case 0x70: case 0x90: case 0xB0: case 0xD0: case 0xF0:
                _state = CycleState.BranchOffset; BeginRead(ProgramCounter); return;
            case 0xAA: X = Accumulator; SetZeroAndNegativeFlags(X); CompleteInstruction(); return;
            case 0xA8: Y = Accumulator; SetZeroAndNegativeFlags(Y); CompleteInstruction(); return;
            case 0x8A: Accumulator = X; SetZeroAndNegativeFlags(Accumulator); CompleteInstruction(); return;
            case 0x98: Accumulator = Y; SetZeroAndNegativeFlags(Accumulator); CompleteInstruction(); return;
            case 0xBA: X = StackPointer; SetZeroAndNegativeFlags(X); CompleteInstruction(); return;
            case 0x9A: StackPointer = X; CompleteInstruction(); return;
            case 0xE8: X++; SetZeroAndNegativeFlags(X); CompleteInstruction(); return;
            case 0xC8: Y++; SetZeroAndNegativeFlags(Y); CompleteInstruction(); return;
            case 0xCA: X--; SetZeroAndNegativeFlags(X); CompleteInstruction(); return;
            case 0x88: Y--; SetZeroAndNegativeFlags(Y); CompleteInstruction(); return;
            case 0x18: SetFlag(CarryFlag, false); CompleteInstruction(); return; case 0x38: SetFlag(CarryFlag, true); CompleteInstruction(); return;
            case 0x58: SetFlag(InterruptDisableFlag, false); CompleteInstruction(); return; case 0x78: SetFlag(InterruptDisableFlag, true); CompleteInstruction(); return;
            case 0xD8: SetFlag(DecimalFlag, false); CompleteInstruction(); return; case 0xF8: SetFlag(DecimalFlag, true); CompleteInstruction(); return;
            case 0xB8: SetFlag(OverflowFlag, false); CompleteInstruction(); return;
            default: throw new InvalidOperationException($"MOS6502 encountered unsupported opcode 0x{opcode:X2} at 0x{(ushort)(ProgramCounter - 1):X4}.");
        }
    }

    private void BeginImmediate(Operation operation) { _operation = operation; _addressingMode = AddressingMode.Immediate; _state = CycleState.ReadOperand; BeginRead(ProgramCounter); }
    private void BeginAddressed(Operation operation, AddressingMode mode)
    {
        _operation = operation; _addressingMode = mode;
        _state = mode is AddressingMode.Absolute or AddressingMode.AbsoluteX or AddressingMode.AbsoluteY or AddressingMode.Indirect
            ? CycleState.ReadAddressLow : CycleState.ReadOperand;
        BeginRead(ProgramCounter);
    }

    private void BeginEffectiveOperation()
    {
        if (IsStoreOperation(_operation)) { _state = CycleState.WriteMemory; BeginWrite(_effectiveAddress, StoreValue); }
        else { _state = CycleState.ReadMemory; BeginRead(_effectiveAddress); }
    }

    private void ExecuteReadOperation(byte value)
    {
        switch (_operation)
        {
            case Operation.Lda: Accumulator = value; SetZeroAndNegativeFlags(value); break;
            case Operation.Ldx: X = value; SetZeroAndNegativeFlags(value); break;
            case Operation.Ldy: Y = value; SetZeroAndNegativeFlags(value); break;
            case Operation.And: Accumulator &= value; SetZeroAndNegativeFlags(Accumulator); break;
            case Operation.Ora: Accumulator |= value; SetZeroAndNegativeFlags(Accumulator); break;
            case Operation.Eor: Accumulator ^= value; SetZeroAndNegativeFlags(Accumulator); break;
            case Operation.Adc: AddWithCarry(value); break;
            case Operation.Sbc: AddWithCarry((byte)~value); break;
            case Operation.Cmp: Compare(Accumulator, value); break;
            case Operation.Cpx: Compare(X, value); break;
            case Operation.Cpy: Compare(Y, value); break;
            case Operation.Bit:
                SetFlag(ZeroFlag, (Accumulator & value) == 0);
                SetFlag(OverflowFlag, (value & OverflowFlag) != 0);
                SetFlag(NegativeFlag, (value & NegativeFlag) != 0);
                break;
        }
    }

    private bool IsStoreOperation(Operation op) => op is Operation.Sta or Operation.Stx or Operation.Sty;
    private bool IsReadModifyWriteOperation(Operation op) => op is Operation.Inc or Operation.Dec or Operation.Asl or Operation.Lsr or Operation.Rol or Operation.Ror;
    private byte StoreValue => _operation switch { Operation.Sta => Accumulator, Operation.Stx => X, Operation.Sty => Y, _ => 0 };

    private byte ApplyReadModifyWrite(byte value)
    {
        byte result = _operation switch
        {
            Operation.Inc => (byte)(value + 1),
            Operation.Dec => (byte)(value - 1),
            Operation.Asl => ShiftLeft(value),
            Operation.Lsr => ShiftRight(value),
            Operation.Rol => RotateLeft(value),
            Operation.Ror => RotateRight(value),
            _ => throw new InvalidOperationException($"Operation {_operation} is not read-modify-write.")
        };
        if (_operation is Operation.Inc or Operation.Dec) SetZeroAndNegativeFlags(result);
        return result;
    }

    private void ApplyAccumulatorShift(Operation operation)
    {
        _operation = operation;
        Accumulator = ApplyReadModifyWrite(Accumulator);
        CompleteInstruction();
    }

    private byte ShiftLeft(byte value)
    {
        SetFlag(CarryFlag, (value & 0x80) != 0);
        var result = (byte)(value << 1); SetZeroAndNegativeFlags(result); return result;
    }

    private byte ShiftRight(byte value)
    {
        SetFlag(CarryFlag, (value & 0x01) != 0);
        var result = (byte)(value >> 1); SetZeroAndNegativeFlags(result); return result;
    }

    private byte RotateLeft(byte value)
    {
        var carryIn = IsFlagSet(CarryFlag) ? 1 : 0;
        SetFlag(CarryFlag, (value & 0x80) != 0);
        var result = (byte)((value << 1) | carryIn); SetZeroAndNegativeFlags(result); return result;
    }

    private byte RotateRight(byte value)
    {
        var carryIn = IsFlagSet(CarryFlag) ? 0x80 : 0;
        SetFlag(CarryFlag, (value & 0x01) != 0);
        var result = (byte)((value >> 1) | carryIn); SetZeroAndNegativeFlags(result); return result;
    }

    private void AddWithCarry(byte value)
    {
        var carry = IsFlagSet(CarryFlag) ? 1 : 0;
        var sum = Accumulator + value + carry;
        var result = (byte)sum;
        SetFlag(CarryFlag, sum > 0xFF);
        SetFlag(OverflowFlag, ((Accumulator ^ result) & (value ^ result) & 0x80) != 0);
        Accumulator = result; SetZeroAndNegativeFlags(result);
    }

    private void Compare(byte register, byte value)
    {
        var result = (byte)(register - value);
        SetFlag(CarryFlag, register >= value); SetZeroAndNegativeFlags(result);
    }

    private bool BranchCondition(byte opcode) => opcode switch
    {
        0x10 => !IsFlagSet(NegativeFlag), 0x30 => IsFlagSet(NegativeFlag),
        0x50 => !IsFlagSet(OverflowFlag), 0x70 => IsFlagSet(OverflowFlag),
        0x90 => !IsFlagSet(CarryFlag), 0xB0 => IsFlagSet(CarryFlag),
        0xD0 => !IsFlagSet(ZeroFlag), 0xF0 => IsFlagSet(ZeroFlag), _ => false
    };

    private void BeginStackPush(Operation operation, byte value) { _operation = operation; _state = CycleState.StackPush; BeginWrite(StackAddress, value); }
    private void BeginStackPull(Operation operation) { _operation = operation; _state = CycleState.StackPullDummy; BeginRead(ProgramCounter); }
    private void CompleteInstruction() { CompletedInstructionCount++; BeginOpcodeFetch(); }

    private void SampleNmiEdge()
    {
        var current = NmiBar.SampledLevel;
        if (_previousNmi == DigitalLevel.High && current == DigitalLevel.Low) _nmiPending = true;
        if (current is DigitalLevel.High or DigitalLevel.Low) _previousNmi = current;
    }

    private void BeginResetSequence()
    {
        _state = CycleState.ResetDummyRead1; _activeInterrupt = InterruptKind.None; _operation = Operation.None; _addressingMode = AddressingMode.None;
        _nmiPending = false; StackPointer = 0; Status = InterruptDisableFlag | UnusedFlag; BeginRead(ProgramCounter);
    }

    private bool TryBeginPendingInterrupt()
    {
        if (_nmiPending) { _nmiPending = false; BeginInterrupt(InterruptKind.Nmi); return true; }
        if (IrqBar.SampledLevel == DigitalLevel.Low && !InterruptDisable) { BeginInterrupt(InterruptKind.Irq); return true; }
        return false;
    }

    private void BeginInterrupt(InterruptKind kind)
    {
        _activeInterrupt = kind; SetFlag(InterruptDisableFlag, true); _state = CycleState.InterruptDummyRead; BeginRead(ProgramCounter);
    }

    private void BeginOpcodeFetch() { _operation = Operation.None; _addressingMode = AddressingMode.None; _state = CycleState.FetchOpcode; BeginRead(ProgramCounter, true); }
    private void BeginRead(ushort address, bool sync = false) { Data.Release(); ReadWrite.Drive(DigitalLevel.High); Sync.Drive(sync ? DigitalLevel.High : DigitalLevel.Low); Address.Drive(address); }
    private void BeginWrite(ushort address, byte value) { Sync.Drive(DigitalLevel.Low); Address.Drive(address); Data.Drive(value); ReadWrite.Drive(DigitalLevel.Low); }
    private bool IsReadCycle() => ReadWrite.DriveLevel != DigitalLevel.Low;
    private ushort StackAddress => (ushort)(0x0100 | StackPointer);
    private ushort VectorAddress => _activeInterrupt == InterruptKind.Nmi ? (ushort)0xFFFA : (ushort)0xFFFE;
    private byte StatusForHardwareInterrupt => (byte)((Status | UnusedFlag) & ~BreakFlag);
    private bool IsFlagSet(byte flag) => (Status & flag) != 0;
    private void SetFlag(byte flag, bool set) { Status = set ? (byte)(Status | flag) : (byte)(Status & ~flag); Status |= UnusedFlag; }
    private void SetZeroAndNegativeFlags(byte value) { SetFlag(ZeroFlag, value == 0); SetFlag(NegativeFlag, (value & 0x80) != 0); }
    private bool TrySampleData(out byte value) { if (Data.TrySample(out var raw)) { value = (byte)raw; return true; } value = 0; return false; }
}
