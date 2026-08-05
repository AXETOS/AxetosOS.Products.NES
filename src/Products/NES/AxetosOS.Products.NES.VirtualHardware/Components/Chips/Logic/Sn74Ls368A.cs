using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Chips.Logic;

/// <summary>
/// Standalone SN74LS368A hex inverting bus driver with three-state outputs.
/// Channels 1-4 share 1G_BAR; channels 5-6 share 2G_BAR.
/// </summary>
public sealed class Sn74Ls368A : VirtualHardwareComponent, IInputDrivenVirtualHardwareComponent
{
    public Sn74Ls368A(string componentId) : base(componentId)
    {
        Vcc = AddPin("VCC", PinDirection.Input);
        Gnd = AddPin("GND", PinDirection.Input);
        Enable1Bar = AddPin("1G_BAR", PinDirection.Input);
        Enable2Bar = AddPin("2G_BAR", PinDirection.Input);

        A = Enumerable.Range(1, 6)
            .Select(channel => AddPin($"{channel}A", PinDirection.Input))
            .ToArray();
        YBar = Enumerable.Range(1, 6)
            .Select(channel => AddPin($"{channel}Y_BAR", PinDirection.Output))
            .ToArray();
    }

    public DigitalPin Vcc { get; }
    public DigitalPin Gnd { get; }
    public DigitalPin Enable1Bar { get; }
    public DigitalPin Enable2Bar { get; }
    public IReadOnlyList<DigitalPin> A { get; }
    public IReadOnlyList<DigitalPin> YBar { get; }

    public override void Evaluate()
    {
        if (!IsPowered())
        {
            foreach (var output in YBar)
            {
                output.Release();
            }

            return;
        }

        EvaluateGroup(Enable1Bar, 0, 4);
        EvaluateGroup(Enable2Bar, 4, 2);
    }

    private void EvaluateGroup(DigitalPin enableBar, int start, int count)
    {
        for (var offset = 0; offset < count; offset++)
        {
            var index = start + offset;
            if (enableBar.SampledLevel == DigitalLevel.High)
            {
                YBar[index].Release();
                continue;
            }

            if (enableBar.SampledLevel != DigitalLevel.Low)
            {
                YBar[index].Drive(DigitalLevel.Unknown);
                continue;
            }

            YBar[index].Drive(A[index].SampledLevel switch
            {
                DigitalLevel.Low => DigitalLevel.High,
                DigitalLevel.High => DigitalLevel.Low,
                _ => DigitalLevel.Unknown
            });
        }
    }

    private bool IsPowered() =>
        Vcc.SampledLevel == DigitalLevel.High && Gnd.SampledLevel == DigitalLevel.Low;
}
