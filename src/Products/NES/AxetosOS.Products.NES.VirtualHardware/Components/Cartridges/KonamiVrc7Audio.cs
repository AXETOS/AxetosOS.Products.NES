using System.Runtime.CompilerServices;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;

/// <summary>
/// Chip-local VRC7 six-channel two-operator FM generator. The VRC7 derives
/// from OPLL-class hardware but exposes only six melodic channels, one custom
/// patch and fifteen mask-ROM patches. The digital generator remains inside
/// the cartridge package; no mapper-specific host-audio path is introduced.
/// </summary>
public sealed class KonamiVrc7Audio
{
    private const int CpuCyclesPerFmSample = 36; // OPLL clock is 2x CPU; output is OPLL clock / 72.
    private const int PhaseBits = 20;
    private const int PhaseMask = (1 << PhaseBits) - 1;
    private const int SineBits = 10;
    private const int EnvelopeMute = 1023;

    private static readonly short[] SineTable = CreateSineTable();
    private static readonly int[] MultiplierTable = [1, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 20, 24, 24, 30, 30];

    // VRC7 mask-ROM instruments. Entry 0 is the eight-byte custom patch RAM.
    private static readonly byte[][] PresetPatches =
    [
        [ 0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00 ],
        [ 0x03,0x21,0x05,0x06,0xE8,0x81,0x42,0x27 ],
        [ 0x13,0x41,0x14,0x0D,0xD8,0xF6,0x23,0x12 ],
        [ 0x11,0x11,0x08,0x08,0xFA,0xB2,0x20,0x12 ],
        [ 0x31,0x61,0x0C,0x07,0xA8,0x64,0x61,0x27 ],
        [ 0x32,0x21,0x1E,0x06,0xE1,0x76,0x01,0x28 ],
        [ 0x02,0x01,0x06,0x00,0xA3,0xE2,0xF4,0xF4 ],
        [ 0x21,0x61,0x1D,0x07,0x82,0x81,0x11,0x07 ],
        [ 0x23,0x21,0x22,0x17,0xA2,0x72,0x01,0x17 ],
        [ 0x35,0x11,0x25,0x00,0x40,0x73,0x72,0x01 ],
        [ 0xB5,0x01,0x0F,0x0F,0xA8,0xA5,0x51,0x02 ],
        [ 0x17,0xC1,0x24,0x07,0xF8,0xF8,0x22,0x12 ],
        [ 0x71,0x23,0x11,0x06,0x65,0x74,0x18,0x16 ],
        [ 0x01,0x02,0xD3,0x05,0xC9,0x95,0x03,0x02 ],
        [ 0x61,0x63,0x0C,0x00,0x94,0xC0,0x33,0xF6 ],
        [ 0x21,0x72,0x0D,0x00,0xC1,0xD5,0x56,0x06 ]
    ];

    private readonly byte[] _registers = new byte[0x40];
    private readonly byte[] _customPatch = new byte[8];
    private readonly FmChannel[] _channels;
    private int _sampleDivider;
    private byte _selectedRegister;
    private short _mixedDacLevel;
    private bool _muted;

    public KonamiVrc7Audio()
    {
        _channels = Enumerable.Range(0, 6).Select(index => new FmChannel(index)).ToArray();
        Reset();
    }

    public IReadOnlyList<FmChannel> Channels => _channels;
    public IReadOnlyList<byte> Registers => _registers;
    public byte SelectedRegister => _selectedRegister;
    public bool Muted => _muted;
    public short MixedDacLevel => _mixedDacLevel;
    public ulong CpuClockCount { get; private set; }
    public ulong SampleClockCount { get; private set; }
    public ulong AddressWriteCount { get; private set; }
    public ulong DataWriteCount { get; private set; }
    public ulong IgnoredWriteCount { get; private set; }
    public ulong OutputEdgeCount { get; private set; }
    public ulong KeyOnCount { get; private set; }

    public void Reset()
    {
        Array.Clear(_registers);
        Array.Clear(_customPatch);
        _selectedRegister = 0;
        _sampleDivider = CpuCyclesPerFmSample;
        _mixedDacLevel = 0;
        _muted = false;
        CpuClockCount = 0;
        SampleClockCount = 0;
        AddressWriteCount = 0;
        DataWriteCount = 0;
        IgnoredWriteCount = 0;
        OutputEdgeCount = 0;
        KeyOnCount = 0;
        foreach (var channel in _channels) channel.Reset();
    }

    public void SetMuted(bool muted)
    {
        _muted = muted;
        if (muted && _mixedDacLevel != 0)
        {
            _mixedDacLevel = 0;
            OutputEdgeCount++;
        }
    }

