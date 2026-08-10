namespace AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;

/// <summary>
/// Sunsoft 5B three-channel PSG circuitry. This is chip-internal state owned by
/// the Mapper-69 cartridge package; it is clocked from CPU bus cycles and keeps
/// tone, noise, envelope, mixer and logarithmic DAC state independent from the
/// motherboard or whole-circuit compiler.
/// </summary>
public sealed class Sunsoft5bPsg
{
    private const int ChannelCount = 3;
    private const int RegisterCount = 16;
    private const uint NoiseLfsrMask = (1u << 17) - 1;
    private static readonly ushort[] DacTable = CreateDacTable();

    private readonly byte[] _registers = new byte[RegisterCount];
    private readonly int[] _toneCounters = new int[ChannelCount];
    private readonly bool[] _toneOutputs = new bool[ChannelCount];
    private readonly byte[] _channelDacLevels = new byte[ChannelCount];

    private byte _selectedRegister;
    private bool _dataWritesDisabled;
    private int _clockDivider;
    private int _noiseCounter;
    private bool _noisePrescaler;
    private uint _noiseLfsr;
    private bool _noiseOutput;
    private int _envelopeCounter;
    private int _envelopeLevel;
    private int _envelopeDirection;
    private bool _envelopeHolding;
    private byte _mixedDacLevel;

    public Sunsoft5bPsg() => Reset();

    public IReadOnlyList<byte> Registers => _registers;
    public IReadOnlyList<int> ToneCounters => _toneCounters;
    public IReadOnlyList<bool> ToneOutputs => _toneOutputs;
    public IReadOnlyList<byte> ChannelDacLevels => _channelDacLevels;
    public byte SelectedRegister => _selectedRegister;
    public bool DataWritesDisabled => _dataWritesDisabled;
    public int ClockDivider => _clockDivider;
    public uint NoiseLfsr => _noiseLfsr;
    public bool NoiseOutput => _noiseOutput;
    public int EnvelopeLevel => _envelopeLevel;
    public bool EnvelopeHolding => _envelopeHolding;
    public byte MixedDacLevel => _mixedDacLevel;

    public ushort TonePeriodA => TonePeriod(0);
    public ushort TonePeriodB => TonePeriod(1);
    public ushort TonePeriodC => TonePeriod(2);
    public byte NoisePeriod => (byte)(_registers[0x06] & 0x1F);
    public ushort EnvelopePeriod => (ushort)(_registers[0x0B] | (_registers[0x0C] << 8));
    public byte EnvelopeShape => (byte)(_registers[0x0D] & 0x0F);

    public ulong CpuClockCount { get; private set; }
    public ulong GeneratorTickCount { get; private set; }
    public ulong RegisterSelectWriteCount { get; private set; }
    public ulong RegisterDataWriteCount { get; private set; }
    public ulong IgnoredDataWriteCount { get; private set; }
    public ulong ToneFlipCount { get; private set; }
    public ulong NoiseShiftCount { get; private set; }
    public ulong EnvelopeStepCount { get; private set; }
    public ulong OutputEdgeCount { get; private set; }

    public void Reset()
    {
        Array.Clear(_registers);
        Array.Clear(_toneCounters);
        Array.Clear(_toneOutputs);
        Array.Clear(_channelDacLevels);
        _selectedRegister = 0;
        _dataWritesDisabled = false;
        _clockDivider = 0;
        _noiseCounter = 0;
        _noisePrescaler = false;
        _noiseLfsr = 1;
        _noiseOutput = true;
        _envelopeCounter = 0;
        _envelopeLevel = 0;
        _envelopeDirection = -1;
        _envelopeHolding = false;
        _mixedDacLevel = 0;
        CpuClockCount = 0;
        GeneratorTickCount = 0;
        RegisterSelectWriteCount = 0;
        RegisterDataWriteCount = 0;
        IgnoredDataWriteCount = 0;
        ToneFlipCount = 0;
        NoiseShiftCount = 0;
        EnvelopeStepCount = 0;
        OutputEdgeCount = 0;
        RecomputeOutput();
    }

    public void WriteRegisterSelect(byte value)
    {
        _selectedRegister = (byte)(value & 0x0F);
        _dataWritesDisabled = (value & 0xF0) != 0;
        RegisterSelectWriteCount++;
    }

    public void WriteRegisterData(byte value)
    {
        if (_dataWritesDisabled)
        {
            IgnoredDataWriteCount++;
            return;
        }

        RegisterDataWriteCount++;
        switch (_selectedRegister)
        {
            case 0x01:
            case 0x03:
            case 0x05:
                _registers[_selectedRegister] = (byte)(value & 0x0F);
                break;
            case 0x06:
                _registers[0x06] = (byte)(value & 0x1F);
                break;
            case 0x07:
                _registers[0x07] = (byte)(value & 0x3F);
                break;
            case 0x08:
            case 0x09:
            case 0x0A:
                _registers[_selectedRegister] = (byte)(value & 0x1F);
                break;
            case 0x0D:
                _registers[0x0D] = (byte)(value & 0x0F);
                ResetEnvelopePhase();
                break;
            case 0x0E:
            case 0x0F:
                // The 5B package does not bond the AY-compatible parallel I/O
                // ports into the cartridge board, so these registers have no
                // externally observable data-path effect.
                break;
            default:
                _registers[_selectedRegister] = value;
                break;
        }

        RecomputeOutput();
    }

