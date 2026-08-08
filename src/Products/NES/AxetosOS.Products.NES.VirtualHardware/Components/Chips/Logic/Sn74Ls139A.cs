using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Chips.Logic;

/// <summary>
/// Standalone SN74LS139A dual 2-to-4 decoder/demultiplexer package.
/// Each section has an independent active-low enable and four active-low outputs.
/// </summary>
public sealed class Sn74Ls139A : VirtualHardwareComponent, ICompiledCombinationalComponent
{
    private readonly DigitalPin[] _section1Outputs;
    private readonly DigitalPin[] _section2Outputs;
    private readonly ulong _powerInputMask;
    private readonly ulong _section1EnableMask;
    private readonly ulong _section1DataMask;
    private readonly ulong _section2EnableMask;
    private readonly ulong _section2DataMask;
    private bool _packagePowered;

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

        _section1Outputs = [Y10Bar, Y11Bar, Y12Bar, Y13Bar];
        _section2Outputs = [Y20Bar, Y21Bar, Y22Bar, Y23Bar];
        _powerInputMask = Vcc.InputChangeMask | Gnd.InputChangeMask;
        _section1EnableMask = Enable1Bar.InputChangeMask;
        _section1DataMask = A1.InputChangeMask | B1.InputChangeMask;
        _section2EnableMask = Enable2Bar.InputChangeMask;
        _section2DataMask = A2.InputChangeMask | B2.InputChangeMask;

        A1.SetOwnerWakeEnabled(false);
        B1.SetOwnerWakeEnabled(false);
        A2.SetOwnerWakeEnabled(false);
        B2.SetOwnerWakeEnabled(false);
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

    private void RefreshDataWakeState()
    {
        var section1Enabled = _packagePowered && Enable1Bar.SampledLevel == DigitalLevel.Low;
        var section2Enabled = _packagePowered && Enable2Bar.SampledLevel == DigitalLevel.Low;
        A1.SetOwnerWakeEnabled(section1Enabled);
        B1.SetOwnerWakeEnabled(section1Enabled);
        A2.SetOwnerWakeEnabled(section2Enabled);
        B2.SetOwnerWakeEnabled(section2Enabled);
    }

    protected override void OnInputChanges(ulong changedInputMask)
    {
        var powerChanged = (changedInputMask & _powerInputMask) != 0;
        if (!_packagePowered && !powerChanged) return;

        if (!IsPowered())
        {
            if (_packagePowered) ReleaseOutputs();
            _packagePowered = false;
            RefreshDataWakeState();
            return;
        }

        if (!_packagePowered)
        {
            _packagePowered = true;
            powerChanged = true;
        }

        if (powerChanged || (changedInputMask & (_section1EnableMask | _section2EnableMask)) != 0)
            RefreshDataWakeState();

        // Each half is its own decoder.  A/B pins may toggle continuously while
        // /G is High; the package pins still receive those levels, but that half
        // has no internal/output work until its enable becomes active again.
        var section1EnableChanged = (changedInputMask & _section1EnableMask) != 0;
        var section1DataChanged = (changedInputMask & _section1DataMask) != 0;
        if (powerChanged || section1EnableChanged ||
            (section1DataChanged && Enable1Bar.SampledLevel != DigitalLevel.High))
        {
            EvaluateSection(Enable1Bar, A1, B1, _section1Outputs);
        }

        var section2EnableChanged = (changedInputMask & _section2EnableMask) != 0;
        var section2DataChanged = (changedInputMask & _section2DataMask) != 0;
        if (powerChanged || section2EnableChanged ||
            (section2DataChanged && Enable2Bar.SampledLevel != DigitalLevel.High))
        {
            EvaluateSection(Enable2Bar, A2, B2, _section2Outputs);
        }
    }

    private bool IsPowered() =>
        Vcc.SampledLevel == DigitalLevel.High && Gnd.SampledLevel == DigitalLevel.Low;

    private static void EvaluateSection(
        DigitalPin enableBar,
        DigitalPin a,
        DigitalPin b,
        DigitalPin[] outputs)
    {
        if (enableBar.SampledLevel == DigitalLevel.High)
        {
            outputs[0].Drive(DigitalLevel.High);
            outputs[1].Drive(DigitalLevel.High);
            outputs[2].Drive(DigitalLevel.High);
            outputs[3].Drive(DigitalLevel.High);
            return;
        }

        if (enableBar.SampledLevel != DigitalLevel.Low ||
            !TryBit(a.SampledLevel, out var aValue) ||
            !TryBit(b.SampledLevel, out var bValue))
        {
            outputs[0].Drive(DigitalLevel.Unknown);
            outputs[1].Drive(DigitalLevel.Unknown);
            outputs[2].Drive(DigitalLevel.Unknown);
            outputs[3].Drive(DigitalLevel.Unknown);
            return;
        }

        var selected = aValue | (bValue << 1);
        outputs[0].Drive(selected == 0 ? DigitalLevel.Low : DigitalLevel.High);
        outputs[1].Drive(selected == 1 ? DigitalLevel.Low : DigitalLevel.High);
        outputs[2].Drive(selected == 2 ? DigitalLevel.Low : DigitalLevel.High);
        outputs[3].Drive(selected == 3 ? DigitalLevel.Low : DigitalLevel.High);
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

    bool ICompiledCombinationalComponent.TryEvaluateCompiledOutput(
        DigitalPin output,
        Func<DigitalPin, DigitalLevel> sampleInput,
        out CompiledDriveState drive)
    {
        DigitalPin enable;
        DigitalPin a;
        DigitalPin b;
        int selectedOutput;
        if (ReferenceEquals(output, Y10Bar)) selectedOutput = 0;
        else if (ReferenceEquals(output, Y11Bar)) selectedOutput = 1;
        else if (ReferenceEquals(output, Y12Bar)) selectedOutput = 2;
        else if (ReferenceEquals(output, Y13Bar)) selectedOutput = 3;
        else selectedOutput = -1;

        if (selectedOutput >= 0)
        {
            enable = Enable1Bar;
            a = A1;
            b = B1;
        }
        else
        {
            if (ReferenceEquals(output, Y20Bar)) selectedOutput = 0;
            else if (ReferenceEquals(output, Y21Bar)) selectedOutput = 1;
            else if (ReferenceEquals(output, Y22Bar)) selectedOutput = 2;
            else if (ReferenceEquals(output, Y23Bar)) selectedOutput = 3;
            else
            {
                drive = default;
                return false;
            }
            enable = Enable2Bar;
            a = A2;
            b = B2;
        }

        var enabled = sampleInput(enable);
        if (enabled == DigitalLevel.High)
        {
            drive = new CompiledDriveState(DigitalLevel.High);
            return true;
        }
        if (enabled != DigitalLevel.Low)
        {
            drive = new CompiledDriveState(DigitalLevel.Unknown);
            return true;
        }

        var av = sampleInput(a);
        var bv = sampleInput(b);
        if (av is not (DigitalLevel.Low or DigitalLevel.High) || bv is not (DigitalLevel.Low or DigitalLevel.High))
        {
            drive = new CompiledDriveState(DigitalLevel.Unknown);
            return true;
        }

        var selected = (av == DigitalLevel.High ? 1 : 0) | (bv == DigitalLevel.High ? 2 : 0);
        drive = new CompiledDriveState(selected == selectedOutput ? DigitalLevel.Low : DigitalLevel.High);
        return true;
    }


}
