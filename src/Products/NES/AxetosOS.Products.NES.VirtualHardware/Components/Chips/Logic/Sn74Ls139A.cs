using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Chips.Logic;

/// <summary>
/// Standalone SN74LS139A dual 2-to-4 decoder/demultiplexer package.
/// Each section has an independent active-low enable and four active-low outputs.
/// </summary>
public sealed class Sn74Ls139A : VirtualHardwareComponent
{
    public Sn74Ls139A(string componentId) : base(componentId)
    {
        Vcc = AddPin("VCC", PinDirection.Input);
        Gnd = AddPin("GND", PinDirection.Input);

        Enable1Bar = AddPin("1G_BAR", PinDirection.Input);
        A1 = AddPin("1A", PinDirection.Input);
        B1 = AddPin("1B", PinDirection.Input);
        Y10Bar = AddPin("1Y0_BAR", PinDirection.Output);
        Y11Bar = AddPin("1Y1_BAR", PinDirection.Output);
        Y12Bar = AddPin("1Y2_BAR", PinDirection.Output);
        Y13Bar = AddPin("1Y3_BAR", PinDirection.Output);

        Enable2Bar = AddPin("2G_BAR", PinDirection.Input);
        A2 = AddPin("2A", PinDirection.Input);
        B2 = AddPin("2B", PinDirection.Input);
        Y20Bar = AddPin("2Y0_BAR", PinDirection.Output);
        Y21Bar = AddPin("2Y1_BAR", PinDirection.Output);
        Y22Bar = AddPin("2Y2_BAR", PinDirection.Output);
        Y23Bar = AddPin("2Y3_BAR", PinDirection.Output);
    }

    public DigitalPin Vcc { get; }
    public DigitalPin Gnd { get; }

    public DigitalPin Enable1Bar { get; }
    public DigitalPin A1 { get; }
    public DigitalPin B1 { get; }
    public DigitalPin Y10Bar { get; }
    public DigitalPin Y11Bar { get; }
    public DigitalPin Y12Bar { get; }
    public DigitalPin Y13Bar { get; }

    public DigitalPin Enable2Bar { get; }
    public DigitalPin A2 { get; }
    public DigitalPin B2 { get; }
    public DigitalPin Y20Bar { get; }
    public DigitalPin Y21Bar { get; }
    public DigitalPin Y22Bar { get; }
    public DigitalPin Y23Bar { get; }

    public override void Evaluate()
    {
        if (!IsPowered())
        {
            ReleaseOutputs();
            return;
        }

        EvaluateSection(Enable1Bar, A1, B1, [Y10Bar, Y11Bar, Y12Bar, Y13Bar]);
        EvaluateSection(Enable2Bar, A2, B2, [Y20Bar, Y21Bar, Y22Bar, Y23Bar]);
    }

    private bool IsPowered() =>
        Vcc.SampledLevel == DigitalLevel.High && Gnd.SampledLevel == DigitalLevel.Low;

    private static void EvaluateSection(
        DigitalPin enableBar,
        DigitalPin a,
        DigitalPin b,
        IReadOnlyList<DigitalPin> outputs)
    {
        if (enableBar.SampledLevel == DigitalLevel.High)
        {
            foreach (var output in outputs)
            {
                output.Drive(DigitalLevel.High);
            }

            return;
        }

        if (enableBar.SampledLevel != DigitalLevel.Low ||
            !TryBit(a.SampledLevel, out var aValue) ||
            !TryBit(b.SampledLevel, out var bValue))
        {
            foreach (var output in outputs)
            {
                output.Drive(DigitalLevel.Unknown);
            }

            return;
        }

        var selected = aValue | (bValue << 1);
        for (var index = 0; index < outputs.Count; index++)
        {
            outputs[index].Drive(index == selected ? DigitalLevel.Low : DigitalLevel.High);
        }
    }

    private static bool TryBit(DigitalLevel level, out int value)
    {
        value = level == DigitalLevel.High ? 1 : 0;
        return level is DigitalLevel.Low or DigitalLevel.High;
    }

    private void ReleaseOutputs()
    {
        Y10Bar.Release();
        Y11Bar.Release();
        Y12Bar.Release();
        Y13Bar.Release();
        Y20Bar.Release();
        Y21Bar.Release();
        Y22Bar.Release();
        Y23Bar.Release();
    }
}
