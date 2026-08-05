using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Logic;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Memory;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareFamicomMotherboardTests
{
    [Fact]
    public void Famicom_board_contains_the_completed_japanese_chip_set_without_a_cic()
    {
        var board = new FamicomMotherboard();

        Assert.IsType<Rp2A03>(board.Cpu);
        Assert.IsType<Rp2C02>(board.Ppu);
        Assert.Equal(2, board.Board.Components.OfType<Hm6116>().Count());
        Assert.Single(board.Board.Components.OfType<Sn74Ls139A>());
        Assert.Single(board.Board.Components.OfType<Sn74Ls373>());
        Assert.Single(board.Board.Components.OfType<Sn74Ls368A>());
        Assert.DoesNotContain(board.Board.Components, component => component.GetType().Name.StartsWith("Cic", StringComparison.Ordinal));
    }

    [Fact]
    public void Famicom_board_wires_cpu_ppu_ram_and_virtual_slot_nodes_as_shared_nets()
    {
        var board = new FamicomMotherboard();

        Assert.Equal(16, board.CpuAddressNets.Count);
        Assert.Equal(8, board.CpuDataNets.Count);
        Assert.Equal(8, board.PpuAddressDataNets.Count);
        Assert.Equal(6, board.PpuHighAddressNets.Count);

        Assert.Same(board.CpuDataNets[0], board.Cpu.Data.Pins[0].Net);
        Assert.Same(board.CpuDataNets[0], board.CpuRam.Data.Pins[0].Net);
        Assert.Same(board.CpuDataNets[0], board.Ppu.CpuData.Pins[0].Net);
        Assert.Same(board.PpuAddressDataNets[0], board.Ppu.MultiplexedAddressData.Pins[0].Net);
        Assert.Same(board.PpuAddressDataNets[0], board.Ciram.Data.Pins[0].Net);
        Assert.Same(board.CpuReadWriteNet, board.Cpu.ReadWrite.Net);
        Assert.Same(board.CpuM2Net, board.Cpu.M2.Net);
        Assert.Same(board.CartridgeIrqNet, board.Cpu.IrqBar.Net);
    }

    [Fact]
    public void Famicom_master_clock_drives_both_custom_chips_and_reset_is_shared()
    {
        var board = new FamicomMotherboard();
        board.PowerOn();

        Assert.Equal(DigitalLevel.Low, board.Cpu.ResetBar.SampledLevel);
        Assert.Equal(DigitalLevel.Low, board.Ppu.ResetBar.SampledLevel);

        board.ReleaseReset();
        Assert.Equal(DigitalLevel.High, board.Cpu.ResetBar.SampledLevel);
        Assert.Equal(DigitalLevel.High, board.Ppu.ResetBar.SampledLevel);

        board.AdvanceMasterCycles(12);

        Assert.Equal(12UL, board.Cpu.MasterClockRisingEdgeCount);
        Assert.Equal(12UL, board.Ppu.MasterClockRisingEdgeCount);
        Assert.Equal(FamicomMotherboard.MasterClockHertz, board.MasterClock.FrequencyHertz);
    }

    [Fact]
    public void Famicom_decoder_selects_cpu_ram_and_ppu_register_regions_from_cpu_address_lines()
    {
        var board = new FamicomMotherboard();

        Assert.Same(board.CpuAddressNets[15], board.AddressDecoder.Enable1Bar.Net);
        Assert.Same(board.CpuAddressNets[15], board.AddressDecoder.Enable2Bar.Net);
        Assert.Same(board.CpuAddressNets[13], board.AddressDecoder.A1.Net);
        Assert.Same(board.CpuAddressNets[14], board.AddressDecoder.B1.Net);
        Assert.Same(board.AddressDecoder.Y10Bar.Net, board.CpuRam.ChipSelectBar.Net);
        Assert.Same(board.AddressDecoder.Y21Bar.Net, board.Ppu.ChipSelectBar.Net);
    }
}
