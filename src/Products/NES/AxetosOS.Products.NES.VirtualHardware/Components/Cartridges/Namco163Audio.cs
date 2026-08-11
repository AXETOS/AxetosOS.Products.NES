using System.Runtime.CompilerServices;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;

/// <summary>
/// Chip-local Namco 163 wavetable/audio circuitry. The ASIC owns one 128-byte
/// internal RAM used both for waveform samples and channel registers, one
/// address/autoincrement port, and one time-multiplexed 4-bit x 4-bit DAC.
/// Exactly one active channel is advanced every 15 CPU cycles. The retained
/// serial DAC node is intentionally not routed into host PCM until the generic
/// cartridge connector exposes a reusable analog path.
/// </summary>
public sealed class Namco163Audio
{
    public const int AudioRamSize = 0x80;

    private readonly byte[] _ram = new byte[AudioRamSize];
    private byte _ramAddress;
    private bool _autoIncrement;
    private bool _soundDisabled;
    private byte _divider;
    private int _currentChannel;
    private int _dacLevel;
    private byte _lastWaveSample;
    private byte _lastVolume;

    public IReadOnlyList<byte> Ram => _ram;
    public byte RamAddress => _ramAddress;
    public bool AutoIncrement => _autoIncrement;
    public bool SoundDisabled => _soundDisabled;
    public byte Divider => _divider;
    public int CurrentChannel => _currentChannel;
    public int ActiveChannelCount => ((_ram[0x7F] >> 4) & 0x07) + 1;
    public int SerialDacLevel => _dacLevel;
    public byte LastWaveSample => _lastWaveSample;
    public byte LastVolume => _lastVolume;
    public ulong CpuClockCount { get; private set; }
    public ulong ChannelUpdateCount { get; private set; }
    public ulong RamAddressWriteCount { get; private set; }
    public ulong RamDataReadCount { get; private set; }
    public ulong RamDataWriteCount { get; private set; }
    public ulong AutoIncrementCount { get; private set; }
    public ulong OutputEdgeCount { get; private set; }

    public Namco163Audio() => Reset();

    public void Reset()
    {
        Array.Clear(_ram);
        _ramAddress = 0;
        _autoIncrement = false;
        _soundDisabled = false;
        _divider = 0;
        _currentChannel = 7;
        _dacLevel = 0;
        _lastWaveSample = 0;
        _lastVolume = 0;
        CpuClockCount = 0;
        ChannelUpdateCount = 0;
        RamAddressWriteCount = 0;
        RamDataReadCount = 0;
        RamDataWriteCount = 0;
        AutoIncrementCount = 0;
        OutputEdgeCount = 0;
    }

    public byte InspectRamByte(int address)
    {
        if ((uint)address >= AudioRamSize) throw new ArgumentOutOfRangeException(nameof(address));
        return _ram[address];
    }

    public uint GetFrequency(int channel)
    {
        ValidateChannel(channel);
        var baseAddress = 0x40 + (channel * 8);
        return (uint)(((_ram[baseAddress + 4] & 0x03) << 16)
            | (_ram[baseAddress + 2] << 8)
            | _ram[baseAddress]);
    }

    public uint GetPhase(int channel)
    {
        ValidateChannel(channel);
        var baseAddress = 0x40 + (channel * 8);
        return (uint)((_ram[baseAddress + 5] << 16)
            | (_ram[baseAddress + 3] << 8)
            | _ram[baseAddress + 1]);
    }

    public int GetWaveLength(int channel)
    {
        ValidateChannel(channel);
        return 256 - (_ram[0x40 + (channel * 8) + 4] & 0xFC);
    }

    public byte GetWaveAddress(int channel)
    {
        ValidateChannel(channel);
        return _ram[0x40 + (channel * 8) + 6];
    }

    public byte GetVolume(int channel)
    {
        ValidateChannel(channel);
        return (byte)(_ram[0x40 + (channel * 8) + 7] & 0x0F);
    }

    public void SetAddressRegister(byte value)
    {
        _ramAddress = (byte)(value & 0x7F);
        _autoIncrement = (value & 0x80) != 0;
        RamAddressWriteCount++;
    }

    public void SetSoundDisabled(bool disabled)
    {
        _soundDisabled = disabled;
        if (!disabled) return;
        SetDacLevel(0);
    }

    public byte PeekData() => _ram[_ramAddress];

    public byte ReadData()
    {
        var value = _ram[_ramAddress];
        RamDataReadCount++;
        AdvanceAddressIfEnabled();
        return value;
    }

    public void CompletePeekedRead()
    {
        RamDataReadCount++;
        AdvanceAddressIfEnabled();
    }

    public void WriteData(byte value)
    {
        _ram[_ramAddress] = value;
        RamDataWriteCount++;
        AdvanceAddressIfEnabled();
        NormalizeCurrentChannel();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ClockCpuCycle()
    {
        CpuClockCount++;
        if (_soundDisabled) return;

        _divider++;
        if (_divider < 15) return;
        _divider = 0;

        NormalizeCurrentChannel();
        UpdateChannel(_currentChannel);
        ChannelUpdateCount++;

        _currentChannel--;
        var minimum = 8 - ActiveChannelCount;
        if (_currentChannel < minimum) _currentChannel = 7;
    }

    private void UpdateChannel(int channel)
    {
        var baseAddress = 0x40 + (channel * 8);
        var frequency = (uint)(((_ram[baseAddress + 4] & 0x03) << 16)
            | (_ram[baseAddress + 2] << 8)
            | _ram[baseAddress]);
        var phase = (uint)((_ram[baseAddress + 5] << 16)
            | (_ram[baseAddress + 3] << 8)
            | _ram[baseAddress + 1]);
        var length = 256 - (_ram[baseAddress + 4] & 0xFC);
        var modulus = (uint)(length << 16);
        phase = modulus == 0 ? 0 : (phase + frequency) % modulus;

        _ram[baseAddress + 5] = (byte)(phase >> 16);
        _ram[baseAddress + 3] = (byte)(phase >> 8);
        _ram[baseAddress + 1] = (byte)phase;

        var samplePosition = (byte)(((phase >> 16) + _ram[baseAddress + 6]) & 0xFF);
        var packed = _ram[samplePosition >> 1];
        var sample = (byte)((samplePosition & 1) != 0 ? packed >> 4 : packed & 0x0F);
        var volume = (byte)(_ram[baseAddress + 7] & 0x0F);

        _lastWaveSample = sample;
        _lastVolume = volume;
        SetDacLevel((sample - 8) * volume);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetDacLevel(int value)
    {
        if (value == _dacLevel) return;
        _dacLevel = value;
        OutputEdgeCount++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AdvanceAddressIfEnabled()
    {
        if (!_autoIncrement) return;
        _ramAddress = (byte)((_ramAddress + 1) & 0x7F);
        AutoIncrementCount++;
    }

    private void NormalizeCurrentChannel()
    {
        var minimum = 8 - ActiveChannelCount;
        if (_currentChannel < minimum || _currentChannel > 7) _currentChannel = 7;
    }

    private static void ValidateChannel(int channel)
    {
        if ((uint)channel >= 8) throw new ArgumentOutOfRangeException(nameof(channel));
    }
}
