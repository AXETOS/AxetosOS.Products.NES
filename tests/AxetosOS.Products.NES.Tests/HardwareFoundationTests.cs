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


    [Fact]
    public void CpuSupportsStackSubroutinesAndTransfers()
    {
        var image = CreateNromImage(16 * 1024);
        var program = new byte[]
        {
            0xA2, 0x05,       // LDX #$05
            0x9A,             // TXS
            0x20, 0x09, 0x80, // JSR $8009
            0x8E, 0x04, 0x00, // STX $0004
            0xE8,             // INX
            0x60              // RTS
        };
        program.CopyTo(image.PrgRom, 0);
        SetVectors(image, reset: 0x8000, nmi: 0x8000, irq: 0x8000);

        var ram = new CpuWorkRam();
        ram.PowerOn();
        var cpu = CreateCpu(image, ram);
        RunInstructions(cpu, 6);

        Assert.Equal(0x06, cpu.X);
        Assert.Equal(0x06, ram.CpuRead(0x0004));
        Assert.Equal(0x05, cpu.StackPointer);
    }

    [Fact]
    public void CpuAdcAndSbcUpdateArithmeticFlags()
    {
        var image = CreateNromImage(16 * 1024);
        var program = new byte[]
        {
            0x18,       // CLC
            0xA9, 0x50, // LDA #$50
            0x69, 0x50, // ADC #$50 => $A0, overflow
            0x38,       // SEC
            0xE9, 0x20  // SBC #$20 => $80
        };
        program.CopyTo(image.PrgRom, 0);
        SetVectors(image, reset: 0x8000, nmi: 0x8000, irq: 0x8000);
        var cpu = CreateCpu(image);

        RunInstructions(cpu, 5);

        Assert.Equal(0x80, cpu.Accumulator);
        Assert.True(cpu.IsFlagSet(Rp2A03Cpu.CarryFlag));
        Assert.True(cpu.IsFlagSet(Rp2A03Cpu.NegativeFlag));
    }

    [Fact]
    public void CpuBranchesWithSignedRelativeOffsets()
    {
        var image = CreateNromImage(16 * 1024);
        var program = new byte[]
        {
            0xA2, 0x03, // LDX #$03
            0xCA,       // DEX
            0xD0, 0xFD, // BNE $8002
            0x8E, 0x05, 0x00
        };
        program.CopyTo(image.PrgRom, 0);
        SetVectors(image, reset: 0x8000, nmi: 0x8000, irq: 0x8000);
        var ram = new CpuWorkRam();
        ram.PowerOn();
        var cpu = CreateCpu(image, ram);

        RunInstructions(cpu, 8);

        Assert.Equal(0, cpu.X);
        Assert.Equal(0, ram.CpuRead(0x0005));
        Assert.True(cpu.IsFlagSet(Rp2A03Cpu.ZeroFlag));
    }

    [Fact]
    public void CpuServicesNmiAndReturnsWithRti()
    {
        var image = CreateNromImage(16 * 1024);
        image.PrgRom[0x0000] = 0xEA; // main NOP
        image.PrgRom[0x1000] = 0xE8; // NMI: INX
        image.PrgRom[0x1001] = 0x40; // RTI
        SetVectors(image, reset: 0x8000, nmi: 0x9000, irq: 0x8000);
        var cpu = CreateCpu(image);

        RunInstructions(cpu, 1);
        cpu.RequestNmi();
        RunInstructions(cpu, 2);

        Assert.Equal(1, cpu.X);
        Assert.Equal(0x8001, cpu.ProgramCounter);
    }

    private static CpuBus CreateBus(NesRomImage image)
    {
        var bus = new CpuBus();
        bus.Attach(new CpuWorkRam());
        bus.Attach(new NromPrgRom(image));
        return bus;
    }


    private static Rp2A03Cpu CreateCpu(NesRomImage image, CpuWorkRam? ram = null)
    {
        ram ??= new CpuWorkRam();
        ram.PowerOn();
        var bus = new CpuBus();
        bus.Attach(ram);
        bus.Attach(new NromPrgRom(image));
        var cpu = new Rp2A03Cpu(bus);
        cpu.PowerOn();
        return cpu;
    }

    private static void RunInstructions(Rp2A03Cpu cpu, int instructionCount)
    {
        var target = cpu.InstructionsExecuted + (ulong)instructionCount;
        while (cpu.InstructionsExecuted < target || !cpu.IsInstructionBoundary)
        {
            cpu.Clock();
        }
    }

    private static void SetVectors(NesRomImage image, ushort reset, ushort nmi, ushort irq)
    {
        image.PrgRom[0x3FFA] = (byte)nmi;
        image.PrgRom[0x3FFB] = (byte)(nmi >> 8);
        image.PrgRom[0x3FFC] = (byte)reset;
        image.PrgRom[0x3FFD] = (byte)(reset >> 8);
        image.PrgRom[0x3FFE] = (byte)irq;
        image.PrgRom[0x3FFF] = (byte)(irq >> 8);
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
