using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Logic;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Memory;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareNtscNesMotherboardTests
{
    [Fact]
    public void Ntsc_board_contains_the_completed_north_american_chip_set()
    {
        var board = new NtscNesMotherboard();

        Assert.IsType<Rp2A03>(board.Cpu);
        Assert.IsType<Rp2C02>(board.Ppu);
        Assert.IsType<Cic3193>(board.Cic);
        Assert.Equal(2, board.Board.Components.OfType<Hm6116>().Count());
        Assert.Single(board.Board.Components.OfType<Sn74Ls139A>());
        Assert.Single(board.Board.Components.OfType<Sn74Ls373>());
        Assert.Single(board.Board.Components.OfType<Sn74Ls368A>());
    }

    [Fact]
    public void Ntsc_board_exposes_the_same_normalized_cpu_and_ppu_slot_nets_as_the_famicom_board()
    {
        var board = new NtscNesMotherboard();

        Assert.Equal(16, board.CpuAddressNets.Count);
        Assert.Equal(8, board.CpuDataNets.Count);
        Assert.Equal(8, board.PpuDataNets.Count);
        Assert.Equal(8, board.PpuLowAddressNets.Count);
        Assert.Equal(6, board.PpuHighAddressNets.Count);
        Assert.Same(board.CpuDataNets[0], board.Cpu.Data.Pins[0].Net);
        Assert.Same(board.PpuDataNets[0], board.Ppu.MultiplexedAddressData.Pins[0].Net);
        Assert.Same(board.PpuLowAddressNets[0], board.PpuAddressLatch.Q.Pins[0].Net);
        Assert.NotSame(board.PpuDataNets[0], board.PpuLowAddressNets[0]);
        Assert.Same(board.CartridgeIrqNet, board.Cpu.IrqBar.Net);
    }

    [Fact]
    public void Cic_owns_the_host_reset_chain_and_exposes_key_side_slot_nets()
    {
        var board = new NtscNesMotherboard();
        board.PowerOn();

        Assert.Same(board.HostResetNet, board.Cic.HostResetBar.Net);
        Assert.Same(board.HostResetNet, board.Cpu.ResetBar.Net);
        Assert.Same(board.HostResetNet, board.Ppu.ResetBar.Net);
        Assert.Same(board.CicSlaveResetNet, board.Cic.SlaveResetBar.Net);
        Assert.Same(board.CicDataToCartridgeNet, board.Cic.DataOut.Net);
        Assert.Same(board.CicDataFromCartridgeNet, board.Cic.DataIn.Net);
        Assert.Equal(DigitalLevel.Low, board.Cpu.ResetBar.SampledLevel);

        board.ReleaseReset();
        board.AdvanceCicCycles(4);

        Assert.True(board.Cic.StartupComplete);
        Assert.Equal(DigitalLevel.High, board.Cpu.ResetBar.SampledLevel);
        Assert.Equal(DigitalLevel.High, board.Ppu.ResetBar.SampledLevel);
    }

    [Fact]
    public void Ntsc_master_clock_drives_cpu_and_ppu_while_cic_uses_its_own_clock_net()
    {
        var board = new NtscNesMotherboard();
        board.PowerOn();
        board.ReleaseReset();

        // The CIC owns the host reset net. Allow its startup sequence to
        // release the RP2A03 and RP2C02 before measuring master-clock edges.
        board.AdvanceCicCycles(4);
        board.AdvanceMasterCycles(12);
        board.AdvanceCicCycles(3);

        Assert.Equal(12UL, board.Cpu.MasterClockRisingEdgeCount);
        Assert.Equal(12UL, board.Ppu.MasterClockRisingEdgeCount);
        Assert.Equal(7UL, board.Cic.ClockRisingEdgeCount);
        Assert.NotSame(board.MasterClock.Output.Net, board.CicClock.Output.Net);
        Assert.Equal(NtscNesMotherboard.MasterClockHertz, board.MasterClock.FrequencyHertz);
        Assert.Equal(NtscNesMotherboard.CicClockHertz, board.CicClock.FrequencyHertz);
    }
}
