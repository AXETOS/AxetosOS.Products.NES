using AxetosOS.Products.NES.Abstractions;
using AxetosOS.Products.NES.Cartridges;
using AxetosOS.Products.NES.Hardware;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class HardwareFoundationTests
{
    [Fact]
    public void CpuWorkRamMirrorsEveryTwoKilobytes()
    {
        var ram = new CpuWorkRam();
        ram.PowerOn();

        ram.CpuWrite(0x0002, 0x42);

        Assert.Equal(0x42, ram.CpuRead(0x0802));
        Assert.Equal(0x42, ram.CpuRead(0x1002));
        Assert.Equal(0x42, ram.CpuRead(0x1802));
    }

    [Fact]
    public void Nrom128MirrorsItsSixteenKilobytePrgRom()
    {
        var image = CreateNromImage(16 * 1024);
        image.PrgRom[0] = 0x12;
        image.PrgRom[0x3FFF] = 0x34;
        var prg = new NromPrgRom(image);

        Assert.Equal(0x12, prg.CpuRead(0x8000));
        Assert.Equal(0x12, prg.CpuRead(0xC000));
        Assert.Equal(0x34, prg.CpuRead(0xBFFF));
        Assert.Equal(0x34, prg.CpuRead(0xFFFF));
    }

    [Fact]
    public void CpuLoadsResetVectorThroughTheBus()
    {
        var image = CreateNromImage(16 * 1024);
        image.PrgRom[0x3FFC] = 0x00;
        image.PrgRom[0x3FFD] = 0x80;
        var bus = CreateBus(image);
        var cpu = new Rp2A03Cpu(bus);

        cpu.PowerOn();

        Assert.Equal(0x8000, cpu.ProgramCounter);
    }

    [Fact]
    public void CpuExecutesInitialProgramThroughVirtualHardware()
    {
        var image = CreateNromImage(16 * 1024);
        var program = new byte[]
        {
            0xA9, 0x7F,       // LDA #$7F
            0x8D, 0x02, 0x00, // STA $0002
            0xEA,             // NOP
            0x4C, 0x05, 0x80  // JMP $8005
        };
        program.CopyTo(image.PrgRom, 0);
        image.PrgRom[0x3FFC] = 0x00;
        image.PrgRom[0x3FFD] = 0x80;

        var ram = new CpuWorkRam();
        ram.PowerOn();
        var bus = new CpuBus();
        bus.Attach(ram);
        bus.Attach(new NromPrgRom(image));
        var cpu = new Rp2A03Cpu(bus);
        cpu.PowerOn();

        for (var cycle = 0; cycle < 18; cycle++)
        {
            cpu.Clock();
        }

        Assert.Equal(0x7F, cpu.Accumulator);
        Assert.Equal(0x7F, ram.CpuRead(0x0002));
        Assert.Equal(0x8005, cpu.ProgramCounter);
    }

    [Fact]
    public void MasterClockRunsCpuAtOneThirdOfPpuRate()
    {
        var cpu = new CountingClockedModule();
        var ppu = new CountingClockedModule();
        var clock = new NesMasterClock(cpu, ppu);

        for (var tick = 0; tick < 12; tick++)
        {
            clock.Tick();
        }

        Assert.Equal(12UL, clock.PpuCycles);
        Assert.Equal(4UL, clock.CpuCycles);
        Assert.Equal(12, ppu.ClockCount);
        Assert.Equal(4, cpu.ClockCount);
    }

    private static CpuBus CreateBus(NesRomImage image)
    {
        var bus = new CpuBus();
        bus.Attach(new CpuWorkRam());
        bus.Attach(new NromPrgRom(image));
        return bus;
    }

    private static NesRomImage CreateNromImage(int prgSize) => new(
        NesHeaderFormat.INes,
        MapperNumber: 0,
        SubmapperNumber: null,
        PrgRomSizeBytes: prgSize,
        ChrRomSizeBytes: 8 * 1024,
        HasTrainer: false,
        HasBatteryBackedMemory: false,
        Mirroring: NametableMirroring.Horizontal,
        PrgRom: new byte[prgSize],
        ChrRom: new byte[8 * 1024]);

    private sealed class CountingClockedModule : IClockedHardwareModule
    {
        public int ClockCount { get; private set; }
        public void Clock() => ClockCount++;
    }
}
