using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components;

/// <summary>
/// Base class for a self-contained physical package. A package can inspect only
/// its own retained pin levels and internal state, and can affect the circuit
/// only by driving its own output-capable pins.
///
/// One package reaction is atomic at the package boundary: all output drive
/// changes made while handling one incoming change are published to the board
/// together after the package logic returns. This is not a scheduler or event
/// queue; it is the software equivalent of one chip changing several package
/// pins as the consequence of the same internal transition.
/// </summary>
public abstract class VirtualHardwareComponent : IVirtualHardwareComponent
{
    private readonly List<DigitalPin> _pins = [];
    private DigitalNet[] _changedOutputNets = new DigitalNet[16];
    private int _changedOutputNetCount;
    private ulong _pendingInputChanges;
    private ulong _outputPublicationSequence;
    private bool _handlingInputChanges;

    protected VirtualHardwareComponent(string componentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentId);
        ComponentId = componentId;
    }

    public string ComponentId { get; }
    public IReadOnlyList<DigitalPin> Pins => _pins;

    protected DigitalPin AddPin(
        string name,
        PinDirection direction,
        DigitalInputActivation inputActivation = DigitalInputActivation.AnyChange,
        int inputActivationPeriod = 1)
    {
        if (inputActivationPeriod < 1) throw new ArgumentOutOfRangeException(nameof(inputActivationPeriod));
        var pinIndex = _pins.Count;
        var pin = new DigitalPin($"{ComponentId}.{name}", direction)
        {
            OwnerComponent = this,
            InputActivation = inputActivation,
            InputActivationPeriod = inputActivationPeriod,
            InputChangeMask = direction is PinDirection.Input or PinDirection.Bidirectional
                ? pinIndex < 64 ? 1UL << pinIndex : ulong.MaxValue
                : 0
        };
        _pins.Add(pin);
        return pin;
    }

    /// <summary>
    /// Receives changed input pins directly from motherboard traces. Re-entrant
    /// changes that arrive while this package is already reacting are folded
    /// into the package's own pending input mask and handled before the package
    /// returns to its caller. No motherboard/simulator queue is involved.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReceiveInputChanges(ulong changedInputMask)
    {
        if (changedInputMask == 0) return;

        if (_handlingInputChanges)
        {
            _pendingInputChanges |= changedInputMask;
            return;
        }

        _handlingInputChanges = true;
        try
        {
            var currentInputChanges = changedInputMask | _pendingInputChanges;
            _pendingInputChanges = 0;

            while (currentInputChanges != 0)
            {
                _outputPublicationSequence++;
                OnInputChanges(currentInputChanges);
                FlushChangedOutputs();

                currentInputChanges = _pendingInputChanges;
                _pendingInputChanges = 0;
            }
        }
        finally
        {
            _handlingInputChanges = false;
            // Package-owned references are permanent members of the board.
            // Retain the reusable backing array and reset only its active count
            // instead of clearing reference slots after every chip reaction.
            _changedOutputNetCount = 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryStageOutputChange(DigitalNet net)
    {
        if (!_handlingInputChanges) return false;

        // O(1) duplicate suppression. A package can touch the same physical
        // trace from more than one output assignment during one reaction; the
        // trace is still resolved only once when the package publishes.
        if (net.TryMarkOutputPublication(this, _outputPublicationSequence))
        {
            var count = _changedOutputNetCount;
            if (count == _changedOutputNets.Length)
                Array.Resize(ref _changedOutputNets, _changedOutputNets.Length * 2);
            _changedOutputNets[count] = net;
            _changedOutputNetCount = count + 1;
        }
        return true;
    }

    /// <summary>
    /// Adds changed package inputs to this chip's current input change-set.
    /// Returns true only when this package needs to be added to the current
    /// direct fan-out list. A package already executing will consume the mask
    /// in its own reaction loop and therefore must not be invoked recursively.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool StageInputChanges(ulong changedInputMask)
    {
        var wasPending = _pendingInputChanges != 0;
        _pendingInputChanges |= changedInputMask;
        return !_handlingInputChanges && !wasPending;
    }

    /// <summary>
    /// Consumes currently staged incoming changes only when this package is not
    /// already executing. If it is executing, its normal ReceiveInputChanges
    /// loop will consume the staged changes before returning.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ulong TakePendingInputChangesForDirectReaction()
    {
        if (_handlingInputChanges || _pendingInputChanges == 0) return 0;

        var changed = _pendingInputChanges;
        _pendingInputChanges = 0;
        return changed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FlushChangedOutputs()
    {
        var count = _changedOutputNetCount;
        if (count == 0) return;

        // Clear only the active count before receivers can react. Re-entrant
        // input changes therefore start a fresh publication set while the same
        // package-owned array is reused without per-reaction Array.Clear work.
        _changedOutputNetCount = 0;
        var changed = _changedOutputNets;
        if (count == 1)
        {
            changed[0].PropagateDriverChange();
            return;
        }

        DigitalNet.PublishChangedOutputs(changed, count);
    }

    /// <summary>
    /// Called only after one or more input-capable package pins changed level.
    /// Implementations must not depend on polling or simulator callbacks.
    /// </summary>
    protected virtual void OnInputChanges(ulong changedInputMask) { }
}
