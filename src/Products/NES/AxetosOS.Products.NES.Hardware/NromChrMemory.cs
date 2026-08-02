using AxetosOS.Products.NES.Abstractions;
using AxetosOS.Products.NES.Cartridges;

namespace AxetosOS.Products.NES.Hardware;

public sealed class NromChrMemory : INesHardwareModule, IPpuBusDevice
{
    private readonly byte[] _memory;

    public NromChrMemory(NesRomImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.MapperNumber is not (0 or 2))
        {
            throw new ArgumentException("CHR memory currently supports mapper 0 and mapper 2 boards.", nameof(image));
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
            throw new ArgumentException("CHR memory must be 8 KiB CHR-ROM or 8 KiB CHR-RAM.", nameof(image));
        }
    }

    public string ModuleId => IsWritable ? "nes.cartridge.chr-ram" : "nes.cartridge.chr-rom";
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
