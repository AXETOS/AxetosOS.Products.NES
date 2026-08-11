using System.Runtime.CompilerServices;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;

/// <summary>
/// Chip-owned VRC6 expansion-audio circuitry: two 16-step pulse generators and
/// one 14-step saw accumulator. This block only models circuitry internal to
/// the cartridge ASIC. Its retained mixed DAC node is intentionally not routed
/// into host PCM until the virtual-hardware connector grows a reusable analog
/// package/net path suitable for all expansion-audio cartridges.
/// </summary>
public sealed class KonamiVrc6Audio
{
    public sealed class PulseChannel
    {
        private byte _volume;
        private byte _dutyCycle;
        private bool _ignoreDuty;
        private ushort _frequency = 1;
        private bool _enabled;
        private int _timer = 1;
        private byte _step;
        private byte _frequencyShift;
        private byte _outputLevel;

        public byte Volume => _volume;
        public byte DutyCycle => _dutyCycle;
        public bool IgnoreDuty => _ignoreDuty;
        public ushort Frequency => _frequency;
        public bool Enabled => _enabled;
        public int Timer => _timer;
        public byte Step => _step;
        public byte FrequencyShift => _frequencyShift;
        public byte OutputLevel => _outputLevel;
        public ulong RegisterWriteCount { get; private set; }
        public ulong TimerStepCount { get; private set; }
        public ulong OutputEdgeCount { get; private set; }

        internal void Reset()
        {
            _volume = 0;
            _dutyCycle = 0;
            _ignoreDuty = false;
            _frequency = 1;
            _enabled = false;
            _timer = 1;
            _step = 0;
            _frequencyShift = 0;
            _outputLevel = 0;
            RegisterWriteCount = 0;
            TimerStepCount = 0;
            OutputEdgeCount = 0;
        }

        internal void WriteRegister(int register, byte value)
        {
            RegisterWriteCount++;
            switch (register)
            {
                case 0:
                    _volume = (byte)(value & 0x0F);
                    _dutyCycle = (byte)((value >> 4) & 0x07);
                    _ignoreDuty = (value & 0x80) != 0;
                    break;
                case 1:
                    _frequency = (ushort)((_frequency & 0x0F00) | value);
                    break;
                case 2:
                    _frequency = (ushort)((_frequency & 0x00FF) | ((value & 0x0F) << 8));
                    _enabled = (value & 0x80) != 0;
                    if (!_enabled) _step = 0;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(register));
            }
            RefreshOutput();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void SetFrequencyShift(byte shift) => _frequencyShift = shift;

        /// <summary>
        /// Clocks every physical CPU cycle. The combinational output node is only
        /// reevaluated when the timer advances phase because no input to that node
        /// can change on the intervening countdown cycles.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool Clock()
        {
            if (!_enabled) return false;

            _timer--;
            if (_timer != 0) return false;

            _step = (byte)((_step + 1) & 0x0F);
            _timer = (_frequency >> _frequencyShift) + 1;
            TimerStepCount++;
            return RefreshOutput();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool RefreshOutput()
        {
            var next = !_enabled
                ? (byte)0
                : _ignoreDuty || _step <= _dutyCycle
                    ? _volume
                    : (byte)0;
            if (next == _outputLevel) return false;

            OutputEdgeCount++;
            _outputLevel = next;
            return true;
        }
    }

    public sealed class SawChannel
    {
        private byte _accumulatorRate;
        private byte _accumulator;
        private ushort _frequency = 1;
        private bool _enabled;
        private int _timer = 1;
        private byte _step;
        private byte _frequencyShift;
        private byte _outputLevel;

        public byte AccumulatorRate => _accumulatorRate;
        public byte Accumulator => _accumulator;
        public ushort Frequency => _frequency;
        public bool Enabled => _enabled;
        public int Timer => _timer;
        public byte Step => _step;
        public byte FrequencyShift => _frequencyShift;
        public byte OutputLevel => _outputLevel;
        public ulong RegisterWriteCount { get; private set; }
        public ulong TimerStepCount { get; private set; }
        public ulong AccumulatorStepCount { get; private set; }
        public ulong OutputEdgeCount { get; private set; }

        internal void Reset()
        {
            _accumulatorRate = 0;
            _accumulator = 0;
            _frequency = 1;
            _enabled = false;
            _timer = 1;
            _step = 0;
            _frequencyShift = 0;
            _outputLevel = 0;
            RegisterWriteCount = 0;
            TimerStepCount = 0;
            AccumulatorStepCount = 0;
            OutputEdgeCount = 0;
        }

