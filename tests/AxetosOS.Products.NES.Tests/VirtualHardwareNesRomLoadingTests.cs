using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareNesRomLoadingTests
{
    [Fact]
    public void Nes20_pal_header_constructs_pal_motherboard_in_auto_mode()
    {
        var machine = VirtualHardwareNesMachineFactory.Load(
            CreateRom(nes20: true, timing: 1),
            "Example.nes");

        Assert.Equal(NesHardwareRegion.Pal, machine.Motherboard.Region);
        Assert.Equal(NesRegionSelectionSource.Nes20Header, machine.RegionSelection.Source);
        Assert.Equal(312, machine.Motherboard.PpuTiming.ScanlinesPerFrame);
    }

    [Fact]
    public void Japanese_filename_refines_ntsc_header_to_ntsc_j_motherboard()
    {
        var machine = VirtualHardwareNesMachineFactory.Load(
            CreateRom(nes20: true, timing: 0),
            "Game (Japan).nes");

        Assert.Equal(NesHardwareRegion.NtscJapan, machine.Motherboard.Region);
        Assert.Equal(NesRegionSelectionSource.FileName, machine.RegionSelection.Source);
    }

    [Fact]
    public void Explicit_region_override_has_priority_over_rom_metadata()
    {
        var machine = VirtualHardwareNesMachineFactory.Load(
            CreateRom(nes20: true, timing: 1),
            "Game (Europe).nes",
            NesRegionSelection.NtscJapan);

        Assert.Equal(NesHardwareRegion.NtscJapan, machine.Motherboard.Region);
        Assert.Equal(NesRegionSelectionSource.ManualOverride, machine.RegionSelection.Source);
    }

    [Fact]
    public void Legacy_ines_pal_hint_selects_pal_before_filename_fallback()
    {
        var machine = VirtualHardwareNesMachineFactory.Load(
            CreateRom(nes20: false, timing: 1),
            "Unknown.nes");

        Assert.Equal(NesHardwareRegion.Pal, machine.Motherboard.Region);
        Assert.Equal(NesRegionSelectionSource.INesHeader, machine.RegionSelection.Source);
    }

    [Fact]
    public void Auto_mode_falls_back_to_ntsc_u_when_metadata_is_ambiguous()
    {
        var machine = VirtualHardwareNesMachineFactory.Load(CreateRom(), "Game.nes");

        Assert.Equal(NesHardwareRegion.NtscNorthAmerica, machine.Motherboard.Region);
        Assert.Equal(NesRegionSelectionSource.Default, machine.RegionSelection.Source);
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
    public void Factory_rejects_mapper_not_yet_wired_into_virtual_hardware()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            VirtualHardwareNesMachineFactory.Load(CreateRom(mapper: 1), "MMC1.nes"));

        Assert.Contains("mapper 0 only", error.Message);
    }

    [Fact]
    public void Factory_constructs_nrom_128_with_physical_prg_mirroring()
    {
        var machine = VirtualHardwareNesMachineFactory.Load(CreateRom(), "Game (USA).nes");

        Assert.Equal(16 * 1024, machine.Cartridge.PrgRomSizeBytes);
        Assert.Equal(0xA9, machine.Motherboard.PrgRom.Inspect(0));
        Assert.Equal(0xA9, machine.Motherboard.PrgRom.Inspect(16 * 1024));
    }

    private static byte[] CreateRom(
        bool nes20 = false,
        int timing = 0,
        int mapper = 0,
        bool hasTrainer = false,
        int chrUnits = 0)
    {
        var trainerSize = hasTrainer ? 512 : 0;
        var bytes = new byte[16 + trainerSize + (16 * 1024) + (chrUnits * 8 * 1024)];
        bytes[0] = 0x4E;
        bytes[1] = 0x45;
        bytes[2] = 0x53;
        bytes[3] = 0x1A;
        bytes[4] = 1;
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
        bytes[payload + 0x3FFC] = 0x00;
        bytes[payload + 0x3FFD] = 0x80;
        if (chrUnits > 0)
            bytes[payload + 16 * 1024] = 0x3C;
        return bytes;
    }
}
