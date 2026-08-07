using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Memory;

/// <summary>
/// Pin-driven asynchronous static RAM. It knows only its address/data pins and
/// active-low chip-select, output-enable and write-enable controls.
/// </summary>
public sealed class StaticRamChip : VirtualHardwareComponent
{
    private readonly byte[] _storage;
    private readonly ulong _addressInputMask;
    private readonly ulong _dataInputMask;
    private readonly ulong _controlInputMask;

    public StaticRamChip(string componentId, int addressWidth)
        : base(componentId)
    {
        if (addressWidth is <= 0 or > 24)
        {
            throw new ArgumentOutOfRangeException(nameof(addressWidth));
        }

        _storage = new byte[1 << addressWidth];

        var addressPins = new DigitalPin[addressWidth];
        for (var bit = 0; bit < addressWidth; bit++)
        {
            addressPins[bit] = AddPin($"A{bit}", PinDirection.Input);
        }

        var dataPins = new DigitalPin[8];
        for (var bit = 0; bit < dataPins.Length; bit++)
        {
            dataPins[bit] = AddPin($"D{bit}", PinDirection.Bidirectional);
        }

        Address = new DigitalBus($"{componentId}.A", addressPins);
        Data = new DigitalBus($"{componentId}.D", dataPins);
        ChipSelectBar = AddPin("/CS", PinDirection.Input);
        OutputEnableBar = AddPin("/OE", PinDirection.Input);
        WriteEnableBar = AddPin("/WE", PinDirection.Input);
        _addressInputMask = Address.InputChangeMask;
        _dataInputMask = Data.InputChangeMask;
        _controlInputMask = ChipSelectBar.InputChangeMask
            | OutputEnableBar.InputChangeMask
            | WriteEnableBar.InputChangeMask;
    
        InitializePackageState();
    }

    public int Capacity => _storage.Length;
    public DigitalBus Address { get; }
    public DigitalBus Data { get; }
    public DigitalPin ChipSelectBar { get; }
    public DigitalPin OutputEnableBar { get; }
    public DigitalPin WriteEnableBar { get; }
    public ulong WriteCount { get; private set; }

    public byte Inspect(int address) => _storage[address];

    private void InitializePackageState()
    {
        Array.Clear(_storage);
        WriteCount = 0;
        Data.Release();
    }

    protected override void OnInputChanges(ulong changedInputMask)
    {
        var controlChanged = (changedInputMask & _controlInputMask) != 0;
        var addressChanged = (changedInputMask & _addressInputMask) != 0;
        var dataChanged = (changedInputMask & _dataInputMask) != 0;
        if (!controlChanged && !addressChanged && !dataChanged) return;

        if (!controlChanged)
        {
            if (ChipSelectBar.SampledLevel == DigitalLevel.High) return;
            if (ChipSelectBar.SampledLevel == DigitalLevel.Low
                && WriteEnableBar.SampledLevel == DigitalLevel.High)
            {
                if (OutputEnableBar.SampledLevel == DigitalLevel.High) return;
                if (dataChanged && !addressChanged) return;
            }
        }

        if (ChipSelectBar.SampledLevel != DigitalLevel.Low || !Address.TrySample(out var rawAddress))
        {
            Data.Release();
            return;
        }

        var address = (int)rawAddress;
        if (WriteEnableBar.SampledLevel == DigitalLevel.Low)
        {
            Data.Release();
            if (Data.TrySample(out var value))
            {
                var next = (byte)value;
                if (_storage[address] != next)
                {
                    _storage[address] = next;
                    WriteCount++;
                }
            }
            return;
        }

        if (OutputEnableBar.SampledLevel == DigitalLevel.Low)
            Data.Drive(_storage[address]);
        else
            Data.Release();
    }
}
