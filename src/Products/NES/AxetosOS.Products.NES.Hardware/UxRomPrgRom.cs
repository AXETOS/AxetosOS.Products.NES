using AxetosOS.Products.NES.Abstractions;
using AxetosOS.Products.NES.Cartridges;

namespace AxetosOS.Products.NES.Hardware;

public sealed class UxRomPrgRom : INesHardwareModule, ICpuBusDevice
{
    private const int BankSize = 16 * 1024;
    private readonly byte[] _prgRom;
    private readonly int _bankCount;
    private byte _selectedBank;

    public UxRomPrgRom(NesRomImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.MapperNumber != 2)
            throw new ArgumentException("UxROM hardware requires mapper 2.", nameof(image));
        if (image.PrgRom.Length < 2 * BankSize || image.PrgRom.Length % BankSize != 0)
            throw new ArgumentException("UxROM PRG-ROM must contain at least two 16 KiB banks.", nameof(image));

        _prgRom = image.PrgRom.ToArray();
        _bankCount = _prgRom.Length / BankSize;
    }

    public string ModuleId => "nes.cartridge.uxrom.prg-rom";
    public int SelectedBank => _selectedBank;
    public int BankCount => _bankCount;

    public void PowerOn() => _selectedBank = 0;
    public void Reset() => _selectedBank = 0;
    public bool HandlesCpuAddress(ushort address) => address >= 0x8000;

    public byte CpuRead(ushort address)
    {
        var bank = address < 0xC000 ? _selectedBank % (_bankCount - 1) : _bankCount - 1;
        var offset = bank * BankSize + (address & 0x3FFF);
        return _prgRom[offset];
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address >= 0x8000)
            _selectedBank = (byte)(value % Math.Max(1, _bankCount - 1));
    }
}
