using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Components;

namespace AxetosOS.Products.NES.VirtualHardware.Electrical;

/// <summary>
/// A physical package pin. Output-capable pins drive the motherboard connection
/// immediately when their state changes. Input-capable pins retain only the
/// resolved digital level presented to the package and notify their owning chip
/// directly when that input changes.
/// </summary>
public sealed class DigitalPin
{
    // Low three bits hold DigitalLevel; bit three holds drive strength.
    // A single compact electrical state improves cache density and lets shared
    // net resolvers load a driver's complete output state once.
    private byte _driveState = PackDriveState(DigitalLevel.HighImpedance, DigitalDriveStrength.Strong);
    private DigitalLevel _sampledLevel = DigitalLevel.Unknown;
    private DigitalLevel _lastAcceptedInputLevel = DigitalLevel.Unknown;
    private int _inputActivationPhase;
    private ulong _inputActivationEdgeCount;
    private bool _ownerWakeEnabled = true;

    public DigitalPin(string name, PinDirection direction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Direction = direction;
    }

    public string Name { get; }
    public PinDirection Direction { get; }
    public DigitalNet? Net { get; internal set; }
    public DigitalLevel DriveLevel => (DigitalLevel)(_driveState & 0x07);
    public DigitalDriveStrength DriveStrength => (DigitalDriveStrength)((_driveState >> 3) & 0x01);
    internal byte PackedDriveState => _driveState;
    public DigitalLevel SampledLevel => _sampledLevel;
    public bool IsInputCapable => Direction is PinDirection.Input or PinDirection.Bidirectional;
    public bool IsOutputCapable => Direction is not PinDirection.Input;

    internal VirtualHardwareComponent? OwnerComponent { get; set; }
    internal int OwnerComponentIndex { get; set; } = -1;
    internal int NetDriverIndex { get; set; } = -1;
    internal ulong InputChangeMask { get; init; }
    internal ulong PackagePinMask { get; init; }
    internal DigitalInputActivation InputActivation { get; set; } = DigitalInputActivation.AnyChange;
    internal int InputActivationPeriod { get; set; } = 1;
    internal ulong InputActivationEdgeCount => _inputActivationEdgeCount;