    public void WritePort(ushort address, byte value)
    {
        switch (address & 0xF030)
        {
            case 0x9010:
                AddressWriteCount++;
                if (_muted)
                {
                    IgnoredWriteCount++;
                    return;
                }
                _selectedRegister = (byte)(value & 0x3F);
                break;
            case 0x9030:
                DataWriteCount++;
                if (_muted)
                {
                    IgnoredWriteCount++;
                    return;
                }
                WriteSelectedRegister(value);
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ClockCpuCycle()
    {
        CpuClockCount++;
        _sampleDivider--;
        if (_sampleDivider > 0) return;
        _sampleDivider += CpuCyclesPerFmSample;
        ClockFmSample();
    }

    private void WriteSelectedRegister(byte value)
    {
        var register = _selectedRegister;
        _registers[register] = value;
        if (register < 8)
        {
            _customPatch[register] = value;
            return;
        }

        if (register is >= 0x10 and <= 0x15 ||
            register is >= 0x20 and <= 0x25 ||
            register is >= 0x30 and <= 0x35)
        {
            var channelIndex = register & 0x0F;
            var channel = _channels[channelIndex];
            var wasKeyOn = channel.KeyOn;
            channel.LoadRegisters(_registers);
            if (!wasKeyOn && channel.KeyOn) KeyOnCount++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClockFmSample()
    {
        SampleClockCount++;
        var mixed = 0;
        for (var i = 0; i < _channels.Length; i++)
        {
            var channel = _channels[i];
            channel.LoadRegisters(_registers);
            mixed += channel.ClockSample(ResolvePatch(channel.Instrument));
        }

        var next = _muted ? (short)0 : (short)Math.Clamp(mixed / 3, short.MinValue, short.MaxValue);
        if (next == _mixedDacLevel) return;
        _mixedDacLevel = next;
        OutputEdgeCount++;
    }

    private ReadOnlySpan<byte> ResolvePatch(byte instrument)
    {
        if (instrument == 0) return _customPatch;
        return PresetPatches[instrument & 0x0F];
    }

    private static short[] CreateSineTable()
    {
        var table = new short[1 << SineBits];
        for (var i = 0; i < table.Length; i++)
            table[i] = (short)Math.Round(Math.Sin(i * (Math.PI * 2.0 / table.Length)) * short.MaxValue);
        return table;
    }

    public sealed class FmChannel
    {
        private enum EnvelopeStage : byte { Off, Attack, Decay, Sustain, Release }

        private uint _modPhase;
        private uint _carrierPhase;
        private int _modEnvelope;
        private int _carrierEnvelope;
        private EnvelopeStage _modStage;
        private EnvelopeStage _carrierStage;
        private bool _previousKeyOn;
        private int _previousModulator;

        internal FmChannel(int index) => Index = index;

        public int Index { get; }
        public ushort FNumber { get; private set; }
        public byte Block { get; private set; }
        public bool KeyOn { get; private set; }
        public bool Sustain { get; private set; }
        public byte Instrument { get; private set; }
        public byte Volume { get; private set; }
        public int OutputLevel { get; private set; }
        public ulong SampleCount { get; private set; }
        public ulong PhaseAdvanceCount { get; private set; }

        internal void Reset()
        {
            FNumber = 0;
            Block = 0;
            KeyOn = false;
            Sustain = false;
            Instrument = 0;
            Volume = 0;
            OutputLevel = 0;
            SampleCount = 0;
            PhaseAdvanceCount = 0;
            _modPhase = 0;
            _carrierPhase = 0;
            _modEnvelope = EnvelopeMute;
            _carrierEnvelope = EnvelopeMute;
            _modStage = EnvelopeStage.Off;
            _carrierStage = EnvelopeStage.Off;
            _previousKeyOn = false;
            _previousModulator = 0;
        }

        internal void LoadRegisters(byte[] registers)
        {
            var low = registers[0x10 + Index];
            var high = registers[0x20 + Index];
            var instrumentVolume = registers[0x30 + Index];
            FNumber = (ushort)(low | ((high & 0x01) << 8));
            Block = (byte)((high >> 1) & 0x07);
            KeyOn = (high & 0x10) != 0;
            Sustain = (high & 0x20) != 0;
            Instrument = (byte)(instrumentVolume >> 4);
            Volume = (byte)(instrumentVolume & 0x0F);

            if (KeyOn && !_previousKeyOn)
            {
                _modPhase = 0;
                _carrierPhase = 0;
                _modEnvelope = EnvelopeMute;
                _carrierEnvelope = EnvelopeMute;
                _modStage = EnvelopeStage.Attack;
                _carrierStage = EnvelopeStage.Attack;
            }
            else if (!KeyOn && _previousKeyOn)
            {
                if (_modStage != EnvelopeStage.Off) _modStage = EnvelopeStage.Release;
                if (_carrierStage != EnvelopeStage.Off) _carrierStage = EnvelopeStage.Release;
            }
            _previousKeyOn = KeyOn;
        }

        internal int ClockSample(ReadOnlySpan<byte> patch)
        {
            SampleCount++;
            if (_modStage == EnvelopeStage.Off && _carrierStage == EnvelopeStage.Off)
            {
                OutputLevel = 0;
                return 0;
            }

            ClockEnvelope(ref _modEnvelope, ref _modStage, patch[4] >> 4, patch[4] & 0x0F, patch[6] >> 4, patch[6] & 0x0F, patch[0], isCarrier: false);
            ClockEnvelope(ref _carrierEnvelope, ref _carrierStage, patch[5] >> 4, patch[5] & 0x0F, patch[7] >> 4, patch[7] & 0x0F, patch[1], isCarrier: true);

            var baseStep = Math.Max(1, (int)FNumber) << Block;
            var modMultiplier = MultiplierTable[patch[0] & 0x0F];
            var carrierMultiplier = MultiplierTable[patch[1] & 0x0F];
            _modPhase = (uint)((_modPhase + (baseStep * modMultiplier)) & PhaseMask);
            _carrierPhase = (uint)((_carrierPhase + (baseStep * carrierMultiplier)) & PhaseMask);
            PhaseAdvanceCount++;

            var feedback = patch[3] & 0x07;
            var modIndex = (int)(_modPhase >> (PhaseBits - SineBits));
            if (feedback != 0) modIndex = (modIndex + (_previousModulator >> (13 - Math.Min(7, feedback)))) & (SineTable.Length - 1);
            var mod = ApplyEnvelope(SineTable[modIndex], _modEnvelope + ((patch[2] & 0x3F) << 3));
            _previousModulator = mod;

            var carrierIndex = ((int)(_carrierPhase >> (PhaseBits - SineBits)) + (mod >> 7)) & (SineTable.Length - 1);
            var carrierAttenuation = _carrierEnvelope + (Volume << 6);
            var output = ApplyEnvelope(SineTable[carrierIndex], carrierAttenuation);
            OutputLevel = output;
            return output;
        }

        private void ClockEnvelope(
            ref int envelope,
            ref EnvelopeStage stage,
            int attack,
            int decay,
            int sustainLevel,
            int release,
            byte operatorControl,
            bool isCarrier)
        {
            var keyRate = ((operatorControl & 0x10) != 0 ? Block : 0) + ((FNumber >> 8) & 1);
            switch (stage)
            {
                case EnvelopeStage.Attack:
                    if (attack == 0) return;
                    envelope -= RateStep(attack, keyRate, attackPhase: true);
                    if (envelope <= 0)
                    {
                        envelope = 0;
                        stage = EnvelopeStage.Decay;
                    }
                    break;
                case EnvelopeStage.Decay:
                    envelope += RateStep(decay, keyRate, attackPhase: false);
                    var target = Math.Min(EnvelopeMute, sustainLevel << 6);
                    if (envelope >= target)
                    {
                        envelope = target;
                        stage = EnvelopeStage.Sustain;
                    }
                    break;
                case EnvelopeStage.Sustain:
                    if (!KeyOn)
                    {
                        stage = EnvelopeStage.Release;
                        break;
                    }
                    if ((operatorControl & 0x20) == 0 && !(isCarrier && Sustain))
                        envelope = Math.Min(EnvelopeMute, envelope + RateStep(release, keyRate, attackPhase: false));
                    break;
                case EnvelopeStage.Release:
                    envelope = Math.Min(EnvelopeMute, envelope + RateStep(Sustain ? 5 : release, keyRate, attackPhase: false));
                    if (envelope >= EnvelopeMute) stage = EnvelopeStage.Off;
                    break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int RateStep(int rate, int keyRate, bool attackPhase)
        {
            if (rate <= 0) return 0;
            var effective = Math.Min(15, rate + (keyRate >> 1));
            return 1 << Math.Max(0, (effective - (attackPhase ? 5 : 7)) >> 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ApplyEnvelope(int sample, int attenuation)
        {
            attenuation = Math.Clamp(attenuation, 0, EnvelopeMute);
            return sample * (EnvelopeMute - attenuation) / EnvelopeMute;
        }
    }
}
