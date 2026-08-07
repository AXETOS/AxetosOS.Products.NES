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
    private DigitalLevel _driveLevel = DigitalLevel.HighImpedance;
    private DigitalDriveStrength _driveStrength = DigitalDriveStrength.Strong;
    private DigitalLevel _sampledLevel = DigitalLevel.Unknown;
    private DigitalLevel _lastAcceptedInputLevel = DigitalLevel.Unknown;
    private int _inputActivationPhase;
    private ulong _inputActivationEdgeCount;

    public DigitalPin(string name, PinDirection direction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Direction = direction;
    }

    public string Name { get; }
    public PinDirection Direction { get; }
    public DigitalNet? Net { get; internal set; }
    public DigitalLevel DriveLevel => _driveLevel;
    public DigitalDriveStrength DriveStrength => _driveStrength;
    public DigitalLevel SampledLevel => _sampledLevel;
    public bool IsInputCapable => Direction is PinDirection.Input or PinDirection.Bidirectional;
    public bool IsOutputCapable => Direction is not PinDirection.Input;

    internal VirtualHardwareComponent? OwnerComponent { get; set; }
    internal int OwnerComponentIndex { get; set; } = -1;
    internal ulong InputChangeMask { get; init; }
    internal DigitalInputActivation InputActivation { get; set; } = DigitalInputActivation.AnyChange;
    internal int InputActivationPeriod { get; set; } = 1;
    internal ulong InputActivationEdgeCount => _inputActivationEdgeCount;

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

        if (_driveLevel == level && _driveStrength == strength) return;

        _driveLevel = level;
        _driveStrength = strength;

        var net = Net;
        if (net is null) return;
        if (OwnerComponent?.TryStageOutputChange(net) == true) return;
        net.PropagateDriverChange();
    }

    /// <summary>
    /// Fast path used by a DigitalBus that validated all member pins as
    /// output-capable at construction. Only binary strong-drive states enter
    /// here, so per-bit direction/contention validation is unnecessary.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void DriveBinaryStrong(DigitalLevel level)
    {
        if (_driveLevel == level && _driveStrength == DigitalDriveStrength.Strong) return;
        _driveLevel = level;
        _driveStrength = DigitalDriveStrength.Strong;

        var net = Net;
        if (net is null) return;
        if (OwnerComponent?.TryStageOutputChange(net) == true) return;
        net.PropagateDriverChange();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ReleaseValidatedOutput()
    {
        if (_driveLevel == DigitalLevel.HighImpedance && _driveStrength == DigitalDriveStrength.Strong) return;
        _driveLevel = DigitalLevel.HighImpedance;
        _driveStrength = DigitalDriveStrength.Strong;

        var net = Net;
        if (net is null) return;
        if (OwnerComponent?.TryStageOutputChange(net) == true) return;
        net.PropagateDriverChange();
    }

    /// <summary>
    /// Updates the drive state of a topology-validated single-driver source.
    /// The compiled motherboard trace performs the immediate propagation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void DriveCompiledSource(DigitalLevel level) => _driveLevel = level;

    public void Release() => Drive(DigitalLevel.HighImpedance);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetObservedLevel(DigitalLevel level)
    {
        _sampledLevel = level;
        if (IsInputCapable) _lastAcceptedInputLevel = level;
    }

    /// <summary>
    /// Hot path for a topology-validated rising-edge-only clock input. The
    /// motherboard must still present both electrical levels, but a falling
    /// edge does nothing beyond recording Low at the package pin. Rising edges
    /// update the chip-owned divider counter and return true only when the
    /// package actually needs to wake.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryAcceptCompiledRisingEdgeClockLevel(DigitalLevel level)
    {
        var previous = _lastAcceptedInputLevel;
        _sampledLevel = level;
        if (previous == level) return false;
        _lastAcceptedInputLevel = level;

        // High -> Low is electrically visible at the pin but never enters the
        // owning chip's logic and never touches the activation/divider counter.
        if (level != DigitalLevel.High) return false;

        _inputActivationEdgeCount++;
        if (InputActivationPeriod == 1) return true;

        _inputActivationPhase++;
        if (_inputActivationPhase < InputActivationPeriod) return false;
        _inputActivationPhase = 0;
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

        if (Direction == PinDirection.Bidirectional && _driveLevel != DigitalLevel.HighImpedance)
        {
            return false;
        }

        var previous = _lastAcceptedInputLevel;
        if (previous == level) return false;
        _lastAcceptedInputLevel = level;

        var activatingEdge = InputActivation switch
        {
            DigitalInputActivation.RisingEdge => level == DigitalLevel.High && previous != DigitalLevel.High,
            DigitalInputActivation.FallingEdge => level == DigitalLevel.Low && previous != DigitalLevel.Low,
            _ => true
        };
        if (!activatingEdge) return false;

        _inputActivationEdgeCount++;
        if (InputActivationPeriod == 1) return true;

        _inputActivationPhase++;
        if (_inputActivationPhase < InputActivationPeriod) return false;
        _inputActivationPhase = 0;
        return true;
    }

    internal void ResetInputActivationCounter()
    {
        _inputActivationPhase = 0;
        _inputActivationEdgeCount = 0;
    }
}
