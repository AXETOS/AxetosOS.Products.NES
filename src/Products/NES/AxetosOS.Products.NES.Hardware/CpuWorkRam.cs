using AxetosOS.Products.NES.Abstractions;

namespace AxetosOS.Products.NES.Hardware;

public sealed record CpuWorkRamWriteTraceEvent(
    ushort CpuAddress,
    ushort PhysicalAddress,
    byte PreviousValue,
    byte Value);

public sealed class CpuWorkRam : IInspectableMemoryModule, ICpuBusDevice
{
    public const int Size = 2 * 1024;
    private readonly byte[] _memory = new byte[Size];

    public string ModuleId => "nes.memory.cpu-work-ram";
    public int CapacityBytes => Size;
    public bool DiagnosticsTraceEnabled { get; set; }
    public event Action<CpuWorkRamWriteTraceEvent>? Written;

    public void PowerOn() => Array.Clear(_memory);

    public void Reset()
    {
        // The physical RAM contents are not cleared by a console reset.
    }

    public bool HandlesCpuAddress(ushort address) => address <= 0x1FFF;

    public byte CpuRead(ushort address) => _memory[address & 0x07FF];

    public void CpuWrite(ushort address, byte value)
    {
        var physicalAddress = (ushort)(address & 0x07FF);
        var previousValue = _memory[physicalAddress];
        _memory[physicalAddress] = value;
        if (DiagnosticsTraceEnabled)
            Written?.Invoke(new CpuWorkRamWriteTraceEvent(address, physicalAddress, previousValue, value));
    }

    public byte ReadPhysicalByte(int offset)
    {
        if ((uint)offset >= (uint)_memory.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        return _memory[offset];
    }

    public void CopyPhysicalBytes(Span<byte> destination)
    {
        if (destination.Length < _memory.Length)
            throw new ArgumentException($"Destination must contain at least {_memory.Length} bytes.", nameof(destination));
        _memory.AsSpan().CopyTo(destination);
    }

    public ReadOnlyMemory<byte> Snapshot() => _memory.ToArray();
}
