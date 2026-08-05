using Xunit;
using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Logic;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Memory;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareDiscreteChipCompletionTests
{
    [Fact]
    public void Discrete_packages_expose_the_complete_named_pin_counts()
    {
        Assert.Equal(16, new Sn74Ls139A("U1").Pins.Count);
        Assert.Equal(20, new Sn74Ls373("U2").Pins.Count);
        Assert.Equal(16, new Sn74Ls368A("U3").Pins.Count);
        Assert.Equal(24, new Hm6116("U4").Pins.Count);

        Assert.Contains(new Sn74Ls139A("U1N").Pins, pin => pin.Name.EndsWith(".1Y3_BAR", StringComparison.Ordinal));
        Assert.Contains(new Sn74Ls373("U2N").Pins, pin => pin.Name.EndsWith(".Q7", StringComparison.Ordinal));
        Assert.Contains(new Sn74Ls368A("U3N").Pins, pin => pin.Name.EndsWith(".6Y_BAR", StringComparison.Ordinal));
        Assert.Contains(new Hm6116("U4N").Pins, pin => pin.Name.EndsWith(".A10", StringComparison.Ordinal));
    }

    [Fact]
    public void Sn74Ls139A_exhaustively_decodes_both_sections_and_propagates_indeterminate_inputs()
    {
        var board = new VirtualHardwareBoard("completion.74ls139");
        var chip = board.Add(new Sn74Ls139A("U1"));
        PowerSources(board, chip.Vcc, chip.Gnd, out _, out _);
        var e1 = Source(board, "E1", DigitalLevel.Low, chip.Enable1Bar);
        var a1 = Source(board, "A1", DigitalLevel.Low, chip.A1);
        var b1 = Source(board, "B1", DigitalLevel.Low, chip.B1);
        var e2 = Source(board, "E2", DigitalLevel.Low, chip.Enable2Bar);
        var a2 = Source(board, "A2", DigitalLevel.Low, chip.A2);
        var b2 = Source(board, "B2", DigitalLevel.Low, chip.B2);
        var first = Observe(board, "Y1", [chip.Y10Bar, chip.Y11Bar, chip.Y12Bar, chip.Y13Bar]);
        var second = Observe(board, "Y2", [chip.Y20Bar, chip.Y21Bar, chip.Y22Bar, chip.Y23Bar]);
        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();

        for (var selected = 0; selected < 4; selected++)
        {
            SetAddress(a1, b1, selected);
            SetAddress(a2, b2, 3 - selected);
            simulator.Settle();
            AssertActiveLowSelection(first, selected);
            AssertActiveLowSelection(second, 3 - selected);
        }

        e1.Set(DigitalLevel.High);
        simulator.Settle();
        Assert.All(first, net => Assert.Equal(DigitalLevel.High, net.Level));

        e1.Set(DigitalLevel.Low);
        a1.Set(DigitalLevel.HighImpedance);
        simulator.Settle();
        Assert.All(first, net => Assert.Equal(DigitalLevel.Unknown, net.Level));

        e2.Set(DigitalLevel.HighImpedance);
        simulator.Settle();
        Assert.All(second, net => Assert.Equal(DigitalLevel.Unknown, net.Level));
    }

    [Fact]
    public void Sn74Ls373_models_transparency_hold_tristate_unknown_storage_and_power_loss()
    {
        var board = new VirtualHardwareBoard("completion.74ls373");
        var chip = board.Add(new Sn74Ls373("U2"));
        PowerSources(board, chip.Vcc, chip.Gnd, out var vcc, out _);
        var le = Source(board, "LE", DigitalLevel.High, chip.LatchEnable);
        var oe = Source(board, "OE", DigitalLevel.Low, chip.OutputEnableBar);
        var data = BusSources(board, "D", chip.D.Pins, 0xA5);
        var outputs = Observe(board, "Q", chip.Q.Pins);
        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();

        Assert.Equal(0xA5UL, Sample(outputs));
        Assert.True(chip.IsLatchedValueKnown);

        le.Set(DigitalLevel.Low);
        Drive(data, 0x3C);
        simulator.Settle();
        Assert.Equal(0xA5UL, Sample(outputs));

        oe.Set(DigitalLevel.High);
        simulator.Settle();
        Assert.All(outputs, net => Assert.Equal(DigitalLevel.Unknown, net.Level));

        oe.Set(DigitalLevel.Low);
        le.Set(DigitalLevel.High);
        data[3].Set(DigitalLevel.HighImpedance);
        simulator.Settle();
        Assert.Equal(DigitalLevel.Unknown, outputs[3].Level);
        Assert.Equal((byte)0xF7, chip.LatchedKnownMask);

        vcc.Set(DigitalLevel.Low);
        simulator.Settle();
        Assert.All(outputs, net => Assert.Equal(DigitalLevel.Unknown, net.Level));
        vcc.Set(DigitalLevel.High);
        le.Set(DigitalLevel.Low);
        simulator.Settle();
        Assert.False(chip.IsLatchedValueKnown);
        Assert.All(outputs, net => Assert.Equal(DigitalLevel.Unknown, net.Level));
    }

    [Fact]
    public void Sn74Ls368A_exhaustively_inverts_each_channel_and_honors_both_tristate_enables()
    {
        var board = new VirtualHardwareBoard("completion.74ls368");
        var chip = board.Add(new Sn74Ls368A("U3"));
        PowerSources(board, chip.Vcc, chip.Gnd, out var vcc, out _);
        var g1 = Source(board, "G1", DigitalLevel.Low, chip.Enable1Bar);
        var g2 = Source(board, "G2", DigitalLevel.Low, chip.Enable2Bar);
        var inputs = chip.A.Select((pin, index) => Source(board, $"A{index}", DigitalLevel.Low, pin)).ToArray();
        var outputs = Observe(board, "Y", chip.YBar);
        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();

        for (var channel = 0; channel < inputs.Length; channel++)
        {
            inputs[channel].Set(DigitalLevel.Low);
            simulator.Settle();
            Assert.Equal(DigitalLevel.High, outputs[channel].Level);
            inputs[channel].Set(DigitalLevel.High);
            simulator.Settle();
            Assert.Equal(DigitalLevel.Low, outputs[channel].Level);
        }

        g1.Set(DigitalLevel.High);
        simulator.Settle();
        Assert.All(outputs.Take(4), net => Assert.Equal(DigitalLevel.Unknown, net.Level));
        Assert.All(outputs.Skip(4), net => Assert.Equal(DigitalLevel.Low, net.Level));

        g1.Set(DigitalLevel.Low);
        g2.Set(DigitalLevel.HighImpedance);
        simulator.Settle();
        Assert.All(outputs.Skip(4), net => Assert.Equal(DigitalLevel.Unknown, net.Level));

        inputs[0].Set(DigitalLevel.HighImpedance);
        simulator.Settle();
        Assert.Equal(DigitalLevel.Unknown, outputs[0].Level);

        vcc.Set(DigitalLevel.Low);
        simulator.Settle();
        Assert.All(outputs, net => Assert.Equal(DigitalLevel.Unknown, net.Level));
    }

    [Fact]
    public void Hm6116_preserves_all_2048_address_locations_while_powered_without_aliasing()
    {
        var fixture = CreateRamFixture();
        fixture.Board.PowerOn();
        fixture.Simulator.Settle();

        for (var address = 0; address < 2048; address++)
        {
            Drive(fixture.Address, (ulong)address);
            Drive(fixture.Data, Pattern(address));
            fixture.WriteEnable.Set(DigitalLevel.Low);
            fixture.OutputEnable.Set(DigitalLevel.High);
            fixture.Simulator.Settle();
            fixture.WriteEnable.Set(DigitalLevel.High);
            fixture.Simulator.Settle();
        }

        DriveHighImpedance(fixture.Data);
        fixture.OutputEnable.Set(DigitalLevel.Low);
        for (var address = 0; address < 2048; address++)
        {
            Drive(fixture.Address, (ulong)address);
            fixture.Simulator.Settle();
            Assert.Equal(Pattern(address), (byte)Sample(fixture.DataNets));
            Assert.True(fixture.Chip.TryInspect(address, out var inspected));
            Assert.Equal(Pattern(address), inspected);
        }
    }

    [Fact]
    public void Hm6116_honors_control_priority_tristate_and_indeterminate_data_bits()
    {
        var fixture = CreateRamFixture();
        fixture.Board.PowerOn();
        Drive(fixture.Address, 0x123);
        Drive(fixture.Data, 0x5A);
        fixture.WriteEnable.Set(DigitalLevel.Low);
        fixture.OutputEnable.Set(DigitalLevel.Low);
        fixture.Simulator.Settle();
        Assert.Equal((byte)0x5A, fixture.Chip.Inspect(0x123));

        fixture.WriteEnable.Set(DigitalLevel.High);
        DriveHighImpedance(fixture.Data);
        fixture.Simulator.Settle();
        Assert.Equal(0x5AUL, Sample(fixture.DataNets));

        fixture.ChipSelect.Set(DigitalLevel.High);
        fixture.Simulator.Settle();
        Assert.All(fixture.DataNets, net => Assert.Equal(DigitalLevel.Unknown, net.Level));

        fixture.ChipSelect.Set(DigitalLevel.Low);
        fixture.OutputEnable.Set(DigitalLevel.High);
        Drive(fixture.Data, 0xFF);
        fixture.Data[2].Set(DigitalLevel.HighImpedance);
        fixture.WriteEnable.Set(DigitalLevel.Low);
        fixture.Simulator.Settle();
        Assert.Equal((byte)0xFB, fixture.Chip.InspectKnownMask(0x123));

        fixture.WriteEnable.Set(DigitalLevel.High);
        fixture.OutputEnable.Set(DigitalLevel.Low);
        DriveHighImpedance(fixture.Data);
        fixture.Simulator.Settle();
        Assert.Equal(DigitalLevel.Unknown, fixture.DataNets[2].Level);
        Assert.All(fixture.DataNets.Where((_, bit) => bit != 2), net => Assert.Equal(DigitalLevel.High, net.Level));
    }

    [Fact]
    public void Hm6116_loses_determinate_contents_after_power_interruption_and_requires_rewrite()
    {
        var fixture = CreateRamFixture();
        fixture.Board.PowerOn();
        Drive(fixture.Address, 0x7FF);
        Drive(fixture.Data, 0xC3);
        fixture.WriteEnable.Set(DigitalLevel.Low);
        fixture.Simulator.Settle();
        Assert.True(fixture.Chip.TryInspect(0x7FF, out var before));
        Assert.Equal((byte)0xC3, before);

        fixture.Vcc.Set(DigitalLevel.Low);
        fixture.Simulator.Settle();
        fixture.Vcc.Set(DigitalLevel.High);
        fixture.WriteEnable.Set(DigitalLevel.High);
        fixture.OutputEnable.Set(DigitalLevel.Low);
        DriveHighImpedance(fixture.Data);
        fixture.Simulator.Settle();

        Assert.False(fixture.Chip.TryInspect(0x7FF, out _));
        Assert.Equal((byte)0x00, fixture.Chip.InspectKnownMask(0x7FF));
        Assert.All(fixture.DataNets, net => Assert.Equal(DigitalLevel.Unknown, net.Level));
    }

    private static RamFixture CreateRamFixture()
    {
        var board = new VirtualHardwareBoard("completion.hm6116");
        var chip = board.Add(new Hm6116("U4"));
        PowerSources(board, chip.Vcc, chip.Gnd, out var vcc, out _);
        var chipSelect = Source(board, "CS", DigitalLevel.Low, chip.ChipSelectBar);
        var outputEnable = Source(board, "OE", DigitalLevel.High, chip.OutputEnableBar);
        var writeEnable = Source(board, "WE", DigitalLevel.High, chip.WriteEnableBar);
        var address = BusSources(board, "A", chip.Address.Pins, 0);
        var data = BusSources(board, "D", chip.Data.Pins, 0);
        var dataNets = chip.Data.Pins.Select(pin => pin.Net!).ToArray();
        return new RamFixture(board, chip, new VirtualHardwareSimulator(board), vcc, chipSelect, outputEnable, writeEnable, address, data, dataNets);
    }

    private static byte Pattern(int address) => (byte)(((address * 73) ^ (address >> 3) ^ 0xA5) & 0xFF);

    private static void PowerSources(
        VirtualHardwareBoard board,
        DigitalPin vccPin,
        DigitalPin gndPin,
        out DigitalSignalSource vcc,
        out DigitalSignalSource gnd)
    {
        vcc = Source(board, $"{vccPin.Name}.power", DigitalLevel.High, vccPin);
        gnd = Source(board, $"{gndPin.Name}.power", DigitalLevel.Low, gndPin);
    }

    private static DigitalSignalSource Source(VirtualHardwareBoard board, string id, DigitalLevel level, DigitalPin target)
    {
        var source = board.Add(new DigitalSignalSource($"src.{id}", level));
        board.Connect($"net.{id}", source.Output, target);
        return source;
    }

    private static DigitalSignalSource[] BusSources(VirtualHardwareBoard board, string id, IReadOnlyList<DigitalPin> targets, ulong value)
    {
        var sources = new DigitalSignalSource[targets.Count];
        for (var bit = 0; bit < targets.Count; bit++)
        {
            sources[bit] = Source(board, $"{id}{bit}", (value & (1UL << bit)) == 0 ? DigitalLevel.Low : DigitalLevel.High, targets[bit]);
        }
        return sources;
    }

    private static DigitalNet[] Observe(VirtualHardwareBoard board, string id, IReadOnlyList<DigitalPin> outputs)
    {
        return outputs.Select((pin, bit) => board.Connect($"observe.{id}{bit}", pin)).ToArray();
    }

    private static void SetAddress(DigitalSignalSource a, DigitalSignalSource b, int value)
    {
        a.Set((value & 1) == 0 ? DigitalLevel.Low : DigitalLevel.High);
        b.Set((value & 2) == 0 ? DigitalLevel.Low : DigitalLevel.High);
    }

    private static void AssertActiveLowSelection(IReadOnlyList<DigitalNet> outputs, int selected)
    {
        for (var index = 0; index < outputs.Count; index++)
        {
            Assert.Equal(index == selected ? DigitalLevel.Low : DigitalLevel.High, outputs[index].Level);
        }
    }

    private static void Drive(IReadOnlyList<DigitalSignalSource> sources, ulong value)
    {
        for (var bit = 0; bit < sources.Count; bit++)
        {
            sources[bit].Set((value & (1UL << bit)) == 0 ? DigitalLevel.Low : DigitalLevel.High);
        }
    }

    private static void DriveHighImpedance(IEnumerable<DigitalSignalSource> sources)
    {
        foreach (var source in sources)
        {
            source.Set(DigitalLevel.HighImpedance);
        }
    }

    private static ulong Sample(IReadOnlyList<DigitalNet> nets)
    {
        ulong value = 0;
        for (var bit = 0; bit < nets.Count; bit++)
        {
            value |= nets[bit].Level switch
            {
                DigitalLevel.Low => 0,
                DigitalLevel.High => 1UL << bit,
                _ => throw new InvalidOperationException($"Net '{nets[bit].Name}' is not resolved.")
            };
        }
        return value;
    }

    private sealed record RamFixture(
        VirtualHardwareBoard Board,
        Hm6116 Chip,
        VirtualHardwareSimulator Simulator,
        DigitalSignalSource Vcc,
        DigitalSignalSource ChipSelect,
        DigitalSignalSource OutputEnable,
        DigitalSignalSource WriteEnable,
        DigitalSignalSource[] Address,
        DigitalSignalSource[] Data,
        DigitalNet[] DataNets);
}
