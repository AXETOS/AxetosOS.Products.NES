using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Machines.Nes;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareRegionalNesMachineTests
{
    [Theory]
    [InlineData("Game (Japan).nes", ActiveNesMotherboard.Famicom)]
    [InlineData("Game (USA).nes", ActiveNesMotherboard.NtscNes)]
    [InlineData("Game (Europe).nes", ActiveNesMotherboard.PalNes)]
    public void One_shared_slot_selects_exactly_one_regional_motherboard(
        string fileName,
        ActiveNesMotherboard expected)
    {
        var machine = new RegionalNesVirtualMachine();
        machine.InsertRom(CreateImage(), fileName);

        Assert.Equal(expected, machine.ActiveMotherboard);
        Assert.NotNull(machine.ActiveBoard);
        Assert.True(machine.Slot.IsOccupied);
        Assert.Equal(1UL, machine.SelectionCount);
    }

    [Fact]
    public void Manual_region_override_wins_over_rom_name()
    {
        var machine = new RegionalNesVirtualMachine();
        machine.InsertRom(CreateImage(), "Game (Japan).nes", NesRegionSelection.Pal, PalCicVariant.PalB3197);

        Assert.Equal(ActiveNesMotherboard.PalNes, machine.ActiveMotherboard);
        Assert.Equal(PalCicVariant.PalB3197, machine.PalNes.CicVariant);
        Assert.NotNull(machine.PalNes.Cic3197);
        Assert.Null(machine.PalNes.Cic3195);
    }

    [Fact]
    public void Only_the_selected_board_is_clocked_by_machine_operations()
    {
        var machine = new RegionalNesVirtualMachine();
        machine.InsertRom(CreateImage(), "Game (Japan).nes");
        machine.PowerOn();
        machine.ReleaseReset();
        machine.AdvanceMasterCycles(12);

        Assert.Equal(12UL, machine.Famicom.Cpu.MasterClockRisingEdgeCount);
        Assert.Equal(0UL, machine.NtscNes.Cpu.MasterClockRisingEdgeCount);
        Assert.Equal(0UL, machine.PalNes.Cpu.MasterClockRisingEdgeCount);
    }

    [Fact]
    public void Slot_has_one_normalized_bus_shape_for_all_regions()
    {
        Assert.Equal(16, SharedVirtualRomSlot.CpuAddressWidth);
        Assert.Equal(8, SharedVirtualRomSlot.CpuDataWidth);
        Assert.Equal(14, SharedVirtualRomSlot.PpuAddressWidth);
        Assert.Equal(8, SharedVirtualRomSlot.PpuDataWidth);
    }

    [Fact]
    public void Powered_machine_rejects_rom_replacement_and_ejection()
    {
        var machine = new RegionalNesVirtualMachine();
        machine.InsertRom(CreateImage(), "Game (USA).nes");
        machine.PowerOn();

        Assert.Throws<InvalidOperationException>(() => machine.InsertRom(CreateImage(), "Other (Japan).nes"));
        Assert.Throws<InvalidOperationException>(machine.EjectRom);
    }

    private static VirtualHardwareNesRomImage CreateImage() => new(
        VirtualHardwareNesHeaderFormat.INes,
        MapperNumber: 0,
        SubmapperNumber: null,
        PrgRomSizeBytes: 16 * 1024,
        ChrRomSizeBytes: 8 * 1024,
        HasTrainer: false,
        HasBatteryBackedMemory: false,
        VirtualHardwareNesMirroring.Horizontal,
        VirtualHardwareNesHeaderTiming.Unknown,
        new byte[16 * 1024],
        new byte[8 * 1024]);
}
