using AxetosOS.Products.NES.VirtualHardware.Boards;

namespace AxetosOS.Products.NES.VirtualHardware.Simulation;

/// <summary>
/// Resolves nets and evaluates components until the circuit reaches a stable
/// digital state or the configured propagation limit is reached.
/// </summary>
public sealed class VirtualHardwareSimulator
{
    public VirtualHardwareSimulator(VirtualHardwareBoard board)
    {
        Board = board ?? throw new ArgumentNullException(nameof(board));
    }

    public VirtualHardwareBoard Board { get; }
    public ulong SettleCount { get; private set; }

    public int Settle(int maximumPropagationPasses = 64)
    {
        if (maximumPropagationPasses <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPropagationPasses));
        }

        for (var pass = 1; pass <= maximumPropagationPasses; pass++)
        {
            var before = GetRevision();

            foreach (var net in Board.Nets)
            {
                net.Resolve();
            }

            foreach (var component in Board.Components)
            {
                component.Evaluate();
            }

            foreach (var net in Board.Nets)
            {
                net.Resolve();
            }

            SettleCount++;
            if (before == GetRevision())
            {
                return pass;
            }
        }

        throw new InvalidOperationException(
            $"Board '{Board.BoardId}' did not settle after {maximumPropagationPasses} propagation passes.");
    }

    private ulong GetRevision()
    {
        ulong revision = 0;
        foreach (var component in Board.Components)
        {
            foreach (var pin in component.Pins)
            {
                revision = unchecked(revision + pin.Revision);
            }
        }

        return revision;
    }
}
