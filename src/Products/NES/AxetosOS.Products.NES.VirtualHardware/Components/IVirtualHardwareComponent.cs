using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components;

/// <summary>
/// A self-contained physical package. The electrical kernel may report only
/// changed input-pin levels. No board, scheduler, source identity, lifecycle
/// callback, or machine-level meaning crosses this boundary.
/// </summary>
public interface IVirtualHardwareComponent
{
    string ComponentId { get; }
    IReadOnlyList<DigitalPin> Pins { get; }
    void ReceiveInputChanges(ulong changedInputMask);
}
