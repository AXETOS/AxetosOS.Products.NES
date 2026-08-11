using System.Runtime.CompilerServices;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;

/// <summary>
/// MMC5 chip-local expansion audio circuitry: two pulse channels and the 8-bit PCM DAC.
/// The pulse timers run on the APU half-rate clock while their envelope and length units
/// use the MMC5's free-running approximately 240 Hz clock. The retained mixed DAC node
/// remains inside the cartridge until a generic analog cartridge-audio net is modeled.
/// </summary>
public sealed class NintendoMmc5Audio
{
    private const int FrameClockCpuCycles = 7457;

    private static readonly byte[] LengthTable =
    [
        10, 254, 20, 2, 40, 4, 80, 6,
        160, 8, 60, 10, 14, 12, 26, 14,
        12, 16, 24, 18, 48, 20, 96, 22,
        192, 24, 72, 26, 16, 28, 32, 30
    ];

    public sealed class PulseChannel
    {
        private byte _control;
        private ushort _period;
        private ushort _timer;
        private byte _dutyStep;
        private byte _lengthCounter;
        private byte _envelopeDivider;
        private byte _envelopeDecay;
        private bool _envelopeStart;
        private bool _enabled;
        private byte _output;

        public byte Control => _control;
        public ushort Period => _period;
        public ushort Timer => _timer;
        public byte Duty => (byte)((_control >> 6) & 0x03);
        public byte DutyStep => _dutyStep;
        public bool Enabled => _enabled;
        public byte LengthCounter => _lengthCounter;
        public byte EnvelopeDivider => _envelopeDivider;
        public byte EnvelopeDecay => _envelopeDecay;
        public byte OutputLevel => _output;
        public ulong TimerClockCount { get; private set; }
        public ulong DutyAdvanceCount { get; private set; }
        public ulong EnvelopeClockCount { get; private set; }
        public ulong LengthClockCount { get; private set; }
        public ulong RegisterWriteCount { get; private set; }
        public ulong OutputEdgeCount { get; private set; }

        internal void Reset()
        {
            _control = 0;
            _period = 0;
            _timer = 0;
            _dutyStep = 0;
            _lengthCounter = 0;
            _envelopeDivider = 0;
            _envelopeDecay = 0;
            _envelopeStart = false;
            _enabled = false;
            _output = 0;
            TimerClockCount = 0;
            DutyAdvanceCount = 0;
            EnvelopeClockCount = 0;
            LengthClockCount = 0;
            RegisterWriteCount = 0;
            OutputEdgeCount = 0;
        }

        internal void Write(int register, byte value)
        {
            RegisterWriteCount++;
            switch (register)
            {
                case 0:
                    _control = value;
                    break;
                case 1:
                    // MMC5 pulse channels have no sweep unit.
                    break;
                case 2:
                    _period = (ushort)((_period & 0x0700) | value);
                    break;
                case 3:
                    _period = (ushort)((_period & 0x00FF) | ((value & 0x07) << 8));
                    if (_enabled) _lengthCounter = LengthTable[(value >> 3) & 0x1F];
                    _dutyStep = 0;
                    _envelopeStart = true;
                    break;
            }
            RecomputeOutput();
        }

        internal void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            if (!enabled) _lengthCounter = 0;
            RecomputeOutput();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool ClockTimer()
        {
            TimerClockCount++;
            if (_timer != 0)
            {
                _timer--;
                return false;
            }

            _timer = _period;
            _dutyStep = (byte)((_dutyStep - 1) & 0x07);
            DutyAdvanceCount++;
            RecomputeOutput();
            return true;
        }

        internal void ClockFrameUnit()
        {
            ClockLengthCounter();
            ClockEnvelope();
            RecomputeOutput();
        }

        private void ClockLengthCounter()
        {
            LengthClockCount++;
            var halt = (_control & 0x20) != 0;
            if (!halt && _lengthCounter != 0) _lengthCounter--;
        }

