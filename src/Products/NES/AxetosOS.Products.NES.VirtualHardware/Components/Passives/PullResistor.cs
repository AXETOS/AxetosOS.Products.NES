using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Passives;

/// <summary>
/// Behavioral digital resistor. It samples its rail terminal and weakly drives
/// the node terminal, allowing a strong chip output to override it.
/// </summary>
public sealed class PullResistor : VirtualHardwareComponent
{
    public PullResistor(string componentId)
        : base(componentId)
    {
        Rail = AddPin("RAIL", PinDirection.Input);
        Node = AddPin("NODE", PinDirection.Output);
    }

    public DigitalPin Rail { get; }
    public DigitalPin Node { get; }

    protected override void OnInputChanges(ulong changedInputMask)
    {
        switch (Rail.SampledLevel)
        {
            case DigitalLevel.Low:
                Node.Drive(DigitalLevel.Low, DigitalDriveStrength.Weak);
                break;
            case DigitalLevel.High:
                Node.Drive(DigitalLevel.High, DigitalDriveStrength.Weak);
                break;
            default:
                Node.Release();
                break;
        }
    }
}
