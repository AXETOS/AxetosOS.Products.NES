namespace AxetosOS.Products.NES.VirtualHardware.Electrical;

/// <summary>
/// Groups physical pins into a numbered digital bus without bypassing net
/// resolution. Bit zero is the least-significant pin.
/// </summary>
public sealed class DigitalBus
{
    private readonly IReadOnlyList<DigitalPin> _pins;

    public DigitalBus(string name, IReadOnlyList<DigitalPin> pins)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(pins);
        if (pins.Count is <= 0 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(pins), "A digital bus must contain between 1 and 64 pins.");
        }

        Name = name;
        _pins = pins;
    }

    public string Name { get; }
    public int Width => _pins.Count;
    public IReadOnlyList<DigitalPin> Pins => _pins;

    public bool TrySample(out ulong value)
    {
        value = 0;
        for (var bit = 0; bit < _pins.Count; bit++)
        {
            switch (_pins[bit].SampledLevel)
            {
                case DigitalLevel.Low:
                    break;
                case DigitalLevel.High:
                    value |= 1UL << bit;
                    break;
                default:
                    value = 0;
                    return false;
            }
        }

        return true;
    }

    public void Drive(ulong value, DigitalDriveStrength strength = DigitalDriveStrength.Strong)
    {
        for (var bit = 0; bit < _pins.Count; bit++)
        {
            _pins[bit].Drive((value & (1UL << bit)) == 0 ? DigitalLevel.Low : DigitalLevel.High, strength);
        }
    }

    public void Release()
    {
        foreach (var pin in _pins)
        {
            pin.Release();
        }
    }
}
