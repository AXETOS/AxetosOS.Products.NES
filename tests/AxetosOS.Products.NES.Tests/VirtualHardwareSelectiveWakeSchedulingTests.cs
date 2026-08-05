using Xunit;
using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Components.Clock;
using AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Memory;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Components.Power;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareSelectiveWakeSchedulingTests
{
    [Fact]
    public void Power_rails_and_oscillators_settle_once_until_their_output_is_changed_externally()
    {
        var board = new VirtualHardwareBoard("one-shot-sources");
        var rail = board.Add(new DigitalPowerRail("vcc", DigitalLevel.High));
        var clock = board.Add(new DigitalOscillator("clock", 1));
        board.Connect("vcc.net", rail.Output);
        board.Connect("clock.net", clock.Output);
        var simulator = new VirtualHardwareSimulator(board);

        simulator.Settle();
        var before = simulator.SettleCount;
        simulator.Settle();
        Assert.Equal(before + 1, simulator.SettleCount);

        clock.AdvanceHalfCycle();
        simulator.Settle();
        Assert.Equal(DigitalLevel.High, clock.Output.SampledLevel);
    }

    [Fact]
    public void Selective_component_can_ignore_resolved_echo_on_a_bidirectional_pin()
    {
        var board = new VirtualHardwareBoard("selective-wake");
        var component = board.Add(new SelectiveBidirectionalProbe("probe"));
        board.Connect("bus", component.Bus);
        var simulator = new VirtualHardwareSimulator(board);

        simulator.Settle();
        Assert.Equal(1, component.EvaluationCount);

        component.Bus.Drive(DigitalLevel.High);
        simulator.Settle();
        Assert.Equal(1, component.EvaluationCount);
    }

    [Fact]
    public void Nrom_ignores_its_data_output_echo_but_wakes_for_multiplexed_address_input()
    {
        var board = new VirtualHardwareBoard("nrom-selective-data");
        var cartridge = board.Add(new NromCartridge("cart"));
        var ale = board.Add(new DigitalSignalSource("ale", DigitalLevel.Low));
        board.Connect("ale.net", ale.Output, cartridge.PpuAle);
        foreach (var pin in cartridge.PpuAddressData.Pins) board.Connect($"ad.{pin.Name}", pin);
        foreach (var pin in cartridge.CpuData.Pins) board.Connect($"cpu.{pin.Name}", pin);
        var simulator = new VirtualHardwareSimulator(board);
        simulator.Settle();

        Assert.False(cartridge.ShouldWakeForSampledPin(cartridge.CpuData.Pins[0]));
        Assert.False(cartridge.ShouldWakeForSampledPin(cartridge.PpuAddressData.Pins[0]));

        ale.Set(DigitalLevel.High);
        simulator.Settle();
        Assert.True(cartridge.ShouldWakeForSampledPin(cartridge.PpuAddressData.Pins[0]));
    }

    [Fact]
    public void Hm6116_data_bus_wakes_only_during_selected_write()
    {
        var board = new VirtualHardwareBoard("ram-selective-data");
        var ram = board.Add(new Hm6116("ram"));
        var chipSelect = board.Add(new DigitalSignalSource("cs", DigitalLevel.High));
        var writeEnable = board.Add(new DigitalSignalSource("we", DigitalLevel.High));
        board.Connect("cs.net", chipSelect.Output, ram.ChipSelectBar);
        board.Connect("we.net", writeEnable.Output, ram.WriteEnableBar);
        foreach (var pin in ram.Data.Pins) board.Connect($"data.{pin.Name}", pin);
        var simulator = new VirtualHardwareSimulator(board);
        simulator.Settle();

        Assert.False(ram.ShouldWakeForSampledPin(ram.Data.Pins[0]));

        chipSelect.Set(DigitalLevel.Low);
        writeEnable.Set(DigitalLevel.Low);
        simulator.Settle();
        Assert.True(ram.ShouldWakeForSampledPin(ram.Data.Pins[0]));
    }

    private sealed class SelectiveBidirectionalProbe : VirtualHardwareComponent, ISelectiveInputDrivenVirtualHardwareComponent
    {
        public SelectiveBidirectionalProbe(string componentId) : base(componentId)
        {
            Bus = AddPin("BUS", PinDirection.Bidirectional);
        }

        public DigitalPin Bus { get; }
        public int EvaluationCount { get; private set; }
        public bool ShouldWakeForSampledPin(DigitalPin pin) => false;
        public override void Evaluate() => EvaluationCount++;
    }
}
