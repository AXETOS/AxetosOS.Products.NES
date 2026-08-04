using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Memory;

/// <summary>
/// Pin-driven asynchronous static RAM. It knows only its address/data pins and
/// active-low chip-select, output-enable and write-enable controls.
/// </summary>
public sealed class StaticRamChip : VirtualHardwareComponent
{
    private readonly byte[] _storage;

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
    }

    public int Capacity => _storage.Length;
    public DigitalBus Address { get; }
    public DigitalBus Data { get; }
    public DigitalPin ChipSelectBar { get; }
    public DigitalPin OutputEnableBar { get; }
    public DigitalPin WriteEnableBar { get; }
    public ulong WriteCount { get; private set; }

    public byte Inspect(int address) => _storage[address];

    public override void PowerOn()
    {
        Array.Clear(_storage);
        WriteCount = 0;
        Data.Release();
    }

    public override void Reset() => Data.Release();

    public override void Evaluate()
    {
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
        {
            Data.Drive(_storage[address]);
        }
        else
        {
            Data.Release();
        }
    }
}
