using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components;

public abstract class VirtualHardwareComponent : IVirtualHardwareComponent
{
    private readonly List<DigitalPin> _pins = [];

    protected VirtualHardwareComponent(string componentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentId);
        ComponentId = componentId;
    }

    public string ComponentId { get; }
    public IReadOnlyList<DigitalPin> Pins => _pins;

    protected DigitalPin AddPin(string name, PinDirection direction)
    {
        var pin = new DigitalPin($"{ComponentId}.{name}", direction);
        _pins.Add(pin);
        return pin;
    }

    public virtual void PowerOn() { }
    public virtual void Reset() { }
    public abstract void Evaluate();
}
