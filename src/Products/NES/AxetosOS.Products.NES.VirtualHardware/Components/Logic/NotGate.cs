using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Logic;

/// <summary>
/// A pin-driven inverter. It has no knowledge of a board or machine.
/// </summary>
public sealed class NotGate : VirtualHardwareComponent
{
    public NotGate(string componentId)
        : base(componentId)
    {
        Input = AddPin("A", PinDirection.Input);
        Output = AddPin("Y", PinDirection.Output);
    }

    public DigitalPin Input { get; }
    public DigitalPin Output { get; }

    protected override void OnInputChanges(ulong changedInputMask)
    {
        switch (Input.SampledLevel)
        {
            case DigitalLevel.Low:
                Output.Drive(DigitalLevel.High);
                break;
            case DigitalLevel.High:
                Output.Drive(DigitalLevel.Low);
                break;
            default:
                Output.Drive(DigitalLevel.Unknown);
                break;
        }
    }
}
