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
    private readonly bool _allOutputCapable;

    public DigitalBus(string name, IReadOnlyList<DigitalPin> pins)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(pins);
        if (pins.Count is <= 0 or > 64)
            throw new ArgumentOutOfRangeException(nameof(pins), "A digital bus must contain between 1 and 64 pins.");

        Name = name;
        _pins = pins as DigitalPin[] ?? pins.ToArray();
        ulong inputChangeMask = 0;
        var allOutputCapable = true;
        for (var index = 0; index < _pins.Length; index++)
        {
            inputChangeMask |= _pins[index].InputChangeMask;
            allOutputCapable &= _pins[index].IsOutputCapable;
        }
        InputChangeMask = inputChangeMask;
        _allOutputCapable = allOutputCapable;
    }

    public string Name { get; }
    public int Width => _pins.Length;
    public IReadOnlyList<DigitalPin> Pins => _pins;
    internal ulong InputChangeMask { get; }


    /// <summary>
    /// Changes only whether accepted pin transitions wake the owning package.
    /// Electrical levels continue to be delivered and retained on every pin.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetOwnerWakeEnabled(bool enabled)
    {
        var pins = _pins;
        for (var index = 0; index < pins.Length; index++)
        {
            if (pins[index].IsInputCapable) pins[index].SetOwnerWakeEnabled(enabled);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TrySample(out ulong value)
    {
        return _pins.Length switch
        {
            6 => TrySample6(out value),
            8 => TrySample8(out value),
            11 => TrySample11(out value),
            16 => TrySample16(out value),
            _ => TrySampleFixed(_pins.Length, out value)
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TrySample6(out ulong value)
    {
        value = 0;
        if (!Accumulate(_pins[0], 1UL, ref value) ||
            !Accumulate(_pins[1], 2UL, ref value) ||
            !Accumulate(_pins[2], 4UL, ref value) ||
            !Accumulate(_pins[3], 8UL, ref value) ||
            !Accumulate(_pins[4], 16UL, ref value) ||
            !Accumulate(_pins[5], 32UL, ref value))
        {
            value = 0;
            return false;
        }
        return true;
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
    private bool TrySample11(out ulong value)
    {
        value = 0;
        if (!Accumulate(_pins[0], 1UL, ref value) ||
            !Accumulate(_pins[1], 2UL, ref value) ||
            !Accumulate(_pins[2], 4UL, ref value) ||
            !Accumulate(_pins[3], 8UL, ref value) ||
            !Accumulate(_pins[4], 16UL, ref value) ||
            !Accumulate(_pins[5], 32UL, ref value) ||
            !Accumulate(_pins[6], 64UL, ref value) ||
            !Accumulate(_pins[7], 128UL, ref value) ||
            !Accumulate(_pins[8], 256UL, ref value) ||
            !Accumulate(_pins[9], 512UL, ref value) ||
            !Accumulate(_pins[10], 1024UL, ref value))
        {
            value = 0;
            return false;
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TrySample16(out ulong value)
    {
        value = 0;
        if (!Accumulate(_pins[0], 1UL, ref value) ||
            !Accumulate(_pins[1], 2UL, ref value) ||
            !Accumulate(_pins[2], 4UL, ref value) ||
            !Accumulate(_pins[3], 8UL, ref value) ||
            !Accumulate(_pins[4], 16UL, ref value) ||
            !Accumulate(_pins[5], 32UL, ref value) ||
            !Accumulate(_pins[6], 64UL, ref value) ||
            !Accumulate(_pins[7], 128UL, ref value) ||
            !Accumulate(_pins[8], 256UL, ref value) ||
            !Accumulate(_pins[9], 512UL, ref value) ||
            !Accumulate(_pins[10], 1024UL, ref value) ||
            !Accumulate(_pins[11], 2048UL, ref value) ||
            !Accumulate(_pins[12], 4096UL, ref value) ||
            !Accumulate(_pins[13], 8192UL, ref value) ||
            !Accumulate(_pins[14], 16384UL, ref value) ||
            !Accumulate(_pins[15], 32768UL, ref value))
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
        if (!_allOutputCapable)
            throw new InvalidOperationException($"Bus '{Name}' contains input-only pins and cannot drive.");

        var pins = _pins;
        if (strength == DigitalDriveStrength.Strong)
        {
            if (pins.Length == 8)
            {
                Drive8Strong(value);
                return;
            }

            var remaining = value;
            for (var bit = 0; bit < pins.Length; bit++)
            {
                pins[bit].DriveBinaryStrong((remaining & 1UL) == 0 ? DigitalLevel.Low : DigitalLevel.High);
                remaining >>= 1;
            }
            return;
        }

        for (var bit = 0; bit < pins.Length; bit++)
            pins[bit].Drive((value & (1UL << bit)) == 0 ? DigitalLevel.Low : DigitalLevel.High, strength);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Drive8Strong(ulong value)
    {
        var pins = _pins;
        pins[0].DriveBinaryStrong((value & 0x01UL) == 0 ? DigitalLevel.Low : DigitalLevel.High);
        pins[1].DriveBinaryStrong((value & 0x02UL) == 0 ? DigitalLevel.Low : DigitalLevel.High);
        pins[2].DriveBinaryStrong((value & 0x04UL) == 0 ? DigitalLevel.Low : DigitalLevel.High);
        pins[3].DriveBinaryStrong((value & 0x08UL) == 0 ? DigitalLevel.Low : DigitalLevel.High);
        pins[4].DriveBinaryStrong((value & 0x10UL) == 0 ? DigitalLevel.Low : DigitalLevel.High);
        pins[5].DriveBinaryStrong((value & 0x20UL) == 0 ? DigitalLevel.Low : DigitalLevel.High);
        pins[6].DriveBinaryStrong((value & 0x40UL) == 0 ? DigitalLevel.Low : DigitalLevel.High);
        pins[7].DriveBinaryStrong((value & 0x80UL) == 0 ? DigitalLevel.Low : DigitalLevel.High);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Release()
    {
        if (!_allOutputCapable)
            throw new InvalidOperationException($"Bus '{Name}' contains input-only pins and cannot drive.");

        var pins = _pins;
        if (pins.Length == 8)
        {
            Release8();
            return;
        }

        for (var index = 0; index < pins.Length; index++) pins[index].ReleaseValidatedOutput();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Release8()
    {
        var pins = _pins;
        pins[0].ReleaseValidatedOutput();
        pins[1].ReleaseValidatedOutput();
        pins[2].ReleaseValidatedOutput();
        pins[3].ReleaseValidatedOutput();
        pins[4].ReleaseValidatedOutput();
        pins[5].ReleaseValidatedOutput();
        pins[6].ReleaseValidatedOutput();
        pins[7].ReleaseValidatedOutput();
    }
}
