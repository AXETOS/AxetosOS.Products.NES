using AxetosOS.Products.NES.Abstractions;

namespace AxetosOS.Products.NES.Hardware;

public sealed class PpuPaletteRam : INesHardwareModule, IPpuBusDevice
{
    private readonly byte[] _memory = new byte[32];

    public string ModuleId => "nes.memory.palette";

    public void PowerOn() => Array.Clear(_memory);
    public void Reset() { }

    public bool HandlesPpuAddress(ushort address) => address is >= 0x3F00 and <= 0x3FFF;

    public byte PpuRead(ushort address) => _memory[MapAddress(address)];

    public void PpuWrite(ushort address, byte value) => _memory[MapAddress(address)] = (byte)(value & 0x3F);

    private static int MapAddress(ushort address)
    {
        var index = (address - 0x3F00) & 0x001F;
        return index switch
        {
            0x10 => 0x00,
            0x14 => 0x04,
            0x18 => 0x08,
            0x1C => 0x0C,
            _ => index
        };
    }
}
