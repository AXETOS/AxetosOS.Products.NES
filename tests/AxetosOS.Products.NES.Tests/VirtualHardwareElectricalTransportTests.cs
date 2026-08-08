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
    public void Multi_driver_compiled_resolver_preserves_strength_contention_and_release_semantics()
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
    public void Four_driver_direct_resolver_preserves_strong_over_weak_and_contention()
    {
        var board = new VirtualHardwareBoard("four-driver-direct-resolver");
        var first = board.Add(new OutputProbe("first"));
        var second = board.Add(new OutputProbe("second"));
        var third = board.Add(new OutputProbe("third"));
        var fourth = board.Add(new OutputProbe("fourth"));
        var receiver = board.Add(new InputProbe("receiver", DigitalInputActivation.AnyChange));
        board.Connect("shared", first.Output, second.Output, third.Output, fourth.Output, receiver.Input);
        _ = new VirtualHardwareSimulator(board);

        first.Set(DigitalLevel.High, DigitalDriveStrength.Weak);
        second.Set(DigitalLevel.High, DigitalDriveStrength.Weak);
        Assert.Equal(DigitalLevel.High, receiver.Input.SampledLevel);

        third.Set(DigitalLevel.Low);
        Assert.Equal(DigitalLevel.Low, receiver.Input.SampledLevel);

        fourth.Set(DigitalLevel.High);
        Assert.Equal(DigitalLevel.Contention, receiver.Input.SampledLevel);

        fourth.Release();
        Assert.Equal(DigitalLevel.Low, receiver.Input.SampledLevel);

        third.Release();
        Assert.Equal(DigitalLevel.High, receiver.Input.SampledLevel);
    }

    [Fact]
    public void Bidirectional_any_change_fast_path_keeps_physical_level_without_self_wakeup()
    {
        var board = new VirtualHardwareBoard("bidirectional-any-change-fastpath");
        var source = board.Add(new OutputProbe("source"));
        var receiver = board.Add(new BidirectionalProbe("receiver"));
        board.Connect("shared", source.Output, receiver.Io);
        _ = new VirtualHardwareSimulator(board);

        var baseline = receiver.ActivationCount;
        source.Set(DigitalLevel.High);
        Assert.Equal(baseline + 1, receiver.ActivationCount);
        Assert.Equal(DigitalLevel.High, receiver.Io.SampledLevel);

        receiver.Drive(DigitalLevel.Low);
        var whileDriving = receiver.ActivationCount;
        source.Set(DigitalLevel.Low);
        source.Set(DigitalLevel.High);

        Assert.Equal(whileDriving, receiver.ActivationCount);
        Assert.Equal(DigitalLevel.Contention, receiver.Io.SampledLevel);

        receiver.Release();
        Assert.Equal(DigitalLevel.High, receiver.Io.SampledLevel);
        Assert.Equal(whileDriving, receiver.ActivationCount);
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

    [Fact]
    public void Packed_four_driver_resolver_matches_reference_electrical_rules_exhaustively()
    {
        var net = new DigitalNet("packed-truth-table");
        var drivers = new[]
        {
            new DigitalPin("D0", PinDirection.Output),
            new DigitalPin("D1", PinDirection.Output),
            new DigitalPin("D2", PinDirection.Output),
            new DigitalPin("D3", PinDirection.Output)
        };
        foreach (var driver in drivers) net.Connect(driver);
        net.Resolve(); // Compile topology and seed the packed driver word.

        var states = new (DigitalLevel Level, DigitalDriveStrength Strength)[]
        {
            (DigitalLevel.Unknown, DigitalDriveStrength.Weak),
            (DigitalLevel.Unknown, DigitalDriveStrength.Strong),
            (DigitalLevel.Low, DigitalDriveStrength.Weak),
            (DigitalLevel.Low, DigitalDriveStrength.Strong),
            (DigitalLevel.High, DigitalDriveStrength.Weak),
            (DigitalLevel.High, DigitalDriveStrength.Strong),
            (DigitalLevel.HighImpedance, DigitalDriveStrength.Weak),
            (DigitalLevel.HighImpedance, DigitalDriveStrength.Strong)
        };

        for (var a = 0; a < states.Length; a++)
        for (var b = 0; b < states.Length; b++)
        for (var c = 0; c < states.Length; c++)
        for (var d = 0; d < states.Length; d++)
        {
            drivers[0].Drive(states[a].Level, states[a].Strength);
            drivers[1].Drive(states[b].Level, states[b].Strength);
            drivers[2].Drive(states[c].Level, states[c].Strength);
            drivers[3].Drive(states[d].Level, states[d].Strength);

            var expected = ResolveReference(states[a], states[b], states[c], states[d]);
            Assert.Equal(expected, net.Level);
        }
    }

    private static DigitalLevel ResolveReference(
        (DigitalLevel Level, DigitalDriveStrength Strength) first,
        (DigitalLevel Level, DigitalDriveStrength Strength) second,
        (DigitalLevel Level, DigitalDriveStrength Strength) third,
        (DigitalLevel Level, DigitalDriveStrength Strength) fourth)
    {
        var haveDriver = false;
        var strongest = DigitalDriveStrength.Weak;
        var low = false;
        var high = false;
        var unknown = false;

        AccumulateReference(first, ref haveDriver, ref strongest, ref low, ref high, ref unknown);
        AccumulateReference(second, ref haveDriver, ref strongest, ref low, ref high, ref unknown);
        AccumulateReference(third, ref haveDriver, ref strongest, ref low, ref high, ref unknown);
        AccumulateReference(fourth, ref haveDriver, ref strongest, ref low, ref high, ref unknown);

        if (!haveDriver) return DigitalLevel.Unknown;
        if (low && high) return DigitalLevel.Contention;
        if (unknown) return DigitalLevel.Unknown;
        return high ? DigitalLevel.High : DigitalLevel.Low;
    }

    private static void AccumulateReference(
        (DigitalLevel Level, DigitalDriveStrength Strength) driver,
        ref bool haveDriver,
        ref DigitalDriveStrength strongest,
        ref bool low,
        ref bool high,
        ref bool unknown)
    {
        if (driver.Level == DigitalLevel.HighImpedance) return;

        if (!haveDriver || (byte)driver.Strength > (byte)strongest)
        {
            haveDriver = true;
            strongest = driver.Strength;
            low = driver.Level == DigitalLevel.Low;
            high = driver.Level == DigitalLevel.High;
            unknown = driver.Level is not (DigitalLevel.Low or DigitalLevel.High);
            return;
        }

        if ((byte)driver.Strength < (byte)strongest) return;
        low |= driver.Level == DigitalLevel.Low;
        high |= driver.Level == DigitalLevel.High;
        unknown |= driver.Level is not (DigitalLevel.Low or DigitalLevel.High);
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


    private sealed class BidirectionalProbe : VirtualHardwareComponent
    {
        public BidirectionalProbe(string componentId) : base(componentId)
        {
            Io = AddPin("IO", PinDirection.Bidirectional);
            Io.Release();
        }

        public DigitalPin Io { get; }
        public int ActivationCount { get; private set; }

        public void Drive(DigitalLevel level) => Io.Drive(level);
        public void Release() => Io.Release();

        protected override void OnInputChanges(ulong changedInputMask) => ActivationCount++;
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
