namespace AxetosOS.Products.NES.VirtualHardware.Components;

/// <summary>
/// Optional high-speed package contract used by the compiled motherboard
/// engine. The bit mask identifies the package pins whose sampled electrical
/// levels changed since the package was last evaluated. The package remains
/// authoritative for all state transitions and output drives.
/// </summary>
public interface ICompiledInputDrivenVirtualHardwareComponent : IVirtualHardwareComponent
{
    void Evaluate(ulong changedInputMask);
}