    public void ClockCpuCycle()
    {
        CpuClockCount++;
        _clockDivider++;
        if (_clockDivider < 16) return;
        _clockDivider = 0;
        GeneratorTickCount++;

        ClockTone(0);
        ClockTone(1);
        ClockTone(2);
        ClockNoise();
        ClockEnvelope();
        RecomputeOutput();
    }

    private void ClockTone(int channel)
    {
        _toneCounters[channel]++;
        var period = Math.Max(1, (int)TonePeriod(channel));
        while (_toneCounters[channel] >= period)
        {
            _toneCounters[channel] -= period;
            _toneOutputs[channel] = !_toneOutputs[channel];
            ToneFlipCount++;
        }
    }

    private void ClockNoise()
    {
        _noiseCounter++;
        var period = Math.Max(1, (int)NoisePeriod);
        if (_noiseCounter < period) return;
        _noiseCounter = 0;

        // The AY/YM noise block has a separate divide-by-two prescaler after
        // the programmable period counter. The 17-bit LFSR shifts only on
        // every second period expiration; its input is output bit 0 XOR bit 3.
        _noisePrescaler = !_noisePrescaler;
        if (_noisePrescaler) return;

        var feedback = ((_noiseLfsr >> 0) ^ (_noiseLfsr >> 3)) & 0x01;
        _noiseLfsr = ((_noiseLfsr >> 1) | (feedback << 16)) & NoiseLfsrMask;
        if (_noiseLfsr == 0) _noiseLfsr = 1;
        _noiseOutput = (_noiseLfsr & 0x01) != 0;
        NoiseShiftCount++;
    }

    private void ClockEnvelope()
    {
        if (_envelopeHolding) return;
        _envelopeCounter++;
        var period = Math.Max(1, (int)EnvelopePeriod);
        if (_envelopeCounter < period) return;
        _envelopeCounter = 0;
        EnvelopeStepCount++;
        AdvanceEnvelopeShape();
    }

    private void ResetEnvelopePhase()
    {
        _envelopeCounter = 0;
        _envelopeHolding = false;
        var attack = (EnvelopeShape & 0x04) != 0;
        _envelopeLevel = attack ? 0 : 31;
        _envelopeDirection = attack ? 1 : -1;
    }

    private void AdvanceEnvelopeShape()
    {
        var next = _envelopeLevel + _envelopeDirection;
        if ((uint)next <= 31)
        {
            _envelopeLevel = next;
            return;
        }

        var shape = EnvelopeShape;
        var @continue = (shape & 0x08) != 0;
        var alternate = (shape & 0x02) != 0;
        var hold = (shape & 0x01) != 0;

        if (!@continue)
        {
            _envelopeLevel = 0;
            _envelopeHolding = true;
            return;
        }

        var terminalLevel = _envelopeDirection > 0 ? 31 : 0;
        if (hold)
        {
            _envelopeLevel = alternate ? 31 - terminalLevel : terminalLevel;
            _envelopeHolding = true;
            return;
        }

        if (alternate)
        {
            // YM2149/5B triangle shapes hold the endpoint for the wrap tick,
            // then leave it on the following envelope step.
            _envelopeDirection = -_envelopeDirection;
            _envelopeLevel = terminalLevel;
            return;
        }

        _envelopeLevel = _envelopeDirection > 0 ? 0 : 31;
    }

    private void RecomputeOutput()
    {
        var previous = _mixedDacLevel;
        var mixer = _registers[0x07];
        var amplitudeSum = 0u;
        for (var channel = 0; channel < ChannelCount; channel++)
        {
            var toneDisabled = (mixer & (1 << channel)) != 0;
            var noiseDisabled = (mixer & (1 << (channel + 3))) != 0;
            var gateOpen = (toneDisabled || _toneOutputs[channel]) && (noiseDisabled || _noiseOutput);
            var volume = _registers[0x08 + channel];
            var dacIndex = (volume & 0x10) != 0
                ? _envelopeLevel
                : FixedVolumeToDacIndex(volume & 0x0F);
            _channelDacLevels[channel] = gateOpen ? (byte)dacIndex : (byte)0;
            amplitudeSum += DacTable[_channelDacLevels[channel]];
        }

        var normalized = amplitudeSum / 3u;
        _mixedDacLevel = (byte)Math.Min(255u, (normalized * 255u + 32767u) / 65535u);
        if (_mixedDacLevel != previous) OutputEdgeCount++;
    }

    private ushort TonePeriod(int channel)
    {
        var lowRegister = channel * 2;
        return (ushort)(_registers[lowRegister] | ((_registers[lowRegister + 1] & 0x0F) << 8));
    }

    private static int FixedVolumeToDacIndex(int volume) => volume == 0 ? 0 : (volume * 2) + 1;

    private static ushort[] CreateDacTable()
    {
        var table = new ushort[32];
        table[0] = 0;
        table[1] = 0;
        for (var level = 2; level < table.Length; level++)
        {
            var attenuationDb = (31 - level) * 0.75;
            var amplitude = Math.Pow(10.0, -attenuationDb / 20.0);
            table[level] = (ushort)Math.Clamp((int)Math.Round(amplitude * ushort.MaxValue), 0, ushort.MaxValue);
        }
        return table;
    }
}
