namespace AxetosOS.Products.NES.Hardware;

/// <summary>
/// External RP2A03 package signals. Lines use asserted/released logical semantics
/// even where the physical package pin is active-low.
/// </summary>
public sealed class Rp2A03SignalLines
{
    public SignalLine Nmi { get; } = new();
    public SignalLine Irq { get; } = new();
    public SignalLine Reset { get; } = new();
    public SignalLine Rdy { get; } = new(initiallyAsserted: true);
}
