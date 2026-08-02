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

    private readonly PulseChannel _pulse1 = new(hasSweepNegateExtra: true);
    private readonly PulseChannel _pulse2 = new(hasSweepNegateExtra: false);
    private readonly TriangleChannel _triangle = new();
    private readonly NoiseChannel _noise = new();
    private readonly List<float> _samples = [];
    private readonly int _sampleRate;
    private ulong _cpuCycles;
    private int _frameCycle;
    private double _sampleAccumulator;

    public Rp2A03Apu(int sampleRate = DefaultSampleRate)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        _sampleRate = sampleRate;
    }

    public string ModuleId => "nes.chip.rp2a03.apu";
    public int SampleRate => _sampleRate;
    public ulong CpuCycles => _cpuCycles;
    public IReadOnlyList<float> Samples => _samples;
    public float LastMixedSample { get; private set; }
    public byte Status => (byte)(
        (_pulse1.LengthCounter > 0 ? 0x01 : 0) |
        (_pulse2.LengthCounter > 0 ? 0x02 : 0) |
        (_triangle.LengthCounter > 0 ? 0x04 : 0) |
        (_noise.LengthCounter > 0 ? 0x08 : 0));

    public void PowerOn()
    {
        _cpuCycles = 0;
        _frameCycle = 0;
        _sampleAccumulator = 0;
        LastMixedSample = 0;
        _samples.Clear();
        _pulse1.PowerOn();
        _pulse2.PowerOn();
        _triangle.PowerOn();
        _noise.PowerOn();
    }

    public void Reset() => PowerOn();

    public bool HandlesCpuAddress(ushort address) =>
        address is >= 0x4000 and <= 0x4013 or 0x4015;

    public byte CpuRead(ushort address)
    {
        if (address != 0x4015)
        {
            return 0;
        }

        return Status;
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
            case 0x4015:
                _pulse1.SetEnabled((value & 0x01) != 0);
                _pulse2.SetEnabled((value & 0x02) != 0);
                _triangle.SetEnabled((value & 0x04) != 0);
                _noise.SetEnabled((value & 0x08) != 0);
                break;
        }
    }

    public void Clock()
    {
        _cpuCycles++;
        _frameCycle++;

        _triangle.ClockTimer();
        if ((_cpuCycles & 1) == 0)
        {
            _pulse1.ClockTimer();
            _pulse2.ClockTimer();
            _noise.ClockTimer();
        }

        // NTSC four-step frame sequencer approximation. Quarter-frame clocks
        // envelopes/linear counters; half-frame clocks length/sweep units.
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
                _frameCycle = 0;
                break;
        }

        _sampleAccumulator += _sampleRate;
        if (_sampleAccumulator < NtscCpuClockHz)
        {
            return;
        }

        _sampleAccumulator -= NtscCpuClockHz;
        LastMixedSample = Mix();
        _samples.Add(LastMixedSample);
    }

    public float[] DrainSamples()
    {
        var result = _samples.ToArray();
        _samples.Clear();
        return result;
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

        var tndInput = (_triangle.Output / 8227.0) + (_noise.Output / 12241.0);
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
}
