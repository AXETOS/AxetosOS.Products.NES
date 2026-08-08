using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Passives;

/// <summary>
/// Behavioral digital resistor. It samples its rail terminal and weakly drives
/// the node terminal, allowing a strong chip output to override it.
/// </summary>
public sealed class PullResistor : VirtualHardwareComponent, ICompiledCombinationalComponent
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

    bool ICompiledCombinationalComponent.TryEvaluateCompiledOutput(
        DigitalPin output,
        Func<DigitalPin, DigitalLevel> sampleInput,
        out CompiledDriveState drive)
    {
        if (!ReferenceEquals(output, Node))
        {
            drive = default;
            return false;
        }
        var level = sampleInput(Rail);
        drive = new CompiledDriveState(
            level is DigitalLevel.Low or DigitalLevel.High ? level : DigitalLevel.HighImpedance,
            DigitalDriveStrength.Weak);
        return true;
    }

}
