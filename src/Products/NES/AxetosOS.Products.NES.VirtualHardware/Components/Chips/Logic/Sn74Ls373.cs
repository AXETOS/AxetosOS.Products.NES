using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Chips.Logic;

/// <summary>
/// Standalone SN74LS373 octal transparent D-type latch with active-low
/// three-state output enable.
/// </summary>
public sealed class Sn74Ls373 : VirtualHardwareComponent
{
    private byte _latchedValue;

    public Sn74Ls373(string componentId) : base(componentId)
    {
        Vcc = AddPin("VCC", PinDirection.Input);
        Gnd = AddPin("GND", PinDirection.Input);
        LatchEnable = AddPin("LE", PinDirection.Input);
        OutputEnableBar = AddPin("OE_BAR", PinDirection.Input);

        D = new DigitalBus(
            $"{componentId}.D",
            Enumerable.Range(0, 8).Select(bit => AddPin($"D{bit}", PinDirection.Input)).ToArray());
        Q = new DigitalBus(
            $"{componentId}.Q",
            Enumerable.Range(0, 8).Select(bit => AddPin($"Q{bit}", PinDirection.Output)).ToArray());
    }

    public DigitalPin Vcc { get; }
    public DigitalPin Gnd { get; }
    public DigitalPin LatchEnable { get; }
    public DigitalPin OutputEnableBar { get; }
    public DigitalBus D { get; }
    public DigitalBus Q { get; }
    public byte LatchedValue => _latchedValue;

    public override void PowerOn() => _latchedValue = 0;

    public override void Reset() => _latchedValue = 0;

    public override void Evaluate()
    {
        if (!IsPowered())
        {
            Q.Release();
            return;
        }

        if (LatchEnable.SampledLevel == DigitalLevel.High && D.TrySample(out var data))
        {
            _latchedValue = (byte)data;
        }

        switch (OutputEnableBar.SampledLevel)
        {
            case DigitalLevel.Low:
                Q.Drive(_latchedValue);
                break;
            case DigitalLevel.High:
                Q.Release();
                break;
            default:
                foreach (var pin in Q.Pins)
                {
                    pin.Drive(DigitalLevel.Unknown);
                }
                break;
        }
    }

    private bool IsPowered() =>
        Vcc.SampledLevel == DigitalLevel.High && Gnd.SampledLevel == DigitalLevel.Low;
}
