using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Electrical;

/// <summary>
/// A physical digital pin. Components drive output-capable pins and sample the
/// resolved level produced by the net attached to the pin.
/// </summary>
public sealed class DigitalPin
{
    private DigitalLevel _driveLevel = DigitalLevel.HighImpedance;
    private DigitalDriveStrength _driveStrength = DigitalDriveStrength.Strong;
    private DigitalLevel _sampledLevel = DigitalLevel.Unknown;

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
    internal ulong Revision { get; private set; }
    internal int OwnerComponentIndex { get; set; } = -1;
    internal VirtualHardwareSimulator? Scheduler { get; set; }
    internal IClockEdgeDrivenVirtualHardwareComponent? ClockEdgeOwner { get; set; }
    internal ISelectiveInputDrivenVirtualHardwareComponent? SelectiveInputOwner { get; set; }
    internal bool WakeOwnerOnSampleChange { get; set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Drive(
        DigitalLevel level,
        DigitalDriveStrength strength = DigitalDriveStrength.Strong)
    {
        if (Direction == PinDirection.Input)
        {
            throw new InvalidOperationException($"Input pin '{Name}' cannot drive a net.");
        }

        if (level == DigitalLevel.Contention)
        {
            throw new ArgumentOutOfRangeException(nameof(level), "Contention is a resolved net state, not a drive state.");
        }

        if (_driveLevel == level && _driveStrength == strength)
        {
            return;
        }

        _driveLevel = level;
        _driveStrength = strength;
        Revision++;
        Net?.MarkDirty();
    }

    public void Release() => Drive(DigitalLevel.HighImpedance);

    internal void SetSampledLevel(DigitalLevel level)
    {
        if (_sampledLevel == level)
        {
            return;
        }

        _sampledLevel = level;
        Revision++;

        // The topology compiler binds each physical pin directly to the
        // applicable package contracts. This keeps the pin-driven model while
        // removing component-array lookup and repeated interface discovery
        // from every resolved electrical transition.
        if (!WakeOwnerOnSampleChange)
        {
            return;
        }

        var clockEdgeOwner = ClockEdgeOwner;
        if (clockEdgeOwner is not null && clockEdgeOwner.TryHandleClockSample(this, level))
        {
            return;
        }

        var selectiveInputOwner = SelectiveInputOwner;
        if (selectiveInputOwner is not null && !selectiveInputOwner.ShouldWakeForSampledPin(this))
        {
            return;
        }

        Scheduler?.NotifyComponentActive(OwnerComponentIndex);
    }
}
