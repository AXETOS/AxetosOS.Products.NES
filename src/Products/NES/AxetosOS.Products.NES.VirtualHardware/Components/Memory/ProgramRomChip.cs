using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Memory;

/// <summary>
/// Pin-driven asynchronous ROM. The chip exposes only address, data,
/// chip-select and output-enable pins to the board.
/// </summary>
public sealed class ProgramRomChip : VirtualHardwareComponent
{
    private readonly byte[] _storage;

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
    }

    public int Capacity => _storage.Length;
    public DigitalBus Address { get; }
    public DigitalBus Data { get; }
    public DigitalPin ChipSelectBar { get; }
    public DigitalPin OutputEnableBar { get; }
    public ulong ReadDriveCount { get; private set; }

    public byte Inspect(int address) => _storage[address];

    public override void PowerOn()
    {
        ReadDriveCount = 0;
        Data.Release();
    }

    public override void Reset() => Data.Release();

    public override void Evaluate()
    {
        if (ChipSelectBar.SampledLevel != DigitalLevel.Low ||
            OutputEnableBar.SampledLevel != DigitalLevel.Low ||
            !Address.TrySample(out var rawAddress))
        {
            Data.Release();
            return;
        }

        Data.Drive(_storage[(int)rawAddress]);
        ReadDriveCount++;
    }
}
