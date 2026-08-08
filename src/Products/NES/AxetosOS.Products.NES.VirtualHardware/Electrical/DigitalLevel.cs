namespace AxetosOS.Products.NES.VirtualHardware.Electrical;

/// <summary>
/// Resolved state of a digital electrical net.
/// </summary>
public enum DigitalLevel : byte
{
    Unknown,
    Low,
    High,
    HighImpedance,
    Contention
}
