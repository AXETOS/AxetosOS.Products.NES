using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareNesPpuBackgroundTests
{
    [Fact]
    public void Background_pipeline_fetches_tile_pattern_attribute_and_palette()
    {
        var machine = CreateMachine(0x0A);
        machine.PpuMemory.LoadForDiagnostics(0x2000, new byte[] { 1 });
        machine.PpuMemory.LoadForDiagnostics(0x23C0, new byte[] { 0x00 });
        machine.PpuMemory.LoadForDiagnostics(0x0010, new byte[] { 0x80 });
        machine.PpuMemory.LoadForDiagnostics(0x3F00, new byte[] { 0x0F, 0x21 });

        machine.AdvancePpuDots(1);

        Assert.Equal(0x21, machine.PpuRegisters.InspectPixel(0, 0));
        Assert.Equal(DigitalLevel.High, machine.Board.Nets.Single(n => n.Name == "PPU_DOT0").Level);
        Assert.True(machine.PpuRegisters.BackgroundFetchCount >= 4);
    }

    [Fact]
    public void Background_pipeline_obeys_left_edge_masking()
    {
        var machine = CreateMachine(0x08);
        machine.PpuMemory.LoadForDiagnostics(0x2000, new byte[] { 1 });
        machine.PpuMemory.LoadForDiagnostics(0x0010, new byte[] { 0x80 });
        machine.PpuMemory.LoadForDiagnostics(0x3F00, new byte[] { 0x0F, 0x21 });

        machine.AdvancePpuDots(1);

        Assert.Equal(0x0F, machine.PpuRegisters.InspectPixel(0, 0));
    }

    [Fact]
    public void Background_pipeline_uses_attribute_palette_quadrants()
    {
        var machine = CreateMachine(0x0A);
        machine.PpuMemory.LoadForDiagnostics(0x2000, new byte[] { 1 });
        machine.PpuMemory.LoadForDiagnostics(0x23C0, new byte[] { 0x02 });
        machine.PpuMemory.LoadForDiagnostics(0x0010, new byte[] { 0x80 });
        machine.PpuMemory.LoadForDiagnostics(0x3F00, new byte[] { 0x0F, 0, 0, 0, 0, 0, 0, 0, 0, 0x2A });

        machine.AdvancePpuDots(1);

        Assert.Equal(0x2A, machine.PpuRegisters.InspectPixel(0, 0));
    }

    [Fact]
    public void Disabled_background_outputs_universal_backdrop_color()
    {
        var machine = CreateMachine(0x00);
        machine.PpuMemory.LoadForDiagnostics(0x3F00, new byte[] { 0x16 });
        var renderedBefore = machine.PpuRegisters.RenderedPixelCount;

        machine.AdvancePpuDots(1);

        Assert.Equal(0x16, machine.PpuRegisters.InspectPixel(0, 0));
        Assert.Equal(renderedBefore + 1, machine.PpuRegisters.RenderedPixelCount);
    }


    [Fact]
    public void Background_pipeline_fetches_exclusively_through_external_ppu_bus()
    {
        var machine = CreateMachine(0x0A);
        machine.PpuMemory.LoadForDiagnostics(0x2000, new byte[] { 1 });
        machine.PpuMemory.LoadForDiagnostics(0x23C0, new byte[] { 0 });
        machine.PpuMemory.LoadForDiagnostics(0x0010, new byte[] { 0x80 });
        machine.PpuMemory.LoadForDiagnostics(0x3F00, new byte[] { 0x0F, 0x21 });
        var readsBefore = machine.PpuRegisters.RenderBusReadCount;

        machine.AdvancePpuDots(1);

        Assert.Equal((byte)0x21, machine.PpuRegisters.InspectPixel(0, 0));
        Assert.True(machine.PpuRegisters.RenderBusReadCount >= readsBefore + 5);
        Assert.True(machine.PpuMemory.ReadDriveCount >= 5);
        Assert.DoesNotContain(machine.Board.Nets, net => net.Level == DigitalLevel.Contention);
    }

    private static NesCpuMotherboard CreateMachine(byte mask)
    {
        var prg = new byte[NesCpuMotherboard.PrgRomSize];
        var program = new byte[] { 0xA9, mask, 0x8D, 0x01, 0x20, 0x00 };
        program.CopyTo(prg, 0);
        prg[0x7FFC] = 0x00;
        prg[0x7FFD] = 0x80;
        var machine = new NesCpuMotherboard(prg);
        machine.PowerOn();
        machine.ReleaseReset();
        machine.RunUntilHalted();
        machine.ResetPpuTiming();
        return machine;
    }
}
