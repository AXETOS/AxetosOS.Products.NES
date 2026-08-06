namespace AxetosOS.Products.NES.VirtualHardware.Components;

/// <summary>
/// Marks a package whose output changes are a zero-delay combinational
/// consequence of its current sampled inputs. The motherboard may keep these
/// packages inside one direct causal routing chain. Stateful, clocked, timed,
/// memory, CPU, PPU, and other sequential packages must not implement this
/// contract; they remain explicit execution boundaries.
/// </summary>
public interface ICombinationalVirtualHardwareComponent : IInputDrivenVirtualHardwareComponent
{
}
