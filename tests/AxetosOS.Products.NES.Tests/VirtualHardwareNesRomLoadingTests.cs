using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;
using AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Machines.Nes;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareNesRomLoadingTests
{
    [Fact]
    public void Nes20_pal_header_selects_the_physical_pal_motherboard_in_auto_mode()
    {
        var machine = VirtualHardwareNesMachineFactory.Load(
            CreateRom(nes20: true, timing: 1),
            "Example.nes");

        Assert.Equal(ActiveNesMotherboard.PalNes, machine.ActiveMotherboard);
        Assert.Equal(NesHardwareRegion.Pal, machine.RegionSelection.Region);
        Assert.Equal(NesRegionSelectionSource.Nes20Header, machine.RegionSelection.Source);
        Assert.IsType<PalNesMotherboard>(machine.ActiveBoard);
        Assert.IsType<Rp2A07>(machine.Hardware.PalNes.Cpu);
        Assert.IsType<Rp2C07>(machine.Hardware.PalNes.Ppu);
    }

    [Fact]
    public void Japanese_filename_selects_the_physical_famicom_board()
    {
        var machine = VirtualHardwareNesMachineFactory.Load(
            CreateRom(nes20: true, timing: 0),
            "Game (Japan).nes");

        Assert.Equal(ActiveNesMotherboard.Famicom, machine.ActiveMotherboard);
        Assert.Equal(NesHardwareRegion.NtscJapan, machine.RegionSelection.Region);
        Assert.Equal(NesRegionSelectionSource.FileName, machine.RegionSelection.Source);
        Assert.IsType<FamicomMotherboard>(machine.ActiveBoard);
    }

    [Fact]
    public void Explicit_region_override_has_priority_over_rom_metadata()
    {
        var machine = VirtualHardwareNesMachineFactory.Load(
            CreateRom(nes20: true, timing: 1),
            "Game (Europe).nes",
            NesRegionSelection.NtscJapan);

        Assert.Equal(ActiveNesMotherboard.Famicom, machine.ActiveMotherboard);
        Assert.Equal(NesHardwareRegion.NtscJapan, machine.RegionSelection.Region);
        Assert.Equal(NesRegionSelectionSource.ManualOverride, machine.RegionSelection.Source);
    }

    [Fact]
    public void Legacy_ines_pal_hint_selects_the_physical_pal_board_before_filename_fallback()
    {
        var machine = VirtualHardwareNesMachineFactory.Load(
            CreateRom(nes20: false, timing: 1),
            "Unknown.nes");

        Assert.Equal(ActiveNesMotherboard.PalNes, machine.ActiveMotherboard);
        Assert.Equal(NesRegionSelectionSource.INesHeader, machine.RegionSelection.Source);
    }

    [Fact]
    public void Auto_mode_falls_back_to_the_physical_ntsc_u_board_when_metadata_is_ambiguous()
    {
        var machine = VirtualHardwareNesMachineFactory.Load(CreateRom(), "Game.nes");

        Assert.Equal(ActiveNesMotherboard.NtscNes, machine.ActiveMotherboard);
        Assert.Equal(NesHardwareRegion.NtscNorthAmerica, machine.RegionSelection.Region);
        Assert.Equal(NesRegionSelectionSource.Default, machine.RegionSelection.Source);
        Assert.IsType<NtscNesMotherboard>(machine.ActiveBoard);
        Assert.IsType<Rp2A03>(machine.Hardware.NtscNes.Cpu);
        Assert.IsType<Rp2C02>(machine.Hardware.NtscNes.Ppu);
    }

    [Fact]
    public void Reader_skips_trainer_and_extracts_prg_and_chr_payloads()
    {
        var rom = CreateRom(hasTrainer: true, chrUnits: 1);
        var image = VirtualHardwareNesRomReader.Read(rom);

        Assert.True(image.HasTrainer);
        Assert.Equal(16 * 1024, image.PrgRom.Length);
        Assert.Equal(8 * 1024, image.ChrRom.Length);
        Assert.Equal(0xA9, image.PrgRom[0]);
        Assert.Equal(0x3C, image.ChrRom[0]);
    }


    [Fact]
    public void Nes20_reader_decodes_explicit_cartridge_ram_and_nvram_capacities()
    {
        var rom = CreateRom(nes20: true, mapper: 1, prgUnits: 2, chrUnits: 1);
        rom[10] = 0x87; // 8 KiB volatile PRG RAM + 16 KiB PRG NVRAM.
        rom[11] = 0x07; // 8 KiB volatile CHR RAM + no CHR NVRAM.

        var image = VirtualHardwareNesRomReader.Read(rom);

        Assert.True(image.HasExplicitRamSizes);
        Assert.Equal(8 * 1024, image.PrgRamSizeBytes);
        Assert.Equal(16 * 1024, image.PrgNvRamSizeBytes);
        Assert.Equal(8 * 1024, image.ChrRamSizeBytes);
        Assert.Equal(0, image.ChrNvRamSizeBytes);
        Assert.Equal(24 * 1024, image.TotalPrgRamSizeBytes);
        Assert.Equal(8 * 1024, image.TotalChrRamSizeBytes);
    }

    [Fact]
    public void Factory_constructs_mmc1_hardware_from_rom_metadata_and_attaches_it_to_selected_board()
    {
        var machine = VirtualHardwareNesMachineFactory.Load(
            CreateRom(mapper: 1, prgUnits: 2, chrUnits: 1),
            "MMC1 (USA).nes");

        Assert.Equal(1, machine.CartridgeBoard.MapperNumber);
        Assert.IsType<Mmc1Cartridge>(machine.CartridgeBoard);
        Assert.Equal(ActiveNesMotherboard.NtscNes, machine.ActiveMotherboard);
        Assert.Contains(machine.CartridgeBoard, machine.Hardware.NtscNes.Board.Components);
    }

    [Fact]
    public void Factory_rejects_mapper_without_physical_cartridge_hardware()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            VirtualHardwareNesMachineFactory.Load(CreateRom(mapper: 2), "UxROM.nes"));

        Assert.Contains("Mapper 2", error.Message);
    }

    [Fact]
    public void Factory_attaches_one_nrom_board_to_the_selected_physical_motherboard()
    {
        var machine = VirtualHardwareNesMachineFactory.Load(CreateRom(chrUnits: 1), "Game (USA).nes");

        Assert.NotNull(machine.CartridgeBoard);
        Assert.True(machine.CartridgeBoard.IsInserted);
        Assert.Contains(
            machine.Hardware.NtscNes.Board.Components,
            component => ReferenceEquals(component, machine.CartridgeBoard));
        Assert.Same(machine.Hardware.NtscNes.CpuAddressNets[0], machine.CartridgeBoard.CpuAddress.Pins[0].Net);
        Assert.Same(machine.Hardware.NtscNes.PpuDataNets[0], machine.CartridgeBoard.PpuData.Pins[0].Net);
        Assert.Same(machine.Hardware.NtscNes.PpuLowAddressNets[0], machine.CartridgeBoard.PpuAddress.Pins[0].Net);
    }

    private static byte[] CreateRom(
        bool nes20 = false,
        int timing = 0,
        int mapper = 0,
        bool hasTrainer = false,
        int chrUnits = 0,
        int prgUnits = 1)
    {
        var trainerSize = hasTrainer ? 512 : 0;
        var bytes = new byte[16 + trainerSize + (prgUnits * 16 * 1024) + (chrUnits * 8 * 1024)];
        bytes[0] = 0x4E;
        bytes[1] = 0x45;
        bytes[2] = 0x53;
        bytes[3] = 0x1A;
        bytes[4] = (byte)prgUnits;
        bytes[5] = (byte)chrUnits;
        bytes[6] = (byte)(((mapper & 0x0F) << 4) | (hasTrainer ? 0x04 : 0));
        bytes[7] = (byte)((mapper & 0xF0) | (nes20 ? 0x08 : 0));
        if (nes20)
        {
            bytes[8] = (byte)((mapper >> 8) & 0x0F);
            bytes[12] = (byte)timing;
        }
        else if (timing == 1)
        {
            bytes[9] = 0x01;
        }

        var payload = 16 + trainerSize;
        bytes[payload] = 0xA9;
        var vectorBase = payload + (prgUnits * 16 * 1024) - 4;
        bytes[vectorBase] = 0x00;
        bytes[vectorBase + 1] = prgUnits == 1 ? (byte)0x80 : (byte)0xC0;
        if (chrUnits > 0)
            bytes[payload + (prgUnits * 16 * 1024)] = 0x3C;
        return bytes;
    }
}
