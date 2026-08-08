using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Logic;

/// <summary>
/// A pin-driven inverter. It has no knowledge of a board or machine.
/// </summary>
public sealed class NotGate : VirtualHardwareComponent, ICompiledCombinationalComponent
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
    bool ICompiledCombinationalComponent.TryEvaluateCompiledOutput(
        DigitalPin output,
        Func<DigitalPin, DigitalLevel> sampleInput,
        out CompiledDriveState drive)
    {
        if (!ReferenceEquals(output, Output))
        {
            drive = default;
            return false;
        }
        drive = new CompiledDriveState(sampleInput(Input) switch
        {
            DigitalLevel.Low => DigitalLevel.High,
            DigitalLevel.High => DigitalLevel.Low,
            _ => DigitalLevel.Unknown
        });
        return true;
    }

}
