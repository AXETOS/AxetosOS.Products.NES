using System.Runtime.CompilerServices;

namespace AxetosOS.Products.NES.VirtualHardware.Electrical;

/// <summary>
/// Groups physical pins into a numbered digital bus without bypassing net
/// resolution. The hot path stores a concrete pin array and uses specialized
/// samplers for the bus widths exercised by the NES CPU/PPU.
/// </summary>
public sealed class DigitalBus
{
    private readonly DigitalPin[] _pins;

    public DigitalBus(string name, IReadOnlyList<DigitalPin> pins)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(pins);
        if (pins.Count is <= 0 or > 64)
            throw new ArgumentOutOfRangeException(nameof(pins), "A digital bus must contain between 1 and 64 pins.");

        Name = name;
        _pins = pins as DigitalPin[] ?? pins.ToArray();
    }

    public string Name { get; }
    public int Width => _pins.Length;
    public IReadOnlyList<DigitalPin> Pins => _pins;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TrySample(out ulong value)
    {
        return _pins.Length switch
        {
            8 => TrySample8(out value),
            9 => TrySampleFixed(9, out value),
            14 => TrySampleFixed(14, out value),
            16 => TrySampleFixed(16, out value),
            _ => TrySampleFixed(_pins.Length, out value)
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TrySample8(out ulong value)
    {
        value = 0;
        if (!Accumulate(_pins[0], 1UL, ref value) ||
            !Accumulate(_pins[1], 2UL, ref value) ||
            !Accumulate(_pins[2], 4UL, ref value) ||
            !Accumulate(_pins[3], 8UL, ref value) ||
            !Accumulate(_pins[4], 16UL, ref value) ||
            !Accumulate(_pins[5], 32UL, ref value) ||
            !Accumulate(_pins[6], 64UL, ref value) ||
            !Accumulate(_pins[7], 128UL, ref value))
        {
            value = 0;
            return false;
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TrySampleFixed(int width, out ulong value)
    {
        value = 0;
        for (var bit = 0; bit < width; bit++)
        {
            var level = _pins[bit].SampledLevel;
            if (level == DigitalLevel.High) value |= 1UL << bit;
            else if (level != DigitalLevel.Low)
            {
                value = 0;
                return false;
            }
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Accumulate(DigitalPin pin, ulong mask, ref ulong value)
    {
        var level = pin.SampledLevel;
        if (level == DigitalLevel.High)
        {
            value |= mask;
            return true;
        }
        return level == DigitalLevel.Low;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Drive(ulong value, DigitalDriveStrength strength = DigitalDriveStrength.Strong)
    {
        var pins = _pins;
        for (var bit = 0; bit < pins.Length; bit++)
            pins[bit].Drive((value & (1UL << bit)) == 0 ? DigitalLevel.Low : DigitalLevel.High, strength);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Release()
    {
        var pins = _pins;
        for (var index = 0; index < pins.Length; index++) pins[index].Release();
    }
}
