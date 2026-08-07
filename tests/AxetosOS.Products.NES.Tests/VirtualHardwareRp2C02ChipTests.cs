using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Components.Memory;
using AxetosOS.Products.NES.VirtualHardware.Components.Nes.Rp2C02;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareRp2C02ChipTests
{
    [Fact]
    public void Vram_address_chip_implements_scroll_address_and_increment_hardware()
    {
        var board = new VirtualHardwareBoard("ppu-address-test");
        var chip = board.Add(new Rp2C02VramAddressRegisters("ppu.addr"));
        var data = Sources(board, "data", 8);
        var reg = Sources(board, "reg", 3);
        var write = board.Add(new DigitalSignalSource("write", DigitalLevel.Low));
        var inc = board.Add(new DigitalSignalSource("inc", DigitalLevel.Low));
        var inc32 = board.Add(new DigitalSignalSource("inc32", DigitalLevel.Low));
        Connect(board, chip.CpuData, data, "D");
        Connect(board, chip.RegisterSelect, reg, "R");
        board.Connect("WRITE", write.Output, chip.WritePulse);
        board.Connect("INC", inc.Output, chip.IncrementPulse);
        board.Connect("INC32", inc32.Output, chip.IncrementBy32);
        var sim = new VirtualHardwareSimulator(board); board.PowerOn(); sim.Settle();

        Set(data, 0x23); Set(reg, 6); Pulse(write, sim);
        Set(data, 0x45); Pulse(write, sim);
        Assert.Equal((ushort)0x2345, chip.Current);

        inc32.Set(DigitalLevel.High); sim.Settle(); Pulse(inc, sim);
        Assert.Equal((ushort)0x2365, chip.Current);

        Set(reg, 5); Set(data, 0x1D); Pulse(write, sim);
        Assert.Equal((byte)5, chip.FineX);
    }

    [Fact]
    public void Data_buffer_is_a_separate_edge_triggered_register()
    {
        var board = new VirtualHardwareBoard("ppu-buffer-test");
        var chip = board.Add(new Rp2C02DataBufferRegister("ppu.buffer"));
        var data = Sources(board, "data", 8);
        var load = board.Add(new DigitalSignalSource("load", DigitalLevel.Low));
        Connect(board, chip.Input, data, "D"); board.Connect("LOAD", load.Output, chip.Load);
        var sim = new VirtualHardwareSimulator(board); board.PowerOn(); sim.Settle();
        Set(data, 0xA7); Pulse(load, sim);
        Assert.Equal((byte)0xA7, chip.Value);
        Set(data, 0x12); sim.Settle();
        Assert.Equal((byte)0xA7, chip.Value);
    }

    [Fact]
    public void Bus_sequencer_reads_memory_only_through_external_pins()
    {
        var board = new VirtualHardwareBoard("ppu-bus-test");
        var bus = board.Add(new Rp2C02BusSequencer("ppu.bus"));
        var contents = new byte[1 << 14];
        contents[0x1234] = 0x6D;
        var ram = board.Add(new ProgramRomChip("ppu.rom", 14, contents));
        var cpuAddress = Sources(board, "cpu-a", 14);
        var request = board.Add(new DigitalSignalSource("request", DigitalLevel.Low));
        var write = board.Add(new DigitalSignalSource("write", DigitalLevel.Low));
        Connect(board, bus.CpuAddress, cpuAddress, "CPU_A");
        ConnectOutput(board, bus.CpuReadData, "CPU_RD");
        board.Connect("REQ", request.Output, bus.CpuRequest);
        board.Connect("WRITE", write.Output, bus.CpuWrite);
        for (var bit = 0; bit < 14; bit++) board.Connect($"A{bit}", bus.ExternalAddress.Pins[bit], ram.Address.Pins[bit]);
        for (var bit = 0; bit < 8; bit++) board.Connect($"D{bit}", bus.ExternalData.Pins[bit], ram.Data.Pins[bit]);
        var low = board.Add(new DigitalSignalSource("low", DigitalLevel.Low));
        board.Connect("CS", low.Output, ram.ChipSelectBar);
        board.Connect("OE", bus.ReadBar, ram.OutputEnableBar);
        var sim = new VirtualHardwareSimulator(board); board.PowerOn(); sim.Settle();
        Set(cpuAddress, 0x1234); Pulse(request, sim);
        Assert.Equal((byte)0x6D, (byte)Sample(bus.CpuReadData));
        Assert.Equal(1UL, bus.CompletedReadCount);
    }

    private static DigitalSignalSource[] Sources(VirtualHardwareBoard board, string prefix, int count)
    {
        var result = new DigitalSignalSource[count];
        for (var i = 0; i < count; i++) result[i] = board.Add(new DigitalSignalSource($"{prefix}{i}", DigitalLevel.Low));
        return result;
    }
    private static void Connect(VirtualHardwareBoard board, DigitalBus bus, DigitalSignalSource[] src, string prefix)
    { for (var i = 0; i < src.Length; i++) board.Connect($"{prefix}{i}", src[i].Output, bus.Pins[i]); }
    private static void ConnectOutput(VirtualHardwareBoard board, DigitalBus bus, string prefix)
    { for (var i = 0; i < bus.Width; i++) board.Connect($"{prefix}{i}", bus.Pins[i]); }
    private static void Set(DigitalSignalSource[] src, ulong value)
    { for (var i = 0; i < src.Length; i++) src[i].Set((value & (1UL << i)) != 0 ? DigitalLevel.High : DigitalLevel.Low); }
    private static void Pulse(DigitalSignalSource src, VirtualHardwareSimulator sim)
    {
        src.Set(DigitalLevel.High);
        sim.Settle();
        src.Set(DigitalLevel.Low);
        sim.Settle();
    }
    private static ulong Sample(DigitalBus bus) => bus.TrySample(out var value) ? value : throw new InvalidOperationException("Bus unresolved.");
}
