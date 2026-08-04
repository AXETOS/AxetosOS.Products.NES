using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Nes.Rp2C02;

/// <summary>Eight-bit RP2C02 PPUDATA read buffer with edge-triggered load.</summary>
public sealed class Rp2C02DataBufferRegister : VirtualHardwareComponent
{
    private bool _loadLast;

    public Rp2C02DataBufferRegister(string componentId) : base(componentId)
    {
        Input = CreateBus("IN", PinDirection.Input);
        Output = CreateBus("OUT", PinDirection.Output);
        Load = AddPin("LOAD", PinDirection.Input);
        Clear = AddPin("CLEAR", PinDirection.Input);
    }

    public DigitalBus Input { get; }
    public DigitalBus Output { get; }
    public DigitalPin Load { get; }
    public DigitalPin Clear { get; }
    public byte Value { get; private set; }

    public override void PowerOn() { Value = 0; _loadLast = false; Output.Drive(0); }
    public override void Reset() { Value = 0; Output.Drive(0); }

    public override void Evaluate()
    {
        if (Clear.SampledLevel == DigitalLevel.High) Value = 0;
        var load = Load.SampledLevel == DigitalLevel.High;
        if (load && !_loadLast && Input.TrySample(out var value)) Value = (byte)value;
        _loadLast = load;
        Output.Drive(Value);
    }

    private DigitalBus CreateBus(string name, PinDirection direction)
    {
        var pins = new DigitalPin[8];
        for (var bit = 0; bit < 8; bit++) pins[bit] = AddPin($"{name}{bit}", direction);
        return new DigitalBus($"{ComponentId}.{name}", pins);
    }
}
