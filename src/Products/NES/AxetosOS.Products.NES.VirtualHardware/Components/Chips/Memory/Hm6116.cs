using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Chips.Memory;

/// <summary>
/// Standalone HM6116-compatible 2K x 8 static RAM package.
/// Memory access occurs only through address, data and control pins.
/// </summary>
public sealed class Hm6116 : VirtualHardwareComponent
{
    private readonly byte[] _memory = new byte[2048];
    private readonly byte[] _knownMasks = new byte[2048];
    private readonly ulong _powerInputMask;
    private readonly ulong _controlInputMask;
    private readonly ulong _addressInputMask;
    private readonly ulong _dataInputMask;
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

        _powerInputMask = Vcc.InputChangeMask | Gnd.InputChangeMask;
        _controlInputMask = ChipSelectBar.InputChangeMask
            | OutputEnableBar.InputChangeMask
            | WriteEnableBar.InputChangeMask;
        _addressInputMask = Address.InputChangeMask;
        _dataInputMask = Data.InputChangeMask;
    
        InitializePackageState();
    }

    public DigitalPin Vcc { get; }
    public DigitalPin Gnd { get; }
    public DigitalPin ChipSelectBar { get; }
    public DigitalPin OutputEnableBar { get; }
    public DigitalPin WriteEnableBar { get; }
    public DigitalBus Address { get; }
    public DigitalBus Data { get; }

    protected override void OnInputChanges(ulong changedInputMask) => ProcessInputChanges(changedInputMask);

    private void InitializePackageState()
    {
        InitializePowerUpState();
        _wasPowered = false;
        Data.Release();
    }

    private void ProcessInputChanges(ulong changedInputMask)
    {
        if (changedInputMask == 0) return;

        // The motherboard has already delivered every changed level.  Decide
        // inside the SRAM whether that pin can reach the storage/output stages.
        // When power or /CS disconnects the part, address/data activity is just
        // electrical activity at package pins and must not scan the 11-bit
        // address bus or 8-bit data bus.
        var powerChanged = (changedInputMask & _powerInputMask) != 0;
        if (!_wasPowered && !powerChanged) return;

        var controlChanged = (changedInputMask & _controlInputMask) != 0;
        var addressChanged = (changedInputMask & _addressInputMask) != 0;
        var dataChanged = (changedInputMask & _dataInputMask) != 0;

        if (!powerChanged && !controlChanged)
        {
            if (!addressChanged && !dataChanged) return;
            if (ChipSelectBar.SampledLevel == DigitalLevel.High) return;

            if (ChipSelectBar.SampledLevel == DigitalLevel.Low
                && WriteEnableBar.SampledLevel == DigitalLevel.High)
            {
                // Deselected output stage: address/data cannot affect RAM.
                if (OutputEnableBar.SampledLevel == DigitalLevel.High) return;
                // During a read the D pins are outputs/echoes, not storage input.
                if (dataChanged && !addressChanged) return;
            }
        }

        var powered = IsPowered();
        if (!powered)
        {
            if (_wasPowered) Data.Release();
            _wasPowered = false;
            return;
        }

        if (!_wasPowered)
        {
            // SRAM contents are unspecified after power is applied, but every
            // cell settles to a concrete zero/one level. This model chooses a
            // deterministic all-zero cold-start state.
            InitializePowerUpState();
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


    private void InitializePowerUpState()
    {
        // Real HM6116 power-up contents are unspecified. An all-zero array is
        // one electrically valid settled state and provides deterministic cold
        // boot behavior. In particular, it avoids random bytes accidentally
        // matching software warm-reset signatures before RAM initialization.
        Array.Clear(_memory);
        Array.Fill(_knownMasks, byte.MaxValue);
    }

    private void CaptureWrite(int address)
    {
        if (Data.TrySample(out var raw))
        {
            _memory[address] = (byte)raw;
            _knownMasks[address] = byte.MaxValue;
            return;
        }

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
        if (knownMask == byte.MaxValue)
        {
            Data.Drive(value);
            return;
        }

        for (var bit = 0; bit < Data.Width; bit++)
        {
            var mask = 1 << bit;
            if ((knownMask & mask) == 0)
                Data.Pins[bit].Drive(DigitalLevel.Unknown);
            else
                Data.Pins[bit].Drive((value & mask) == 0 ? DigitalLevel.Low : DigitalLevel.High);
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
