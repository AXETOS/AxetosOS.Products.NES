using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Logic;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Memory;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwarePalNesMotherboardTests
{
    [Fact]
    public void Pal_a_board_contains_the_completed_3195_regional_chip_set()
    {
        var board = new PalNesMotherboard(PalCicVariant.PalA3195);

        Assert.IsType<Rp2A07>(board.Cpu);
        Assert.IsType<Rp2C07>(board.Ppu);
        Assert.IsType<Cic3195>(board.Cic);
        Assert.NotNull(board.Cic3195);
        Assert.Null(board.Cic3197);
        Assert.DoesNotContain(board.Board.Components, component => component is Cic3197);
        Assert.Equal(2, board.Board.Components.OfType<Hm6116>().Count());
        Assert.Single(board.Board.Components.OfType<Sn74Ls139A>());
        Assert.Single(board.Board.Components.OfType<Sn74Ls373>());
        Assert.Single(board.Board.Components.OfType<Sn74Ls368A>());
    }

    [Fact]
    public void Pal_b_board_contains_the_completed_3197_regional_chip_set()
    {
        var board = new PalNesMotherboard(PalCicVariant.PalB3197);

        Assert.IsType<Rp2A07>(board.Cpu);
        Assert.IsType<Rp2C07>(board.Ppu);
        Assert.IsType<Cic3197>(board.Cic);
        Assert.Null(board.Cic3195);
        Assert.NotNull(board.Cic3197);
        Assert.DoesNotContain(board.Board.Components, component => component is Cic3195);
    }

    [Theory]
    [InlineData(PalCicVariant.PalA3195)]
    [InlineData(PalCicVariant.PalB3197)]
    public void Pal_board_exposes_the_normalized_shared_slot_nets(PalCicVariant variant)
    {
        var board = new PalNesMotherboard(variant);

        Assert.Equal(16, board.CpuAddressNets.Count);
        Assert.Equal(8, board.CpuDataNets.Count);
        Assert.Equal(8, board.PpuAddressDataNets.Count);
        Assert.Equal(6, board.PpuHighAddressNets.Count);
        Assert.Same(board.CpuDataNets[0], board.Cpu.Data.Pins[0].Net);
        Assert.Same(board.PpuAddressDataNets[0], board.Ppu.MultiplexedAddressData.Pins[0].Net);
        Assert.Same(board.CartridgeIrqNet, board.Cpu.IrqBar.Net);
    }

    [Theory]
    [InlineData(PalCicVariant.PalA3195)]
    [InlineData(PalCicVariant.PalB3197)]
    public void Selected_pal_cic_owns_host_reset_and_slot_side_serial_nets(PalCicVariant variant)
    {
        var board = new PalNesMotherboard(variant);
        board.PowerOn();

        Assert.Same(board.HostResetNet, board.Cpu.ResetBar.Net);
        Assert.Same(board.HostResetNet, board.Ppu.ResetBar.Net);
        Assert.Equal(DigitalLevel.Low, board.Cpu.ResetBar.SampledLevel);

        if (variant == PalCicVariant.PalA3195)
        {
            Assert.Same(board.HostResetNet, board.Cic3195!.HostResetBar.Net);
            Assert.Same(board.CicSlaveResetNet, board.Cic3195.SlaveResetBar.Net);
            Assert.Same(board.CicDataToCartridgeNet, board.Cic3195.DataOut.Net);
            Assert.Same(board.CicDataFromCartridgeNet, board.Cic3195.DataIn.Net);
        }
        else
        {
            Assert.Same(board.HostResetNet, board.Cic3197!.HostResetBar.Net);
            Assert.Same(board.CicSlaveResetNet, board.Cic3197.SlaveResetBar.Net);
            Assert.Same(board.CicDataToCartridgeNet, board.Cic3197.DataOut.Net);
            Assert.Same(board.CicDataFromCartridgeNet, board.Cic3197.DataIn.Net);
        }

        board.ReleaseReset();
        board.AdvanceCicCycles(4);

        Assert.Equal(DigitalLevel.High, board.Cpu.ResetBar.SampledLevel);
        Assert.Equal(DigitalLevel.High, board.Ppu.ResetBar.SampledLevel);
    }

    [Theory]
    [InlineData(PalCicVariant.PalA3195)]
    [InlineData(PalCicVariant.PalB3197)]
    public void Pal_master_clock_drives_rp2a07_and_rp2c07_while_cic_has_its_own_domain(PalCicVariant variant)
    {
        var board = new PalNesMotherboard(variant);
        board.PowerOn();
        board.ReleaseReset();
        board.AdvanceCicCycles(4);
        board.AdvanceMasterCycles(16);
        board.AdvanceCicCycles(3);

        Assert.Equal(16UL, board.Cpu.MasterClockRisingEdgeCount);
        Assert.Equal(16UL, board.Ppu.MasterClockRisingEdgeCount);
        Assert.NotSame(board.MasterClock.Output.Net, board.CicClock.Output.Net);
        Assert.Equal(PalNesMotherboard.MasterClockHertz, board.MasterClock.FrequencyHertz);
        Assert.Equal(PalNesMotherboard.CicClockHertz, board.CicClock.FrequencyHertz);
        Assert.Equal(7UL, variant == PalCicVariant.PalA3195
            ? board.Cic3195!.ClockRisingEdgeCount
            : board.Cic3197!.ClockRisingEdgeCount);
    }
}
