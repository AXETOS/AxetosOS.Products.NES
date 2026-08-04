using System.Runtime.InteropServices;
using AxetosOS.Products.NES.Abstractions;

namespace AxetosOS.Products.NES.Hardware;

public sealed class Rp2A03Apu : INesHardwareModule, IClockedHardwareModule, ICpuBusDevice
{
    public const double NtscCpuClockHz = 1_789_773.0;
    public const int DefaultSampleRate = 44_100;

    private static readonly byte[] LengthTable =
    [
        10, 254, 20, 2, 40, 4, 80, 6,
        160, 8, 60, 10, 14, 12, 26, 14,
        12, 16, 24, 18, 48, 20, 96, 22,
        192, 24, 72, 26, 16, 28, 32, 30
    ];

    private static readonly ushort[] NoisePeriods =
    [
        4, 8, 16, 32, 64, 96, 128, 160,
        202, 254, 380, 508, 762, 1_016, 2_034, 4_068
    ];

    private static readonly ushort[] DmcPeriods =
    [
        428, 380, 340, 320, 286, 254, 226, 214,
        190, 160, 142, 128, 106, 85, 72, 54
    ];

    private readonly PulseChannel _pulse1 = new(hasSweepNegateExtra: true);
    private readonly PulseChannel _pulse2 = new(hasSweepNegateExtra: false);
    private readonly TriangleChannel _triangle = new();
    private readonly NoiseChannel _noise = new();
    private readonly DmcChannel _dmc = new();
    private readonly List<float> _samples = [];
    private readonly int _sampleRate;
    private ulong _cpuCycles;
    private int _frameCycle;
    private bool _fiveStepMode;
    private bool _frameIrqInhibit;
    private bool _frameIrq;
    private double _sampleAccumulator;
    private float _highPass90LastInput;
    private float _highPass90LastOutput;
    private float _highPass440LastInput;
    private float _highPass440LastOutput;
    private float _lowPass14kLastOutput;
    private CpuBus? _dmcBus;
    private Action<int>? _dmcStall;
    private Action<ushort>? _dmcDmaRequest;

