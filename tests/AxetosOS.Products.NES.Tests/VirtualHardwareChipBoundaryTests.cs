using System.Reflection;
using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareChipBoundaryTests
{
    [Fact]
    public void Component_contract_has_no_polling_evaluate_entry_point()
    {
        Assert.Null(typeof(IVirtualHardwareComponent).GetMethod("Evaluate"));
        Assert.Null(typeof(IVirtualHardwareComponent).GetMethod("PowerOn"));
        Assert.Null(typeof(IVirtualHardwareComponent).GetMethod("Reset"));
        Assert.Null(typeof(VirtualHardwareComponent).GetMethod(
            "Evaluate",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.Null(typeof(VirtualHardwareComponent).GetMethod(
            "PowerOn",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.Null(typeof(VirtualHardwareComponent).GetMethod(
            "Reset",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
    }

    [Fact]
    public void Concrete_chip_packages_hold_no_board_or_simulator_references()
    {
        var componentType = typeof(VirtualHardwareComponent);
        var forbiddenTypes = new[]
        {
            typeof(VirtualHardwareBoard),
            typeof(VirtualHardwareSimulator)
        };

        var concretePackages = componentType.Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract && componentType.IsAssignableFrom(type));

        foreach (var package in concretePackages)
        {
            var fields = package.GetFields(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly);

            foreach (var field in fields)
            {
                Assert.False(
                    forbiddenTypes.Any(forbidden => forbidden.IsAssignableFrom(field.FieldType)),
                    $"{package.FullName} retains forbidden field {field.Name} of type {field.FieldType.FullName}.");
                Assert.False(
                    typeof(IVirtualHardwareComponent).IsAssignableFrom(field.FieldType),
                    $"{package.FullName} retains peer package field {field.Name} of type {field.FieldType.FullName}.");
                Assert.False(
                    typeof(Delegate).IsAssignableFrom(field.FieldType),
                    $"{package.FullName} retains callback field {field.Name} of type {field.FieldType.FullName}.");
            }
        }
    }

    [Fact]
    public void Concrete_chip_packages_expose_no_lifecycle_or_callback_side_channels()
    {
        var componentType = typeof(VirtualHardwareComponent);
        var concretePackages = componentType.Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract && componentType.IsAssignableFrom(type));

        foreach (var package in concretePackages)
        {
            Assert.Null(package.GetMethod("Evaluate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly));
            Assert.Null(package.GetMethod("PowerOn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly));
            Assert.Null(package.GetMethod("Reset", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly));
            Assert.Empty(package.GetEvents(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly));
        }
    }

    [Fact]
    public void Output_only_pin_changes_never_activate_the_owning_package()
    {
        var board = new VirtualHardwareBoard("output-only-package");
        var package = board.Add(new OutputOnlyProbe("probe"));
        board.Connect("output", package.Output);
        var simulator = new VirtualHardwareSimulator(board);

        package.Set(DigitalLevel.High);
        simulator.Settle();

        Assert.Equal(0, package.InputActivationCount);
        Assert.Equal(DigitalLevel.High, package.Output.SampledLevel);
    }

    [Fact]
    public void Releasing_bidirectional_pin_does_not_invent_an_input_transition_when_external_level_is_unchanged()
    {
        var board = new VirtualHardwareBoard("bidirectional-bus-handoff");
        var source = board.Add(new DigitalSignalSource("external", DigitalLevel.Low));
        var package = board.Add(new BidirectionalProbe("probe"));
        board.Connect("BUS", source.Output, package.Bus);
        var simulator = new VirtualHardwareSimulator(board);

        simulator.Settle();
        var baselineActivations = package.InputActivationCount;

        package.Drive(DigitalLevel.Low);
        simulator.Settle();
        Assert.Equal(baselineActivations, package.InputActivationCount);
        Assert.Equal(DigitalLevel.Low, package.Bus.SampledLevel);

        package.Release();
        simulator.Settle();

        // The package drove and released its own pin, but the external source
        // stayed Low throughout. There was therefore no incoming transition.
        Assert.Equal(baselineActivations, package.InputActivationCount);
        Assert.Equal(DigitalLevel.Low, package.Bus.SampledLevel);
    }

    [Fact]
    public void A_package_with_no_changed_input_pin_is_not_artificially_started()
    {
        var board = new VirtualHardwareBoard("no-startup-kick");
        var package = board.Add(new InputProbe("probe"));
        board.Connect("floating", package.Input);
        var simulator = new VirtualHardwareSimulator(board);

        board.PowerOn();
        simulator.Settle();
        simulator.Settle();

        Assert.Equal(0, package.InputActivationCount);
    }

    [Fact]
    public void One_package_output_change_set_reaches_each_destination_package_once()
    {
        var board = new VirtualHardwareBoard("coalesced-direct-fanout");
        var trigger = board.Add(new DigitalSignalSource("trigger", DigitalLevel.Low));
        var source = board.Add(new DualOutputProbe("source"));
        var receiver = board.Add(new DualInputProbe("receiver"));
        board.Connect("trigger.net", trigger.Output, source.Trigger);
        board.Connect("a.net", source.OutputA, receiver.InputA);
        board.Connect("b.net", source.OutputB, receiver.InputB);
        _ = new VirtualHardwareSimulator(board);

        var baseline = receiver.InputActivationCount;
        trigger.Set(DigitalLevel.High);

        Assert.Equal(baseline + 1, receiver.InputActivationCount);
        Assert.Equal(0b11UL, receiver.LastChangedInputMask);
        Assert.Equal(DigitalLevel.High, receiver.InputA.SampledLevel);
        Assert.Equal(DigitalLevel.High, receiver.InputB.SampledLevel);
    }

    [Fact]
    public void Compiled_master_clock_trace_propagates_immediately_without_generic_settle_work()
    {
        var board = new VirtualHardwareBoard("compiled-clock-direct");
        var clock = board.Add(new AxetosOS.Products.NES.VirtualHardware.Components.Clock.DigitalOscillator("clock", 1));
        var receiver = board.Add(new InputProbe("receiver"));
        board.Connect("clock.net", clock.Output, receiver.Input);
        var simulator = new VirtualHardwareSimulator(board);
        var plan = new CompiledClockExecutionPlan(clock, simulator);
        var baseline = receiver.InputActivationCount;

        plan.AdvanceHalfCycle();

        Assert.Equal(DigitalLevel.High, clock.Output.SampledLevel);
        Assert.Equal(DigitalLevel.High, receiver.Input.SampledLevel);
        Assert.Equal(baseline + 1, receiver.InputActivationCount);
    }

    private sealed class OutputOnlyProbe : VirtualHardwareComponent
    {
        public OutputOnlyProbe(string componentId) : base(componentId)
        {
            Output = AddPin("OUT", PinDirection.Output);
        }

        public DigitalPin Output { get; }
        public int InputActivationCount { get; private set; }

        public void Set(DigitalLevel level) => Output.Drive(level);

        protected override void OnInputChanges(ulong changedInputMask) => InputActivationCount++;
    }

    private sealed class BidirectionalProbe : VirtualHardwareComponent
    {
        public BidirectionalProbe(string componentId) : base(componentId)
        {
            Bus = AddPin("BUS", PinDirection.Bidirectional);
        }

        public DigitalPin Bus { get; }
        public int InputActivationCount { get; private set; }

        public void Drive(DigitalLevel level) => Bus.Drive(level);
        public void Release() => Bus.Release();

        protected override void OnInputChanges(ulong changedInputMask) => InputActivationCount++;
    }

    private sealed class DualOutputProbe : VirtualHardwareComponent
    {
        public DualOutputProbe(string componentId) : base(componentId)
        {
            Trigger = AddPin("TRIGGER", PinDirection.Input);
            OutputA = AddPin("A", PinDirection.Output);
            OutputB = AddPin("B", PinDirection.Output);
        }

        public DigitalPin Trigger { get; }
        public DigitalPin OutputA { get; }
        public DigitalPin OutputB { get; }

        protected override void OnInputChanges(ulong changedInputMask)
        {
            if (Trigger.SampledLevel != DigitalLevel.High) return;
            OutputA.Drive(DigitalLevel.High);
            OutputB.Drive(DigitalLevel.High);
        }
    }

    private sealed class DualInputProbe : VirtualHardwareComponent
    {
        public DualInputProbe(string componentId) : base(componentId)
        {
            InputA = AddPin("A", PinDirection.Input);
            InputB = AddPin("B", PinDirection.Input);
        }

        public DigitalPin InputA { get; }
        public DigitalPin InputB { get; }
        public int InputActivationCount { get; private set; }
        public ulong LastChangedInputMask { get; private set; }

        protected override void OnInputChanges(ulong changedInputMask)
        {
            InputActivationCount++;
            LastChangedInputMask = changedInputMask;
        }
    }

    private sealed class InputProbe : VirtualHardwareComponent
    {
        public InputProbe(string componentId) : base(componentId)
        {
            Input = AddPin("IN", PinDirection.Input);
        }

        public DigitalPin Input { get; }
        public int InputActivationCount { get; private set; }

        protected override void OnInputChanges(ulong changedInputMask) => InputActivationCount++;
    }
}
