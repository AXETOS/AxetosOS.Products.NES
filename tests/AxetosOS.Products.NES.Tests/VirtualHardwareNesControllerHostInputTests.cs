using AxetosOS.Products.NES.VirtualHardware.Components.Nes;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Machines.Nes;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareNesControllerHostInputTests
{
    [Theory]
    [InlineData(ControllerExecutionMode.SpecializedNrom)]
    [InlineData(ControllerExecutionMode.GenericCompiled)]
    [InlineData(ControllerExecutionMode.RawPhysical)]
    public void Powered_host_input_reaches_cpu_only_through_controller_button_traces_and_serial_hardware(
        ControllerExecutionMode mode)
    {
        var host = new VirtualNesBootHost();
        if (mode == ControllerExecutionMode.GenericCompiled)
            host.Machine.SetCompiledLabExecutionEnabled(true);
        if (mode == ControllerExecutionMode.RawPhysical)
            host.AutomaticCompiledExecutionEnabled = false;

        host.LoadRom(CreateControllerPollingImage(), "Controller Input (Japan).nes", NesRegionSelection.NtscJapan);
        if (mode == ControllerExecutionMode.RawPhysical)
            host.Machine.Famicom.SetCompiledPhysicalMachineEnabled(false);

        host.PowerAndReleaseReset();

        // First loop observes the unpressed physical button inputs.
        host.RunUntil(diagnostics => diagnostics.CpuInstructions >= 30, 200_000);
        AssertControllerRam(host, 0x00);

        // Change external switch levels while the console is running. These
        // writes touch only host-source output pins connected to controller
        // button traces; the CPU sees them after its normal $4016 strobe/read.
        SetPattern(host, 0xA5);
        Assert.Equal(DigitalLevel.High,
            host.Machine.Famicom.Controller1.Buttons.Pins[(int)NesControllerButton.A].SampledLevel);
        Assert.Equal(DigitalLevel.Low,
            host.Machine.Famicom.Controller1.Buttons.Pins[(int)NesControllerButton.B].SampledLevel);

        host.RunUntil(diagnostics => diagnostics.CpuInstructions >= 60, 200_000);
        AssertControllerRam(host, 0xA5);

        SetPattern(host, 0x5A);
        host.RunUntil(diagnostics => diagnostics.CpuInstructions >= 90, 200_000);
        AssertControllerRam(host, 0x5A);
    }

    [Fact]
    public void Both_controller_ports_have_independent_external_button_sources()
    {
        var host = new VirtualNesBootHost();
        host.LoadRom(CreateControllerPollingImage(), "Controller Ports (Japan).nes", NesRegionSelection.NtscJapan);

        host.Machine.SetControllerButton(0, NesControllerButton.A, true);
        host.Machine.SetControllerButton(1, NesControllerButton.B, true);

        Assert.Equal((byte)0x01, host.Machine.InspectControllerButtons(0));
        Assert.Equal((byte)0x02, host.Machine.InspectControllerButtons(1));
        Assert.Equal(DigitalLevel.High,
            host.Machine.Famicom.Controller1.Buttons.Pins[(int)NesControllerButton.A].SampledLevel);
        Assert.Equal(DigitalLevel.High,
            host.Machine.Famicom.Controller2.Buttons.Pins[(int)NesControllerButton.B].SampledLevel);
    }

    private static void SetPattern(VirtualNesBootHost host, byte pattern)
    {
        foreach (var button in Enum.GetValues<NesControllerButton>())
        {
            var pressed = (pattern & (1 << (int)button)) != 0;
            host.Machine.SetControllerButton(0, button, pressed);
        }
    }

    private static void AssertControllerRam(VirtualNesBootHost host, byte expectedPattern)
    {
        for (var bit = 0; bit < 8; bit++)
        {
            Assert.Equal((byte)((expectedPattern >> bit) & 1), host.Machine.Famicom.CpuRam.Inspect(bit));
        }
    }

    private static VirtualHardwareNesRomImage CreateControllerPollingImage()
    {
        var prg = Enumerable.Repeat((byte)0xEA, 16 * 1024).ToArray();
        var program = new List<byte>
        {
            0xA9, 0x01,             // LDA #$01
            0x8D, 0x16, 0x40,       // STA $4016 - strobe High
            0xA9, 0x00,             // LDA #$00
            0x8D, 0x16, 0x40        // STA $4016 - latch/freeze
        };

        for (byte bit = 0; bit < 8; bit++)
        {
            program.AddRange(
            [
                0xAD, 0x16, 0x40,   // LDA $4016
                0x29, 0x01,         // AND #$01
                0x85, bit           // STA $00+bit
            ]);
        }

        program.AddRange([0x4C, 0x00, 0x80]); // JMP $8000
        program.CopyTo(prg, 0);
        prg[0x3FFC] = 0x00;
        prg[0x3FFD] = 0x80;

        return new VirtualHardwareNesRomImage(
            VirtualHardwareNesHeaderFormat.INes,
            0,
            null,
            prg.Length,
            8 * 1024,
            false,
            false,
            VirtualHardwareNesMirroring.Horizontal,
            VirtualHardwareNesHeaderTiming.Ntsc,
            prg,
            new byte[8 * 1024]);
    }

    public enum ControllerExecutionMode
    {
        SpecializedNrom,
        GenericCompiled,
        RawPhysical
    }
}