    public Rp2A03Apu(int sampleRate = DefaultSampleRate)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        _sampleRate = sampleRate;
        FrameSequencer = new Rp2A03ApuFrameSequencer(this);
        Pulse1 = new Rp2A03PulseChannelComponent(this, 1);
        Pulse2 = new Rp2A03PulseChannelComponent(this, 2);
        Triangle = new Rp2A03TriangleChannelComponent(this);
        Noise = new Rp2A03NoiseChannelComponent(this);
        Dmc = new Rp2A03DmcChannelComponent(this);
    }

    public string ModuleId => "nes.chip.rp2a03.apu";
    public Rp2A03ApuFrameSequencer FrameSequencer { get; }
    public Rp2A03PulseChannelComponent Pulse1 { get; }
    public Rp2A03PulseChannelComponent Pulse2 { get; }
    public Rp2A03TriangleChannelComponent Triangle { get; }
    public Rp2A03NoiseChannelComponent Noise { get; }
    public Rp2A03DmcChannelComponent Dmc { get; }
    public int SampleRate => _sampleRate;
    public ulong CpuCycles => _cpuCycles;
    public IReadOnlyList<float> Samples => _samples;
    public float LastMixedSample { get; private set; }
    public bool FrameIrqAsserted => _frameIrq;
    public bool DmcIrqAsserted => _dmc.IrqAsserted;
    public bool IrqAsserted => FrameIrqAsserted || DmcIrqAsserted;
    public ushort DmcCurrentAddress => _dmc.CurrentAddress;
    public ushort DmcBytesRemaining => _dmc.BytesRemaining;
    public byte DmcOutputLevel => _dmc.Output;
    public event Action<bool>? IrqLineChanged;
    public byte Status => (byte)(
        (_pulse1.LengthCounter > 0 ? 0x01 : 0) |
        (_pulse2.LengthCounter > 0 ? 0x02 : 0) |
        (_triangle.LengthCounter > 0 ? 0x04 : 0) |
        (_noise.LengthCounter > 0 ? 0x08 : 0) |
        (_dmc.BytesRemaining > 0 ? 0x10 : 0) |
        (_frameIrq ? 0x40 : 0) |
        (_dmc.IrqAsserted ? 0x80 : 0));

    internal int FrameCycle => _frameCycle;
    internal bool FiveStepMode => _fiveStepMode;
    internal bool FrameIrqInhibit => _frameIrqInhibit;
    internal ApuPulseSnapshot GetPulseSnapshot(int channel) => channel switch
    {
        1 => _pulse1.Snapshot,
        2 => _pulse2.Snapshot,
        _ => throw new ArgumentOutOfRangeException(nameof(channel))
    };
    internal ApuTriangleSnapshot TriangleSnapshot => _triangle.Snapshot;
    internal ApuNoiseSnapshot NoiseSnapshot => _noise.Snapshot;
    internal ApuDmcSnapshot DmcSnapshot => _dmc.Snapshot;

    public void PowerOn()
    {
        _cpuCycles = 0;
        _frameCycle = 0;
        _fiveStepMode = false;
        _frameIrqInhibit = false;
        _frameIrq = false;
        _sampleAccumulator = 0;
        _highPass90LastInput = 0;
        _highPass90LastOutput = 0;
        _highPass440LastInput = 0;
        _highPass440LastOutput = 0;
        _lowPass14kLastOutput = 0;
        LastMixedSample = 0;
        _samples.Clear();
        _pulse1.PowerOn();
        _pulse2.PowerOn();
        _triangle.PowerOn();
        _noise.PowerOn();
        _dmc.PowerOn();
        NotifyIrqLine();
    }

    public void Reset() => PowerOn();

    public void AttachDmcMemory(CpuBus bus, Action<int> stallCpu)
    {
        _dmcBus = bus ?? throw new ArgumentNullException(nameof(bus));
        _dmcStall = stallCpu ?? throw new ArgumentNullException(nameof(stallCpu));
        _dmcDmaRequest = null;
    }

    public void AttachDmcDma(Action<ushort> requestDma)
    {
        _dmcDmaRequest = requestDma ?? throw new ArgumentNullException(nameof(requestDma));
        _dmcBus = null;
        _dmcStall = null;
    }

    public void CompleteDmcDma(byte value)
    {
        var irqBefore = IrqAsserted;
        _dmc.CompleteDma(value);
        if (irqBefore != IrqAsserted) NotifyIrqLine();
    }

    public bool HandlesCpuAddress(ushort address) =>
        address is >= 0x4000 and <= 0x4013 or 0x4015 or 0x4017;

    public byte CpuRead(ushort address)
    {
        if (address != 0x4015)
        {
            return 0;
        }

        var status = Status;
        _frameIrq = false;
        NotifyIrqLine();
        return status;
    }

    public void CpuWrite(ushort address, byte value)
    {
        switch (address)
        {
            case 0x4000: _pulse1.WriteControl(value); break;
            case 0x4001: _pulse1.WriteSweep(value); break;
            case 0x4002: _pulse1.WriteTimerLow(value); break;
            case 0x4003: _pulse1.WriteTimerHigh(value, LengthTable); break;
            case 0x4004: _pulse2.WriteControl(value); break;
            case 0x4005: _pulse2.WriteSweep(value); break;
            case 0x4006: _pulse2.WriteTimerLow(value); break;
            case 0x4007: _pulse2.WriteTimerHigh(value, LengthTable); break;
            case 0x4008: _triangle.WriteControl(value); break;
            case 0x400A: _triangle.WriteTimerLow(value); break;
            case 0x400B: _triangle.WriteTimerHigh(value, LengthTable); break;
            case 0x400C: _noise.WriteControl(value); break;
            case 0x400E: _noise.WritePeriod(value, NoisePeriods); break;
            case 0x400F: _noise.WriteLength(value, LengthTable); break;
            case 0x4010: _dmc.WriteControl(value, DmcPeriods); NotifyIrqLine(); break;
            case 0x4011: _dmc.WriteDirectLoad(value); break;
            case 0x4012: _dmc.WriteSampleAddress(value); break;
            case 0x4013: _dmc.WriteSampleLength(value); break;
            case 0x4015:
                _pulse1.SetEnabled((value & 0x01) != 0);
                _pulse2.SetEnabled((value & 0x02) != 0);
                _triangle.SetEnabled((value & 0x04) != 0);
                _noise.SetEnabled((value & 0x08) != 0);
                _dmc.SetEnabled((value & 0x10) != 0);
                NotifyIrqLine();
                break;
            case 0x4017:
                _fiveStepMode = (value & 0x80) != 0;
                _frameIrqInhibit = (value & 0x40) != 0;
                if (_frameIrqInhibit)
                {
                    _frameIrq = false;
                    NotifyIrqLine();
                }
                _frameCycle = 0;
                if (_fiveStepMode)
                {
                    ClockQuarterFrame();
                    ClockHalfFrame();
                }
                break;
        }
    }

    public void Clock()
    {
        _cpuCycles++;
        _frameCycle++;

        _triangle.ClockTimer();
        var irqBeforeDmcClock = IrqAsserted;
        _dmc.ClockTimer(RequestDmcByte);
        if (irqBeforeDmcClock != IrqAsserted)
        {
            NotifyIrqLine();
        }
        if ((_cpuCycles & 1) == 0)
        {
            _pulse1.ClockTimer();
            _pulse2.ClockTimer();
            _noise.ClockTimer();
        }

        ClockFrameSequencer();

        _sampleAccumulator += _sampleRate;
        if (_sampleAccumulator < NtscCpuClockHz)
        {
            return;
        }

        _sampleAccumulator -= NtscCpuClockHz;
        LastMixedSample = Filter(Mix());
        _samples.Add(LastMixedSample);
    }

    public int DrainSamples(Span<float> destination)
    {
        if (destination.IsEmpty || _samples.Count == 0)
        {
            return 0;
        }

        var count = Math.Min(destination.Length, _samples.Count);
        CollectionsMarshal.AsSpan(_samples)[..count].CopyTo(destination);
        _samples.RemoveRange(0, count);
        return count;
    }

    public float[] DrainSamples()
    {
        if (_samples.Count == 0)
        {
            return [];
        }

        var result = new float[_samples.Count];
        DrainSamples(result);
        return result;
    }

    private void ClockFrameSequencer()
    {
        if (_fiveStepMode)
        {
            switch (_frameCycle)
            {
                case 7_457:
                    ClockQuarterFrame();
                    break;
                case 14_913:
                    ClockQuarterFrame();
                    ClockHalfFrame();
                    break;
                case 22_371:
                    ClockQuarterFrame();
                    break;
                case 29_829:
                    break;
                case 37_281:
                    ClockQuarterFrame();
                    ClockHalfFrame();
                    _frameCycle = 0;
                    break;
            }
            return;
        }

        switch (_frameCycle)
        {
            case 7_457:
                ClockQuarterFrame();
                break;
            case 14_913:
                ClockQuarterFrame();
                ClockHalfFrame();
                break;
            case 22_371:
                ClockQuarterFrame();
                break;
            case 29_829:
                ClockQuarterFrame();
                ClockHalfFrame();
                if (!_frameIrqInhibit)
                {
                    _frameIrq = true;
                    NotifyIrqLine();
                }
                _frameCycle = 0;
                break;
        }
    }

    private void RequestDmcByte(ushort address)
    {
        if (_dmcDmaRequest is not null)
        {
            _dmcDmaRequest(address);
            return;
        }

        // Compatibility path for tests and hosts not yet connected to the
        // shared DMA bus owner. New console hosts use AttachDmcDma instead.
        if (_dmcBus is null || _dmcStall is null)
        {
            CompleteDmcDma(0);
            return;
        }

        _dmcStall(4);
        CompleteDmcDma(_dmcBus.Read(address));
    }

    private void NotifyIrqLine() => IrqLineChanged?.Invoke(IrqAsserted);

    private float Filter(float input)
    {
        var highPass90 = HighPass(input, 90.0, ref _highPass90LastInput, ref _highPass90LastOutput);
        var highPass440 = HighPass(highPass90, 440.0, ref _highPass440LastInput, ref _highPass440LastOutput);
        var lowPassAlpha = 1.0 - Math.Exp(-2.0 * Math.PI * 14_000.0 / _sampleRate);
        _lowPass14kLastOutput += (float)(lowPassAlpha * (highPass440 - _lowPass14kLastOutput));
        return _lowPass14kLastOutput;
    }

    private float HighPass(float input, double cutoff, ref float lastInput, ref float lastOutput)
    {
        var alpha = Math.Exp(-2.0 * Math.PI * cutoff / _sampleRate);
        var output = (float)(alpha * (lastOutput + input - lastInput));
        lastInput = input;
        lastOutput = output;
        return output;
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
        _pulse1.ClockLengthAndSweep();
        _pulse2.ClockLengthAndSweep();
        _triangle.ClockLength();
        _noise.ClockLength();
    }

    private float Mix()
    {
        var pulseSum = _pulse1.Output + _pulse2.Output;
        var pulse = pulseSum == 0 ? 0.0 : 95.88 / ((8128.0 / pulseSum) + 100.0);

        var tndInput = (_triangle.Output / 8227.0) + (_noise.Output / 12241.0) + (_dmc.Output / 22638.0);
        var tnd = tndInput == 0 ? 0.0 : 159.79 / ((1.0 / tndInput) + 100.0);
        return (float)Math.Clamp(pulse + tnd, 0.0, 1.0);
    }

    private sealed class PulseChannel(bool hasSweepNegateExtra)
    {
        private static readonly byte[][] DutySequences =
        [
            [0, 1, 0, 0, 0, 0, 0, 0],
            [0, 1, 1, 0, 0, 0, 0, 0],
            [0, 1, 1, 1, 1, 0, 0, 0],
            [1, 0, 0, 1, 1, 1, 1, 1]
        ];

        private bool _enabled;
        private byte _duty;
        private byte _sequence;
        private bool _lengthHalt;
        private bool _constantVolume;
        private byte _volume;
        private byte _envelopeDivider;
        private byte _envelopeDecay;
        private bool _envelopeStart;
        private ushort _timerPeriod;
        private ushort _timer;
        private bool _sweepEnabled;
        private byte _sweepPeriod;
        private bool _sweepNegate;
        private byte _sweepShift;
        private byte _sweepDivider;
        private bool _sweepReload;

        public byte LengthCounter { get; private set; }
        public ApuPulseSnapshot Snapshot => new(
            _enabled, _duty, _sequence, LengthCounter, _timerPeriod, _timer,
            _constantVolume, _volume, _envelopeDecay, _sweepEnabled,
            _sweepNegate, _sweepShift, Output);

        public byte Output
        {
            get
            {
                if (!_enabled || LengthCounter == 0 || _timerPeriod < 8 || SweepTarget > 0x7FF)
                {
                    return 0;
                }

                if (DutySequences[_duty][_sequence] == 0)
                {
                    return 0;
                }

                return _constantVolume ? _volume : _envelopeDecay;
            }
        }

        private int SweepTarget
        {
            get
            {
                var change = _timerPeriod >> _sweepShift;
                if (!_sweepNegate)
                {
                    return _timerPeriod + change;
                }

                return _timerPeriod - change - (hasSweepNegateExtra ? 1 : 0);
            }
        }

        public void PowerOn()
        {
            _enabled = false;
            _duty = 0;
            _sequence = 0;
            _lengthHalt = false;
            _constantVolume = false;
            _volume = 0;
            _envelopeDivider = 0;
            _envelopeDecay = 0;
            _envelopeStart = false;
            _timerPeriod = 0;
            _timer = 0;
            _sweepEnabled = false;
            _sweepPeriod = 0;
            _sweepNegate = false;
            _sweepShift = 0;
            _sweepDivider = 0;
            _sweepReload = false;
            LengthCounter = 0;
        }

        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            if (!enabled)
            {
                LengthCounter = 0;
            }
        }

        public void WriteControl(byte value)
        {
            _duty = (byte)((value >> 6) & 0x03);
            _lengthHalt = (value & 0x20) != 0;
            _constantVolume = (value & 0x10) != 0;
            _volume = (byte)(value & 0x0F);
        }

        public void WriteSweep(byte value)
        {
            _sweepEnabled = (value & 0x80) != 0;
            _sweepPeriod = (byte)(((value >> 4) & 0x07) + 1);
            _sweepNegate = (value & 0x08) != 0;
            _sweepShift = (byte)(value & 0x07);
            _sweepReload = true;
        }

        public void WriteTimerLow(byte value) => _timerPeriod = (ushort)((_timerPeriod & 0x0700) | value);

        public void WriteTimerHigh(byte value, IReadOnlyList<byte> lengthTable)
        {
            _timerPeriod = (ushort)((_timerPeriod & 0x00FF) | ((value & 0x07) << 8));
            if (_enabled)
            {
                LengthCounter = lengthTable[(value >> 3) & 0x1F];
            }
            _sequence = 0;
            _envelopeStart = true;
        }

        public void ClockTimer()
        {
            if (_timer == 0)
            {
                _timer = _timerPeriod;
                _sequence = (byte)((_sequence + 1) & 0x07);
            }
            else
            {
                _timer--;
            }
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
            if (_envelopeDecay > 0)
            {
                _envelopeDecay--;
            }
            else if (_lengthHalt)
            {
                _envelopeDecay = 15;
            }
        }

        public void ClockLengthAndSweep()
        {
            if (!_lengthHalt && LengthCounter > 0)
            {
                LengthCounter--;
            }

            if (_sweepDivider == 0 && _sweepEnabled && _sweepShift > 0 && _timerPeriod >= 8 && SweepTarget <= 0x7FF && SweepTarget >= 0)
            {
                _timerPeriod = (ushort)SweepTarget;
            }

            if (_sweepDivider == 0 || _sweepReload)
            {
                _sweepDivider = _sweepPeriod;
                _sweepReload = false;
            }
            else
            {
                _sweepDivider--;
            }
        }
    }

    private sealed class TriangleChannel
    {
        private static readonly byte[] Sequence =
        [15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0,
          0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

        private bool _enabled;
        private bool _control;
        private byte _linearReloadValue;
        private byte _linearCounter;
        private bool _linearReload;
        private ushort _timerPeriod;
        private ushort _timer;
        private byte _sequence;

        public byte LengthCounter { get; private set; }
        public byte Output => _enabled && LengthCounter > 0 && _linearCounter > 0 && _timerPeriod > 1 ? Sequence[_sequence] : (byte)0;
        public ApuTriangleSnapshot Snapshot => new(
            _enabled, LengthCounter, _linearCounter, _linearReloadValue,
            _timerPeriod, _timer, _sequence, Output);

        public void PowerOn()
        {
            _enabled = false;
            _control = false;
            _linearReloadValue = 0;
            _linearCounter = 0;
            _linearReload = false;
            _timerPeriod = 0;
            _timer = 0;
            _sequence = 0;
            LengthCounter = 0;
        }

        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            if (!enabled) LengthCounter = 0;
        }

        public void WriteControl(byte value)
        {
            _control = (value & 0x80) != 0;
            _linearReloadValue = (byte)(value & 0x7F);
        }

        public void WriteTimerLow(byte value) => _timerPeriod = (ushort)((_timerPeriod & 0x0700) | value);

        public void WriteTimerHigh(byte value, IReadOnlyList<byte> lengthTable)
        {
            _timerPeriod = (ushort)((_timerPeriod & 0x00FF) | ((value & 0x07) << 8));
            if (_enabled) LengthCounter = lengthTable[(value >> 3) & 0x1F];
            _linearReload = true;
        }

        public void ClockTimer()
        {
            if (_timer == 0)
            {
                _timer = _timerPeriod;
                if (_enabled && LengthCounter > 0 && _linearCounter > 0 && _timerPeriod > 1)
                {
                    _sequence = (byte)((_sequence + 1) & 0x1F);
                }
            }
            else
            {
                _timer--;
            }
        }

        public void ClockLinearCounter()
        {
            if (_linearReload)
            {
                _linearCounter = _linearReloadValue;
            }
            else if (_linearCounter > 0)
            {
                _linearCounter--;
            }

            if (!_control)
            {
                _linearReload = false;
            }
        }

        public void ClockLength()
        {
            if (!_control && LengthCounter > 0)
            {
                LengthCounter--;
            }
        }
    }

    private sealed class NoiseChannel
    {
        private bool _enabled;
        private bool _lengthHalt;
        private bool _constantVolume;
        private byte _volume;
        private byte _envelopeDivider;
        private byte _envelopeDecay;
        private bool _envelopeStart;
        private bool _mode;
        private ushort _timerPeriod;
        private ushort _timer;
        private ushort _shiftRegister;

        public byte LengthCounter { get; private set; }
        public byte Output => _enabled && LengthCounter > 0 && (_shiftRegister & 1) == 0
            ? (_constantVolume ? _volume : _envelopeDecay)
            : (byte)0;
        public ApuNoiseSnapshot Snapshot => new(
            _enabled, LengthCounter, _mode, _timerPeriod, _timer,
            _shiftRegister, _constantVolume, _volume, _envelopeDecay, Output);

        public void PowerOn()
        {
            _enabled = false;
            _lengthHalt = false;
            _constantVolume = false;
            _volume = 0;
            _envelopeDivider = 0;
            _envelopeDecay = 0;
            _envelopeStart = false;
            _mode = false;
            _timerPeriod = 4;
            _timer = 0;
            _shiftRegister = 1;
            LengthCounter = 0;
        }

        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            if (!enabled) LengthCounter = 0;
        }

        public void WriteControl(byte value)
        {
            _lengthHalt = (value & 0x20) != 0;
            _constantVolume = (value & 0x10) != 0;
            _volume = (byte)(value & 0x0F);
        }

        public void WritePeriod(byte value, IReadOnlyList<ushort> periods)
        {
            _mode = (value & 0x80) != 0;
            _timerPeriod = periods[value & 0x0F];
        }

        public void WriteLength(byte value, IReadOnlyList<byte> lengthTable)
        {
            if (_enabled) LengthCounter = lengthTable[(value >> 3) & 0x1F];
            _envelopeStart = true;
        }

        public void ClockTimer()
        {
            if (_timer == 0)
            {
                _timer = _timerPeriod;
                var tap = _mode ? 6 : 1;
                var feedback = (ushort)((_shiftRegister & 1) ^ ((_shiftRegister >> tap) & 1));
                _shiftRegister = (ushort)((_shiftRegister >> 1) | (feedback << 14));
            }
            else
            {
                _timer--;
            }
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

        public void ClockLength()
        {
            if (!_lengthHalt && LengthCounter > 0) LengthCounter--;
        }
    }
    private sealed class DmcChannel
    {
        private bool _enabled;
        private bool _irqEnabled;
        private bool _loop;
        private ushort _timerPeriod = 428;
        private ushort _timer;
        private ushort _sampleAddress = 0xC000;
        private ushort _sampleLength = 1;
        private byte? _sampleBuffer;
        private byte _shiftRegister;
        private byte _bitsRemaining = 8;
        private bool _silence = true;

        public bool IrqAsserted { get; private set; }
        public ushort CurrentAddress { get; private set; }
        public ushort BytesRemaining { get; private set; }
        public byte Output { get; private set; }
        public ApuDmcSnapshot Snapshot => new(
            _enabled, _irqEnabled, _loop, _timerPeriod, _timer,
            _sampleAddress, _sampleLength, CurrentAddress, BytesRemaining,
            Output, _bitsRemaining, _silence, _dmaPending, IrqAsserted);

        public void PowerOn()
        {
            _enabled = false;
            _irqEnabled = false;
            _loop = false;
            _timerPeriod = 428;
            _timer = 0;
            _sampleAddress = 0xC000;
            _sampleLength = 1;
            _sampleBuffer = null;
            _dmaPending = false;
            _shiftRegister = 0;
            _bitsRemaining = 8;
            _silence = true;
            IrqAsserted = false;
            CurrentAddress = _sampleAddress;
            BytesRemaining = 0;
            Output = 0;
        }

        public void WriteControl(byte value, IReadOnlyList<ushort> periods)
        {
            _irqEnabled = (value & 0x80) != 0;
            _loop = (value & 0x40) != 0;
            _timerPeriod = periods[value & 0x0F];
            if (!_irqEnabled)
            {
                IrqAsserted = false;
            }
        }

        public void WriteDirectLoad(byte value) => Output = (byte)(value & 0x7F);
        public void WriteSampleAddress(byte value) => _sampleAddress = (ushort)(0xC000 | (value << 6));
        public void WriteSampleLength(byte value) => _sampleLength = (ushort)((value << 4) | 1);

        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            IrqAsserted = false;
            if (!enabled)
            {
                BytesRemaining = 0;
                return;
            }

            if (BytesRemaining == 0)
            {
                RestartSample();
            }
        }

        private bool _dmaPending;

        public void ClockTimer(Action<ushort> requestByte)
        {
            if (_enabled && _sampleBuffer is null && BytesRemaining > 0 && !_dmaPending)
            {
                _dmaPending = true;
                requestByte(CurrentAddress);
            }

            if (_timer > 0)
            {
                _timer--;
                return;
            }

            _timer = _timerPeriod;
            if (!_silence)
            {
                if ((_shiftRegister & 1) != 0)
                {
                    if (Output <= 125) Output += 2;
                }
                else if (Output >= 2)
                {
                    Output -= 2;
                }
            }

            _shiftRegister >>= 1;
            if (--_bitsRemaining != 0)
            {
                return;
            }

            _bitsRemaining = 8;
            if (_sampleBuffer is byte buffered)
            {
                _shiftRegister = buffered;
                _sampleBuffer = null;
                _silence = false;
            }
            else
            {
                _silence = true;
            }
        }


        public void CompleteDma(byte value)
        {
            if (!_dmaPending) return;

            _dmaPending = false;
            _sampleBuffer = value;
            CurrentAddress = CurrentAddress == 0xFFFF ? (ushort)0x8000 : (ushort)(CurrentAddress + 1);
            BytesRemaining--;
            if (BytesRemaining != 0) return;

            if (_loop)
            {
                RestartSample();
            }
            else if (_irqEnabled)
            {
                IrqAsserted = true;
            }
        }

        private void RestartSample()
        {
            CurrentAddress = _sampleAddress;
            BytesRemaining = _sampleLength;
        }
    }

}
