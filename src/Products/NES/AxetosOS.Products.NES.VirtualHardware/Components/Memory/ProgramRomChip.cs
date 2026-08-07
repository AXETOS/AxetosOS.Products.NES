using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Memory;

/// <summary>
/// Pin-driven asynchronous ROM. The chip exposes only address, data,
/// chip-select and output-enable pins to the board.
/// </summary>
public sealed class ProgramRomChip : VirtualHardwareComponent
{
    private readonly byte[] _storage;
    private readonly ulong _addressInputMask;
    private readonly ulong _controlInputMask;
    private bool _drivingData;

    public ProgramRomChip(string componentId, int addressWidth, ReadOnlySpan<byte> contents)
        : base(componentId)
    {
        if (addressWidth is <= 0 or > 24)
        {
            throw new ArgumentOutOfRangeException(nameof(addressWidth));
        }

        _storage = new byte[1 << addressWidth];
        if (contents.Length > _storage.Length)
        {
            throw new ArgumentException("ROM contents exceed the selected address width.", nameof(contents));
        }

        contents.CopyTo(_storage);

        var addressPins = new DigitalPin[addressWidth];
        for (var bit = 0; bit < addressWidth; bit++)
        {
            addressPins[bit] = AddPin($"A{bit}", PinDirection.Input);
        }

        var dataPins = new DigitalPin[8];
        for (var bit = 0; bit < dataPins.Length; bit++)
        {
            dataPins[bit] = AddPin($"D{bit}", PinDirection.Output);
        }

        Address = new DigitalBus($"{componentId}.A", addressPins);
        Data = new DigitalBus($"{componentId}.D", dataPins);
        ChipSelectBar = AddPin("/CS", PinDirection.Input);
        OutputEnableBar = AddPin("/OE", PinDirection.Input);
        _addressInputMask = Address.InputChangeMask;
        _controlInputMask = ChipSelectBar.InputChangeMask | OutputEnableBar.InputChangeMask;
    
        InitializePackageState();
    }

    public int Capacity => _storage.Length;
    public DigitalBus Address { get; }
    public DigitalBus Data { get; }
    public DigitalPin ChipSelectBar { get; }
    public DigitalPin OutputEnableBar { get; }
    public ulong ReadDriveCount { get; private set; }

    public byte Inspect(int address) => _storage[address];

    private void InitializePackageState()
    {
        ReadDriveCount = 0;
        _drivingData = false;
        Data.Release();
    }

    protected override void OnInputChanges(ulong changedInputMask) => ProcessInputChanges(changedInputMask);

    private void ProcessInputChanges(ulong changedInputMask)
    {
        if (changedInputMask == 0) return;
        var controlChanged = (changedInputMask & _controlInputMask) != 0;
        var addressChanged = (changedInputMask & _addressInputMask) != 0;
        if (!controlChanged && !addressChanged) return;

        if (ChipSelectBar.SampledLevel != DigitalLevel.Low ||
            OutputEnableBar.SampledLevel != DigitalLevel.Low)
        {
            if (_drivingData)
            {
                Data.Release();
                _drivingData = false;
            }
            return;
        }

        if (!Address.TrySample(out var rawAddress))
        {
            if (_drivingData)
            {
                Data.Release();
                _drivingData = false;
            }
            return;
        }

        Data.Drive(_storage[(int)rawAddress]);
        _drivingData = true;
        ReadDriveCount++;
    }
}
