using AxetosOS.Products.NES.Abstractions;
using AxetosOS.Products.NES.Cartridges;

namespace AxetosOS.Products.NES.Hardware;

public interface IBatteryBackedMemory
{
    bool HasBattery { get; }
    int PersistentSize { get; }
    void LoadPersistent(ReadOnlySpan<byte> data);
    byte[] SavePersistent();
}

public interface ICartridgeMirroringProvider
{
    NametableMirroring Mirroring { get; }
    event Action<NametableMirroring>? MirroringChanged;
}

/// <summary>
/// MMC1 (mapper 1) cartridge hardware. The CPU and PPU sides intentionally share
/// one serial register set because they are pins on the same physical mapper chip.
/// </summary>
public sealed class Mmc1CartridgeMemory : INesHardwareModule, ICpuBusDevice, IPpuBusDevice,
    IBatteryBackedMemory, ICartridgeMirroringProvider
{
    private const int PrgBankSize = 16 * 1024;
    private const int ChrBankSize = 4 * 1024;
    private readonly byte[] _prgRom;
    private readonly byte[] _chrMemory;
    private readonly byte[] _prgRam = new byte[8 * 1024];
    private readonly bool _chrWritable;
    private byte _shiftRegister;
    private byte _control;
    private byte _chrBank0;
    private byte _chrBank1;
    private byte _prgBank;

    public Mmc1CartridgeMemory(NesRomImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.MapperNumber != 1)
            throw new ArgumentException("MMC1 hardware requires mapper 1.", nameof(image));
        if (image.PrgRom.Length < 2 * PrgBankSize || image.PrgRom.Length % PrgBankSize != 0)
            throw new ArgumentException("MMC1 PRG-ROM must contain complete 16 KiB banks.", nameof(image));

        _prgRom = image.PrgRom.ToArray();
        _chrWritable = image.ChrRom.Length == 0;
        _chrMemory = _chrWritable ? new byte[8 * 1024] : image.ChrRom.ToArray();
        if (_chrMemory.Length < 8 * 1024 || _chrMemory.Length % ChrBankSize != 0)
            throw new ArgumentException("MMC1 CHR memory must contain complete 4 KiB banks.", nameof(image));

        HasBattery = image.HasBatteryBackedMemory;
        PowerOn();
    }

    public string ModuleId => "nes.cartridge.mmc1";
    public bool HasBattery { get; }
    public int PersistentSize => _prgRam.Length;
    public NametableMirroring Mirroring => (_control & 0x03) switch
    {
        0 => NametableMirroring.SingleScreenLower,
        1 => NametableMirroring.SingleScreenUpper,
        2 => NametableMirroring.Vertical,
        _ => NametableMirroring.Horizontal
    };

    public event Action<NametableMirroring>? MirroringChanged;

    public void PowerOn()
    {
        _shiftRegister = 0x10;
        _control = 0x0C;
        _chrBank0 = 0;
        _chrBank1 = 0;
        _prgBank = 0;
        if (_chrWritable) Array.Clear(_chrMemory);
        Array.Clear(_prgRam);
    }

    public void Reset()
    {
        _shiftRegister = 0x10;
        _control |= 0x0C;
        MirroringChanged?.Invoke(Mirroring);
    }

    public bool HandlesCpuAddress(ushort address) => address >= 0x6000;

    public byte CpuRead(ushort address)
    {
        if (address < 0x8000)
            return PrgRamEnabled ? _prgRam[address - 0x6000] : (byte)0xFF;

        var bankCount = _prgRom.Length / PrgBankSize;
        var mode = (_control >> 2) & 0x03;
        int bank;
        int offset;
        if (mode is 0 or 1)
        {
            bank = ((_prgBank & 0x0E) % bankCount) & ~1;
            offset = (bank * PrgBankSize) + (address - 0x8000);
        }
        else if (mode == 2)
        {
            bank = address < 0xC000 ? 0 : (_prgBank & 0x0F) % bankCount;
            offset = (bank * PrgBankSize) + (address & 0x3FFF);
        }
        else
        {
            bank = address < 0xC000 ? (_prgBank & 0x0F) % bankCount : bankCount - 1;
            offset = (bank * PrgBankSize) + (address & 0x3FFF);
        }

        return _prgRom[offset % _prgRom.Length];
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address < 0x8000)
        {
            if (PrgRamEnabled) _prgRam[address - 0x6000] = value;
            return;
        }

        if ((value & 0x80) != 0)
        {
            _shiftRegister = 0x10;
            _control |= 0x0C;
            MirroringChanged?.Invoke(Mirroring);
            return;
        }

        var complete = (_shiftRegister & 0x01) != 0;
        _shiftRegister = (byte)((_shiftRegister >> 1) | ((value & 0x01) << 4));
        if (!complete) return;

        var registerValue = (byte)(_shiftRegister & 0x1F);
        if (address <= 0x9FFF)
        {
            var previous = Mirroring;
            _control = registerValue;
            if (previous != Mirroring) MirroringChanged?.Invoke(Mirroring);
        }
        else if (address <= 0xBFFF) _chrBank0 = registerValue;
        else if (address <= 0xDFFF) _chrBank1 = registerValue;
        else _prgBank = registerValue;

        _shiftRegister = 0x10;
    }

    public bool HandlesPpuAddress(ushort address) => address <= 0x1FFF;

    public byte PpuRead(ushort address) => _chrMemory[MapChrAddress(address)];

    public void PpuWrite(ushort address, byte value)
    {
        if (_chrWritable) _chrMemory[MapChrAddress(address)] = value;
    }

    public void LoadPersistent(ReadOnlySpan<byte> data)
    {
        data[..Math.Min(data.Length, _prgRam.Length)].CopyTo(_prgRam);
    }

    public byte[] SavePersistent() => _prgRam.ToArray();

    private bool PrgRamEnabled => (_prgBank & 0x10) == 0;

    private int MapChrAddress(ushort address)
    {
        var bankCount = Math.Max(1, _chrMemory.Length / ChrBankSize);
        if ((_control & 0x10) == 0)
        {
            var bank = (_chrBank0 & 0x1E) % bankCount;
            return ((bank * ChrBankSize) + address) % _chrMemory.Length;
        }

        var selected = address < 0x1000 ? _chrBank0 : _chrBank1;
        var bankIndex = selected % bankCount;
        return (bankIndex * ChrBankSize) + (address & 0x0FFF);
    }
}