        internal void WriteRegister(int register, byte value)
        {
            RegisterWriteCount++;
            switch (register)
            {
                case 0:
                    _accumulatorRate = (byte)(value & 0x3F);
                    break;
                case 1:
                    _frequency = (ushort)((_frequency & 0x0F00) | value);
                    break;
                case 2:
                    _frequency = (ushort)((_frequency & 0x00FF) | ((value & 0x0F) << 8));
                    _enabled = (value & 0x80) != 0;
                    if (!_enabled)
                    {
                        _accumulator = 0;
                        _step = 0;
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(register));
            }
            RefreshOutput();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void SetFrequencyShift(byte shift) => _frequencyShift = shift;

        /// <summary>
        /// Clocks every physical CPU cycle. The retained DAC node is only
        /// reevaluated when the 14-step sequencer can change the accumulator.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool Clock()
        {
            if (!_enabled) return false;

            _timer--;
            if (_timer != 0) return false;

            _step = (byte)((_step + 1) % 14);
            _timer = (_frequency >> _frequencyShift) + 1;
            TimerStepCount++;
            if (_step == 0)
            {
                _accumulator = 0;
                return RefreshOutput();
            }

            if ((_step & 1) != 0) return false;

            _accumulator = unchecked((byte)(_accumulator + _accumulatorRate));
            AccumulatorStepCount++;
            return RefreshOutput();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool RefreshOutput()
        {
            var next = _enabled ? (byte)(_accumulator >> 3) : (byte)0;
            if (next == _outputLevel) return false;

            OutputEdgeCount++;
            _outputLevel = next;
            return true;
        }
    }

    private bool _halted;
    private byte _frequencyShift;
    private byte _controlRegister;
    private byte _mixedDacLevel;

    public KonamiVrc6Audio()
    {
        Pulse1 = new PulseChannel();
        Pulse2 = new PulseChannel();
        Saw = new SawChannel();
        Reset();
    }

    public PulseChannel Pulse1 { get; }
    public PulseChannel Pulse2 { get; }
    public SawChannel Saw { get; }
    public bool Halted => _halted;
    public byte FrequencyShift => _frequencyShift;
    public byte ControlRegister => _controlRegister;
    public byte MixedDacLevel => _mixedDacLevel;
    public ulong CpuClockCount { get; private set; }
    public ulong RegisterWriteCount { get; private set; }
    public ulong OutputEdgeCount { get; private set; }

    public void Reset()
    {
        Pulse1.Reset();
        Pulse2.Reset();
        Saw.Reset();
        _halted = false;
        _frequencyShift = 0;
        _controlRegister = 0;
        _mixedDacLevel = 0;
        CpuClockCount = 0;
        RegisterWriteCount = 0;
        OutputEdgeCount = 0;
    }

    public void WriteRegister(ushort address, byte value)
    {
        RegisterWriteCount++;
        var register = address & 0xF003;
        if (register is >= 0x9000 and <= 0x9002)
        {
            Pulse1.WriteRegister(register & 0x03, value);
        }
        else if (register == 0x9003)
        {
            _controlRegister = value;
            _halted = (value & 0x01) != 0;
            _frequencyShift = (byte)((value & 0x04) != 0 ? 8 : (value & 0x02) != 0 ? 4 : 0);
            Pulse1.SetFrequencyShift(_frequencyShift);
            Pulse2.SetFrequencyShift(_frequencyShift);
            Saw.SetFrequencyShift(_frequencyShift);
        }
        else if (register is >= 0xA000 and <= 0xA002)
        {
            Pulse2.WriteRegister(register & 0x03, value);
        }
        else if (register is >= 0xB000 and <= 0xB002)
        {
            Saw.WriteRegister(register & 0x03, value);
        }
        RefreshMixedOutput();
    }

    /// <summary>
    /// Clocks every physical CPU cycle. Channel timers are never skipped; only
    /// redundant combinational DAC reevaluation is suppressed between state
    /// changes, exactly as stable combinational hardware behaves.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ClockCpuCycle()
    {
        CpuClockCount++;
        if (_halted) return;

        // Deliberately use non-short-circuit OR so all three physical channels
        // receive every CPU clock even when an earlier channel changes output.
        var outputChanged = Pulse1.Clock() | Pulse2.Clock() | Saw.Clock();
        if (outputChanged) RefreshMixedOutput();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RefreshMixedOutput()
    {
        var next = (byte)(Pulse1.OutputLevel + Pulse2.OutputLevel + Saw.OutputLevel);
        if (next == _mixedDacLevel) return;

        OutputEdgeCount++;
        _mixedDacLevel = next;
    }
}
