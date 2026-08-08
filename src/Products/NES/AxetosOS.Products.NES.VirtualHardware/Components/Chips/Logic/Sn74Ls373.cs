using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Chips.Logic;

/// <summary>
/// Standalone SN74LS373 octal transparent D-type latch with active-low
/// three-state output enable. Storage and output behavior are determined only
/// by package power and pin levels.
/// </summary>
public sealed class Sn74Ls373 : VirtualHardwareComponent, ICompiledBitProjectionComponent
{
    private byte _latchedValue;
    private byte _latchedKnownMask;
    private bool _wasPowered;
    private bool _outputStateValid;
    private byte _outputValue;
    private byte _outputKnownMask;
    private DigitalLevel _outputEnableState = DigitalLevel.Unknown;
    private readonly ulong _powerInputMask;
    private readonly ulong _latchEnableInputMask;
    private readonly ulong _outputEnableInputMask;
    private readonly ulong _dataInputMask;

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

        _powerInputMask = Vcc.InputChangeMask | Gnd.InputChangeMask;
        _latchEnableInputMask = LatchEnable.InputChangeMask;
        _outputEnableInputMask = OutputEnableBar.InputChangeMask;
        _dataInputMask = D.InputChangeMask;

        D.SetOwnerWakeEnabled(false);
    
        InitializePackageState();
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

    private void RefreshDataWakeState() =>
        D.SetOwnerWakeEnabled(_wasPowered && LatchEnable.SampledLevel == DigitalLevel.High);

    private void InitializePackageState()
    {
        _latchedValue = 0;
        _latchedKnownMask = 0;
        _wasPowered = false;
        _outputStateValid = false;
        _outputEnableState = DigitalLevel.High;
        RefreshDataWakeState();
        Q.Release();
    }

    protected override void OnInputChanges(ulong changedInputMask) => ProcessInputChanges(changedInputMask);

    private void ProcessInputChanges(ulong changedInputMask)
    {
        if (changedInputMask == 0) return;

        var powerChanged = (changedInputMask & _powerInputMask) != 0;
        if (!_wasPowered && !powerChanged) return;

        var latchEnableChanged = (changedInputMask & _latchEnableInputMask) != 0;
        var outputEnableChanged = (changedInputMask & _outputEnableInputMask) != 0;
        var dataChanged = (changedInputMask & _dataInputMask) != 0;
        if (!powerChanged && !latchEnableChanged && !outputEnableChanged && !dataChanged) return;

        // The transparent input stage is disconnected from storage while LE is
        // Low. D0-D7 still receive their electrical levels, but changes there
        // cannot alter storage or Q until LE becomes High again.
        if (!powerChanged && !latchEnableChanged && !outputEnableChanged
            && dataChanged && LatchEnable.SampledLevel == DigitalLevel.Low)
        {
            return;
        }

        if (powerChanged)
        {
            if (!IsPowered())
            {
                _wasPowered = false;
                if (_outputEnableState != DigitalLevel.High || _outputStateValid) Q.Release();
                _outputEnableState = DigitalLevel.High;
                _outputStateValid = false;
                RefreshDataWakeState();
                return;
            }

            if (!_wasPowered)
            {
                // A real unclocked latch has no guaranteed power-up contents.
                _latchedValue = 0;
                _latchedKnownMask = 0;
                _wasPowered = true;
            }
        }

        if (powerChanged || latchEnableChanged) RefreshDataWakeState();

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
                if (_outputEnableState != DigitalLevel.High) Q.Release();
                _outputEnableState = DigitalLevel.High;
                _outputStateValid = false;
                break;
            default:
                if (_outputEnableState != DigitalLevel.Unknown || _outputStateValid) DriveUnknownOutputs();
                _outputEnableState = DigitalLevel.Unknown;
                _outputStateValid = false;
                break;
        }
    }

    private void CaptureInputs()
    {
        if (D.TrySample(out var raw))
        {
            _latchedValue = (byte)raw;
            _latchedKnownMask = byte.MaxValue;
            return;
        }

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
        if (_outputStateValid
            && _outputEnableState == DigitalLevel.Low
            && _outputValue == _latchedValue
            && _outputKnownMask == _latchedKnownMask)
        {
            return;
        }

        if (_latchedKnownMask == byte.MaxValue)
        {
            Q.Drive(_latchedValue);
        }
        else
        {
            for (var bit = 0; bit < Q.Width; bit++)
            {
                var mask = 1 << bit;
                if ((_latchedKnownMask & mask) == 0)
                    Q.Pins[bit].Drive(DigitalLevel.Unknown);
                else
                    Q.Pins[bit].Drive((_latchedValue & mask) == 0 ? DigitalLevel.Low : DigitalLevel.High);
            }
        }

        _outputStateValid = true;
        _outputEnableState = DigitalLevel.Low;
        _outputValue = _latchedValue;
        _outputKnownMask = _latchedKnownMask;
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

    bool ICompiledBitProjectionComponent.TryTraceCompiledOutput(
        DigitalPin output,
        Func<DigitalPin, DigitalLevel> sampleStaticInput,
        out DigitalPin input)
    {
        for (var bit = 0; bit < Q.Width; bit++)
        {
            if (!ReferenceEquals(output, Q.Pins[bit])) continue;
            if (sampleStaticInput(OutputEnableBar) != DigitalLevel.Low)
            {
                input = null!;
                return false;
            }
            input = D.Pins[bit];
            return true;
        }

        input = null!;
        return false;
    }


}
