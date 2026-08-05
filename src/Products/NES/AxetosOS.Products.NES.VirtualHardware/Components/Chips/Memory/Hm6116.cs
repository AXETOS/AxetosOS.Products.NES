using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Chips.Memory;

/// <summary>
/// Standalone HM6116-compatible 2K x 8 static RAM package.
/// Memory access occurs only through address, data and control pins.
/// </summary>
public sealed class Hm6116 : VirtualHardwareComponent, ISelectiveInputDrivenVirtualHardwareComponent
{
    private readonly byte[] _memory = new byte[2048];
    private readonly byte[] _knownMasks = new byte[2048];
    private bool _wasPowered;

    public Hm6116(string componentId) : base(componentId)
    {
        Vcc = AddPin("VCC", PinDirection.Input);
        Gnd = AddPin("GND", PinDirection.Input);
        ChipSelectBar = AddPin("CS_BAR", PinDirection.Input);
        OutputEnableBar = AddPin("OE_BAR", PinDirection.Input);
        WriteEnableBar = AddPin("WE_BAR", PinDirection.Input);

        Address = new DigitalBus(
            $"{componentId}.A",
            Enumerable.Range(0, 11).Select(bit => AddPin($"A{bit}", PinDirection.Input)).ToArray());
        Data = new DigitalBus(
            $"{componentId}.D",
            Enumerable.Range(0, 8).Select(bit => AddPin($"D{bit}", PinDirection.Bidirectional)).ToArray());
    }

    public DigitalPin Vcc { get; }
    public DigitalPin Gnd { get; }
    public DigitalPin ChipSelectBar { get; }
    public DigitalPin OutputEnableBar { get; }
    public DigitalPin WriteEnableBar { get; }
    public DigitalBus Address { get; }
    public DigitalBus Data { get; }

    public bool ShouldWakeForSampledPin(DigitalPin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);

        if (pin.Direction == PinDirection.Input) return true;

        if (Data.Pins.Contains(pin))
        {
            // D0-D7 are inputs only during a selected write. During reads the
            // resolved bus level is the RAM package's own output echo, and when
            // deselected the bus cannot affect package state.
            return ChipSelectBar.SampledLevel == DigitalLevel.Low &&
                WriteEnableBar.SampledLevel != DigitalLevel.High;
        }

        return false;
    }

    public override void PowerOn()
    {
        Array.Clear(_memory);
        Array.Clear(_knownMasks);
        _wasPowered = false;
        Data.Release();
    }

    public override void Evaluate()
    {
        var powered = IsPowered();
        if (!powered)
        {
            _wasPowered = false;
            Data.Release();
            return;
        }

        if (!_wasPowered)
        {
            // SRAM data is not retained without package power. Keep a
            // deterministic value array but mark every bit electrically unknown.
            Array.Clear(_memory);
            Array.Clear(_knownMasks);
            _wasPowered = true;
        }

        if (ChipSelectBar.SampledLevel == DigitalLevel.High)
        {
            Data.Release();
            return;
        }

        if (ChipSelectBar.SampledLevel != DigitalLevel.Low)
        {
            DriveIndeterminateSelection();
            return;
        }

        if (!Address.TrySample(out var rawAddress))
        {
            Data.Release();
            return;
        }

        var address = (int)(rawAddress & 0x07FF);

        switch (WriteEnableBar.SampledLevel)
        {
            case DigitalLevel.Low:
                Data.Release();
                CaptureWrite(address);
                return;
            case DigitalLevel.High:
                EvaluateRead(address);
                return;
            default:
                // An uncertain /WE while selected may write an arbitrary value.
                _knownMasks[address] = 0;
                Data.Release();
                return;
        }
    }

    /// <summary>Test/inspection access only; not an electrical communication path.</summary>
    public byte Inspect(int address)
    {
        ValidateAddress(address);
        return _memory[address];
    }

    /// <summary>Returns which inspected bits have a determinate stored value.</summary>
    public byte InspectKnownMask(int address)
    {
        ValidateAddress(address);
        return _knownMasks[address];
    }

    public bool TryInspect(int address, out byte value)
    {
        ValidateAddress(address);
        value = _memory[address];
        return _knownMasks[address] == byte.MaxValue;
    }

    private void CaptureWrite(int address)
    {
        var value = _memory[address];
        var knownMask = _knownMasks[address];

        for (var bit = 0; bit < Data.Width; bit++)
        {
            var mask = (byte)(1 << bit);
            switch (Data.Pins[bit].SampledLevel)
            {
                case DigitalLevel.Low:
                    value &= (byte)~mask;
                    knownMask |= mask;
                    break;
                case DigitalLevel.High:
                    value |= mask;
                    knownMask |= mask;
                    break;
                default:
                    knownMask &= (byte)~mask;
                    break;
            }
        }

        _memory[address] = value;
        _knownMasks[address] = knownMask;
    }

    private void EvaluateRead(int address)
    {
        switch (OutputEnableBar.SampledLevel)
        {
            case DigitalLevel.Low:
                DriveStoredValue(address);
                break;
            case DigitalLevel.High:
                Data.Release();
                break;
            default:
                DriveUnknownData();
                break;
        }
    }

    private void DriveStoredValue(int address)
    {
        var value = _memory[address];
        var knownMask = _knownMasks[address];
        for (var bit = 0; bit < Data.Width; bit++)
        {
            var mask = 1 << bit;
            if ((knownMask & mask) == 0)
            {
                Data.Pins[bit].Drive(DigitalLevel.Unknown);
            }
            else
            {
                Data.Pins[bit].Drive((value & mask) == 0 ? DigitalLevel.Low : DigitalLevel.High);
            }
        }
    }

    private void DriveIndeterminateSelection()
    {
        if (WriteEnableBar.SampledLevel == DigitalLevel.High &&
            OutputEnableBar.SampledLevel != DigitalLevel.High)
        {
            DriveUnknownData();
        }
        else
        {
            Data.Release();
        }
    }

    private void DriveUnknownData()
    {
        foreach (var pin in Data.Pins)
        {
            pin.Drive(DigitalLevel.Unknown);
        }
    }

    private static void ValidateAddress(int address)
    {
        if ((uint)address >= 2048)
        {
            throw new ArgumentOutOfRangeException(nameof(address));
        }
    }

    private bool IsPowered() =>
        Vcc.SampledLevel == DigitalLevel.High && Gnd.SampledLevel == DigitalLevel.Low;
}
