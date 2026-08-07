namespace AxetosOS.Products.NES.VirtualHardware.Electrical;

/// <summary>
/// Declares when a package wants an accepted digital input transition to wake
/// its internal logic. The motherboard still presents every 0/1 level change
/// to the pin; this is package-owned edge sensitivity, not board scheduling.
/// </summary>
public enum DigitalInputActivation : byte
{
    AnyChange,
    RisingEdge,
    FallingEdge
}
