using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;

/// <summary>
/// External digital stimulus used for switches, test fixtures and future host
/// connectors. The source still affects the circuit only through its pin.
/// </summary>
public sealed class DigitalSignalSource : VirtualHardwareComponent
{
    private DigitalLevel _level;

    public DigitalSignalSource(string componentId, DigitalLevel initialLevel = DigitalLevel.Low)
        : base(componentId)
    {
        if (initialLevel == DigitalLevel.Contention)
        {
            throw new ArgumentOutOfRangeException(nameof(initialLevel));
        }

        _level = initialLevel;
        Output = AddPin("OUT", PinDirection.Output);
        Output.Drive(initialLevel);
    }

    public DigitalPin Output { get; }
    public DigitalLevel Level => _level;

    public void Set(DigitalLevel level)
    {
        if (level == DigitalLevel.Contention)
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        _level = level;
        if (level == DigitalLevel.HighImpedance)
        {
            Output.Release();
        }
        else
        {
            Output.Drive(level);
        }
    }

    public override void Evaluate() => Set(_level);
}
