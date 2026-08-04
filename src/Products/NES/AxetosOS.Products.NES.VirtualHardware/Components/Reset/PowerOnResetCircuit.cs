using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Reset;

/// <summary>
/// A small active-low reset circuit. With power absent the output is unknown;
/// with power present it drives /RESET according to the physical reset state.
/// </summary>
public sealed class PowerOnResetCircuit : VirtualHardwareComponent
{
    private bool _released;

    public PowerOnResetCircuit(string componentId)
        : base(componentId)
    {
        Vcc = AddPin("VCC", PinDirection.Input);
        ResetBar = AddPin("/RESET", PinDirection.Output);
        ResetBar.Drive(DigitalLevel.Unknown);
    }

    public DigitalPin Vcc { get; }
    public DigitalPin ResetBar { get; }
    public bool IsReleased => _released;

    public void Press() => _released = false;
    public void Release() => _released = true;

    public override void PowerOn()
    {
        _released = false;
        ResetBar.Drive(DigitalLevel.Unknown);
    }

    public override void Reset() => Press();

    public override void Evaluate()
    {
        if (Vcc.SampledLevel != DigitalLevel.High)
        {
            ResetBar.Drive(DigitalLevel.Unknown);
            return;
        }

        ResetBar.Drive(_released ? DigitalLevel.High : DigitalLevel.Low);
    }
}
