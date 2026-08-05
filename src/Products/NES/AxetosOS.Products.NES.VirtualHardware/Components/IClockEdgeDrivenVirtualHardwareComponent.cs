using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components;

/// <summary>
/// Allows a package to consume electrically resolved clock levels without
/// running its complete Evaluate method for inactive clock edges. The clock
/// still travels through a real pin and net; only the package dispatch is
/// specialized.
/// </summary>
public interface IClockEdgeDrivenVirtualHardwareComponent
{
    bool TryHandleClockSample(DigitalPin pin, DigitalLevel level);
}
