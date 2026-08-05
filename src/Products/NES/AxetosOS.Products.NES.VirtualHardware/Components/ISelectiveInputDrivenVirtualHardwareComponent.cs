using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components;

/// <summary>
/// Allows an input-driven package to decide whether a sampled change on a
/// particular package pin is an actual input dependency. This is required for
/// bidirectional buses: when the package is currently driving the bus, the
/// resolved echo of its own output must not wake it again.
/// </summary>
public interface ISelectiveInputDrivenVirtualHardwareComponent : IInputDrivenVirtualHardwareComponent
{
    bool ShouldWakeForSampledPin(DigitalPin pin);
}
