using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Chips.Logic;

/// <summary>
/// Standalone SN74LS373 octal transparent D-type latch with active-low
/// three-state output enable. Storage and output behavior are determined only
/// by package power and pin levels.
/// </summary>
public sealed class Sn74Ls373 : VirtualHardwareComponent
{
    private byte _latchedValue;
    private byte _latchedKnownMask;
    private bool _wasPowered;

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
    public byte LatchedKnownMask => _latchedKnownMask;
    public bool IsLatchedValueKnown => _latchedKnownMask == byte.MaxValue;

    public override void PowerOn()
    {
        _latchedValue = 0;
        _latchedKnownMask = 0;
        _wasPowered = false;
        Q.Release();
    }

    public override void Evaluate()
    {
        var powered = IsPowered();
        if (!powered)
        {
            _wasPowered = false;
            Q.Release();
            return;
        }

        if (!_wasPowered)
        {
            // A real unclocked latch has no guaranteed power-up contents.
            _latchedValue = 0;
            _latchedKnownMask = 0;
            _wasPowered = true;
        }

        if (LatchEnable.SampledLevel == DigitalLevel.High)
        {
            CaptureInputs();
        }
        else if (LatchEnable.SampledLevel is not DigitalLevel.Low)
        {
            // An indeterminate LE can change any storage node.
            _latchedKnownMask = 0;
        }

        switch (OutputEnableBar.SampledLevel)
        {
            case DigitalLevel.Low:
                DriveLatchedValue();
                break;
            case DigitalLevel.High:
                Q.Release();
                break;
            default:
                DriveUnknownOutputs();
                break;
        }
    }

    private void CaptureInputs()
    {
        for (var bit = 0; bit < D.Width; bit++)
        {
            var mask = (byte)(1 << bit);
            switch (D.Pins[bit].SampledLevel)
            {
                case DigitalLevel.Low:
                    _latchedValue &= (byte)~mask;
                    _latchedKnownMask |= mask;
                    break;
                case DigitalLevel.High:
                    _latchedValue |= mask;
                    _latchedKnownMask |= mask;
                    break;
                default:
                    _latchedKnownMask &= (byte)~mask;
                    break;
            }
        }
    }

    private void DriveLatchedValue()
    {
        for (var bit = 0; bit < Q.Width; bit++)
        {
            var mask = 1 << bit;
            if ((_latchedKnownMask & mask) == 0)
            {
                Q.Pins[bit].Drive(DigitalLevel.Unknown);
            }
            else
            {
                Q.Pins[bit].Drive((_latchedValue & mask) == 0 ? DigitalLevel.Low : DigitalLevel.High);
            }
        }
    }

    private void DriveUnknownOutputs()
    {
        foreach (var pin in Q.Pins)
        {
            pin.Drive(DigitalLevel.Unknown);
        }
    }

    private bool IsPowered() =>
        Vcc.SampledLevel == DigitalLevel.High && Gnd.SampledLevel == DigitalLevel.Low;
}
