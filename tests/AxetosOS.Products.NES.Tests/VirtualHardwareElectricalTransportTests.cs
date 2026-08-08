using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareElectricalTransportTests
{
    [Fact]
    public void Ordinary_input_changes_still_wake_for_every_real_level_transition()
    {
        var board = new VirtualHardwareBoard("ordinary-input-fastpath");
        var source = board.Add(new OutputProbe("source"));
        var receiver = board.Add(new InputProbe("receiver", DigitalInputActivation.AnyChange));
        board.Connect("signal", source.Output, receiver.Input);
        _ = new VirtualHardwareSimulator(board);

        var baseline = receiver.ActivationCount;
        source.Set(DigitalLevel.High);
        source.Set(DigitalLevel.Low);

        Assert.Equal(baseline + 2, receiver.ActivationCount);
        Assert.Equal(DigitalLevel.Low, receiver.Input.SampledLevel);
    }

    [Fact]
    public void Two_driver_fast_resolver_preserves_contention_and_release_semantics()
    {
        var board = new VirtualHardwareBoard("two-driver-fast-resolver");
        var first = board.Add(new OutputProbe("first"));
        var second = board.Add(new OutputProbe("second"));
        var receiver = board.Add(new InputProbe("receiver", DigitalInputActivation.AnyChange));
        board.Connect("shared", first.Output, second.Output, receiver.Input);
        _ = new VirtualHardwareSimulator(board);

        first.Set(DigitalLevel.Low);
        Assert.Equal(DigitalLevel.Low, receiver.Input.SampledLevel);

        second.Set(DigitalLevel.High);
        Assert.Equal(DigitalLevel.Contention, receiver.Input.SampledLevel);

        second.Release();
        Assert.Equal(DigitalLevel.Low, receiver.Input.SampledLevel);
    }


    [Fact]
    public void Multi_driver_resolver_preserves_strength_contention_and_release_semantics()
    {
        var board = new VirtualHardwareBoard("multi-driver-compiled-resolver");
        var first = board.Add(new OutputProbe("first"));
        var second = board.Add(new OutputProbe("second"));
        var third = board.Add(new OutputProbe("third"));
        var receiver = board.Add(new InputProbe("receiver", DigitalInputActivation.AnyChange));
        board.Connect("shared", first.Output, second.Output, third.Output, receiver.Input);
        _ = new VirtualHardwareSimulator(board);

        first.Set(DigitalLevel.High, DigitalDriveStrength.Weak);
        Assert.Equal(DigitalLevel.High, receiver.Input.SampledLevel);

        second.Set(DigitalLevel.Low);
        Assert.Equal(DigitalLevel.Low, receiver.Input.SampledLevel);

        third.Set(DigitalLevel.High);
        Assert.Equal(DigitalLevel.Contention, receiver.Input.SampledLevel);

        third.Release();
        Assert.Equal(DigitalLevel.Low, receiver.Input.SampledLevel);

        second.Release();
        Assert.Equal(DigitalLevel.High, receiver.Input.SampledLevel);
    }

    [Fact]
    public void Four_driver_hot_bus_resolver_preserves_strength_and_unknown_semantics()
    {
        var board = new VirtualHardwareBoard("four-driver-hot-resolver");
        var first = board.Add(new OutputProbe("first"));
        var second = board.Add(new OutputProbe("second"));
        var third = board.Add(new OutputProbe("third"));
        var fourth = board.Add(new OutputProbe("fourth"));
        var receiver = board.Add(new InputProbe("receiver", DigitalInputActivation.AnyChange));
        board.Connect("shared", first.Output, second.Output, third.Output, fourth.Output, receiver.Input);
        _ = new VirtualHardwareSimulator(board);

        first.Set(DigitalLevel.High, DigitalDriveStrength.Weak);
        second.Set(DigitalLevel.Low, DigitalDriveStrength.Weak);
        Assert.Equal(DigitalLevel.Contention, receiver.Input.SampledLevel);

        third.Set(DigitalLevel.Low);
        Assert.Equal(DigitalLevel.Low, receiver.Input.SampledLevel);

        fourth.Set(DigitalLevel.Unknown);
        Assert.Equal(DigitalLevel.Unknown, receiver.Input.SampledLevel);

        fourth.Release();
        Assert.Equal(DigitalLevel.Low, receiver.Input.SampledLevel);
    }

    [Fact]
    public void Package_batch_with_multiple_changed_drivers_on_one_trace_presents_only_final_electrical_state()
    {
        var board = new VirtualHardwareBoard("same-trace-package-batch");
        var trigger = board.Add(new OutputProbe("trigger"));
        var source = board.Add(new DualOutputProbe("source"));
        var bias = board.Add(new OutputProbe("bias"));
        var receiver = board.Add(new RecordingInputProbe("receiver"));
        board.Connect("trigger.net", trigger.Output, source.Trigger);
        board.Connect("shared", source.First, source.Second, bias.Output, receiver.Input);
        _ = new VirtualHardwareSimulator(board);
        bias.Set(DigitalLevel.High, DigitalDriveStrength.Weak);

        var baseline = receiver.ObservedLevels.Count;
        trigger.Set(DigitalLevel.High);

        Assert.Equal(baseline + 1, receiver.ObservedLevels.Count);
        Assert.Equal(DigitalLevel.Contention, receiver.ObservedLevels[^1]);
        Assert.Equal(DigitalLevel.Contention, receiver.Input.SampledLevel);
    }

    private sealed class OutputProbe : VirtualHardwareComponent
    {
        public OutputProbe(string componentId) : base(componentId)
        {
            Output = AddPin("OUT", PinDirection.Output);
        }

        public DigitalPin Output { get; }

        public void Set(
            DigitalLevel level,
            DigitalDriveStrength strength = DigitalDriveStrength.Strong) => Output.Drive(level, strength);
        public void Release() => Output.Release();
    }

    private sealed class DualOutputProbe : VirtualHardwareComponent
    {
        public DualOutputProbe(string componentId) : base(componentId)
        {
            Trigger = AddPin("TRIGGER", PinDirection.Input);
            First = AddPin("FIRST", PinDirection.Output);
            Second = AddPin("SECOND", PinDirection.Output);
            First.Release();
            Second.Release();
        }

        public DigitalPin Trigger { get; }
        public DigitalPin First { get; }
        public DigitalPin Second { get; }

        protected override void OnInputChanges(ulong changedInputMask)
        {
            if (Trigger.SampledLevel != DigitalLevel.High) return;
            First.Drive(DigitalLevel.Low);
            Second.Drive(DigitalLevel.High);
        }
    }

    private sealed class RecordingInputProbe : VirtualHardwareComponent
    {
        public RecordingInputProbe(string componentId) : base(componentId)
        {
            Input = AddPin("IN", PinDirection.Input);
        }

        public DigitalPin Input { get; }
        public List<DigitalLevel> ObservedLevels { get; } = [];

        protected override void OnInputChanges(ulong changedInputMask) => ObservedLevels.Add(Input.SampledLevel);
    }

    private sealed class InputProbe : VirtualHardwareComponent
    {
        public InputProbe(string componentId, DigitalInputActivation activation) : base(componentId)
        {
            Input = AddPin("IN", PinDirection.Input, activation);
        }

        public DigitalPin Input { get; }
        public int ActivationCount { get; private set; }

        protected override void OnInputChanges(ulong changedInputMask) => ActivationCount++;
    }
}
