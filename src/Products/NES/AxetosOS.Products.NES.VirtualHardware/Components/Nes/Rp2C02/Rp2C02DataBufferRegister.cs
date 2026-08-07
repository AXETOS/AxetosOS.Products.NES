using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Nes.Rp2C02;

/// <summary>Eight-bit RP2C02 PPUDATA read buffer with edge-triggered load.</summary>
public sealed class Rp2C02DataBufferRegister : VirtualHardwareComponent
{
    private readonly ulong _loadInputMask;
    private readonly ulong _clearInputMask;

    public Rp2C02DataBufferRegister(string componentId) : base(componentId)
    {
        Input = CreateBus("IN", PinDirection.Input);
        Output = CreateBus("OUT", PinDirection.Output);
        Load = AddPin("LOAD", PinDirection.Input, DigitalInputActivation.RisingEdge);
        Clear = AddPin("CLEAR", PinDirection.Input);
        _loadInputMask = Load.InputChangeMask;
        _clearInputMask = Clear.InputChangeMask;
    
        InitializePackageState();
    }

    public DigitalBus Input { get; }
    public DigitalBus Output { get; }
    public DigitalPin Load { get; }
    public DigitalPin Clear { get; }
    public byte Value { get; private set; }

    private void InitializePackageState() { Value = 0; Output.Drive(0); }
    protected override void OnInputChanges(ulong changedInputMask)
    {
        var clearChanged = (changedInputMask & _clearInputMask) != 0;
        var loadRising = (changedInputMask & _loadInputMask) != 0;
        if (!clearChanged && !loadRising) return;

        if (Clear.SampledLevel == DigitalLevel.High)
        {
            if (Value != 0)
            {
                Value = 0;
                Output.Drive(0);
            }
            return;
        }

        if (!loadRising || !Input.TrySample(out var value)) return;
        var next = (byte)value;
        if (Value == next) return;
        Value = next;
        Output.Drive(Value);
    }

    private DigitalBus CreateBus(string name, PinDirection direction)
    {
        var pins = new DigitalPin[8];
        for (var bit = 0; bit < 8; bit++) pins[bit] = AddPin($"{name}{bit}", direction);
        return new DigitalBus($"{ComponentId}.{name}", pins);
    }
}
