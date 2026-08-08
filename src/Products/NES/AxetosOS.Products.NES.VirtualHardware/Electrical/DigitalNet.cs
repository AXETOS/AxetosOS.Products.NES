using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Electrical;

/// <summary>
/// A motherboard electrical connection. An attached output drive change resolves
/// the trace immediately and presents the resulting electrical level directly to
/// connected package pins. There is no signal queue, component queue, scheduler,
/// or board-wide settle pass in the runtime propagation path.
///
/// When one package changes several outputs during one internal reaction, its
/// base package presents all affected traces before any receiver executes. That
/// package-boundary atomicity prevents software-only half-updated buses while
/// retaining direct, synchronous propagation. The trace never inspects a
/// receiver's activation/edge/divider semantics: transport is topology-only;
/// the receiving DigitalPin/chip owns the decision to wake or return.
/// </summary>
public sealed class DigitalNet
{
    private readonly List<DigitalPin> _pins = [];
    private DigitalPin[] _driverPins = [];
    private CompiledInputRoute[] _inputRoutes = [];
    private DigitalPin[] _observerPins = [];
    private bool _pinSnapshotDirty = true;
    private bool _compiled;
    private NetResolverKind _resolverKind;
    private DigitalPin? _compiledSingleDriverSource;
    private DigitalPin[] _compiledSingleDriverObservers = [];

