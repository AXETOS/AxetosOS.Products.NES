using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Components;

/// <summary>
/// Base class for a self-contained physical package. A package can inspect only
/// its own retained pin levels and internal state, and can affect the circuit
/// only by driving its own output-capable pins. Internal functional blocks are
/// ordinary package-local state/classes and communicate directly; the package
/// itself never retains motherboard nets, peer packages, or a private board.
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
    private DigitalPin[] _packagePins = [];
    private ulong _changedOutputPinMask;
    private DigitalPin[] _changedOutputOverflowPins = new DigitalPin[4];
    private int _changedOutputOverflowCount;
    private ulong _pendingInputChanges;
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
                : 0,
            PackagePinMask = pinIndex < 64 ? 1UL << pinIndex : 0
        };
        _pins.Add(pin);
        Array.Resize(ref _packagePins, pinIndex + 1);
        _packagePins[pinIndex] = pin;
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
                OnInputChanges(currentInputChanges);
                FlushChangedOutputs();

                currentInputChanges = _pendingInputChanges;
                _pendingInputChanges = 0;
            }
        }
        finally
        {
            _handlingInputChanges = false;
            _changedOutputPinMask = 0;
            _changedOutputOverflowCount = 0;
        }
    }

    /// <summary>
    /// Diagnostics-only sampled reaction path. Normal emulation never enters
    /// this method. It mirrors the package-owned re-entrant folding semantics
    /// of ReceiveInputChanges while allowing selected large ICs to expose
    /// package-internal timing without storing profiler state in the chip.
    /// </summary>
    internal void ReceiveInputChangesProfiled(
        ulong changedInputMask,
        VirtualHardwareProfileSample sample)
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
                OnInputChangesProfiled(currentInputChanges, sample);
                FlushChangedOutputs();

                currentInputChanges = _pendingInputChanges;
                _pendingInputChanges = 0;
            }
        }
        finally
        {
            _handlingInputChanges = false;
            _changedOutputPinMask = 0;
            _changedOutputOverflowCount = 0;
        }
    }

    internal bool IsHandlingInputChanges => _handlingInputChanges;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void StageOutputChanges(ulong changedPackagePinMask)
    {
        _changedOutputPinMask |= changedPackagePinMask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryStageOutputChange(DigitalPin pin)
    {
        if (!_handlingInputChanges) return false;

        // Every current NES package fits inside one 64-bit package-pin mask.
        // This is still a set of physical package pins, not a logical bus: each
        // set bit is later published through that pin's own attached net. The
        // mask only replaces hot reference-array writes and publication stamps.
        var packagePinMask = pin.PackagePinMask;
        if (packagePinMask != 0)
        {
            _changedOutputPinMask |= packagePinMask;
            return true;
        }

        // Generic laboratory packages may exceed 64 pins. Keep a cold overflow
        // path so the physical model has no package-size restriction.
        var count = _changedOutputOverflowCount;
        var overflow = _changedOutputOverflowPins;
        for (var index = 0; index < count; index++)
        {
            if (ReferenceEquals(overflow[index], pin)) return true;
        }

        if (count == overflow.Length)
        {
            Array.Resize(ref _changedOutputOverflowPins, overflow.Length * 2);
            overflow = _changedOutputOverflowPins;
        }
        overflow[count] = pin;
        _changedOutputOverflowCount = count + 1;
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
        var changedMask = _changedOutputPinMask;
        var overflowCount = _changedOutputOverflowCount;
        if (changedMask == 0 && overflowCount == 0) return;

        // Clear package-local staging before any receiver can react. A nested
        // input transition therefore starts a fresh atomic package change-set.
        _changedOutputPinMask = 0;
        _changedOutputOverflowCount = 0;

        if (overflowCount == 0 && changedMask != 0 && (changedMask & (changedMask - 1)) == 0)
        {
            var pinIndex = System.Numerics.BitOperations.TrailingZeroCount(changedMask);
            _packagePins[pinIndex].PublishStagedDriveChange();
            return;
        }

        DigitalPin.PublishStagedDriveChanges(
            _packagePins,
            changedMask,
            _changedOutputOverflowPins,
            overflowCount);
    }

    /// <summary>
    /// Called only after one or more input-capable package pins changed level.
    /// Implementations must not depend on polling or simulator callbacks.
    /// </summary>
    protected virtual void OnInputChanges(ulong changedInputMask) { }

    /// <summary>
    /// Optional sampled diagnostics override. The default preserves the exact
    /// physical package behavior by delegating to the normal input handler.
    /// </summary>
    protected virtual void OnInputChangesProfiled(
        ulong changedInputMask,
        VirtualHardwareProfileSample sample) => OnInputChanges(changedInputMask);
}