    /// <summary>
    /// Package-owned activation latch for this input pin. The motherboard always
    /// delivers the electrical level and this pin always records it first. A chip
    /// may disable wake-up for ordinary data/address pins while its own select or
    /// enable circuitry disconnects them. Activation/control pins normally remain
    /// enabled so they can switch the internal circuitry back on.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte PackDriveState(DigitalLevel level, DigitalDriveStrength strength) =>
        (byte)((byte)level | ((byte)strength << 3));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PublishPackedDriverStateIfCompiled(byte driveState)
    {
        var driverIndex = NetDriverIndex;
        if (driverIndex >= 0) Net!.UpdatePackedDriverState(driverIndex, driveState);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetOwnerWakeEnabled(bool enabled) => _ownerWakeEnabled = enabled;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool OwnerWantsWake() => _ownerWakeEnabled;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Drive(
        DigitalLevel level,
        DigitalDriveStrength strength = DigitalDriveStrength.Strong)
    {
        if (!IsOutputCapable)
        {
            throw new InvalidOperationException($"Input pin '{Name}' cannot drive a net.");
        }

        if (level == DigitalLevel.Contention)
        {
            throw new ArgumentOutOfRangeException(nameof(level), "Contention is a resolved net state, not a drive state.");
        }

        var driveState = PackDriveState(level, strength);
        if (_driveState == driveState) return;

        _driveState = driveState;
        PublishPackedDriverStateIfCompiled(driveState);

        var net = Net;
        if (net is null) return;
        if (OwnerComponent?.TryStageOutputChange(this) == true) return;
        net.PropagateDriverChange(this);
    }

    /// <summary>
    /// Fast path used by a DigitalBus that validated all member pins as
    /// output-capable at construction. Only binary strong-drive states enter
    /// here, so per-bit direction/contention validation is unnecessary.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool SetBinaryStrongForPackage(DigitalLevel level)
    {
        var driveState = PackDriveState(level, DigitalDriveStrength.Strong);
        if (_driveState == driveState) return false;
        _driveState = driveState;
        PublishPackedDriverStateIfCompiled(driveState);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool SetReleasedForPackage()
    {
        var driveState = PackDriveState(DigitalLevel.HighImpedance, DigitalDriveStrength.Strong);
        if (_driveState == driveState) return false;
        _driveState = driveState;
        PublishPackedDriverStateIfCompiled(driveState);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void DriveBinaryStrong(DigitalLevel level)
    {
        var driveState = PackDriveState(level, DigitalDriveStrength.Strong);
        if (_driveState == driveState) return;
        _driveState = driveState;
        PublishPackedDriverStateIfCompiled(driveState);

        var net = Net;
        if (net is null) return;
        if (OwnerComponent?.TryStageOutputChange(this) == true) return;
        net.PropagateDriverChange(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ReleaseValidatedOutput()
    {
        var driveState = PackDriveState(DigitalLevel.HighImpedance, DigitalDriveStrength.Strong);
        if (_driveState == driveState) return;
        _driveState = driveState;
        PublishPackedDriverStateIfCompiled(driveState);

        var net = Net;
        if (net is null) return;
        if (OwnerComponent?.TryStageOutputChange(this) == true) return;
        net.PropagateDriverChange(this);
    }

    /// <summary>
    /// Publishes this package-owned output pin to whatever physical trace is
    /// attached. The owning chip does not know or retain that trace.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void PublishStagedDriveChange() => Net?.PropagateDriverChange(this);

    /// <summary>
    /// Publishes one chip reaction's changed physical package pins as a single
    /// electrical change-set. Trace lookup happens here at the package pin
    /// boundary, outside the chip's internal state.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void PublishStagedDriveChanges(
        DigitalPin[] packagePins,
        ulong changedPinMask,
        DigitalPin[] overflowPins,
        int overflowCount) =>
        DigitalNet.PublishChangedOutputPins(packagePins, changedPinMask, overflowPins, overflowCount);

    /// <summary>
    /// Updates the drive state of a topology-validated single-driver source.
    /// The compiled motherboard trace performs the immediate propagation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void DriveCompiledSource(DigitalLevel level) =>
        _driveState = PackDriveState(level, DigitalDriveStrength.Strong);

    public void Release() => Drive(DigitalLevel.HighImpedance);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetObservedLevel(DigitalLevel level)
    {
        _sampledLevel = level;
        if (IsInputCapable) _lastAcceptedInputLevel = level;
    }

    /// <summary>
    /// Topology-compiled store for an output-only package pin. DigitalNet calls
    /// this only for pins already proven non-input-capable, so the several
    /// billion normal-run physical deliveries do not repeat a direction test.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetObservedOutputLevel(DigitalLevel level) => _sampledLevel = level;


    /// <summary>
    /// Topology-compiled fast path for an ordinary input-only AnyChange pin.
    /// The electrical layer has already proven this pin is not bidirectional and
    /// does not use edge/divider activation, so the hot delivery path needs only
    /// to retain the physical level/history and consult the chip-owned wake gate.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryAcceptCompiledAnyChangeInput(DigitalLevel level)
    {
        _sampledLevel = level;

        if (!_ownerWakeEnabled)
        {
            _lastAcceptedInputLevel = level;
            return false;
        }

        if (_lastAcceptedInputLevel == level) return false;
        _lastAcceptedInputLevel = level;
        return true;
    }

    /// <summary>
    /// Topology-compiled fast path for a bidirectional AnyChange package pin.
    /// The physical package level is always retained; while this chip drives the
    /// line, its own receiver remains disconnected exactly as on the generic path.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryAcceptCompiledAnyChangeBidirectional(DigitalLevel level)
    {
        _sampledLevel = level;
        if ((_driveState & 0x07) != (byte)DigitalLevel.HighImpedance) return false;

        if (!_ownerWakeEnabled)
        {
            _lastAcceptedInputLevel = level;
            return false;
        }

        if (_lastAcceptedInputLevel == level) return false;
        _lastAcceptedInputLevel = level;
        return true;
    }

    /// <summary>
    /// Presents a resolved motherboard level to this package pin. A
    /// bidirectional pin accepts an incoming transition only while its own
    /// output driver is released, so a chip cannot react to its own drive.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryAcceptInputLevel(DigitalLevel level)
    {
        // SampledLevel is the physical level at the package pin even while a
        // bidirectional output is driving. Incoming-transition history is kept
        // separately so releasing a bus does not invent a chip input edge.
        _sampledLevel = level;

        if (Direction == PinDirection.Bidirectional &&
            (_driveState & 0x07) != (byte)DigitalLevel.HighImpedance)
        {
            return false;
        }

        // Ordinary AnyChange package pins dominate the NES motherboard. When a
        // chip-owned select/enable gate is closed, the physical level and input
        // history must still advance, but there is no reason to compare the old
        // value or enter activation machinery merely to discover that the chip
        // will not wake. This remains package-owned suppression: DigitalNet has
        // already delivered the resolved electrical level unconditionally.
        var activation = InputActivation;
        if (activation == DigitalInputActivation.AnyChange)
        {
            if (!OwnerWantsWake())
            {
                _lastAcceptedInputLevel = level;
                return false;
            }

            var previous = _lastAcceptedInputLevel;
            if (previous == level) return false;
            _lastAcceptedInputLevel = level;
            return true;
        }

        var previousEdgeLevel = _lastAcceptedInputLevel;
        if (previousEdgeLevel == level) return false;
        _lastAcceptedInputLevel = level;

        if (activation == DigitalInputActivation.RisingEdge)
        {
            if (level != DigitalLevel.High) return false;
        }
        else if (activation == DigitalInputActivation.FallingEdge)
        {
            if (level != DigitalLevel.Low) return false;
        }

        // Edge counters/dividers belong only to edge-activated pins such as
        // clocks. No ordinary bus pin pays this cost.
        _inputActivationEdgeCount++;
        if (InputActivationPeriod == 1) return OwnerWantsWake();

        _inputActivationPhase++;
        if (_inputActivationPhase < InputActivationPeriod) return false;
        _inputActivationPhase = 0;
        return OwnerWantsWake();
    }

    internal void ResetInputActivationCounter()
    {
        _inputActivationPhase = 0;
        _inputActivationEdgeCount = 0;
    }
}
