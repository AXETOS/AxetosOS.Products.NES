using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Simulation;

/// <summary>
/// Resolves nets and evaluates components until the circuit reaches a stable
/// digital state or the configured propagation limit is reached.
/// </summary>
public sealed class VirtualHardwareSimulator
{
    private DigitalNet[] _nets;
    private IVirtualHardwareComponent[] _components;
    private DigitalPin[] _pins;
    private int _knownNetCount;
    private int _knownComponentCount;

    public VirtualHardwareSimulator(VirtualHardwareBoard board)
    {
        Board = board ?? throw new ArgumentNullException(nameof(board));
        _nets = Board.Nets.ToArray();
        _components = Board.Components.ToArray();
        _pins = _components.SelectMany(static component => component.Pins).ToArray();
        _knownNetCount = _nets.Length;
        _knownComponentCount = _components.Length;
    }

    public VirtualHardwareBoard Board { get; }
    public ulong SettleCount { get; private set; }

    public int Settle(int maximumPropagationPasses = 64)
    {
        if (maximumPropagationPasses <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPropagationPasses));
        }

        RefreshTopologyIfNeeded();

        for (var pass = 1; pass <= maximumPropagationPasses; pass++)
        {
            var before = GetBoardRevision();

            foreach (var net in _nets)
            {
                net.Resolve();
            }

            foreach (var component in _components)
            {
                component.Evaluate();
            }

            foreach (var net in _nets)
            {
                net.Resolve();
            }

            SettleCount++;
            if (before == GetBoardRevision())
            {
                return pass;
            }
        }

        throw new InvalidOperationException(
            $"Board '{Board.BoardId}' did not settle after {maximumPropagationPasses} propagation passes.");
    }

    private ulong GetBoardRevision()
    {
        ulong revision = 0;
        foreach (var pin in _pins)
        {
            revision = unchecked(revision + pin.Revision);
        }

        return revision;
    }

    private void RefreshTopologyIfNeeded()
    {
        if (_knownNetCount == Board.Nets.Count && _knownComponentCount == Board.Components.Count)
        {
            return;
        }

        _nets = Board.Nets.ToArray();
        _components = Board.Components.ToArray();
        _pins = _components.SelectMany(static component => component.Pins).ToArray();
        _knownNetCount = _nets.Length;
        _knownComponentCount = _components.Length;
    }
}
