namespace AxetosOS.Products.NES.VirtualHardware.Components;

/// <summary>
/// Marks a component whose externally observable and stored digital state can
/// change only after one of its package pins changes. The simulator may avoid
/// reevaluating a settled instance until one of those pins changes again.
/// Output-pin changes automatically schedule a follow-up evaluation, allowing
/// normal multi-pass propagation without polling the component continuously.
/// </summary>
public interface IInputDrivenVirtualHardwareComponent : IVirtualHardwareComponent
{
}
