using Xunit;
using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Components.Logic;
using AxetosOS.Products.NES.VirtualHardware.Components.Memory;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareDigitalComponentsTests
{
    [Fact]
    public void Tri_state_buffer_releases_and_drives_bus_from_enable_pin()
    {
        var board = new VirtualHardwareBoard("test.board.buffer");
        var buffer = board.Add(new TriStateBuffer("test.buffer", 4));
        var enable = board.Add(new DigitalSignalSource("test.enable", DigitalLevel.High));
        var inputs = CreateSources(board, "test.input", 4, 0b1010);

        ConnectBus(board, "A", inputs.Select(source => source.Output).ToArray(), buffer.Inputs.Pins);
        board.Connect("/OE", enable.Output, buffer.OutputEnableBar);
        var outputNets = ConnectOutputs(board, "Y", buffer.Outputs.Pins);

        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();
        Assert.All(outputNets, net => Assert.Equal(DigitalLevel.Unknown, net.Level));

        enable.Set(DigitalLevel.Low);
        simulator.Settle();
        Assert.Equal(0b1010UL, SampleNets(outputNets));
    }

    [Fact]
    public void Counter_advances_only_on_rising_clock_edges_received_at_its_pin()
    {
        var board = new VirtualHardwareBoard("test.board.counter");
        var counter = board.Add(new BinaryCounter("test.counter", 4));
        var clock = board.Add(new DigitalSignalSource("test.clock", DigitalLevel.Low));
        var reset = board.Add(new DigitalSignalSource("test.reset", DigitalLevel.High));
        var enable = board.Add(new DigitalSignalSource("test.enable", DigitalLevel.High));
        board.Connect("CLK", clock.Output, counter.Clock);
        board.Connect("/RESET", reset.Output, counter.ResetBar);
        board.Connect("EN", enable.Output, counter.Enable);
        ConnectOutputs(board, "Q", counter.Outputs.Pins);

        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();

        clock.Set(DigitalLevel.High);
        simulator.Settle();
        clock.Set(DigitalLevel.Low);
        simulator.Settle();
        clock.Set(DigitalLevel.High);
        simulator.Settle();

        Assert.Equal(2UL, counter.Value);
    }

    [Fact]
    public void Address_decoder_selects_output_only_from_address_and_enable_pins()
    {
        var board = new VirtualHardwareBoard("test.board.decoder");
        var decoder = board.Add(new BinaryAddressDecoder("test.decoder", 2));
        var enable = board.Add(new DigitalSignalSource("test.enable", DigitalLevel.Low));
        var address = CreateSources(board, "test.address", 2, 2);
        ConnectBus(board, "A", address.Select(source => source.Output).ToArray(), decoder.Address.Pins);
        board.Connect("/E", enable.Output, decoder.EnableBar);
        var outputs = ConnectOutputs(board, "Y", decoder.Outputs);

        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();

        Assert.Equal(DigitalLevel.Low, outputs[2].Level);
        Assert.Equal(DigitalLevel.High, outputs[0].Level);
        Assert.Equal(DigitalLevel.High, outputs[1].Level);
        Assert.Equal(DigitalLevel.High, outputs[3].Level);
    }

    [Fact]
    public void Static_ram_writes_and_reads_only_through_address_data_and_control_pins()
    {
        var board = new VirtualHardwareBoard("test.board.ram");
        var ram = board.Add(new StaticRamChip("test.ram", 4));
        var address = CreateSources(board, "test.address", 4, 3);
        var data = CreateSources(board, "test.data", 8, 0xA5);
        var chipSelect = board.Add(new DigitalSignalSource("test.cs", DigitalLevel.Low));
        var outputEnable = board.Add(new DigitalSignalSource("test.oe", DigitalLevel.High));
        var writeEnable = board.Add(new DigitalSignalSource("test.we", DigitalLevel.Low));

        ConnectBus(board, "A", address.Select(source => source.Output).ToArray(), ram.Address.Pins);
        var dataNets = ConnectBus(board, "D", data.Select(source => source.Output).ToArray(), ram.Data.Pins);
        board.Connect("/CS", chipSelect.Output, ram.ChipSelectBar);
        board.Connect("/OE", outputEnable.Output, ram.OutputEnableBar);
        board.Connect("/WE", writeEnable.Output, ram.WriteEnableBar);

        var simulator = new VirtualHardwareSimulator(board);
        board.PowerOn();
        simulator.Settle();
        Assert.Equal(0xA5, ram.Inspect(3));

        foreach (var source in data)
        {
            source.Set(DigitalLevel.HighImpedance);
        }
        writeEnable.Set(DigitalLevel.High);
        outputEnable.Set(DigitalLevel.Low);
        simulator.Settle();

        Assert.Equal(0xA5UL, SampleNets(dataNets));
        Assert.Equal(1UL, ram.WriteCount);
    }

    private static DigitalSignalSource[] CreateSources(
        VirtualHardwareBoard board,
        string prefix,
        int width,
        ulong value)
    {
        var sources = new DigitalSignalSource[width];
        for (var bit = 0; bit < width; bit++)
        {
            var level = (value & (1UL << bit)) == 0 ? DigitalLevel.Low : DigitalLevel.High;
            sources[bit] = board.Add(new DigitalSignalSource($"{prefix}.{bit}", level));
        }
        return sources;
    }

    private static DigitalNet[] ConnectBus(
        VirtualHardwareBoard board,
        string prefix,
        IReadOnlyList<DigitalPin> drivers,
        IReadOnlyList<DigitalPin> receivers)
    {
        Assert.Equal(drivers.Count, receivers.Count);
        var nets = new DigitalNet[drivers.Count];
        for (var bit = 0; bit < drivers.Count; bit++)
        {
            nets[bit] = board.Connect($"{prefix}{bit}", drivers[bit], receivers[bit]);
        }
        return nets;
    }

    private static DigitalNet[] ConnectOutputs(
        VirtualHardwareBoard board,
        string prefix,
        IReadOnlyList<DigitalPin> outputs)
    {
        var nets = new DigitalNet[outputs.Count];
        for (var bit = 0; bit < outputs.Count; bit++)
        {
            nets[bit] = board.Connect($"{prefix}{bit}", outputs[bit]);
        }
        return nets;
    }

    private static ulong SampleNets(IReadOnlyList<DigitalNet> nets)
    {
        ulong value = 0;
        for (var bit = 0; bit < nets.Count; bit++)
        {
            Assert.True(nets[bit].Level is DigitalLevel.Low or DigitalLevel.High);
            if (nets[bit].Level == DigitalLevel.High)
            {
                value |= 1UL << bit;
            }
        }
        return value;
    }
}
