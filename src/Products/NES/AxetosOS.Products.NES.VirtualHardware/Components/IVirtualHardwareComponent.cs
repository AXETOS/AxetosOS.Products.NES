using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components;

public interface IVirtualHardwareComponent
{
    string ComponentId { get; }
    IReadOnlyList<DigitalPin> Pins { get; }
    void PowerOn();
    void Reset();
    void Evaluate();
}
