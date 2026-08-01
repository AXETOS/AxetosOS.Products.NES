using AxetosOS.Products.NES.Abstractions;
using AxetosOS.Products.NES.Cartridges;

namespace AxetosOS.Products.NES.Hardware;

public sealed class NromPrgRom : INesHardwareModule, ICpuBusDevice
{
    private readonly byte[] _prgRom;

    public NromPrgRom(NesRomImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.MapperNumber != 0)
        {
            throw new ArgumentException("NROM hardware requires mapper 0.", nameof(image));
        }

        if (image.PrgRom.Length is not (16 * 1024) and not (32 * 1024))
        {
            throw new ArgumentException("NROM PRG-ROM must be 16 KiB or 32 KiB.", nameof(image));
        }

        _prgRom = image.PrgRom.ToArray();
    }

    public string ModuleId => "nes.cartridge.nrom.prg-rom";

    public void PowerOn()
    {
    }

    public void Reset()
    {
    }

    public bool HandlesCpuAddress(ushort address) => address >= 0x8000;

    public byte CpuRead(ushort address)
    {
        var offset = address - 0x8000;
        if (_prgRom.Length == 16 * 1024)
        {
            offset &= 0x3FFF;
        }

        return _prgRom[offset];
    }

    public void CpuWrite(ushort address, byte value)
    {
        // NROM PRG memory is read-only. Writes may matter for bus-conflict
        // boards later, but plain NROM ignores them.
    }
}
