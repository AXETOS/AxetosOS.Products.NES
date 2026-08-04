using AxetosOS.Products.NES.Abstractions;
using AxetosOS.Products.NES.Cartridges;

namespace AxetosOS.Products.NES.Hardware;

public sealed record CiramDiagnosticsSnapshot(
    NametableMirroring Mirroring,
    int LowerNonZeroBytes,
    int UpperNonZeroBytes,
    uint LowerHash,
    uint UpperHash);

public sealed class CiramNametableRam : IInspectableMemoryModule, IPpuBusDevice
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
    public int CapacityBytes => _memory.Length;
    public NametableMirroring Mirroring { get; private set; }

    public void SetMirroring(NametableMirroring mirroring)
    {
        if (mirroring == NametableMirroring.FourScreen)
            throw new NotSupportedException("Four-screen nametable memory requires cartridge-provided VRAM.");
        Mirroring = mirroring;
    }

    public void PowerOn() => Array.Clear(_memory);
    public void Reset() { }

    public bool HandlesPpuAddress(ushort address) => address is >= 0x2000 and <= 0x3EFF;

    public byte PpuRead(ushort address) => _memory[MapAddress(address)];

    public void PpuWrite(ushort address, byte value) => _memory[MapAddress(address)] = value;

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

    public CiramDiagnosticsSnapshot GetDiagnostics()
    {
        var lower = _memory.AsSpan(0, 0x400);
        var upper = _memory.AsSpan(0x400, 0x400);
        return new CiramDiagnosticsSnapshot(
            Mirroring,
            CountNonZero(lower),
            CountNonZero(upper),
            Hash(lower),
            Hash(upper));
    }

    private static int CountNonZero(ReadOnlySpan<byte> data)
    {
        var count = 0;
        foreach (var value in data)
            if (value != 0) count++;
        return count;
    }

    private static uint Hash(ReadOnlySpan<byte> data)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        var hash = offsetBasis;
        foreach (var value in data)
        {
            hash ^= value;
            hash *= prime;
        }
        return hash;
    }

    private int MapAddress(ushort address)
    {
        var normalized = (address - 0x2000) & 0x0FFF;
        var table = normalized / 0x0400;
        var offset = normalized & 0x03FF;
        var physicalTable = Mirroring switch
        {
            NametableMirroring.Horizontal => table < 2 ? 0 : 1,
            NametableMirroring.Vertical => table & 1,
            NametableMirroring.SingleScreenLower => 0,
            NametableMirroring.SingleScreenUpper => 1,
            _ => throw new InvalidOperationException("Unsupported CIRAM mirroring mode.")
        };

        return (physicalTable * 0x0400) + offset;
    }
}
