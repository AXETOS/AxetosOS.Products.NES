namespace AxetosOS.Products.NES.VirtualHardware.Components;

/// <summary>
/// A package that is normally woken by sampled input-pin changes, but may
/// explicitly request immediate follow-up evaluations while an internal
/// electrical transaction is unfinished. This avoids global polling without
/// hiding multi-phase bus activity inside the simulator.
/// </summary>
public interface IEventDrivenVirtualHardwareComponent : IInputDrivenVirtualHardwareComponent
{
    bool HasPendingInternalWork { get; }
}