        private void ClockEnvelope()
        {
            EnvelopeClockCount++;
            var period = (byte)(_control & 0x0F);
            if (_envelopeStart)
            {
                _envelopeStart = false;
                _envelopeDecay = 15;
                _envelopeDivider = period;
                return;
            }

            if (_envelopeDivider != 0)
            {
                _envelopeDivider--;
                return;
            }

            _envelopeDivider = period;
            if (_envelopeDecay != 0)
            {
                _envelopeDecay--;
            }
            else if ((_control & 0x20) != 0)
            {
                _envelopeDecay = 15;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void RecomputeOutput()
        {
            var old = _output;
            if (!_enabled || _lengthCounter == 0)
            {
                _output = 0;
            }
            else
            {
                var dutyHigh = Duty switch
                {
                    0 => _dutyStep == 7,
                    1 => _dutyStep is 6 or 7,
                    2 => _dutyStep is 4 or 5 or 6 or 7,
                    3 => _dutyStep is 0 or 1 or 2 or 3 or 4 or 5,
                    _ => false
                };
                var volume = (_control & 0x10) != 0 ? (byte)(_control & 0x0F) : _envelopeDecay;
                _output = dutyHigh ? volume : (byte)0;
            }

            if (_output != old) OutputEdgeCount++;
        }
    }

    private int _frameDivider;
    private bool _apuHalfCycle;
    private int _mixedDacLevel;
    private bool _pcmReadMode;
    private bool _pcmIrqEnabled;
    private bool _pcmIrqPending;
    private byte _pcmOutput;

    public NintendoMmc5Audio()
    {
        Pulse1 = new PulseChannel();
        Pulse2 = new PulseChannel();
        Reset();
    }

    public PulseChannel Pulse1 { get; }
    public PulseChannel Pulse2 { get; }
    public bool PcmReadMode => _pcmReadMode;
    public bool PcmIrqEnabled => _pcmIrqEnabled;
    public bool PcmIrqPending => _pcmIrqPending;
    public byte PcmOutput => _pcmOutput;
    public int MixedDacLevel => _mixedDacLevel;
    public ulong CpuClockCount { get; private set; }
    public ulong ApuHalfClockCount { get; private set; }
    public ulong FrameClockCount { get; private set; }
    public ulong RegisterWriteCount { get; private set; }
    public ulong RegisterReadCount { get; private set; }
    public ulong PcmReadSampleCount { get; private set; }
    public ulong PcmIrqAssertCount { get; private set; }
    public ulong OutputEdgeCount { get; private set; }

    public void Reset()
    {
        Pulse1.Reset();
        Pulse2.Reset();
        _frameDivider = FrameClockCpuCycles;
        _apuHalfCycle = false;
        _mixedDacLevel = 0;
        _pcmReadMode = false;
        _pcmIrqEnabled = false;
        _pcmIrqPending = false;
        _pcmOutput = 0;
        CpuClockCount = 0;
        ApuHalfClockCount = 0;
        FrameClockCount = 0;
        RegisterWriteCount = 0;
        RegisterReadCount = 0;
        PcmReadSampleCount = 0;
        PcmIrqAssertCount = 0;
        OutputEdgeCount = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ClockCpuCycle()
    {
        CpuClockCount++;
        var outputMayHaveChanged = false;
        _apuHalfCycle = !_apuHalfCycle;
        if (_apuHalfCycle)
        {
            ApuHalfClockCount++;
            outputMayHaveChanged |= Pulse1.ClockTimer();
            outputMayHaveChanged |= Pulse2.ClockTimer();
        }

        _frameDivider--;
        if (_frameDivider <= 0)
        {
            _frameDivider += FrameClockCpuCycles;
            FrameClockCount++;
            Pulse1.ClockFrameUnit();
            Pulse2.ClockFrameUnit();
            outputMayHaveChanged = true;
        }

        if (outputMayHaveChanged) RecomputeMixedOutput();
    }

    public void WriteRegister(ushort address, byte value)
    {
        RegisterWriteCount++;
        if (address is >= 0x5000 and <= 0x5003)
        {
            Pulse1.Write(address - 0x5000, value);
        }
        else if (address is >= 0x5004 and <= 0x5007)
        {
            Pulse2.Write(address - 0x5004, value);
        }
        else
        {
            switch (address)
            {
                case 0x5010:
                    _pcmReadMode = (value & 0x01) != 0;
                    _pcmIrqEnabled = (value & 0x80) != 0;
                    if (!_pcmIrqEnabled) _pcmIrqPending = false;
                    break;
                case 0x5011:
                    if (!_pcmReadMode && value != 0) _pcmOutput = value;
                    break;
                case 0x5015:
                    Pulse1.SetEnabled((value & 0x01) != 0);
                    Pulse2.SetEnabled((value & 0x02) != 0);
                    break;
            }
        }
        RecomputeMixedOutput();
    }

    public byte ReadRegister(ushort address)
    {
        RegisterReadCount++;
        return address switch
        {
            0x5010 => ReadPcmStatus(),
            0x5015 => (byte)((Pulse1.LengthCounter != 0 ? 0x01 : 0x00) |
                             (Pulse2.LengthCounter != 0 ? 0x02 : 0x00)),
            _ => 0
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ObserveCpuRead(ushort address, byte value)
    {
        if (!_pcmReadMode || address is < 0x8000 or > 0xBFFF) return;
        PcmReadSampleCount++;
        if (value == 0)
        {
            if (_pcmIrqEnabled && !_pcmIrqPending)
            {
                _pcmIrqPending = true;
                PcmIrqAssertCount++;
            }
            return;
        }
        _pcmOutput = value;
        RecomputeMixedOutput();
    }

    public void ClearPcmIrq()
    {
        _pcmIrqPending = false;
    }

    private byte ReadPcmStatus()
    {
        var value = _pcmIrqPending ? (byte)0x80 : (byte)0x00;
        _pcmIrqPending = false;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecomputeMixedOutput()
    {
        var next = -(Pulse1.OutputLevel + Pulse2.OutputLevel + _pcmOutput);
        if (next == _mixedDacLevel) return;
        _mixedDacLevel = next;
        OutputEdgeCount++;
    }
}
