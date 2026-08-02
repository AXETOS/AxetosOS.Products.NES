using AxetosOS.Products.NES.Abstractions;
using AxetosOS.Products.NES.Cartridges;

namespace AxetosOS.Products.NES.Hardware;

public sealed class CiramNametableRam : INesHardwareModule, IPpuBusDevice
{
    private readonly byte[] _memory = new byte[2 * 1024];

    public CiramNametableRam(NametableMirroring mirroring)
    {
        if (mirroring == NametableMirroring.FourScreen)
        {
            throw new NotSupportedException("Four-screen nametable memory requires cartridge-provided VRAM.");
        }

        Mirroring = mirroring;
    }

    public string ModuleId => "nes.memory.ciram";
    public NametableMirroring Mirroring { get; }

    public void PowerOn() => Array.Clear(_memory);
    public void Reset() { }

    public bool HandlesPpuAddress(ushort address) => address is >= 0x2000 and <= 0x3EFF;

    public byte PpuRead(ushort address) => _memory[MapAddress(address)];

    public void PpuWrite(ushort address, byte value) => _memory[MapAddress(address)] = value;

    private int MapAddress(ushort address)
    {
        var normalized = (address - 0x2000) & 0x0FFF;
        var table = normalized / 0x0400;
        var offset = normalized & 0x03FF;
        var physicalTable = Mirroring switch
        {
            NametableMirroring.Horizontal => table < 2 ? 0 : 1,
            NametableMirroring.Vertical => table & 1,
            _ => throw new InvalidOperationException("Unsupported CIRAM mirroring mode.")
        };

        return (physicalTable * 0x0400) + offset;
    }
}
