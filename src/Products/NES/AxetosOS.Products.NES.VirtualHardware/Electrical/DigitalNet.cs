namespace AxetosOS.Products.NES.VirtualHardware.Electrical;

/// <summary>
/// An electrical node shared by connected pins. The net, not a component,
/// resolves all active drivers into the level observed by every attached pin.
/// </summary>
public sealed class DigitalNet
{
    private readonly List<DigitalPin> _pins = [];

    public DigitalNet(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public string Name { get; }
    public IReadOnlyList<DigitalPin> Pins => _pins;
    public DigitalLevel Level { get; private set; } = DigitalLevel.Unknown;
    public ulong ResolutionCount { get; private set; }

    public void Connect(DigitalPin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);

        if (pin.Net is not null && !ReferenceEquals(pin.Net, this))
        {
            throw new InvalidOperationException($"Pin '{pin.Name}' is already connected to net '{pin.Net.Name}'.");
        }

        if (_pins.Contains(pin))
        {
            return;
        }

        _pins.Add(pin);
        pin.Net = this;
        pin.SetSampledLevel(Level);
    }

    public DigitalLevel Resolve()
    {
        var strongest = DigitalDriveStrength.Weak;
        var foundDriver = false;
        var sawLow = false;
        var sawHigh = false;
        var sawUnknown = false;

        foreach (var pin in _pins)
        {
            var level = pin.DriveLevel;
            if (level == DigitalLevel.HighImpedance)
            {
                continue;
            }

            var strength = pin.DriveStrength;
            if (!foundDriver || strength > strongest)
            {
                foundDriver = true;
                strongest = strength;
                sawLow = false;
                sawHigh = false;
                sawUnknown = false;
            }
            else if (strength < strongest)
            {
                continue;
            }

            switch (level)
            {
                case DigitalLevel.Low:
                    sawLow = true;
                    break;
                case DigitalLevel.High:
                    sawHigh = true;
                    break;
                default:
                    sawUnknown = true;
                    break;
            }
        }

        var resolved = !foundDriver
            ? DigitalLevel.Unknown
            : sawLow && sawHigh
                ? DigitalLevel.Contention
                : sawUnknown
                    ? DigitalLevel.Unknown
                    : sawHigh
                        ? DigitalLevel.High
                        : DigitalLevel.Low;

        Level = resolved;
        ResolutionCount++;

        foreach (var pin in _pins)
        {
            pin.SetSampledLevel(resolved);
        }

        return resolved;
    }
}