    public DigitalNet(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public string Name { get; }
    public IReadOnlyList<DigitalPin> Pins => _pins;
    public DigitalLevel Level { get; private set; } = DigitalLevel.Unknown;

    internal VirtualHardwareSimulator? Diagnostics { get; set; }

    [ThreadStatic]
    private static PropagationFrame? s_freePropagationFrames;

    /// <summary>
    /// Publishes one package's complete output change-set through motherboard
    /// traces. This is a synchronous call frame, not an event/signal queue: all
    /// affected traces are resolved immediately, each destination package is
    /// accumulated once, and every destination then reacts once before return.
    /// Peer-package fan-out remains in the electrical layer; the source package
    /// never stores or inspects another package.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void PublishChangedOutputPins(DigitalPin[] changedPins, int count)
    {
        var frame = AcquirePropagationFrame();

        try
        {
            VirtualHardwareSimulator? diagnostics = null;

            // Every changed package output already contains its final drive state
            // before the package boundary is published. Resolve directly from
            // those physical pin states. If two changed package pins share one
            // trace, the first resolution therefore already sees the complete
            // final package state and the second becomes a cheap no-change check.
            // This avoids maintaining a second software copy of every driver.
            for (var index = 0; index < count; index++)
            {
                var pin = changedPins[index];
                var net = pin.Net;
                if (net is null) continue;
                diagnostics ??= net.Diagnostics;
                net.PresentCurrentDriverState(pin, frame);
            }

            for (var index = 0; index < frame.Count; index++)
            {
                var entry = frame[index];
                var receiver = entry.Component;
                var changedInputMask = receiver.TakePendingInputChangesForDirectReaction();
                if (changedInputMask == 0) continue;

                if (diagnostics is null)
                    receiver.ReceiveInputChanges(changedInputMask);
                else
                    diagnostics.DeliverInputImmediate(entry.ComponentIndex, receiver, changedInputMask);
            }
        }
        finally
        {
            ReleasePropagationFrame(frame);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static PropagationFrame AcquirePropagationFrame()
    {
        var frame = s_freePropagationFrames;
        if (frame is null) return new PropagationFrame();
        s_freePropagationFrames = frame.Next;
        frame.Next = null;
        return frame;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReleasePropagationFrame(PropagationFrame frame)
    {
        frame.Reset();
        frame.Next = s_freePropagationFrames;
        s_freePropagationFrames = frame;
    }

    public void Connect(DigitalPin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);

        if (pin.Net is not null && !ReferenceEquals(pin.Net, this))
        {
            throw new InvalidOperationException($"Pin '{pin.Name}' is already connected to net '{pin.Net.Name}'.");
        }

        if (_pins.Contains(pin)) return;

        _pins.Add(pin);
        _pinSnapshotDirty = true;
        _compiledSingleDriverSource = null;
        _compiledSingleDriverObservers = [];
        pin.Net = this;
        pin.SetObservedLevel(Level);

        // A live board stays live when a connector/package is attached. Rebuild
        // only this trace and propagate the resulting electrical level directly.
        if (_compiled)
        {
            CompileTopology();
            if (ResolveAndPresent()) ReactPresentedInputs();
        }
    }

    /// <summary>
    /// Compiles the common master-clock case once: one known strong digital
    /// source drives this trace. Runtime clock edges can then bypass the generic
    /// resolver and present the already-known 0/1 source level directly.
    /// </summary>
    internal void ValidateCompiledSingleDriverSource(DigitalPin source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_pinSnapshotDirty || !_compiled) CompileTopology();
        if (_resolverKind != NetResolverKind.SingleDriver ||
            _driverPins.Length != 1 ||
            !ReferenceEquals(_driverPins[0], source))
        {
            throw new InvalidOperationException(
                $"Net '{Name}' is not a compiled single-driver trace for pin '{source.Name}'.");
        }

        _compiledSingleDriverSource = source;
        _compiledSingleDriverObservers = _observerPins
            .Where(pin => !ReferenceEquals(pin, source))
            .ToArray();
    }

    /// <summary>
    /// Immediate no-queue propagation for a topology-validated single-driver
    /// source such as the NES master oscillator. All pins first see the new
    /// physical level, then receiving chips react.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void PropagateCompiledSingleDriverFast()
    {
        var source = _compiledSingleDriverSource!;
        var resolved = source.DriveLevel;
        if (Level == resolved) return;

        Level = resolved;
        source.SetObservedOutputLevel(resolved);

        var observers = _compiledSingleDriverObservers;
        for (var index = 0; index < observers.Length; index++)
            observers[index].SetObservedOutputLevel(resolved);

        var routes = _inputRoutes;
        if (routes.Length == 2)
        {
            var wake0 = routes[0].AcceptCompiledInputLevel(resolved);
            var wake1 = routes[1].AcceptCompiledInputLevel(resolved);
            if (wake0) routes[0].ReactCompiledDirect();
            if (wake1) routes[1].ReactCompiledDirect();
            return;
        }

        if (routes.Length == 1)
        {
            if (routes[0].AcceptCompiledInputLevel(resolved))
                routes[0].ReactCompiledDirect();
            return;
        }

        ulong wakeRoutes = 0;
        var routeCount = Math.Min(routes.Length, 64);
        for (var index = 0; index < routeCount; index++)
        {
            if (routes[index].AcceptCompiledInputLevel(resolved))
                wakeRoutes |= 1UL << index;
        }
        for (var index = 0; index < routeCount; index++)
        {
            if ((wakeRoutes & (1UL << index)) != 0)
                routes[index].ReactCompiledDirect();
        }

        for (var index = 64; index < routes.Length; index++)
        {
            if (routes[index].AcceptCompiledInputLevel(resolved))
                routes[index].ReactCompiledDirect();
        }
    }

    /// <summary>
    /// Diagnostics-only counterpart of PropagateCompiledSingleDriverFast. It
    /// preserves the exact compiled transport path used by normal execution,
    /// but samples electrical transport and receiver package timing.
    /// </summary>
    internal void PropagateCompiledSingleDriverProfiled(VirtualHardwareSimulator diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        diagnostics.RecordNetResolutionAttempt();
        var profileStarted = diagnostics.BeginNetResolutionTimingSample();

        var source = _compiledSingleDriverSource!;
        var resolved = source.DriveLevel;
        if (Level == resolved)
        {
            diagnostics.EndNetResolutionTimingSample(profileStarted);
            return;
        }

        Level = resolved;
        source.SetObservedOutputLevel(resolved);

        var observers = _compiledSingleDriverObservers;
        for (var index = 0; index < observers.Length; index++)
            observers[index].SetObservedOutputLevel(resolved);

        diagnostics.RecordNetLevelChange(observers.Length + _inputRoutes.Length);

        var routes = _inputRoutes;
        if (routes.Length == 2)
        {
            var wake0 = routes[0].AcceptCompiledInputLevel(resolved);
            var wake1 = routes[1].AcceptCompiledInputLevel(resolved);
            diagnostics.EndNetResolutionTimingSample(profileStarted);
            if (wake0) routes[0].ReactCompiledDirect(diagnostics);
            if (wake1) routes[1].ReactCompiledDirect(diagnostics);
            return;
        }

        if (routes.Length == 1)
        {
            var wake = routes[0].AcceptCompiledInputLevel(resolved);
            diagnostics.EndNetResolutionTimingSample(profileStarted);
            if (wake) routes[0].ReactCompiledDirect(diagnostics);
            return;
        }

        ulong wakeRoutes = 0;
        var routeCount = Math.Min(routes.Length, 64);
        for (var index = 0; index < routeCount; index++)
        {
            if (routes[index].AcceptCompiledInputLevel(resolved))
                wakeRoutes |= 1UL << index;
        }
        bool[]? extraWakeRoutes = null;
        if (routes.Length > 64)
        {
            extraWakeRoutes = new bool[routes.Length - 64];
            for (var index = 64; index < routes.Length; index++)
            {
                // More than 64 receivers is extremely uncommon. Profiling may
                // allocate here, but normal execution never enters this method.
                extraWakeRoutes[index - 64] = routes[index].AcceptCompiledInputLevel(resolved);
            }
        }

        diagnostics.EndNetResolutionTimingSample(profileStarted);

        for (var index = 0; index < routeCount; index++)
        {
            if ((wakeRoutes & (1UL << index)) != 0)
                routes[index].ReactCompiledDirect(diagnostics);
        }
        if (extraWakeRoutes is not null)
        {
            for (var index = 64; index < routes.Length; index++)
            {
                if (extraWakeRoutes[index - 64])
                    routes[index].ReactCompiledDirect(diagnostics);
            }
        }
    }

    /// <summary>
    /// Called by an attached output pin at the exact point its drive changes
    /// outside a package reaction. The physical consequence is presented and
    /// receivers react synchronously before this call returns.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void PropagateDriverChange(DigitalPin source)
    {
        if (!_compiled) return;
        if (!PresentCurrentDriverState(source, null)) return;
        ReactPresentedInputs();
    }

    /// <summary>
    /// Resolves and presents this trace from its current package-driver state but
    /// deliberately does not execute receiving packages. The one-driver trace
    /// retains its direct source fast path; common three/four-driver shared buses
    /// use compact unrolled electrical resolvers over the package pin states.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool PresentDriverChange(DigitalPin source)
    {
        if (!_compiled) return false;
        return PresentCurrentDriverState(source, null);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool PresentCurrentDriverState(DigitalPin source, PropagationFrame? receivers)
    {
        if (!_compiled) return false;
        return IsCompiledSingleDriver(source)
            ? PresentResolvedSingleDriver(source, receivers)
            : ResolveAndPresent(receivers);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsCompiledSingleDriver(DigitalPin source) =>
        _resolverKind == NetResolverKind.SingleDriver
        && ReferenceEquals(_driverPins[0], source)
        && Diagnostics is null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool PresentResolvedSingleDriver(DigitalPin source, PropagationFrame? receivers)
    {
        var drive = source.DriveLevel;
        var resolved = drive is DigitalLevel.Low or DigitalLevel.High ? drive : DigitalLevel.Unknown;
        if (Level == resolved) return false;
        Level = resolved;

        var observers = _observerPins;
        for (var index = 0; index < observers.Length; index++)
            observers[index].SetObservedOutputLevel(resolved);

        var routes = _inputRoutes;
        if (routes.Length == 1) routes[0].Accept(resolved, receivers);
        else if (routes.Length == 2)
        {
            routes[0].Accept(resolved, receivers);
            routes[1].Accept(resolved, receivers);
        }
        else
        {
            for (var index = 0; index < routes.Length; index++)
                routes[index].Accept(resolved, receivers);
        }
        return true;
    }

    /// <summary>
    /// Executes any packages whose inputs were changed during a preceding
    /// presentation. Pending masks live on the receiving packages themselves;
    /// the first route that reaches a package consumes its complete mask.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ReactPresentedInputs()
    {
        var routes = _inputRoutes;
        var diagnostics = Diagnostics;
        if (diagnostics is null)
        {
            for (var index = 0; index < routes.Length; index++)
                routes[index].ReactFast();
            return;
        }

        for (var index = 0; index < routes.Length; index++)
            routes[index].ReactProfiled(diagnostics);
    }

    public DigitalLevel Resolve()
    {
        if (_pinSnapshotDirty) CompileTopology();
        if (ResolveAndPresent()) ReactPresentedInputs();
        return Level;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool ResolveAndPresent(PropagationFrame? receivers = null)
    {
        var diagnostics = Diagnostics;
        return diagnostics is null
            ? ResolveAndPresentFast(receivers)
            : ResolveAndPresentProfiled(receivers, diagnostics);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool ResolveAndPresentFast(PropagationFrame? receivers)
    {
        var resolved = _resolverKind switch
        {
            NetResolverKind.Floating => DigitalLevel.Unknown,
            NetResolverKind.SingleDriver => ResolveSingleDriver(_driverPins[0]),
            NetResolverKind.TwoDrivers => ResolveTwoDrivers(_driverPins[0], _driverPins[1]),
            NetResolverKind.ThreeDrivers => ResolveThreeDrivers(_driverPins[0], _driverPins[1], _driverPins[2]),
            NetResolverKind.FourDrivers => ResolveFourDrivers(_driverPins[0], _driverPins[1], _driverPins[2], _driverPins[3]),
            _ => ResolveMultipleDrivers(_driverPins)
        };

        if (Level == resolved) return false;
        Level = resolved;

        var observers = _observerPins;
        for (var index = 0; index < observers.Length; index++)
            observers[index].SetObservedOutputLevel(resolved);

        var routes = _inputRoutes;
        if (routes.Length == 1)
        {
            routes[0].Accept(resolved, receivers);
        }
        else if (routes.Length == 2)
        {
            routes[0].Accept(resolved, receivers);
            routes[1].Accept(resolved, receivers);
        }
        else
        {
            for (var index = 0; index < routes.Length; index++)
                routes[index].Accept(resolved, receivers);
        }

        return true;
    }

    private bool ResolveAndPresentProfiled(
        PropagationFrame? receivers,
        VirtualHardwareSimulator diagnostics)
    {
        diagnostics.RecordNetResolutionAttempt();
        var profileStarted = diagnostics.BeginNetResolutionTimingSample();

        var resolved = _resolverKind switch
        {
            NetResolverKind.Floating => DigitalLevel.Unknown,
            NetResolverKind.SingleDriver => ResolveSingleDriver(_driverPins[0]),
            NetResolverKind.TwoDrivers => ResolveTwoDrivers(_driverPins[0], _driverPins[1]),
            NetResolverKind.ThreeDrivers => ResolveThreeDrivers(_driverPins[0], _driverPins[1], _driverPins[2]),
            NetResolverKind.FourDrivers => ResolveFourDrivers(_driverPins[0], _driverPins[1], _driverPins[2], _driverPins[3]),
            _ => ResolveMultipleDrivers(_driverPins)
        };

        if (Level == resolved)
        {
            diagnostics.EndNetResolutionTimingSample(profileStarted);
            return false;
        }

        Level = resolved;
        diagnostics.RecordNetLevelChange(_observerPins.Length + _inputRoutes.Length);

        var observers = _observerPins;
        for (var index = 0; index < observers.Length; index++)
            observers[index].SetObservedOutputLevel(resolved);

        var routes = _inputRoutes;
        if (routes.Length == 1)
        {
            routes[0].Accept(resolved, receivers);
        }
        else if (routes.Length == 2)
        {
            routes[0].Accept(resolved, receivers);
            routes[1].Accept(resolved, receivers);
        }
        else
        {
            for (var index = 0; index < routes.Length; index++)
                routes[index].Accept(resolved, receivers);
        }

        diagnostics.EndNetResolutionTimingSample(profileStarted);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static DigitalLevel ResolveSingleDriver(DigitalPin driver) => driver.DriveLevel switch
    {
        DigitalLevel.HighImpedance => DigitalLevel.Unknown,
        DigitalLevel.Low => DigitalLevel.Low,
        DigitalLevel.High => DigitalLevel.High,
        _ => DigitalLevel.Unknown
    };

    /// <summary>
    /// Common shared-line resolver with exactly two possible drivers. This is
    /// electrically identical to the generic resolver but avoids its array
    /// scan/state-machine overhead on every transition.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static DigitalLevel ResolveTwoDrivers(DigitalPin first, DigitalPin second)
    {
        var firstLevel = first.DriveLevel;
        var secondLevel = second.DriveLevel;

        if (firstLevel == DigitalLevel.HighImpedance) return ResolveSingleDriver(second);
        if (secondLevel == DigitalLevel.HighImpedance) return ResolveSingleDriver(first);

        var firstStrength = first.DriveStrength;
        var secondStrength = second.DriveStrength;
        if (firstStrength > secondStrength) return ResolveSingleDriver(first);
        if (secondStrength > firstStrength) return ResolveSingleDriver(second);

        if ((firstLevel == DigitalLevel.Low && secondLevel == DigitalLevel.High) ||
            (firstLevel == DigitalLevel.High && secondLevel == DigitalLevel.Low))
        {
            return DigitalLevel.Contention;
        }

        if (firstLevel == secondLevel)
        {
            return firstLevel is DigitalLevel.Low or DigitalLevel.High
                ? firstLevel
                : DigitalLevel.Unknown;
        }

        return DigitalLevel.Unknown;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static DigitalLevel ResolveThreeDrivers(
        DigitalPin first,
        DigitalPin second,
        DigitalPin third)
    {
        var strongest = DigitalDriveStrength.Weak;
        var haveDriver = false;
        var low = false;
        var high = false;
        var unknown = false;
        AccumulateDriver(first, ref strongest, ref haveDriver, ref low, ref high, ref unknown);
        AccumulateDriver(second, ref strongest, ref haveDriver, ref low, ref high, ref unknown);
        AccumulateDriver(third, ref strongest, ref haveDriver, ref low, ref high, ref unknown);
        return FinishDriverResolution(haveDriver, low, high, unknown);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static DigitalLevel ResolveFourDrivers(
        DigitalPin first,
        DigitalPin second,
        DigitalPin third,
        DigitalPin fourth)
    {
        var strongest = DigitalDriveStrength.Weak;
        var haveDriver = false;
        var low = false;
        var high = false;
        var unknown = false;
        AccumulateDriver(first, ref strongest, ref haveDriver, ref low, ref high, ref unknown);
        AccumulateDriver(second, ref strongest, ref haveDriver, ref low, ref high, ref unknown);
        AccumulateDriver(third, ref strongest, ref haveDriver, ref low, ref high, ref unknown);
        AccumulateDriver(fourth, ref strongest, ref haveDriver, ref low, ref high, ref unknown);
        return FinishDriverResolution(haveDriver, low, high, unknown);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AccumulateDriver(
        DigitalPin driver,
        ref DigitalDriveStrength strongest,
        ref bool haveDriver,
        ref bool low,
        ref bool high,
        ref bool unknown)
    {
        var level = driver.DriveLevel;
        if (level == DigitalLevel.HighImpedance) return;

        var strength = driver.DriveStrength;
        if (!haveDriver || strength > strongest)
        {
            strongest = strength;
            haveDriver = true;
            low = level == DigitalLevel.Low;
            high = level == DigitalLevel.High;
            unknown = level is not (DigitalLevel.Low or DigitalLevel.High);
            return;
        }

        if (strength < strongest) return;
        low |= level == DigitalLevel.Low;
        high |= level == DigitalLevel.High;
        unknown |= level is not (DigitalLevel.Low or DigitalLevel.High);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static DigitalLevel FinishDriverResolution(
        bool haveDriver,
        bool low,
        bool high,
        bool unknown)
    {
        if (!haveDriver) return DigitalLevel.Unknown;
        if (low && high) return DigitalLevel.Contention;
        if (unknown) return DigitalLevel.Unknown;
        return high ? DigitalLevel.High : DigitalLevel.Low;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static DigitalLevel ResolveMultipleDrivers(DigitalPin[] drivers)
    {
        var strongest = DigitalDriveStrength.Weak;
        var haveDriver = false;
        var low = false;
        var high = false;
        var unknown = false;

        for (var index = 0; index < drivers.Length; index++)
            AccumulateDriver(drivers[index], ref strongest, ref haveDriver, ref low, ref high, ref unknown);

        return FinishDriverResolution(haveDriver, low, high, unknown);
    }

    internal void CompileTopology()
    {
        if (!_pinSnapshotDirty && _compiled) return;

        var pins = _pins.ToArray();
        _driverPins = pins.Where(pin => pin.IsOutputCapable).ToArray();
        _inputRoutes = pins
            .Where(pin => pin.IsInputCapable)
            .Select(pin => new CompiledInputRoute(pin))
            .ToArray();
        _observerPins = pins.Where(pin => !pin.IsInputCapable).ToArray();

        _resolverKind = _driverPins.Length switch
        {
            0 => NetResolverKind.Floating,
            1 => NetResolverKind.SingleDriver,
            2 => NetResolverKind.TwoDrivers,
            3 => NetResolverKind.ThreeDrivers,
            4 => NetResolverKind.FourDrivers,
            _ => NetResolverKind.MultipleDrivers
        };

        _compiledSingleDriverSource = null;
        _compiledSingleDriverObservers = [];
        _pinSnapshotDirty = false;
        _compiled = true;
    }

    /// <summary>
    /// Presents this trace's initial electrical state without executing a chip.
    /// The simulator performs this for every trace first so each package sees a
    /// complete motherboard state on its first real input reaction.
    /// </summary>
    internal bool PresentInitialState()
    {
        if (_pinSnapshotDirty) CompileTopology();
        return ResolveAndPresent();
    }

    /// <summary>
    /// Compatibility helper for callers that explicitly initialize one trace.
    /// Normal simulator startup uses the board-wide two-phase initial publish.
    /// </summary>
    internal void PropagateInitialState()
    {
        if (PresentInitialState()) ReactPresentedInputs();
    }

    private readonly struct CompiledInputRoute
    {
        private readonly DigitalPin _pin;
        private readonly VirtualHardwareComponent? _component;
        private readonly int _componentIndex;
        private readonly ulong _inputMask;

        public CompiledInputRoute(DigitalPin pin)
        {
            _pin = pin;
            _component = pin.OwnerComponent;
            _componentIndex = pin.OwnerComponentIndex;
            _inputMask = pin.InputChangeMask;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AcceptCompiledInputLevel(DigitalLevel level) =>
            _component is not null && _pin.TryAcceptInputLevel(level);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReactCompiledDirect()
        {
            if (_component is null) return;
            _component.ReceiveInputChanges(_inputMask);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReactCompiledDirect(VirtualHardwareSimulator diagnostics)
        {
            if (_component is null) return;
            diagnostics.DeliverInputImmediate(_componentIndex, _component, _inputMask);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Accept(DigitalLevel level, PropagationFrame? receivers)
        {
            if (_component is null || !_pin.TryAcceptInputLevel(level)) return;
            if (_component.StageInputChanges(_inputMask) && receivers is not null)
                receivers.Add(_component, _componentIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReactFast()
        {
            if (_component is null) return;
            var changedInputMask = _component.TakePendingInputChangesForDirectReaction();
            if (changedInputMask != 0)
                _component.ReceiveInputChanges(changedInputMask);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReactProfiled(VirtualHardwareSimulator diagnostics)
        {
            if (_component is null) return;
            var changedInputMask = _component.TakePendingInputChangesForDirectReaction();
            if (changedInputMask != 0)
                diagnostics.DeliverInputImmediate(_componentIndex, _component, changedInputMask);
        }
    }

    private sealed class PropagationFrame
    {
        private ReceiverEntry[] _receivers = new ReceiverEntry[16];
        public int Count { get; private set; }
        public PropagationFrame? Next;

        public ReceiverEntry this[int index] => _receivers[index];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(VirtualHardwareComponent component, int componentIndex)
        {
            if (Count == _receivers.Length) Array.Resize(ref _receivers, _receivers.Length * 2);
            _receivers[Count++] = new ReceiverEntry(component, componentIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Reset()
        {
            // Receiver components are permanent members of the compiled board.
            // Stale references behind Count are harmless and retaining them
            // avoids clearing this hot reusable frame after every propagation.
            Count = 0;
        }
    }

    private readonly record struct ReceiverEntry(
        VirtualHardwareComponent Component,
        int ComponentIndex);

    private enum NetResolverKind : byte
    {
        Floating,
        SingleDriver,
        TwoDrivers,
        ThreeDrivers,
        FourDrivers,
        MultipleDrivers
    }
}
