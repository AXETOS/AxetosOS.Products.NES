using AxetosOS.Products.NES.Abstractions;
using AxetosOS.Products.NES.Cartridges;

namespace AxetosOS.Products.NES.Hardware;

public sealed class NromChrMemory : INesHardwareModule, IPpuBusDevice
{
    private readonly byte[] _memory;

    public NromChrMemory(NesRomImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.MapperNumber != 0)
        {
            throw new ArgumentException("NROM CHR hardware requires mapper 0.", nameof(image));
        }

        if (image.ChrRom.Length == 0)
        {
            _memory = new byte[8 * 1024];
            IsWritable = true;
        }
        else if (image.ChrRom.Length == 8 * 1024)
        {
            _memory = image.ChrRom.ToArray();
            IsWritable = false;
        }
        else
        {
            throw new ArgumentException("NROM CHR memory must be 8 KiB CHR-ROM or 8 KiB CHR-RAM.", nameof(image));
        }
    }

    public string ModuleId => IsWritable ? "nes.cartridge.nrom.chr-ram" : "nes.cartridge.nrom.chr-rom";
    public bool IsWritable { get; }

    public void PowerOn()
    {
        if (IsWritable)
        {
            Array.Clear(_memory);
        }
    }

    public void Reset() { }

    public bool HandlesPpuAddress(ushort address) => address <= 0x1FFF;
    public byte PpuRead(ushort address) => _memory[address & 0x1FFF];

    public void PpuWrite(ushort address, byte value)
    {
        if (IsWritable)
        {
            _memory[address & 0x1FFF] = value;
        }
    }
}
