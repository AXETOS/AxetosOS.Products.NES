using AxetosOS.Products.NES.Abstractions;

namespace AxetosOS.Products.NES.Hardware;

/// <summary>
/// Inspectable primary OAM component backed by the live 256-byte storage inside
/// the RP2C02 implementation. The PPU remains the only runtime owner of writes.
/// </summary>
public sealed class Rp2C02PrimaryOamMemory : IInspectableMemoryModule
{
    public const int Size = 256;
    private readonly Rp2C02Ppu _ppu;

    public Rp2C02PrimaryOamMemory(Rp2C02Ppu ppu)
    {
        _ppu = ppu ?? throw new ArgumentNullException(nameof(ppu));
    }

    public string ModuleId => "nes.memory.rp2c02.primary-oam";
    public int CapacityBytes => Size;

    public byte ReadPhysicalByte(int offset)
    {
        if ((uint)offset >= Size)
            throw new ArgumentOutOfRangeException(nameof(offset));
        return _ppu.ReadOamByte((byte)offset);
    }

    public void CopyPhysicalBytes(Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException($"Destination must contain at least {Size} bytes.", nameof(destination));
        for (var i = 0; i < Size; i++)
            destination[i] = _ppu.ReadOamByte((byte)i);
    }

    public void PowerOn() { }
    public void Reset() { }
}

/// <summary>
/// Inspectable secondary OAM component backed by the live 32-byte sprite
/// evaluation storage. Its clear/copy behavior remains clocked by the evaluator.
/// </summary>
public sealed class Rp2C02SecondaryOamMemory : IInspectableMemoryModule
{
    public const int Size = 32;
    private readonly Rp2C02SpriteEvaluator _evaluator;

    public Rp2C02SecondaryOamMemory(Rp2C02SpriteEvaluator evaluator)
    {
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    }

    public string ModuleId => "nes.memory.rp2c02.secondary-oam";
    public int CapacityBytes => Size;

    public byte ReadPhysicalByte(int offset) => _evaluator.ReadSecondaryOamByte(offset);

    public void CopyPhysicalBytes(Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException($"Destination must contain at least {Size} bytes.", nameof(destination));
        _evaluator.SecondaryOam.CopyTo(destination);
    }

    public void PowerOn() { }
    public void Reset() { }
}
