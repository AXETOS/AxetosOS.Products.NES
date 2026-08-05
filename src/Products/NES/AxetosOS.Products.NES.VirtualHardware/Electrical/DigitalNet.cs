using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Electrical;

/// <summary>
/// An electrical node shared by connected pins. The net, not a component,
/// resolves all active drivers into the level observed by every attached pin.
/// </summary>
public sealed class DigitalNet
{
    private readonly List<DigitalPin> _pins = [];
    private DigitalPin[] _resolvedPins = [];
    private DigitalPin[] _driverPins = [];
    private ObserverGroup[] _observerGroups = [];
    private DigitalPin[] _passivePins = [];
    private bool _pinSnapshotDirty = true;
    private NetResolverKind _resolverKind;

    public DigitalNet(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public string Name { get; }
    public IReadOnlyList<DigitalPin> Pins => _pins;
    public DigitalLevel Level { get; private set; } = DigitalLevel.Unknown;
    public ulong ResolutionCount { get; private set; }
    internal bool IsDirty { get; private set; } = true;
    internal int SchedulerIndex { get; set; } = -1;
    internal VirtualHardwareSimulator? Scheduler { get; set; }

    public void Connect(DigitalPin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);

        if (pin.Net is not null && !ReferenceEquals(pin.Net, this))
        {
            throw new InvalidOperationException($"Pin '{pin.Name}' is already connected to net '{pin.Net.Name}'.");
        }

        if (_pins.Contains(pin))
        {
            return;
        }

        _pins.Add(pin);
        _pinSnapshotDirty = true;
        pin.Net = this;
        pin.SetSampledLevel(Level);
        MarkDirty();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void MarkDirty()
    {
        if (IsDirty)
        {
            return;
        }

        IsDirty = true;
        if (SchedulerIndex >= 0)
        {
            Scheduler?.NotifyNetDirty(SchedulerIndex);
        }
    }

    public DigitalLevel Resolve()
    {
        IsDirty = false;
        if (_pinSnapshotDirty) CompileTopology();

        var resolved = _resolverKind switch
        {
            NetResolverKind.Floating => DigitalLevel.Unknown,
            NetResolverKind.SingleDriver => ResolveSingleDriver(_driverPins[0]),
            _ => ResolveMultipleDrivers(_driverPins)
        };

        ResolutionCount++;

        // A large proportion of electrical events are control/strength changes
        // that settle back to the already-observed logic level. Do not walk and
        // callback every attached package when the electrical observation did
        // not actually change.
        if (Level == resolved)
        {
            return resolved;
        }

        Level = resolved;
        PropagateCompiledFanOut(resolved);
        return resolved;
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PropagateCompiledFanOut(DigitalLevel resolved)
    {
        // Pins without an owning package still retain the exact sampled state.
        var passivePins = _passivePins;
        for (var index = 0; index < passivePins.Length; index++)
        {
            passivePins[index].ApplySampledLevel(resolved);
        }

        // A net may connect several input pins on the same package. The generic
        // pin path would attempt to queue that package once per changed pin. The
        // compiled fan-out updates every physical pin but performs one causal
        // wake decision for the package after all of its pins have sampled the
        // new electrical level.
        var groups = _observerGroups;
        for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            ref readonly var group = ref groups[groupIndex];
            var pins = group.Pins;
            var wake = false;
            for (var pinIndex = 0; pinIndex < pins.Length; pinIndex++)
            {
                var pin = pins[pinIndex];
                if (!pin.ApplySampledLevel(resolved) || !pin.WakeOwnerOnSampleChange)
                {
                    continue;
                }

                if (!pin.HandlesSampleChangeWithoutQueue(resolved))
                {
                    wake = true;
                }
            }

            if (wake)
            {
                Scheduler?.NotifyComponentActive(group.OwnerComponentIndex);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static DigitalLevel ResolveSingleDriver(DigitalPin driver)
    {
        return driver.DriveLevel switch
        {
            DigitalLevel.HighImpedance => DigitalLevel.Unknown,
            DigitalLevel.Low => DigitalLevel.Low,
            DigitalLevel.High => DigitalLevel.High,
            _ => DigitalLevel.Unknown
        };
    }

    private static DigitalLevel ResolveMultipleDrivers(DigitalPin[] drivers)
    {
        var strongest = DigitalDriveStrength.Weak;
        var foundDriver = false;
        var sawLow = false;
        var sawHigh = false;
        var sawUnknown = false;

        for (var index = 0; index < drivers.Length; index++)
        {
            var pin = drivers[index];
            var level = pin.DriveLevel;
            if (level == DigitalLevel.HighImpedance)
            {
                continue;
            }

            var strength = pin.DriveStrength;
            if (!foundDriver || strength > strongest)
            {
                foundDriver = true;
                strongest = strength;
                sawLow = false;
                sawHigh = false;
                sawUnknown = false;
            }
            else if (strength < strongest)
            {
                continue;
            }

            switch (level)
            {
                case DigitalLevel.Low:
                    sawLow = true;
                    break;
                case DigitalLevel.High:
                    sawHigh = true;
                    break;
                default:
                    sawUnknown = true;
                    break;
            }
        }

        return !foundDriver
            ? DigitalLevel.Unknown
            : sawLow && sawHigh
                ? DigitalLevel.Contention
                : sawUnknown
                    ? DigitalLevel.Unknown
                    : sawHigh
                        ? DigitalLevel.High
                        : DigitalLevel.Low;
    }

    internal void InvalidateCompiledTopology() => _pinSnapshotDirty = true;

    internal void CompileTopology()
    {
        if (!_pinSnapshotDirty) return;

        _resolvedPins = _pins.ToArray();
        var driverCount = 0;
        for (var index = 0; index < _resolvedPins.Length; index++)
        {
            if (_resolvedPins[index].Direction != PinDirection.Input) driverCount++;
        }

        _driverPins = new DigitalPin[driverCount];
        var driverIndex = 0;
        for (var index = 0; index < _resolvedPins.Length; index++)
        {
            var pin = _resolvedPins[index];
            if (pin.Direction != PinDirection.Input) _driverPins[driverIndex++] = pin;
        }
        _resolverKind = driverCount switch
        {
            0 => NetResolverKind.Floating,
            1 => NetResolverKind.SingleDriver,
            _ => NetResolverKind.MultipleDrivers
        };

        var grouped = new Dictionary<int, List<DigitalPin>>();
        var passive = new List<DigitalPin>();
        for (var index = 0; index < _resolvedPins.Length; index++)
        {
            var pin = _resolvedPins[index];
            if (pin.OwnerComponentIndex < 0)
            {
                passive.Add(pin);
                continue;
            }

            if (!grouped.TryGetValue(pin.OwnerComponentIndex, out var ownerPins))
            {
                ownerPins = [];
                grouped.Add(pin.OwnerComponentIndex, ownerPins);
            }
            ownerPins.Add(pin);
        }

        _passivePins = passive.ToArray();
        _observerGroups = grouped
            .Select(static pair => new ObserverGroup(pair.Key, pair.Value.ToArray()))
            .ToArray();
        _pinSnapshotDirty = false;
    }

    private readonly record struct ObserverGroup(int OwnerComponentIndex, DigitalPin[] Pins);

    private enum NetResolverKind : byte
    {
        Floating,
        SingleDriver,
        MultipleDrivers
    }
}

