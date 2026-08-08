using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;

/// <summary>
/// Standalone Ricoh RP2A03 package under construction. This phase implements the
/// package power pins, master-clock divider, M2 bus clock, external CPU bus and
/// the integrated 6502-derived execution section. It has no motherboard, RAM,
/// cartridge, PPU, or other component references.
/// </summary>
public sealed class Rp2A03 : VirtualHardwareComponent
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
        BranchOffset, BranchApply, BranchPageCross,
        JsrPushHigh, JsrPushLow, RtsDummyRead, RtsPullLow, RtsPullHigh, RtsIncrement,
        RtiDummyRead, RtiPullStatus, RtiPullLow, RtiPullHigh,
        StackPush, StackPullDummy, StackPull,
        BrkPaddingRead, InterruptDummyRead, InterruptPushProgramCounterHigh, InterruptPushProgramCounterLow,
        InterruptPushStatus, InterruptVectorLow, InterruptVectorHigh, Halted
    }

    private enum InterruptKind { None, Irq, Nmi, Brk }
    private enum AddressingMode { None, Immediate, ZeroPage, ZeroPageX, ZeroPageY, Absolute, AbsoluteX, AbsoluteY, IndexedIndirect, IndirectIndexed, Indirect }
    private enum Operation
    {
        None, Lda, Ldx, Ldy, Sta, Stx, Sty, And, Ora, Eor, Adc, Sbc, Cmp, Cpx, Cpy,
        Bit, Inc, Dec, Asl, Lsr, Rol, Ror, Jmp, Jsr, Pha, Php, Pla, Plp,
        Nop, Lax, Sax, Slo, Rla, Sre, Rra, Dcp, Isc, Anc, Alr, Arr, Axs
    }

    private CycleState _state;
    private InterruptKind _activeInterrupt;
    private Operation _operation;
    private AddressingMode _addressingMode;
    private DigitalLevel _m2Level;
    private bool _sync;
    private DigitalLevel _previousNmi;
    private byte _lowByte;
    private byte _operand;
    private ushort _effectiveAddress;
    private ushort _pointerAddress;
    private byte _readModifyValue;
    private bool _nmiPending;
    private ushort _busAddress;
    private byte _busWriteValue;
    private bool _busRead;
    private bool _dmaPending;
    private bool _dmaActive;
    private bool _dmaReadPhase;
    private byte _dmaPage;
    private byte _dmaIndex;
    private byte _dmaLatch;
    private int _dmaDummyCycles;
    private byte _controllerOutputLatch;
    private byte _controllerRead1Latch;
    private byte _controllerRead2Latch;
    private bool _controllerRead1Valid;
    private bool _controllerRead2Valid;

    // Internal APU circuits of the standalone RP2A03 package.
    private readonly PulseChannel _pulse1 = new(onesComplementNegate: true);
    private readonly PulseChannel _pulse2 = new(onesComplementNegate: false);
    private readonly TriangleChannel _triangle = new();
    private readonly NoiseChannel _noise = new();
    private readonly DmcChannel _dmc = new();
    private ulong _apuCpuCycles;
    private int _frameSequenceCycle;
    private bool _frameFiveStepMode;
    private bool _frameIrqInhibit;
    private bool _frameIrqPending;
    private bool _frameCounterWritePending;
    private byte _pendingFrameCounterValue;
    private int _frameCounterWriteDelay;
    private bool _apuTimerPhase;
    private byte _audioDacLevel;
    private byte _lastMixedPulse1;
    private byte _lastMixedPulse2;
    private byte _lastMixedTriangle;
    private byte _lastMixedNoise;
    private byte _lastMixedDmc;
    private bool _dmcFetchActive;
    private bool _dmcFetchDuringOamDma;
    private int _dmcFetchDelayCycles;
    private int _dmcCurrentFetchStallCycles;
    private ushort _dmcSavedBusAddress;
    private byte _dmcSavedBusWriteValue;
    private bool _dmcSavedBusRead;
    private bool _dmcSavedSync;
    private bool _packagePowered;
    private bool _resetAsserted;
    private readonly ulong _powerInputMask;
    private readonly ulong _masterClockInputMask;
    private readonly ulong _nmiInputMask;
    private readonly ulong _controller1InputMask;
    private readonly ulong _controller2InputMask;
    private readonly ulong _controllerInputMask;

    public Rp2A03(string componentId) : base(componentId)
    {
        Vcc = AddPin("VCC", PinDirection.Input);
        Gnd = AddPin("GND", PinDirection.Input);
        MasterClock = AddPin("CLK", PinDirection.Input, DigitalInputActivation.RisingEdge, 6);
        M2 = AddPin("M2", PinDirection.Output);
        ResetBar = AddPin("/RES", PinDirection.Input);
        IrqBar = AddPin("/IRQ", PinDirection.Input);
        NmiBar = AddPin("/NMI", PinDirection.Input);
        ControllerRead1Bar = AddPin("/OE1", PinDirection.Output);
        ControllerRead2Bar = AddPin("/OE2", PinDirection.Output);
        ControllerOut0 = AddPin("OUT0", PinDirection.Output);
        ControllerOut1 = AddPin("OUT1", PinDirection.Output);
        ControllerOut2 = AddPin("OUT2", PinDirection.Output);
        ControllerData1 = AddPin("IN0", PinDirection.Input);
        ControllerData2 = AddPin("IN1", PinDirection.Input);
        AudioOut = AddPin("AUDIO", PinDirection.Output);
        AudioDacOutput = new BufferedOutputPin<RicohAudioDacSample>(
            $"{componentId}.AUDIO_DAC",
            new RicohAudioDacSample(0, 0));

        var addressPins = new DigitalPin[16];
        var dataPins = new DigitalPin[8];
        for (var bit = 0; bit < 16; bit++) addressPins[bit] = AddPin($"A{bit}", PinDirection.Output);
        for (var bit = 0; bit < 8; bit++) dataPins[bit] = AddPin($"D{bit}", PinDirection.Bidirectional);
        Address = new DigitalBus($"{componentId}.A", addressPins);
        Data = new DigitalBus($"{componentId}.D", dataPins);
        ReadWrite = AddPin("R/W", PinDirection.Output);

        _powerInputMask = Vcc.InputChangeMask | Gnd.InputChangeMask;
        _masterClockInputMask = MasterClock.InputChangeMask;
        _nmiInputMask = NmiBar.InputChangeMask;
        _controller1InputMask = ControllerData1.InputChangeMask;
        _controller2InputMask = ControllerData2.InputChangeMask;
        _controllerInputMask = _controller1InputMask | _controller2InputMask;

        // D0-D7, /IRQ and /RES are synchronous CPU inputs in this package model:
        // their physical levels must remain current, but an individual transition
        // cannot do work until the next internal CPU clock boundary. /NMI remains
        // edge-sensitive and therefore deliberately ungated.
        Data.SetOwnerWakeEnabled(false);
        IrqBar.SetOwnerWakeEnabled(false);
        ResetBar.SetOwnerWakeEnabled(false);
        ControllerData1.SetOwnerWakeEnabled(false);
        ControllerData2.SetOwnerWakeEnabled(false);

        InitializePackageState();
    }

    public DigitalPin Vcc { get; }
    public DigitalPin Gnd { get; }
    public DigitalPin MasterClock { get; }
    public DigitalPin M2 { get; }
    public DigitalBus Address { get; }
    public DigitalBus Data { get; }
    public DigitalPin ReadWrite { get; }
    public DigitalPin ResetBar { get; }
    public DigitalPin IrqBar { get; }
    public DigitalPin NmiBar { get; }
    public DigitalPin ControllerRead1Bar { get; }
    public DigitalPin ControllerRead2Bar { get; }
    public DigitalPin ControllerOut0 { get; }
    public DigitalPin ControllerOut1 { get; }
    public DigitalPin ControllerOut2 { get; }
    public DigitalPin ControllerData1 { get; }
    public DigitalPin ControllerData2 { get; }
    public DigitalPin AudioOut { get; }
    public BufferedOutputPin<RicohAudioDacSample> AudioDacOutput { get; }
    public ushort ProgramCounter { get; private set; }
    public byte StackPointer { get; private set; }
    public byte Status { get; private set; }
    public byte Accumulator { get; private set; }
    public byte X { get; private set; }
    public byte Y { get; private set; }
    public byte CurrentOpcode { get; private set; }
    public string CurrentCycleState => _state.ToString();
    public ushort CurrentBusAddress => _busAddress;
    public bool CurrentBusIsRead => _busRead;
    public DigitalLevel CurrentM2Level => M2.DriveLevel;
    public bool TryInspectDataBus(out byte value)
    {
        if (Data.TrySample(out var raw))
        {
            value = (byte)raw;
            return true;
        }

        value = 0;
        return false;
    }
    public bool IsHalted => _state == CycleState.Halted;
    public bool InterruptDisable => IsFlagSet(InterruptDisableFlag);
    public bool NmiPending => _nmiPending;
    public bool SyncState => _sync;
    public ulong MasterClockRisingEdgeCount => MasterClock.InputActivationEdgeCount;
    public ulong RisingEdgeCount { get; private set; }
    public ulong CompletedInstructionCount { get; private set; }
    public ulong CompletedInterruptCount { get; private set; }
    public ulong ReadyStallCount { get; private set; }
    public bool DmaActive => _dmaActive || _dmaPending;
    public ulong DmaTransferCount { get; private set; }
    public byte ControllerOutputLatch => _controllerOutputLatch;
    public ulong ApuCpuCycleCount => _apuCpuCycles;
    public bool FrameIrqPending => _frameIrqPending;
    public bool FrameFiveStepMode => _frameFiveStepMode;
    public bool FrameCounterWritePending => _frameCounterWritePending;
    public int FrameSequenceCycle => _frameSequenceCycle;
    public byte AudioDacLevel => _audioDacLevel;
    public byte Pulse1OutputLevel => _pulse1.Output;
    public byte Pulse2OutputLevel => _pulse2.Output;
    public ushort Pulse1TimerPeriod => _pulse1.TimerPeriod;
    public ushort Pulse2TimerPeriod => _pulse2.TimerPeriod;
    public byte Pulse1LengthCounter => _pulse1.LengthCounter;
    public byte Pulse2LengthCounter => _pulse2.LengthCounter;
    public byte TriangleOutputLevel => _triangle.Output;
    public ushort TriangleTimerPeriod => _triangle.TimerPeriod;
    public byte TriangleLengthCounter => _triangle.LengthCounter;
    public byte TriangleLinearCounter => _triangle.LinearCounter;
    public byte NoiseOutputLevel => _noise.Output;
    public ushort NoiseTimerPeriod => _noise.TimerPeriod;
    public byte NoiseLengthCounter => _noise.LengthCounter;
    public ushort NoiseShiftRegister => _noise.ShiftRegister;
    public byte DmcOutputLevel => _dmc.Output;
    public ushort DmcCurrentAddress => _dmc.CurrentAddress;
    public ushort DmcBytesRemaining => _dmc.BytesRemaining;
    public bool DmcIrqPending => _dmc.IrqPending;
    public ulong DmcMemoryReadCount { get; private set; }
    public ulong DmcOamDmaInterleaveCount { get; private set; }
    public ulong DmcCpuStallCount { get; private set; }
    public int LastDmcFetchStallCycles { get; private set; }

    private void InitializePackageState()
    {
        ProgramCounter = 0; StackPointer = 0; Status = InterruptDisableFlag | UnusedFlag;
        Accumulator = X = Y = CurrentOpcode = 0; _lowByte = _operand = 0; _effectiveAddress = 0;
        _activeInterrupt = InterruptKind.None; _operation = Operation.None; _addressingMode = AddressingMode.None; _nmiPending = false;
        RisingEdgeCount = CompletedInstructionCount = CompletedInterruptCount = ReadyStallCount = 0;
        DmaTransferCount = 0;
        MasterClock.ResetInputActivationCounter();
        _m2Level = DigitalLevel.Low;
        _sync = false;
        _busAddress = 0; _busWriteValue = 0; _busRead = true;
        _dmaPending = _dmaActive = false; _dmaReadPhase = true; _dmaPage = _dmaIndex = _dmaLatch = 0; _dmaDummyCycles = 0;
        _controllerOutputLatch = 0;
        _controllerRead1Latch = _controllerRead2Latch = 0;
        _controllerRead1Valid = _controllerRead2Valid = false;
        _pulse1.Reset();
        _pulse2.Reset();
        _triangle.Reset();
        _noise.Reset();
        _dmc.Reset();
        _apuCpuCycles = 0;
        _frameSequenceCycle = 0;
        _frameFiveStepMode = false;
        _frameIrqInhibit = false;
        _frameIrqPending = false;
        _frameCounterWritePending = false;
        _pendingFrameCounterValue = 0;
        _frameCounterWriteDelay = 0;
        _apuTimerPhase = false;
        _audioDacLevel = 0;
        _lastMixedPulse1 = 0;
        _lastMixedPulse2 = 0;
        _lastMixedTriangle = 0;
        _lastMixedNoise = 0;
        _lastMixedDmc = 0;
        _dmcFetchActive = false;
        _dmcFetchDuringOamDma = false;
        _dmcFetchDelayCycles = 0;
        _dmcCurrentFetchStallCycles = 0;
        _dmcSavedBusAddress = 0;
        _dmcSavedBusWriteValue = 0;
        _dmcSavedBusRead = true;
        _dmcSavedSync = false;
        DmcMemoryReadCount = 0;
        DmcOamDmaInterleaveCount = 0;
        DmcCpuStallCount = 0;
        LastDmcFetchStallCycles = 0;
        _previousNmi = DigitalLevel.High;
        M2.Drive(DigitalLevel.Low);
        ControllerRead1Bar.Drive(DigitalLevel.High);
        ControllerRead2Bar.Drive(DigitalLevel.High);
        RefreshControllerInputWakeState();
        ControllerOut0.Drive(DigitalLevel.Low);
        ControllerOut1.Drive(DigitalLevel.Low);
        ControllerOut2.Drive(DigitalLevel.Low);
        AudioOut.Drive(DigitalLevel.Low);
        AudioDacOutput.Drive(new RicohAudioDacSample(0, 0));
        BeginResetSequence();
    }

    protected override void OnInputChanges(ulong changedInputMask)
    {
        if (changedInputMask == _masterClockInputMask && _packagePowered)
        {
            // Dominant steady-state path: the physical CLK pin has already
            // received/stored every level and its chip-owned divider admitted
            // this exact activation. Avoid re-decoding unrelated asynchronous
            // package masks on every M2 half-cycle.
            _m2Level = _m2Level == DigitalLevel.High ? DigitalLevel.Low : DigitalLevel.High;
            M2.Drive(_m2Level);
            if (_m2Level != DigitalLevel.High) return;

            if (ResetBar.SampledLevel == DigitalLevel.Low)
            {
                if (!_resetAsserted) BeginResetSequence();
                _resetAsserted = true;
                return;
            }

            _resetAsserted = false;
            RisingEdgeCount++;
            if (ControllerReadActive) SampleControllerInputs();
            ClockApuCpuCycle();
            ExecuteBusCycle();
            return;
        }

        // Data/IRQ/reset package pins are always electrically current, but most
        // of them are sampled only at an internal CPU clock boundary.  Avoid
        // entering the CPU/APU core at all for pin traffic that cannot cause an
        // asynchronous action.
        var powerChanged = (changedInputMask & _powerInputMask) != 0;
        if (!_packagePowered && !powerChanged) return;
        var clockChanged = (changedInputMask & _masterClockInputMask) != 0;
        var nmiChanged = (changedInputMask & _nmiInputMask) != 0;
        var controllerChanged = (changedInputMask & _controllerInputMask) != 0;
        if (!powerChanged && !clockChanged && !nmiChanged && !controllerChanged) return;

        if (!_packagePowered || powerChanged)
        {
            if (!IsPowered())
            {
                ReleasePackageOutputs();
                _packagePowered = false;
                _resetAsserted = false;
                RefreshControllerInputWakeState();
                return;
            }

            if (!_packagePowered)
            {
                InitializePackageState();
                _packagePowered = true;
                RefreshControllerInputWakeState();
            }
        }

        if (nmiChanged) SampleNmiEdge();
        if (controllerChanged)
        {
            var selectedControllerChanged =
                ((changedInputMask & _controller1InputMask) != 0 && ControllerRead1Bar.DriveLevel == DigitalLevel.Low)
                || ((changedInputMask & _controller2InputMask) != 0 && ControllerRead2Bar.DriveLevel == DigitalLevel.Low);
            if (selectedControllerChanged) SampleControllerInputs();
        }
        if (!clockChanged) return;

        // The physical CLK pin still receives both Low and High levels, but
        // this package declares rising-edge activation for that input. Reaching
        // this point therefore already means a real Low -> High clock edge.
        if (MasterClock.SampledLevel != DigitalLevel.High) return;

        // The chip-owned CLK input counts every physical master-clock rising
        // edge but wakes the full package only at the internal M2 divider
        // boundary. Reaching this point is therefore one M2 half-cycle.
        _m2Level = _m2Level == DigitalLevel.High ? DigitalLevel.Low : DigitalLevel.High;
        M2.Drive(_m2Level);
        if (_m2Level != DigitalLevel.High) return;

        if (ResetBar.SampledLevel == DigitalLevel.Low)
        {
            if (!_resetAsserted) BeginResetSequence();
            _resetAsserted = true;
            return;
        }

        _resetAsserted = false;
        RisingEdgeCount++;
        // Controller data can legitimately remain at the same electrical level
        // for consecutive selected reads. Sample at the CPU boundary only while
        // one of the package's controller input stages is actually enabled.
        if (ControllerReadActive) SampleControllerInputs();
        ClockApuCpuCycle();
        ExecuteBusCycle();
    }

    protected override void OnInputChangesProfiled(
        ulong changedInputMask,
        VirtualHardwareProfileSample sample)
    {
        // Data/IRQ/reset package pins are always electrically current, but most
        // of them are sampled only at an internal CPU clock boundary.  Avoid
        // entering the CPU/APU core at all for pin traffic that cannot cause an
        // asynchronous action.
        var powerChanged = (changedInputMask & _powerInputMask) != 0;
        if (!_packagePowered && !powerChanged) return;
        var clockChanged = (changedInputMask & _masterClockInputMask) != 0;
        var nmiChanged = (changedInputMask & _nmiInputMask) != 0;
        var controllerChanged = (changedInputMask & _controllerInputMask) != 0;
        if (!powerChanged && !clockChanged && !nmiChanged && !controllerChanged) return;

        if (!_packagePowered || powerChanged)
        {
            if (!IsPowered())
            {
                ReleasePackageOutputs();
                _packagePowered = false;
                _resetAsserted = false;
                RefreshControllerInputWakeState();
                return;
            }

            if (!_packagePowered)
            {
                InitializePackageState();
                _packagePowered = true;
                RefreshControllerInputWakeState();
            }
        }

        if (nmiChanged) SampleNmiEdge();
        if (controllerChanged)
        {
            var selectedControllerChanged =
                ((changedInputMask & _controller1InputMask) != 0 && ControllerRead1Bar.DriveLevel == DigitalLevel.Low)
                || ((changedInputMask & _controller2InputMask) != 0 && ControllerRead2Bar.DriveLevel == DigitalLevel.Low);
            if (selectedControllerChanged)
            {
                var controllerStarted = sample.BeginSection();
                SampleControllerInputs();
                sample.EndSection(VirtualHardwareProfileSection.Rp2A03ControllerIo, controllerStarted);
            }
        }
        if (!clockChanged) return;

        // The physical CLK pin still receives both Low and High levels, but
        // this package declares rising-edge activation for that input. Reaching
        // this point therefore already means a real Low -> High clock edge.
        if (MasterClock.SampledLevel != DigitalLevel.High) return;

        // The chip-owned CLK input counts every physical master-clock rising
        // edge but wakes the full package only at the internal M2 divider
        // boundary. Reaching this point is therefore one M2 half-cycle.
        _m2Level = _m2Level == DigitalLevel.High ? DigitalLevel.Low : DigitalLevel.High;
        M2.Drive(_m2Level);
        if (_m2Level != DigitalLevel.High) return;

        if (ResetBar.SampledLevel == DigitalLevel.Low)
        {
            if (!_resetAsserted) BeginResetSequence();
            _resetAsserted = true;
            return;
        }

        _resetAsserted = false;
        RisingEdgeCount++;
        // Controller data can legitimately remain at the same electrical level
        // for consecutive selected reads. Do not enter/timestamp controller IO
        // on ordinary CPU cycles while both package read-enable outputs are High.
        if (ControllerReadActive)
        {
            var controllerStartedAtCpuBoundary = sample.BeginSection();
            SampleControllerInputs();
            sample.EndSection(VirtualHardwareProfileSection.Rp2A03ControllerIo, controllerStartedAtCpuBoundary);
        }

        var apuStarted = sample.BeginSection();
        ClockApuCpuCycle();
        sample.EndSection(VirtualHardwareProfileSection.Rp2A03Apu, apuStarted);

        var dmaPath = _dmcFetchActive || _dmaPending || _dmaActive || (_dmc.NeedsSample && _busRead);
        var busStarted = sample.BeginSection();
        ExecuteBusCycle();
        sample.EndSection(
            dmaPath ? VirtualHardwareProfileSection.Rp2A03Dma : VirtualHardwareProfileSection.Rp2A03CpuCore,
            busStarted);
    }

    private bool ControllerReadActive =>
        ControllerRead1Bar.DriveLevel == DigitalLevel.Low
        || ControllerRead2Bar.DriveLevel == DigitalLevel.Low;

    private void RefreshControllerInputWakeState()
    {
        ControllerData1.SetOwnerWakeEnabled(
            _packagePowered && ControllerRead1Bar.DriveLevel == DigitalLevel.Low);
        ControllerData2.SetOwnerWakeEnabled(
            _packagePowered && ControllerRead2Bar.DriveLevel == DigitalLevel.Low);
    }

    private bool IsPowered() =>
        Vcc.SampledLevel == DigitalLevel.High && Gnd.SampledLevel == DigitalLevel.Low;

    private void ReleasePackageOutputs()
    {
        Address.Release();
        Data.Release();
        ReadWrite.Release();
        M2.Release();
        ControllerRead1Bar.Release();
        ControllerRead2Bar.Release();
        ControllerOut0.Release();
        ControllerOut1.Release();
        ControllerOut2.Release();
        AudioOut.Release();
    }

    private void ExecuteBusCycle()
    {
        if (_dmcFetchActive)
        {
            // Outside OAM DMA, the DMC DMA unit halts the CPU, performs a
            // dummy cycle, optionally aligns to the APU get phase, and only
            // then owns the external bus for the sample read.  The package
            // simulator completes a read on the cycle after BeginRead(), so
            // one or two retained-bus delay cycles produce the physical
            // three-or-four-cycle CPU stall.
            if (_dmcFetchDelayCycles > 0)
            {
                _dmcFetchDelayCycles--;
                CountDmcCpuStallCycle();
                if (_dmcFetchDelayCycles == 0) BeginRead(_dmc.CurrentAddress);
                return;
            }

            if (!TrySampleData(out var dmcSample)) return;
            if (!_dmcFetchDuringOamDma) CountDmcCpuStallCycle();
            _dmc.AcceptSample(dmcSample);
            DmcMemoryReadCount++;
            LastDmcFetchStallCycles = _dmcCurrentFetchStallCycles;
            _dmcFetchActive = false;
            RestoreBusAfterDmcFetch();
            return;
        }

        if (_dmaPending || _dmaActive)
        {
            // The DMC and OAM engines share the package CPU bus.  A DMC
            // sample request may take an OAM read slot, after which the same
            // OAM source read is repeated before its paired $2004 write.
            // Never interrupt an OAM write slot: the latched byte must reach
            // the external bus unchanged.
            if (_dmaActive && _dmaDummyCycles == 0 && _dmaReadPhase && _dmc.NeedsSample)
            {
                DmcOamDmaInterleaveCount++;
                BeginDmcFetch(duringOamDma: true);
                return;
            }

            ExecuteDmaCycle();
            return;
        }

        if (_dmc.NeedsSample && _busRead)
        {
            BeginDmcFetch(duringOamDma: false);
            return;
        }

        ServiceInternalWriteCycle();

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
                CurrentOpcode = opcode; ProgramCounter++; _sync = false; DecodeOpcode(opcode); break;

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
                    BeginIndexedAddress(Y);
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
                    BeginIndexedAddress(_addressingMode == AddressingMode.AbsoluteX ? X : Y);
                }
                else BeginEffectiveOperation();
                break;

            case CycleState.ReadAbsoluteIndexed:
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
                var branchOrigin = ProgramCounter;
                var branchTarget = (ushort)(ProgramCounter + (sbyte)_operand);
                ProgramCounter = branchTarget;
                if ((branchOrigin & 0xFF00) != (branchTarget & 0xFF00))
                {
                    _state = CycleState.BranchPageCross;
                    BeginRead((ushort)((branchOrigin & 0xFF00) | (branchTarget & 0x00FF)));
                }
                else CompleteInstruction();
                break;
            case CycleState.BranchPageCross:
                CompleteInstruction(); break;

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

            case CycleState.BrkPaddingRead:
                // BRK performs a real padding-byte read and increments PC
                // before stacking the return address.  It then shares the
                // IRQ vector but stacks B=1.
                ProgramCounter++;
                _state = CycleState.InterruptPushProgramCounterHigh;
                BeginWrite(StackAddress, (byte)(ProgramCounter >> 8));
                break;
            case CycleState.InterruptDummyRead:
                _state = CycleState.InterruptPushProgramCounterHigh; BeginWrite(StackAddress, (byte)(ProgramCounter >> 8)); break;
            case CycleState.InterruptPushProgramCounterHigh:
                StackPointer--; _state = CycleState.InterruptPushProgramCounterLow; BeginWrite(StackAddress, (byte)ProgramCounter); break;
            case CycleState.InterruptPushProgramCounterLow:
                StackPointer--;
                _state = CycleState.InterruptPushStatus;
                BeginWrite(StackAddress, _activeInterrupt == InterruptKind.Brk ? StatusForBrk : StatusForHardwareInterrupt);
                break;
            case CycleState.InterruptPushStatus:
                // The stacked byte reflects the pre-interrupt status.  The
                // interrupt-disable latch is asserted only after that bus
                // write has completed, before vector fetch begins.
                StackPointer--;
                SetFlag(InterruptDisableFlag, true);
                _state = CycleState.InterruptVectorLow;
                BeginRead(VectorAddress);
                break;
            case CycleState.InterruptVectorLow:
                if (!TrySampleData(out _lowByte)) return;
                _state = CycleState.InterruptVectorHigh; BeginRead((ushort)(VectorAddress + 1)); break;
            case CycleState.InterruptVectorHigh:
                if (!TrySampleData(out var vectorHigh)) return;
                ProgramCounter = (ushort)(_lowByte | vectorHigh << 8);
                if (_activeInterrupt == InterruptKind.Brk) CompletedInstructionCount++;
                else CompletedInterruptCount++;
                _activeInterrupt = InterruptKind.None;
                BeginOpcodeFetch(); break;
            case CycleState.Halted: break;
        }
    }

    private void DecodeOpcode(byte opcode)
    {
        switch (opcode)
        {
            case 0xEA: CompleteInstruction(); return;
            case 0x00:
                _activeInterrupt = InterruptKind.Brk;
                _state = CycleState.BrkPaddingRead;
                BeginRead(ProgramCounter);
                return;

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

            // Stable NMOS 6502 unofficial opcodes used by commercial software.
            case 0xEB: BeginImmediate(Operation.Sbc); return;
            case 0x0B: case 0x2B: BeginImmediate(Operation.Anc); return;
            case 0x4B: BeginImmediate(Operation.Alr); return;
            case 0x6B: BeginImmediate(Operation.Arr); return;
            case 0xCB: BeginImmediate(Operation.Axs); return;

            case 0xA7: BeginAddressed(Operation.Lax, AddressingMode.ZeroPage); return;
            case 0xB7: BeginAddressed(Operation.Lax, AddressingMode.ZeroPageY); return;
            case 0xAF: BeginAddressed(Operation.Lax, AddressingMode.Absolute); return;
            case 0xBF: BeginAddressed(Operation.Lax, AddressingMode.AbsoluteY); return;
            case 0xA3: BeginAddressed(Operation.Lax, AddressingMode.IndexedIndirect); return;
            case 0xB3: BeginAddressed(Operation.Lax, AddressingMode.IndirectIndexed); return;
            case 0x87: BeginAddressed(Operation.Sax, AddressingMode.ZeroPage); return;
            case 0x97: BeginAddressed(Operation.Sax, AddressingMode.ZeroPageY); return;
            case 0x8F: BeginAddressed(Operation.Sax, AddressingMode.Absolute); return;
            case 0x83: BeginAddressed(Operation.Sax, AddressingMode.IndexedIndirect); return;

            case 0x07: BeginAddressed(Operation.Slo, AddressingMode.ZeroPage); return; case 0x17: BeginAddressed(Operation.Slo, AddressingMode.ZeroPageX); return;
            case 0x0F: BeginAddressed(Operation.Slo, AddressingMode.Absolute); return; case 0x1F: BeginAddressed(Operation.Slo, AddressingMode.AbsoluteX); return;
            case 0x1B: BeginAddressed(Operation.Slo, AddressingMode.AbsoluteY); return; case 0x03: BeginAddressed(Operation.Slo, AddressingMode.IndexedIndirect); return; case 0x13: BeginAddressed(Operation.Slo, AddressingMode.IndirectIndexed); return;
            case 0x27: BeginAddressed(Operation.Rla, AddressingMode.ZeroPage); return; case 0x37: BeginAddressed(Operation.Rla, AddressingMode.ZeroPageX); return;
            case 0x2F: BeginAddressed(Operation.Rla, AddressingMode.Absolute); return; case 0x3F: BeginAddressed(Operation.Rla, AddressingMode.AbsoluteX); return;
            case 0x3B: BeginAddressed(Operation.Rla, AddressingMode.AbsoluteY); return; case 0x23: BeginAddressed(Operation.Rla, AddressingMode.IndexedIndirect); return; case 0x33: BeginAddressed(Operation.Rla, AddressingMode.IndirectIndexed); return;
            case 0x47: BeginAddressed(Operation.Sre, AddressingMode.ZeroPage); return; case 0x57: BeginAddressed(Operation.Sre, AddressingMode.ZeroPageX); return;
            case 0x4F: BeginAddressed(Operation.Sre, AddressingMode.Absolute); return; case 0x5F: BeginAddressed(Operation.Sre, AddressingMode.AbsoluteX); return;
            case 0x5B: BeginAddressed(Operation.Sre, AddressingMode.AbsoluteY); return; case 0x43: BeginAddressed(Operation.Sre, AddressingMode.IndexedIndirect); return; case 0x53: BeginAddressed(Operation.Sre, AddressingMode.IndirectIndexed); return;
            case 0x67: BeginAddressed(Operation.Rra, AddressingMode.ZeroPage); return; case 0x77: BeginAddressed(Operation.Rra, AddressingMode.ZeroPageX); return;
            case 0x6F: BeginAddressed(Operation.Rra, AddressingMode.Absolute); return; case 0x7F: BeginAddressed(Operation.Rra, AddressingMode.AbsoluteX); return;
            case 0x7B: BeginAddressed(Operation.Rra, AddressingMode.AbsoluteY); return; case 0x63: BeginAddressed(Operation.Rra, AddressingMode.IndexedIndirect); return; case 0x73: BeginAddressed(Operation.Rra, AddressingMode.IndirectIndexed); return;
            case 0xC7: BeginAddressed(Operation.Dcp, AddressingMode.ZeroPage); return; case 0xD7: BeginAddressed(Operation.Dcp, AddressingMode.ZeroPageX); return;
            case 0xCF: BeginAddressed(Operation.Dcp, AddressingMode.Absolute); return; case 0xDF: BeginAddressed(Operation.Dcp, AddressingMode.AbsoluteX); return;
            case 0xDB: BeginAddressed(Operation.Dcp, AddressingMode.AbsoluteY); return; case 0xC3: BeginAddressed(Operation.Dcp, AddressingMode.IndexedIndirect); return; case 0xD3: BeginAddressed(Operation.Dcp, AddressingMode.IndirectIndexed); return;
            case 0xE7: BeginAddressed(Operation.Isc, AddressingMode.ZeroPage); return; case 0xF7: BeginAddressed(Operation.Isc, AddressingMode.ZeroPageX); return;
            case 0xEF: BeginAddressed(Operation.Isc, AddressingMode.Absolute); return; case 0xFF: BeginAddressed(Operation.Isc, AddressingMode.AbsoluteX); return;
            case 0xFB: BeginAddressed(Operation.Isc, AddressingMode.AbsoluteY); return; case 0xE3: BeginAddressed(Operation.Isc, AddressingMode.IndexedIndirect); return; case 0xF3: BeginAddressed(Operation.Isc, AddressingMode.IndirectIndexed); return;

            // Read-NOPs preserve their documented bus accesses and lengths.
            case 0x80: case 0x82: case 0x89: case 0xC2: case 0xE2: BeginImmediate(Operation.Nop); return;
            case 0x04: case 0x44: case 0x64: BeginAddressed(Operation.Nop, AddressingMode.ZeroPage); return;
            case 0x14: case 0x34: case 0x54: case 0x74: case 0xD4: case 0xF4: BeginAddressed(Operation.Nop, AddressingMode.ZeroPageX); return;
            case 0x0C: BeginAddressed(Operation.Nop, AddressingMode.Absolute); return;
            case 0x1C: case 0x3C: case 0x5C: case 0x7C: case 0xDC: case 0xFC: BeginAddressed(Operation.Nop, AddressingMode.AbsoluteX); return;
            case 0x1A: case 0x3A: case 0x5A: case 0x7A: case 0xDA: case 0xFA: CompleteInstruction(); return;

            // KIL/JAM opcodes stop the CPU core until /RES is asserted.
            case 0x02: case 0x12: case 0x22: case 0x32: case 0x42: case 0x52:
            case 0x62: case 0x72: case 0x92: case 0xB2: case 0xD2: case 0xF2:
                CompletedInstructionCount++; _state = CycleState.Halted; Data.Release(); _sync = false; return;

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
    private void BeginIndexedAddress(byte index)
    {
        var baseAddress = _effectiveAddress;
        var finalAddress = (ushort)(baseAddress + index);
        var pageCrossed = (baseAddress & 0xFF00) != (finalAddress & 0xFF00);
        _effectiveAddress = finalAddress;

        // Stores and read-modify-write instructions always perform the
        // indexed dummy read. Ordinary reads do so only on a page crossing.
        if (pageCrossed || IsStoreOperation(_operation) || IsReadModifyWriteOperation(_operation))
        {
            var provisional = (ushort)((baseAddress & 0xFF00) | (finalAddress & 0x00FF));
            _state = CycleState.ReadAbsoluteIndexed;
            BeginRead(provisional);
            return;
        }

        BeginEffectiveOperation();
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
            case Operation.Nop: break;
            case Operation.Lax: Accumulator = X = value; SetZeroAndNegativeFlags(value); break;
            case Operation.Anc: Accumulator &= value; SetZeroAndNegativeFlags(Accumulator); SetFlag(CarryFlag, (Accumulator & 0x80) != 0); break;
            case Operation.Alr: Accumulator &= value; Accumulator = ShiftRight(Accumulator); break;
            case Operation.Arr:
                Accumulator &= value;
                Accumulator = (byte)((Accumulator >> 1) | (IsFlagSet(CarryFlag) ? 0x80 : 0));
                SetZeroAndNegativeFlags(Accumulator);
                SetFlag(CarryFlag, (Accumulator & 0x40) != 0);
                SetFlag(OverflowFlag, (((Accumulator >> 6) ^ (Accumulator >> 5)) & 1) != 0);
                break;
            case Operation.Axs:
                var ax = (byte)(Accumulator & X);
                var axResult = (byte)(ax - value);
                SetFlag(CarryFlag, ax >= value); X = axResult; SetZeroAndNegativeFlags(X); break;
            case Operation.Bit:
                SetFlag(ZeroFlag, (Accumulator & value) == 0);
                SetFlag(OverflowFlag, (value & OverflowFlag) != 0);
                SetFlag(NegativeFlag, (value & NegativeFlag) != 0);
                break;
        }
    }

    private bool IsStoreOperation(Operation op) => op is Operation.Sta or Operation.Stx or Operation.Sty or Operation.Sax;
    private bool IsReadModifyWriteOperation(Operation op) => op is Operation.Inc or Operation.Dec or Operation.Asl or Operation.Lsr or Operation.Rol or Operation.Ror or Operation.Slo or Operation.Rla or Operation.Sre or Operation.Rra or Operation.Dcp or Operation.Isc;
    private byte StoreValue => _operation switch { Operation.Sta => Accumulator, Operation.Stx => X, Operation.Sty => Y, Operation.Sax => (byte)(Accumulator & X), _ => 0 };

    private byte ApplyReadModifyWrite(byte value)
    {
        byte result = _operation switch
        {
            Operation.Inc or Operation.Isc => (byte)(value + 1),
            Operation.Dec or Operation.Dcp => (byte)(value - 1),
            Operation.Asl or Operation.Slo => ShiftLeft(value),
            Operation.Lsr or Operation.Sre => ShiftRight(value),
            Operation.Rol or Operation.Rla => RotateLeft(value),
            Operation.Ror or Operation.Rra => RotateRight(value),
            _ => throw new InvalidOperationException($"Operation {_operation} is not read-modify-write.")
        };
        if (_operation is Operation.Inc or Operation.Dec) SetZeroAndNegativeFlags(result);
        switch (_operation)
        {
            case Operation.Slo: Accumulator |= result; SetZeroAndNegativeFlags(Accumulator); break;
            case Operation.Rla: Accumulator &= result; SetZeroAndNegativeFlags(Accumulator); break;
            case Operation.Sre: Accumulator ^= result; SetZeroAndNegativeFlags(Accumulator); break;
            case Operation.Rra: AddWithCarry(result); break;
            case Operation.Dcp: Compare(Accumulator, result); break;
            case Operation.Isc: AddWithCarry((byte)~result); break;
        }
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
        _state = CycleState.ResetDummyRead1;
        _activeInterrupt = InterruptKind.None;
        _operation = Operation.None;
        _addressingMode = AddressingMode.None;
        _nmiPending = false;

        // /RES is not a second power-on.  The NMOS CPU core performs three
        // stack-page read cycles and decrements the existing stack pointer;
        // it does not reload S with a fixed value.  Existing arithmetic flags
        // remain internal state while reset forces interrupt disable.
        Status = (byte)((Status | InterruptDisableFlag | UnusedFlag) & ~BreakFlag);
        BeginRead(ProgramCounter);
    }

    private bool TryBeginPendingInterrupt()
    {
        if (_nmiPending) { _nmiPending = false; BeginInterrupt(InterruptKind.Nmi); return true; }
        if ((IrqBar.SampledLevel == DigitalLevel.Low || _frameIrqPending || _dmc.IrqPending) && !InterruptDisable)
        {
            BeginInterrupt(InterruptKind.Irq);
            return true;
        }
        return false;
    }

    private void BeginInterrupt(InterruptKind kind)
    {
        _activeInterrupt = kind;
        _state = CycleState.InterruptDummyRead;
        BeginRead(ProgramCounter);
    }

    private void BeginOpcodeFetch() { _operation = Operation.None; _addressingMode = AddressingMode.None; _state = CycleState.FetchOpcode; BeginRead(ProgramCounter, true); }
    private void BeginRead(ushort address, bool sync = false)
    {
        Data.Release();
        ReadWrite.Drive(DigitalLevel.High);
        _sync = sync;
        Address.Drive(address);
        _busAddress = address;
        _busRead = true;

        // The RP2A03 controller output-enable pins are bus-cycle signals.
        // Assert the selected line for the full $4016/$4017 read cycle so an
        // external controller shift register has time to place its serial bit
        // on IN0/IN1 before the CPU samples it.
        var readController1 = address == 0x4016;
        var readController2 = address == 0x4017;
        if (readController1) _controllerRead1Valid = false;
        if (readController2) _controllerRead2Valid = false;
        ControllerRead1Bar.Drive(readController1 ? DigitalLevel.Low : DigitalLevel.High);
        ControllerRead2Bar.Drive(readController2 ? DigitalLevel.Low : DigitalLevel.High);
        RefreshControllerInputWakeState();
    }
    private void BeginWrite(ushort address, byte value)
    {
        ControllerRead1Bar.Drive(DigitalLevel.High);
        ControllerRead2Bar.Drive(DigitalLevel.High);
        RefreshControllerInputWakeState();
        _sync = false;
        Address.Drive(address);
        Data.Drive(value);
        ReadWrite.Drive(DigitalLevel.Low);
        _busAddress = address;
        _busWriteValue = value;
        _busRead = false;
    }
    private bool IsReadCycle() => ReadWrite.DriveLevel != DigitalLevel.Low;
    private ushort StackAddress => (ushort)(0x0100 | StackPointer);
    private ushort VectorAddress => _activeInterrupt == InterruptKind.Nmi ? (ushort)0xFFFA : (ushort)0xFFFE;
    private byte StatusForBrk => (byte)(Status | BreakFlag | UnusedFlag);
    private byte StatusForHardwareInterrupt => (byte)((Status | UnusedFlag) & ~BreakFlag);
    private bool IsFlagSet(byte flag) => (Status & flag) != 0;
    private void SetFlag(byte flag, bool set) { Status = set ? (byte)(Status | flag) : (byte)(Status & ~flag); Status |= UnusedFlag; }
    private void SetZeroAndNegativeFlags(byte value) { SetFlag(ZeroFlag, value == 0); SetFlag(NegativeFlag, (value & 0x80) != 0); }
    private void SampleControllerInputs()
    {
        // /OE1 and /OE2 are asserted for the complete CPU read cycle.  Sample
        // the corresponding package input during propagation settling and
        // retain it until the CPU consumes the cycle.  This models the input
        // latch inside the RP2A03 and avoids depending on component evaluation
        // order at the exact M2 edge.
        if (ControllerRead1Bar.DriveLevel == DigitalLevel.Low &&
            ControllerData1.SampledLevel is DigitalLevel.Low or DigitalLevel.High)
        {
            _controllerRead1Latch = (byte)(ControllerData1.SampledLevel == DigitalLevel.High ? 1 : 0);
            _controllerRead1Valid = true;
        }

        if (ControllerRead2Bar.DriveLevel == DigitalLevel.Low &&
            ControllerData2.SampledLevel is DigitalLevel.Low or DigitalLevel.High)
        {
            _controllerRead2Latch = (byte)(ControllerData2.SampledLevel == DigitalLevel.High ? 1 : 0);
            _controllerRead2Valid = true;
        }
    }

    private bool TrySampleData(out byte value)
    {
        if (_busRead && _busAddress == 0x4015)
        {
            value = (byte)((_pulse1.LengthCounter > 0 ? 0x01 : 0) |
                           (_pulse2.LengthCounter > 0 ? 0x02 : 0) |
                           (_triangle.LengthCounter > 0 ? 0x04 : 0) |
                           (_noise.LengthCounter > 0 ? 0x08 : 0) |
                           (_dmc.BytesRemaining > 0 ? 0x10 : 0) |
                           (_frameIrqPending ? 0x40 : 0) |
                           (_dmc.IrqPending ? 0x80 : 0));
            _frameIrqPending = false;
            return true;
        }

        if (_busRead && _busAddress == 0x4016)
        {
            return TrySampleControllerInput(
                ControllerData1,
                ref _controllerRead1Latch,
                ref _controllerRead1Valid,
                out value);
        }

        if (_busRead && _busAddress == 0x4017)
        {
            return TrySampleControllerInput(
                ControllerData2,
                ref _controllerRead2Latch,
                ref _controllerRead2Valid,
                out value);
        }

        if (Data.TrySample(out var raw)) { value = (byte)raw; return true; }
        value = 0;
        return false;
    }

    private static bool TrySampleControllerInput(
        DigitalPin input,
        ref byte latchedValue,
        ref bool latchedValueValid,
        out byte value)
    {
        // IN0/IN1 are package inputs, not values supplied through the external
        // CPU data bus.  At the completion of a $4016/$4017 read, sample the
        // resolved input pin directly.  The retained value is only a fallback
        // for a propagation pass where the pin is temporarily unresolved.
        if (input.SampledLevel is DigitalLevel.Low or DigitalLevel.High)
        {
            latchedValue = (byte)(input.SampledLevel == DigitalLevel.High ? 1 : 0);
            latchedValueValid = true;
        }

        value = latchedValue;
        return latchedValueValid;
    }

    private void ServiceInternalWriteCycle()
    {
        if (_busRead) return;

        if (_busAddress == 0x4016)
        {
            _controllerOutputLatch = (byte)(_busWriteValue & 0x07);
            ControllerOut0.Drive((_controllerOutputLatch & 0x01) != 0 ? DigitalLevel.High : DigitalLevel.Low);
            ControllerOut1.Drive((_controllerOutputLatch & 0x02) != 0 ? DigitalLevel.High : DigitalLevel.Low);
            ControllerOut2.Drive((_controllerOutputLatch & 0x04) != 0 ? DigitalLevel.High : DigitalLevel.Low);
        }
        else if (_busAddress == 0x4014)
        {
            _dmaPage = _busWriteValue;
            _dmaPending = true;
        }
        else if (_busAddress is >= 0x4000 and <= 0x4003)
        {
            _pulse1.WriteRegister(_busAddress - 0x4000, _busWriteValue);
        }
        else if (_busAddress is >= 0x4004 and <= 0x4007)
        {
            _pulse2.WriteRegister(_busAddress - 0x4004, _busWriteValue);
        }
        else if (_busAddress == 0x4008)
        {
            _triangle.WriteControl(_busWriteValue);
        }
        else if (_busAddress == 0x400A)
        {
            _triangle.WriteTimerLow(_busWriteValue);
        }
        else if (_busAddress == 0x400B)
        {
            _triangle.WriteTimerHighAndLength(_busWriteValue);
        }
        else if (_busAddress == 0x400C)
        {
            _noise.WriteEnvelope(_busWriteValue);
        }
        else if (_busAddress == 0x400E)
        {
            _noise.WritePeriodAndMode(_busWriteValue);
        }
        else if (_busAddress == 0x400F)
        {
            _noise.WriteLength(_busWriteValue);
        }
        else if (_busAddress == 0x4010)
        {
            _dmc.WriteControl(_busWriteValue);
        }
        else if (_busAddress == 0x4011)
        {
            _dmc.WriteDirectLoad(_busWriteValue);
        }
        else if (_busAddress == 0x4012)
        {
            _dmc.WriteSampleAddress(_busWriteValue);
        }
        else if (_busAddress == 0x4013)
        {
            _dmc.WriteSampleLength(_busWriteValue);
        }
        else if (_busAddress == 0x4015)
        {
            _pulse1.SetEnabled((_busWriteValue & 0x01) != 0);
            _pulse2.SetEnabled((_busWriteValue & 0x02) != 0);
            _triangle.SetEnabled((_busWriteValue & 0x04) != 0);
            _noise.SetEnabled((_busWriteValue & 0x08) != 0);
            _dmc.SetEnabled((_busWriteValue & 0x10) != 0);
        }
        else if (_busAddress == 0x4017)
        {
            // The frame-counter mode and divider reset do not take effect on
            // the write edge.  The RP2A03 delays the reload by three or four
            // CPU cycles according to the current APU phase.  IRQ inhibit,
            // however, clears a pending frame IRQ immediately.
            _pendingFrameCounterValue = _busWriteValue;
            _frameCounterWritePending = true;
            _frameCounterWriteDelay = (_apuCpuCycles & 1UL) == 0 ? 3 : 4;
            if ((_busWriteValue & 0x40) != 0) _frameIrqPending = false;
        }
    }

    private void ClockApuCpuCycle()
    {
        _apuCpuCycles++;

        if (_frameCounterWritePending && --_frameCounterWriteDelay == 0)
        {
            ApplyPendingFrameCounterWrite();
        }

        _frameSequenceCycle++;
        _apuTimerPhase = !_apuTimerPhase;
        _triangle.ClockTimer();
        _dmc.ClockTimer();
        if (_apuTimerPhase)
        {
            _pulse1.ClockTimer();
            _pulse2.ClockTimer();
            _noise.ClockTimer();
        }

        if (_frameFiveStepMode)
        {
            switch (_frameSequenceCycle)
            {
                case 7457:
                case 22371:
                    ClockQuarterFrame();
                    break;
                case 14913:
                case 37281:
                    ClockQuarterFrame();
                    ClockHalfFrame();
                    if (_frameSequenceCycle == 37281) _frameSequenceCycle = 0;
                    break;
            }
        }
        else
        {
            switch (_frameSequenceCycle)
            {
                case 7457:
                case 22371:
                    ClockQuarterFrame();
                    break;
                case 14913:
                    ClockQuarterFrame();
                    ClockHalfFrame();
                    break;
                case 29829:
                    ClockQuarterFrame();
                    ClockHalfFrame();
                    if (!_frameIrqInhibit) _frameIrqPending = true;
                    _frameSequenceCycle = 0;
                    break;
            }
        }

        var pulse1Output = _pulse1.Output;
        var pulse2Output = _pulse2.Output;
        var triangleOutput = _triangle.Output;
        var noiseOutput = _noise.Output;
        var dmcOutput = _dmc.Output;
        if (pulse1Output != _lastMixedPulse1
            || pulse2Output != _lastMixedPulse2
            || triangleOutput != _lastMixedTriangle
            || noiseOutput != _lastMixedNoise
            || dmcOutput != _lastMixedDmc)
        {
            _lastMixedPulse1 = pulse1Output;
            _lastMixedPulse2 = pulse2Output;
            _lastMixedTriangle = triangleOutput;
            _lastMixedNoise = noiseOutput;
            _lastMixedDmc = dmcOutput;

            var mixedDacLevel = RicohApuMixer.Mix(
                pulse1Output,
                pulse2Output,
                triangleOutput,
                noiseOutput,
                dmcOutput);
            if (_audioDacLevel != mixedDacLevel)
            {
                var previousDigitalLevel = _audioDacLevel == 0 ? DigitalLevel.Low : DigitalLevel.High;
                _audioDacLevel = mixedDacLevel;
                AudioDacOutput.Drive(new RicohAudioDacSample(
                    MasterClockRisingEdgeCount,
                    mixedDacLevel));

                var digitalLevel = mixedDacLevel == 0 ? DigitalLevel.Low : DigitalLevel.High;
                if (digitalLevel != previousDigitalLevel) AudioOut.Drive(digitalLevel);
            }
        }
    }

    private void ApplyPendingFrameCounterWrite()
    {
        _frameCounterWritePending = false;
        _frameFiveStepMode = (_pendingFrameCounterValue & 0x80) != 0;
        _frameIrqInhibit = (_pendingFrameCounterValue & 0x40) != 0;
        if (_frameIrqInhibit) _frameIrqPending = false;
        _frameSequenceCycle = 0;

        // Five-step mode clocks both frame units when the delayed reload
        // occurs. Four-step mode begins a new sequence without an immediate
        // quarter/half-frame clock.
        if (_frameFiveStepMode)
        {
            ClockQuarterFrame();
            ClockHalfFrame();
        }
    }

    private void ClockQuarterFrame()
    {
        _pulse1.ClockEnvelope();
        _pulse2.ClockEnvelope();
        _triangle.ClockLinearCounter();
        _noise.ClockEnvelope();
    }

    private void ClockHalfFrame()
    {
        _pulse1.ClockLengthCounter();
        _pulse2.ClockLengthCounter();
        _triangle.ClockLengthCounter();
        _noise.ClockLengthCounter();
        _pulse1.ClockSweep();
        _pulse2.ClockSweep();
    }


    private void BeginDmcFetch(bool duringOamDma)
    {
        _dmcSavedBusAddress = _busAddress;
        _dmcSavedBusWriteValue = _busWriteValue;
        _dmcSavedBusRead = _busRead;
        _dmcSavedSync = _sync;
        _dmcFetchActive = true;
        _dmcFetchDuringOamDma = duringOamDma;
        _dmcCurrentFetchStallCycles = 0;

        if (duringOamDma)
        {
            // The CPU is already halted by OAM DMA.  The DMC takes the
            // selected OAM read slot directly; restoring the repeated OAM
            // source read provides the required post-read realignment.
            BeginRead(_dmc.CurrentAddress);
            return;
        }

        CountDmcCpuStallCycle();
        _dmcFetchDelayCycles = (RisingEdgeCount & 1UL) == 0 ? 1 : 2;
    }

    private void CountDmcCpuStallCycle()
    {
        _dmcCurrentFetchStallCycles++;
        DmcCpuStallCount++;
        ReadyStallCount++;
    }

    private void RestoreBusAfterDmcFetch()
    {
        if (_dmcSavedBusRead) BeginRead(_dmcSavedBusAddress, _dmcSavedSync);
        else BeginWrite(_dmcSavedBusAddress, _dmcSavedBusWriteValue);
    }

    private void ExecuteDmaCycle()
    {
        if (_dmaPending)
        {
            _dmaPending = false;
            _dmaActive = true;
            _dmaIndex = 0;
            _dmaReadPhase = true;
            _dmaDummyCycles = 1 + ((RisingEdgeCount & 1UL) != 0 ? 1 : 0);
            BeginRead(ProgramCounter);
            return;
        }

        if (_dmaDummyCycles > 0)
        {
            _dmaDummyCycles--;
            if (_dmaDummyCycles == 0)
            {
                BeginRead((ushort)((_dmaPage << 8) | _dmaIndex));
            }
            return;
        }

        if (_dmaReadPhase)
        {
            if (!TrySampleData(out _dmaLatch)) return;
            BeginWrite(0x2004, _dmaLatch);
            _dmaReadPhase = false;
            return;
        }

        DmaTransferCount++;
        _dmaIndex++;
        if (_dmaIndex == 0)
        {
            _dmaActive = false;
            BeginOpcodeFetch();
            return;
        }

        BeginRead((ushort)((_dmaPage << 8) | _dmaIndex));
        _dmaReadPhase = true;
    }

    private sealed class PulseChannel
    {
        private static readonly byte[] LengthTable =
        [
            10, 254, 20, 2, 40, 4, 80, 6,
            160, 8, 60, 10, 14, 12, 26, 14,
            12, 16, 24, 18, 48, 20, 96, 22,
            192, 24, 72, 26, 16, 28, 32, 30
        ];

        private static readonly byte[,] DutyTable =
        {
            { 0, 1, 0, 0, 0, 0, 0, 0 },
            { 0, 1, 1, 0, 0, 0, 0, 0 },
            { 0, 1, 1, 1, 1, 0, 0, 0 },
            { 1, 0, 0, 1, 1, 1, 1, 1 }
        };

        private readonly bool _onesComplementNegate;
        private bool _enabled;
        private byte _duty;
        private byte _sequenceStep;
        private bool _lengthHalt;
        private bool _constantVolume;
        private byte _volume;
        private bool _envelopeStart;
        private byte _envelopeDivider;
        private byte _envelopeDecay;
        private bool _sweepEnabled;
        private byte _sweepPeriod;
        private bool _sweepNegate;
        private byte _sweepShift;
        private bool _sweepReload;
        private byte _sweepDivider;
        private ushort _timerCounter;

        public PulseChannel(bool onesComplementNegate) => _onesComplementNegate = onesComplementNegate;
        public ushort TimerPeriod { get; private set; }
        public byte LengthCounter { get; private set; }
        public byte Output
        {
            get
            {
                if (!_enabled || LengthCounter == 0 || TimerPeriod < 8 || IsSweepMuted()) return 0;
                if (DutyTable[_duty, _sequenceStep] == 0) return 0;
                return _constantVolume ? _volume : _envelopeDecay;
            }
        }

        public void Reset()
        {
            _enabled = false;
            _duty = _sequenceStep = 0;
            _lengthHalt = _constantVolume = false;
            _volume = 0;
            _envelopeStart = false;
            _envelopeDivider = _envelopeDecay = 0;
            _sweepEnabled = _sweepNegate = _sweepReload = false;
            _sweepPeriod = _sweepShift = _sweepDivider = 0;
            _timerCounter = TimerPeriod = 0;
            LengthCounter = 0;
        }

        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            if (!enabled) LengthCounter = 0;
        }

        public void WriteRegister(int register, byte value)
        {
            switch (register)
            {
                case 0:
                    _duty = (byte)((value >> 6) & 0x03);
                    _lengthHalt = (value & 0x20) != 0;
                    _constantVolume = (value & 0x10) != 0;
                    _volume = (byte)(value & 0x0F);
                    break;
                case 1:
                    _sweepEnabled = (value & 0x80) != 0;
                    _sweepPeriod = (byte)((value >> 4) & 0x07);
                    _sweepNegate = (value & 0x08) != 0;
                    _sweepShift = (byte)(value & 0x07);
                    _sweepReload = true;
                    break;
                case 2:
                    TimerPeriod = (ushort)((TimerPeriod & 0x0700) | value);
                    break;
                case 3:
                    TimerPeriod = (ushort)((TimerPeriod & 0x00FF) | ((value & 0x07) << 8));
                    if (_enabled) LengthCounter = LengthTable[value >> 3];
                    _sequenceStep = 0;
                    _envelopeStart = true;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(register));
            }
        }

        public void ClockTimer()
        {
            if (_timerCounter == 0)
            {
                _timerCounter = TimerPeriod;
                _sequenceStep = (byte)((_sequenceStep + 1) & 0x07);
            }
            else _timerCounter--;
        }

        public void ClockEnvelope()
        {
            if (_envelopeStart)
            {
                _envelopeStart = false;
                _envelopeDecay = 15;
                _envelopeDivider = _volume;
                return;
            }
            if (_envelopeDivider > 0)
            {
                _envelopeDivider--;
                return;
            }
            _envelopeDivider = _volume;
            if (_envelopeDecay > 0) _envelopeDecay--;
            else if (_lengthHalt) _envelopeDecay = 15;
        }

        public void ClockLengthCounter()
        {
            if (!_lengthHalt && LengthCounter > 0) LengthCounter--;
        }

        public void ClockSweep()
        {
            if (_sweepDivider == 0 && _sweepEnabled && _sweepShift > 0 && !IsSweepMuted())
                TimerPeriod = SweepTarget();
            if (_sweepDivider == 0 || _sweepReload)
            {
                _sweepDivider = _sweepPeriod;
                _sweepReload = false;
            }
            else _sweepDivider--;
        }

        private bool IsSweepMuted() => TimerPeriod < 8 || SweepTarget() > 0x07FF;
        private ushort SweepTarget()
        {
            var change = TimerPeriod >> _sweepShift;
            if (!_sweepNegate) return (ushort)(TimerPeriod + change);
            return (ushort)(TimerPeriod - change - (_onesComplementNegate ? 1 : 0));
        }
    }

    private sealed class TriangleChannel
    {
        private static readonly byte[] LengthTable =
        [
            10, 254, 20, 2, 40, 4, 80, 6,
            160, 8, 60, 10, 14, 12, 26, 14,
            12, 16, 24, 18, 48, 20, 96, 22,
            192, 24, 72, 26, 16, 28, 32, 30
        ];

        private static readonly byte[] Sequence =
        [15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0,
          0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

        private bool _enabled;
        private bool _controlFlag;
        private byte _linearReloadValue;
        private bool _linearReloadFlag;
        private ushort _timerCounter;
        private byte _sequenceStep;

        public ushort TimerPeriod { get; private set; }
        public byte LengthCounter { get; private set; }
        public byte LinearCounter { get; private set; }
        public byte Output => _enabled && LengthCounter > 0 && LinearCounter > 0 && TimerPeriod > 1
            ? Sequence[_sequenceStep]
            : (byte)0;

        public void Reset()
        {
            _enabled = false;
            _controlFlag = false;
            _linearReloadValue = 0;
            _linearReloadFlag = false;
            _timerCounter = 0;
            _sequenceStep = 0;
            TimerPeriod = 0;
            LengthCounter = 0;
            LinearCounter = 0;
        }

        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            if (!enabled) LengthCounter = 0;
        }

        public void WriteControl(byte value)
        {
            _controlFlag = (value & 0x80) != 0;
            _linearReloadValue = (byte)(value & 0x7F);
        }

        public void WriteTimerLow(byte value) =>
            TimerPeriod = (ushort)((TimerPeriod & 0x0700) | value);

        public void WriteTimerHighAndLength(byte value)
        {
            TimerPeriod = (ushort)((TimerPeriod & 0x00FF) | ((value & 0x07) << 8));
            if (_enabled) LengthCounter = LengthTable[value >> 3];
            _linearReloadFlag = true;
        }

        public void ClockTimer()
        {
            if (_timerCounter == 0)
            {
                _timerCounter = TimerPeriod;
                if (LengthCounter > 0 && LinearCounter > 0 && TimerPeriod > 1)
                    _sequenceStep = (byte)((_sequenceStep + 1) & 0x1F);
            }
            else _timerCounter--;
        }

        public void ClockLinearCounter()
        {
            if (_linearReloadFlag) LinearCounter = _linearReloadValue;
            else if (LinearCounter > 0) LinearCounter--;
            if (!_controlFlag) _linearReloadFlag = false;
        }

        public void ClockLengthCounter()
        {
            if (!_controlFlag && LengthCounter > 0) LengthCounter--;
        }
    }

    private sealed class NoiseChannel
    {
        private static readonly byte[] LengthTable =
        [
            10, 254, 20, 2, 40, 4, 80, 6,
            160, 8, 60, 10, 14, 12, 26, 14,
            12, 16, 24, 18, 48, 20, 96, 22,
            192, 24, 72, 26, 16, 28, 32, 30
        ];

        private static readonly ushort[] PeriodTable =
        [4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068];

        private bool _enabled;
        private bool _lengthHalt;
        private bool _constantVolume;
        private byte _volume;
        private bool _envelopeStart;
        private byte _envelopeDivider;
        private byte _envelopeDecay;
        private bool _mode;
        private ushort _timerCounter;

        public ushort TimerPeriod { get; private set; }
        public byte LengthCounter { get; private set; }
        public ushort ShiftRegister { get; private set; }
        public byte Output
        {
            get
            {
                if (!_enabled || LengthCounter == 0 || (ShiftRegister & 1) != 0) return 0;
                return _constantVolume ? _volume : _envelopeDecay;
            }
        }

        public void Reset()
        {
            _enabled = false;
            _lengthHalt = _constantVolume = false;
            _volume = 0;
            _envelopeStart = false;
            _envelopeDivider = _envelopeDecay = 0;
            _mode = false;
            _timerCounter = TimerPeriod = 0;
            LengthCounter = 0;
            ShiftRegister = 1;
        }

        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            if (!enabled) LengthCounter = 0;
        }

        public void WriteEnvelope(byte value)
        {
            _lengthHalt = (value & 0x20) != 0;
            _constantVolume = (value & 0x10) != 0;
            _volume = (byte)(value & 0x0F);
        }

        public void WritePeriodAndMode(byte value)
        {
            _mode = (value & 0x80) != 0;
            TimerPeriod = PeriodTable[value & 0x0F];
        }

        public void WriteLength(byte value)
        {
            if (_enabled) LengthCounter = LengthTable[value >> 3];
            _envelopeStart = true;
        }

        public void ClockTimer()
        {
            if (_timerCounter == 0)
            {
                _timerCounter = TimerPeriod;
                var tap = _mode ? 6 : 1;
                var feedback = (ushort)((ShiftRegister & 1) ^ ((ShiftRegister >> tap) & 1));
                ShiftRegister = (ushort)((ShiftRegister >> 1) | (feedback << 14));
            }
            else _timerCounter--;
        }

        public void ClockEnvelope()
        {
            if (_envelopeStart)
            {
                _envelopeStart = false;
                _envelopeDecay = 15;
                _envelopeDivider = _volume;
                return;
            }
            if (_envelopeDivider > 0)
            {
                _envelopeDivider--;
                return;
            }
            _envelopeDivider = _volume;
            if (_envelopeDecay > 0) _envelopeDecay--;
            else if (_lengthHalt) _envelopeDecay = 15;
        }

        public void ClockLengthCounter()
        {
            if (!_lengthHalt && LengthCounter > 0) LengthCounter--;
        }
    }

    private sealed class DmcChannel
    {
        private static readonly ushort[] PeriodTable =
        [428, 380, 340, 320, 286, 254, 226, 214, 190, 160, 142, 128, 106, 84, 72, 54];

        private bool _enabled;
        private bool _irqEnabled;
        private bool _loop;
        private ushort _timerPeriod;
        private ushort _timerCounter;
        private ushort _sampleAddress;
        private ushort _sampleLength;
        private byte? _sampleBuffer;
        private byte _shiftRegister;
        private byte _bitsRemaining;
        private bool _silence;

        public byte Output { get; private set; }
        public ushort CurrentAddress { get; private set; }
        public ushort BytesRemaining { get; private set; }
        public bool IrqPending { get; private set; }
        public bool NeedsSample => _enabled && _sampleBuffer is null && BytesRemaining > 0;

        public void Reset()
        {
            _enabled = false;
            _irqEnabled = false;
            _loop = false;
            _timerPeriod = PeriodTable[0];
            _timerCounter = _timerPeriod;
            _sampleAddress = 0xC000;
            _sampleLength = 1;
            CurrentAddress = _sampleAddress;
            BytesRemaining = 0;
            _sampleBuffer = null;
            _shiftRegister = 0;
            _bitsRemaining = 8;
            _silence = true;
            Output = 0;
            IrqPending = false;
        }

        public void WriteControl(byte value)
        {
            _irqEnabled = (value & 0x80) != 0;
            _loop = (value & 0x40) != 0;
            _timerPeriod = PeriodTable[value & 0x0F];
            if (!_irqEnabled) IrqPending = false;
        }

        public void WriteDirectLoad(byte value) => Output = (byte)(value & 0x7F);

        public void WriteSampleAddress(byte value) =>
            _sampleAddress = (ushort)(0xC000 | (value << 6));

        public void WriteSampleLength(byte value) =>
            _sampleLength = (ushort)((value << 4) | 1);

        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            IrqPending = false;
            if (!enabled)
            {
                BytesRemaining = 0;
                return;
            }
            if (BytesRemaining == 0) RestartSample();
        }

        public void AcceptSample(byte value)
        {
            if (!NeedsSample) return;
            _sampleBuffer = value;
            CurrentAddress = CurrentAddress == 0xFFFF ? (ushort)0x8000 : (ushort)(CurrentAddress + 1);
            BytesRemaining--;
            if (BytesRemaining != 0) return;
            if (_loop) RestartSample();
            else if (_irqEnabled) IrqPending = true;
        }

        public void ClockTimer()
        {
            if (_timerCounter > 0)
            {
                _timerCounter--;
                return;
            }
            _timerCounter = _timerPeriod;
            ClockOutputUnit();
        }

        private void ClockOutputUnit()
        {
            if (!_silence)
            {
                if ((_shiftRegister & 1) != 0)
                {
                    if (Output <= 125) Output += 2;
                }
                else if (Output >= 2) Output -= 2;
            }

            _shiftRegister >>= 1;
            if (--_bitsRemaining != 0) return;

            _bitsRemaining = 8;
            if (_sampleBuffer is byte sample)
            {
                _shiftRegister = sample;
                _sampleBuffer = null;
                _silence = false;
            }
            else
            {
                _silence = true;
            }
        }

        private void RestartSample()
        {
            CurrentAddress = _sampleAddress;
            BytesRemaining = _sampleLength;
        }
    }


}
