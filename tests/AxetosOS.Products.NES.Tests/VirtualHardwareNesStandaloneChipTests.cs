using Xunit;
using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Logic;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Memory;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Components.Power;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareNesStandaloneChipTests
{
    [Fact]
    public void Sn74Ls139A_decodes_both_sections_independently()
    {
        var board = new VirtualHardwareBoard("chiptest.74ls139");
        var chip = board.Add(new Sn74Ls139A("U1"));
        Power(board, chip.Vcc, chip.Gnd);

        var e1 = Source(board, "E1", DigitalLevel.Low, chip.Enable1Bar);
        var a1 = Source(board, "A1", DigitalLevel.High, chip.A1);
        var b1 = Source(board, "B1", DigitalLevel.Low, chip.B1);
        Source(board, "E2", DigitalLevel.High, chip.Enable2Bar);
        Source(board, "A2", DigitalLevel.Low, chip.A2);
        Source(board, "B2", DigitalLevel.Low, chip.B2);

        var y1 = Observe(board, "Y1", [chip.Y10Bar, chip.Y11Bar, chip.Y12Bar, chip.Y13Bar]);
        var y2 = Observe(board, "Y2", [chip.Y20Bar, chip.Y21Bar, chip.Y22Bar, chip.Y23Bar]);

        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();

        Assert.Equal(DigitalLevel.Low, y1[1].Level);
        Assert.Equal(DigitalLevel.High, y1[0].Level);
        Assert.All(y2, net => Assert.Equal(DigitalLevel.High, net.Level));

        e1.Set(DigitalLevel.High);
        a1.Set(DigitalLevel.Low);
        b1.Set(DigitalLevel.High);
        simulator.Settle();
        Assert.All(y1, net => Assert.Equal(DigitalLevel.High, net.Level));
    }

    [Fact]
    public void Sn74Ls373_is_transparent_when_le_is_high_and_holds_when_low()
    {
        var board = new VirtualHardwareBoard("chiptest.74ls373");
        var chip = board.Add(new Sn74Ls373("U2"));
        Power(board, chip.Vcc, chip.Gnd);
        var le = Source(board, "LE", DigitalLevel.High, chip.LatchEnable);
        Source(board, "OE", DigitalLevel.Low, chip.OutputEnableBar);
        var data = BusSources(board, "D", chip.D.Pins, 0x3C);
        var outputs = Observe(board, "Q", chip.Q.Pins);

        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();
        Assert.Equal(0x3CUL, Sample(outputs));

        le.Set(DigitalLevel.Low);
        Drive(data, 0xA5);
        simulator.Settle();
        Assert.Equal(0x3CUL, Sample(outputs));

        le.Set(DigitalLevel.High);
        simulator.Settle();
        Assert.Equal(0xA5UL, Sample(outputs));
    }

    [Fact]
    public void Sn74Ls368A_inverts_enabled_channels_and_releases_disabled_group()
    {
        var board = new VirtualHardwareBoard("chiptest.74ls368");
        var chip = board.Add(new Sn74Ls368A("U3"));
        Power(board, chip.Vcc, chip.Gnd);
        Source(board, "G1", DigitalLevel.Low, chip.Enable1Bar);
        Source(board, "G2", DigitalLevel.High, chip.Enable2Bar);

        for (var index = 0; index < chip.A.Count; index++)
        {
            Source(board, $"A{index}", index % 2 == 0 ? DigitalLevel.Low : DigitalLevel.High, chip.A[index]);
        }

        var outputs = Observe(board, "Y", chip.YBar);
        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();

        Assert.Equal(DigitalLevel.High, outputs[0].Level);
        Assert.Equal(DigitalLevel.Low, outputs[1].Level);
        Assert.Equal(DigitalLevel.High, outputs[2].Level);
        Assert.Equal(DigitalLevel.Low, outputs[3].Level);
        Assert.Equal(DigitalLevel.Unknown, outputs[4].Level);
        Assert.Equal(DigitalLevel.Unknown, outputs[5].Level);
    }

    [Fact]
    public void Hm6116_reads_and_writes_only_through_package_pins()
    {
        var board = new VirtualHardwareBoard("chiptest.hm6116");
        var chip = board.Add(new Hm6116("U4"));
        Power(board, chip.Vcc, chip.Gnd);
        Source(board, "CS", DigitalLevel.Low, chip.ChipSelectBar);
        var oe = Source(board, "OE", DigitalLevel.High, chip.OutputEnableBar);
        var we = Source(board, "WE", DigitalLevel.Low, chip.WriteEnableBar);
        BusSources(board, "A", chip.Address.Pins, 0x321);
        var dataSources = BusSources(board, "D", chip.Data.Pins, 0x6D);
        var dataNets = chip.Data.Pins.Select(pin => pin.Net!).ToArray();

        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();
        Assert.Equal(0x6D, chip.Inspect(0x321));

        // Immediate hardware propagation means control edges happen when the
        // source changes, not later at Settle(). End the write before removing
        // its data, then enable the RAM output exactly as real bus timing would.
        we.Set(DigitalLevel.High);
        DriveHighImpedance(dataSources);
        oe.Set(DigitalLevel.Low);
        simulator.Settle();
        Assert.Equal(0x6DUL, Sample(dataNets));
    }

    private static void Power(VirtualHardwareBoard board, DigitalPin vcc, DigitalPin gnd)
    {
        var high = board.Add(new DigitalPowerRail($"{vcc.Name}.rail", DigitalLevel.High));
        var low = board.Add(new DigitalPowerRail($"{gnd.Name}.rail", DigitalLevel.Low));
        board.Connect($"{vcc.Name}.net", high.Output, vcc);
        board.Connect($"{gnd.Name}.net", low.Output, gnd);
    }

    private static DigitalSignalSource Source(
        VirtualHardwareBoard board,
        string id,
        DigitalLevel level,
        DigitalPin target)
    {
        var source = board.Add(new DigitalSignalSource($"src.{id}", level));
        board.Connect($"net.{id}", source.Output, target);
        return source;
    }

    private static DigitalSignalSource[] BusSources(
        VirtualHardwareBoard board,
        string id,
        IReadOnlyList<DigitalPin> targets,
        ulong value)
    {
        var sources = new DigitalSignalSource[targets.Count];
        for (var bit = 0; bit < targets.Count; bit++)
        {
            sources[bit] = Source(
                board,
                $"{id}{bit}",
                (value & (1UL << bit)) == 0 ? DigitalLevel.Low : DigitalLevel.High,
                targets[bit]);
        }

        return sources;
    }

    private static DigitalNet[] Observe(
        VirtualHardwareBoard board,
        string id,
        IReadOnlyList<DigitalPin> outputs)
    {
        var nets = new DigitalNet[outputs.Count];
        for (var bit = 0; bit < outputs.Count; bit++)
        {
            nets[bit] = board.Connect($"observe.{id}{bit}", outputs[bit]);
        }

        return nets;
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
}
