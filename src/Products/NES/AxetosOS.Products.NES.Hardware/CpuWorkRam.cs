using AxetosOS.Products.NES.Abstractions;

namespace AxetosOS.Products.NES.Hardware;

public sealed class CpuWorkRam : INesHardwareModule, ICpuBusDevice
{
    public const int Size = 2 * 1024;
    private readonly byte[] _memory = new byte[Size];

    public string ModuleId => "nes.memory.cpu-work-ram";

    public void PowerOn() => Array.Clear(_memory);

    public void Reset()
    {
        // The physical RAM contents are not cleared by a console reset.
    }

    public bool HandlesCpuAddress(ushort address) => address <= 0x1FFF;

    public byte CpuRead(ushort address) => _memory[address & 0x07FF];

    public void CpuWrite(ushort address, byte value) => _memory[address & 0x07FF] = value;

    public ReadOnlyMemory<byte> Snapshot() => _memory.ToArray();
}
