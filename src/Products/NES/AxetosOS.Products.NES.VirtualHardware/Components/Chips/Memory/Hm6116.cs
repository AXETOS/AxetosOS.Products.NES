using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Chips.Memory;

/// <summary>
/// Standalone HM6116-compatible 2K x 8 static RAM package.
/// Memory access occurs only through address, data and control pins.
/// </summary>
public sealed class Hm6116 : VirtualHardwareComponent
{
    private readonly byte[] _memory = new byte[2048];

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

    public override void PowerOn() => Data.Release();

    public override void Reset() => Data.Release();

    public override void Evaluate()
    {
        if (!IsPowered() || ChipSelectBar.SampledLevel != DigitalLevel.Low)
        {
            Data.Release();
            return;
        }

        if (!Address.TrySample(out var rawAddress))
        {
            Data.Release();
            return;
        }

        var address = (int)(rawAddress & 0x07FF);

        if (WriteEnableBar.SampledLevel == DigitalLevel.Low)
        {
            Data.Release();
            if (Data.TrySample(out var value))
            {
                _memory[address] = (byte)value;
            }

            return;
        }

        if (WriteEnableBar.SampledLevel == DigitalLevel.High &&
            OutputEnableBar.SampledLevel == DigitalLevel.Low)
        {
            Data.Drive(_memory[address]);
            return;
        }

        Data.Release();
    }

    /// <summary>Test/inspection access only; not an electrical communication path.</summary>
    public byte Inspect(int address)
    {
        if ((uint)address >= _memory.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(address));
        }

        return _memory[address];
    }

    private bool IsPowered() =>
        Vcc.SampledLevel == DigitalLevel.High && Gnd.SampledLevel == DigitalLevel.Low;
}
