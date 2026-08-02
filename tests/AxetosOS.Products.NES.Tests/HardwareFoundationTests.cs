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


    [Fact]
    public void CpuSupportsIndexedAndIndirectAddressingModes()
    {
        var image = CreateNromImage(16 * 1024);
        var program = new byte[]
        {
            0xA2, 0x04,       // LDX #$04
            0xA0, 0x03,       // LDY #$03
            0xA9, 0x10,       // LDA #$10
            0x85, 0x20,       // STA $20
            0xA9, 0x00,       // LDA #$00
            0x85, 0x21,       // STA $21 => pointer $0010
            0xA9, 0x7B,       // LDA #$7B
            0x95, 0x0C,       // STA $0C,X => $10
            0xB1, 0x20,       // LDA ($20),Y => $0013
            0x81, 0x1C        // STA ($1C,X) => pointer at $20
        };
        program.CopyTo(image.PrgRom, 0);
        SetVectors(image, reset: 0x8000, nmi: 0x8000, irq: 0x8000);
        var ram = new CpuWorkRam();
        ram.PowerOn();
        ram.CpuWrite(0x0013, 0x42);
        var cpu = CreateCpu(image, ram);

        RunInstructions(cpu, 10);

        Assert.Equal(0x42, cpu.Accumulator);
        Assert.Equal(0x42, ram.CpuRead(0x0010));
    }

    [Fact]
    public void CpuSupportsLogicShiftAndCompareInstructions()
    {
        var image = CreateNromImage(16 * 1024);
        var program = new byte[]
        {
            0xA9, 0x81, // LDA #$81
            0x0A,       // ASL A => $02, carry
            0x2A,       // ROL A => $05
            0x49, 0xFF, // EOR #$FF => $FA
            0x29, 0x0F, // AND #$0F => $0A
            0x09, 0x80, // ORA #$80 => $8A
            0xC9, 0x8A  // CMP #$8A
        };
        program.CopyTo(image.PrgRom, 0);
        SetVectors(image, reset: 0x8000, nmi: 0x8000, irq: 0x8000);
        var cpu = CreateCpu(image);

        RunInstructions(cpu, 7);

        Assert.Equal(0x8A, cpu.Accumulator);
        Assert.True(cpu.IsFlagSet(Rp2A03Cpu.ZeroFlag));
        Assert.True(cpu.IsFlagSet(Rp2A03Cpu.CarryFlag));
    }

    [Fact]
    public void CpuSupportsMemoryIncrementDecrementAndBitTest()
    {
        var image = CreateNromImage(16 * 1024);
        var program = new byte[]
        {
            0xA9, 0x40,       // LDA #$40
            0x85, 0x30,       // STA $30
            0xE6, 0x30,       // INC $30
            0xC6, 0x30,       // DEC $30
            0x24, 0x30        // BIT $30
        };
        program.CopyTo(image.PrgRom, 0);
        SetVectors(image, reset: 0x8000, nmi: 0x8000, irq: 0x8000);
        var ram = new CpuWorkRam();
        ram.PowerOn();
        var cpu = CreateCpu(image, ram);

        RunInstructions(cpu, 5);

        Assert.Equal(0x40, ram.CpuRead(0x0030));
        Assert.False(cpu.IsFlagSet(Rp2A03Cpu.ZeroFlag));
        Assert.True(cpu.IsFlagSet(Rp2A03Cpu.OverflowFlag));
    }

    [Fact]
    public void CpuSupportsStackAccumulatorAndStatusInstructions()
    {
        var image = CreateNromImage(16 * 1024);
        var program = new byte[]
        {
            0xA9, 0xAA, // LDA #$AA
            0x48,       // PHA
            0xA9, 0x00, // LDA #$00
            0x68,       // PLA
            0x38,       // SEC
            0x08,       // PHP
            0x18,       // CLC
            0x28        // PLP
        };
        program.CopyTo(image.PrgRom, 0);
        SetVectors(image, reset: 0x8000, nmi: 0x8000, irq: 0x8000);
        var cpu = CreateCpu(image);

        RunInstructions(cpu, 8);

        Assert.Equal(0xAA, cpu.Accumulator);
        Assert.True(cpu.IsFlagSet(Rp2A03Cpu.CarryFlag));
    }

    [Fact]
    public void CpuImplementsThe6502IndirectJumpPageWrapQuirk()
    {
        var image = CreateNromImage(16 * 1024);
        var program = new byte[] { 0x6C, 0xFF, 0x02 }; // JMP ($02FF)
        program.CopyTo(image.PrgRom, 0);
        SetVectors(image, reset: 0x8000, nmi: 0x8000, irq: 0x8000);
        var ram = new CpuWorkRam();
        ram.PowerOn();
        ram.CpuWrite(0x02FF, 0x34);
        ram.CpuWrite(0x0200, 0x12);
        ram.CpuWrite(0x0300, 0x99);
        var cpu = CreateCpu(image, ram);

        RunInstructions(cpu, 1);

        Assert.Equal(0x1234, cpu.ProgramCounter);
    }


    [Fact]
    public void PpuBusMasksAddressesToFourteenBits()
    {
        var palette = new PpuPaletteRam();
        palette.PowerOn();
        var bus = new PpuBus();
        bus.Attach(palette);

        bus.Write(0x7F00, 0x2A);

        Assert.Equal(0x2A, bus.Read(0x3F00));
    }

    [Fact]
    public void CiramImplementsHorizontalAndVerticalMirroring()
    {
        var horizontal = new CiramNametableRam(NametableMirroring.Horizontal);
        horizontal.PowerOn();
        horizontal.PpuWrite(0x2000, 0x11);
        horizontal.PpuWrite(0x2800, 0x22);
        Assert.Equal(0x11, horizontal.PpuRead(0x2400));
        Assert.Equal(0x22, horizontal.PpuRead(0x2C00));

        var vertical = new CiramNametableRam(NametableMirroring.Vertical);
        vertical.PowerOn();
        vertical.PpuWrite(0x2000, 0x33);
        vertical.PpuWrite(0x2400, 0x44);
        Assert.Equal(0x33, vertical.PpuRead(0x2800));
        Assert.Equal(0x44, vertical.PpuRead(0x2C00));
    }

    [Fact]
    public void PaletteRamImplementsUniversalBackgroundAliases()
    {
        var palette = new PpuPaletteRam();
        palette.PowerOn();

        palette.PpuWrite(0x3F10, 0x7F);

        Assert.Equal(0x3F, palette.PpuRead(0x3F00));
        Assert.Equal(0x3F, palette.PpuRead(0x3F30));
    }

    [Fact]
    public void NromChrSupportsRomAndRamCartridges()
    {
        var romImage = CreateNromImage(16 * 1024);
        romImage.ChrRom[0x123] = 0x55;
        var chrRom = new NromChrMemory(romImage);
        chrRom.PpuWrite(0x0123, 0xAA);
        Assert.False(chrRom.IsWritable);
        Assert.Equal(0x55, chrRom.PpuRead(0x0123));

        var ramImage = romImage with
        {
            ChrRomSizeBytes = 0,
            ChrRom = []
        };
        var chrRam = new NromChrMemory(ramImage);
        chrRam.PowerOn();
        chrRam.PpuWrite(0x0123, 0xAA);
        Assert.True(chrRam.IsWritable);
        Assert.Equal(0xAA, chrRam.PpuRead(0x0123));
    }

    [Fact]
    public void PpuRegistersWriteAndReadThroughThePpuBus()
    {
        var (ppu, bus, _) = CreatePpu();
        ppu.CpuWrite(0x2006, 0x20);
        ppu.CpuWrite(0x2006, 0x00);
        ppu.CpuWrite(0x2007, 0x5A);
        Assert.Equal(0x5A, bus.Read(0x2000));

        ppu.CpuWrite(0x2006, 0x20);
        ppu.CpuWrite(0x2006, 0x00);
        Assert.Equal(0x00, ppu.CpuRead(0x2007));
        Assert.Equal(0x5A, ppu.CpuRead(0x2007));
    }

    [Fact]
    public void PpuEntersVBlankAssertsNmiAndStatusReadClearsIt()
    {
        var (ppu, _, nmi) = CreatePpu();
        ppu.CpuWrite(0x2000, 0x80);

        var clocksToVBlank = (241 * 341) + 2;
        for (var i = 0; i < clocksToVBlank; i++)
        {
            ppu.Clock();
        }

        Assert.True(ppu.InVBlank);
        Assert.True(nmi.IsAsserted);
        Assert.True((ppu.CpuRead(0x2002) & 0x80) != 0);
        Assert.False(ppu.InVBlank);
        Assert.False(nmi.IsAsserted);
    }

    [Fact]
    public void PpuCompletesAFrameAndProducesFramebufferPixels()
    {
        var (ppu, bus, _) = CreatePpu();
        bus.Write(0x3F00, 0x21);

        var clocksPerFrame = 262 * 341;
        for (var i = 0; i < clocksPerFrame; i++)
        {
            ppu.Clock();
        }

        Assert.True(ppu.FrameCompleted);
        Assert.Equal(1UL, ppu.Frame);
        Assert.NotEqual(0U, ppu.Framebuffer[Rp2C02Ppu.ScreenWidth]);
        Assert.Equal(Rp2C02Ppu.ScreenWidth * Rp2C02Ppu.ScreenHeight, ppu.Framebuffer.Length);
    }

    [Fact]
    public void PpuRendersBackgroundPatternAndAttributePalette()
    {
        var image = CreateNromImage(16 * 1024);
        for (var row = 0; row < 8; row++)
        {
            image.ChrRom[16 + row] = 0b10101010;
            image.ChrRom[24 + row] = 0;
        }

        var bus = new PpuBus();
        var chr = new NromChrMemory(image);
        var ciram = new CiramNametableRam(image.Mirroring);
        var palette = new PpuPaletteRam();
        chr.PowerOn();
        ciram.PowerOn();
        palette.PowerOn();
        bus.Attach(chr);
        bus.Attach(ciram);
        bus.Attach(palette);
        bus.Write(0x2000, 1);
        bus.Write(0x23C0, 0b00000010);
        bus.Write(0x3F00, 0x0F);
        bus.Write(0x3F05, 0x21);
        var ppu = new Rp2C02Ppu(bus, new SignalLine());
        ppu.PowerOn();
        ppu.CpuWrite(0x2001, 0x0A);

        for (var i = 0; i < 9; i++)
        {
            ppu.Clock();
        }

        Assert.NotEqual(ppu.Framebuffer[0], ppu.Framebuffer[1]);
        Assert.Equal(ppu.Framebuffer[0], ppu.Framebuffer[2]);
    }

    [Fact]
    public void PpuUsesSelectedBackgroundPatternTable()
    {
        var image = CreateNromImage(16 * 1024);
        image.ChrRom[0x1010] = 0x80;
        var bus = new PpuBus();
        var chr = new NromChrMemory(image);
        var ciram = new CiramNametableRam(image.Mirroring);
        var palette = new PpuPaletteRam();
        chr.PowerOn();
        ciram.PowerOn();
        palette.PowerOn();
        bus.Attach(chr);
        bus.Attach(ciram);
        bus.Attach(palette);
        bus.Write(0x2000, 1);
        bus.Write(0x3F00, 0x0F);
        bus.Write(0x3F01, 0x30);
        var ppu = new Rp2C02Ppu(bus, new SignalLine());
        ppu.PowerOn();
        ppu.CpuWrite(0x2000, 0x10);
        ppu.CpuWrite(0x2001, 0x0A);

        ppu.Clock();
        ppu.Clock();
        ppu.Clock();

        Assert.NotEqual(ppu.Framebuffer[0], ppu.Framebuffer[1]);
    }

    [Fact]
    public void OamDmaCopiesCpuPageAndWrapsFromCurrentOamAddress()
    {
        var image = CreateNromImage(16 * 1024);
        SetVectors(image, reset: 0x8000, nmi: 0x8000, irq: 0x8000);
        var ram = new CpuWorkRam();
        ram.PowerOn();
        for (var index = 0; index < 256; index++)
        {
            ram.CpuWrite((ushort)(0x0200 + index), (byte)index);
        }

        var (ppu, _, _) = CreatePpu();
        ppu.CpuWrite(0x2003, 0xFE);
        var cpuBus = new CpuBus();
        cpuBus.Attach(ram);
        cpuBus.Attach(ppu);
        cpuBus.Attach(new NromPrgRom(image));
        var cpu = new Rp2A03Cpu(cpuBus);
        cpu.PowerOn();
        var dma = new OamDmaController(cpuBus, ppu, cpu);
        dma.PowerOn();
        cpuBus.Attach(dma);

        cpuBus.Write(0x4014, 0x02);

        Assert.Equal(0x00, ppu.ReadOamByte(0xFE));
        Assert.Equal(0x01, ppu.ReadOamByte(0xFF));
        Assert.Equal(0x02, ppu.ReadOamByte(0x00));
        Assert.Equal(1UL, dma.Transfers);
        Assert.Equal(0x02, dma.LastPage);

        for (var cycle = 0; cycle < 513; cycle++)
        {
            cpu.Clock();
        }

        Assert.Equal(0UL, cpu.InstructionsExecuted);
    }

    [Fact]
    public void PpuRendersSpritePixelsAndSetsSpriteZeroHit()
    {
        var image = CreateNromImage(16 * 1024);
        for (var row = 0; row < 8; row++)
        {
            image.ChrRom[16 + row] = 0xFF; // background tile 1
            image.ChrRom[32 + row] = 0xFF; // sprite tile 2
        }

        var bus = new PpuBus();
        var chr = new NromChrMemory(image);
        var ciram = new CiramNametableRam(image.Mirroring);
        var palette = new PpuPaletteRam();
        chr.PowerOn();
        ciram.PowerOn();
        palette.PowerOn();
        bus.Attach(chr);
        bus.Attach(ciram);
        bus.Attach(palette);
        bus.Write(0x2000, 1);
        bus.Write(0x3F01, 0x21);
        bus.Write(0x3F11, 0x30);

        var ppu = new Rp2C02Ppu(bus, new SignalLine());
        ppu.PowerOn();
        ppu.CpuWrite(0x2003, 0x00);
        ppu.CpuWrite(0x2004, 0x00); // visible from scanline 1
        ppu.CpuWrite(0x2004, 0x02);
        ppu.CpuWrite(0x2004, 0x00);
        ppu.CpuWrite(0x2004, 0x00);
        ppu.CpuWrite(0x2001, 0x1E);

        for (var cycle = 0; cycle < 343; cycle++)
        {
            ppu.Clock();
        }

        Assert.True((ppu.Status & 0x40) != 0);
        Assert.NotEqual(0U, ppu.Framebuffer[0]);
    }

    [Fact]
    public void PpuSetsSpriteOverflowWhenMoreThanEightSpritesShareAScanline()
    {
        var image = CreateNromImage(16 * 1024);
        for (var row = 0; row < 8; row++)
        {
            image.ChrRom[32 + row] = 0xFF;
        }

        var bus = new PpuBus();
        var chr = new NromChrMemory(image);
        var ciram = new CiramNametableRam(image.Mirroring);
        var palette = new PpuPaletteRam();
        chr.PowerOn();
        ciram.PowerOn();
        palette.PowerOn();
        bus.Attach(chr);
        bus.Attach(ciram);
        bus.Attach(palette);
        bus.Write(0x3F11, 0x30);

        var ppu = new Rp2C02Ppu(bus, new SignalLine());
        ppu.PowerOn();
        ppu.CpuWrite(0x2003, 0x00);
        for (var sprite = 0; sprite < 9; sprite++)
        {
            ppu.CpuWrite(0x2004, 0x00);
            ppu.CpuWrite(0x2004, 0x02);
            ppu.CpuWrite(0x2004, 0x00);
            ppu.CpuWrite(0x2004, (byte)(sprite * 8));
        }

        ppu.CpuWrite(0x2001, 0x14);
        for (var cycle = 0; cycle < 343; cycle++)
        {
            ppu.Clock();
        }

        Assert.True((ppu.Status & 0x20) != 0);
    }

    [Fact]
    public void PpuHonorsSpritePriorityBehindOpaqueBackground()
    {
        var image = CreateNromImage(16 * 1024);
        for (var row = 0; row < 8; row++)
        {
            image.ChrRom[16 + row] = 0xFF;
            image.ChrRom[32 + row] = 0xFF;
        }

        var bus = new PpuBus();
        var chr = new NromChrMemory(image);
        var ciram = new CiramNametableRam(image.Mirroring);
        var palette = new PpuPaletteRam();
        chr.PowerOn();
        ciram.PowerOn();
        palette.PowerOn();
        bus.Attach(chr);
        bus.Attach(ciram);
        bus.Attach(palette);
        bus.Write(0x2000, 1);
        bus.Write(0x3F01, 0x21);
        bus.Write(0x3F11, 0x30);

        var backgroundOnly = new Rp2C02Ppu(bus, new SignalLine());
        backgroundOnly.PowerOn();
        backgroundOnly.CpuWrite(0x2001, 0x0A);
        for (var cycle = 0; cycle < 343; cycle++)
        {
            backgroundOnly.Clock();
        }
        var expectedBackground = backgroundOnly.Framebuffer[Rp2C02Ppu.ScreenWidth];

        var ppu = new Rp2C02Ppu(bus, new SignalLine());
        ppu.PowerOn();
        ppu.CpuWrite(0x2003, 0x00);
        ppu.CpuWrite(0x2004, 0x00);
        ppu.CpuWrite(0x2004, 0x02);
        ppu.CpuWrite(0x2004, 0x20); // behind background
        ppu.CpuWrite(0x2004, 0x00);
        ppu.CpuWrite(0x2001, 0x1E);
        for (var cycle = 0; cycle < 343; cycle++)
        {
            ppu.Clock();
        }

        Assert.Equal(expectedBackground, ppu.Framebuffer[Rp2C02Ppu.ScreenWidth]);
    }



    [Fact]
    public void PpuDoesNotWrapHiddenSpriteAtY255OntoTopScanline()
    {
        var image = CreateNromImage(16 * 1024);
        for (var row = 0; row < 8; row++)
        {
            image.ChrRom[32 + row] = 0xFF;
        }

        var bus = new PpuBus();
        var chr = new NromChrMemory(image);
        var ciram = new CiramNametableRam(image.Mirroring);
        var palette = new PpuPaletteRam();
        chr.PowerOn();
        ciram.PowerOn();
        palette.PowerOn();
        bus.Attach(chr);
        bus.Attach(ciram);
        bus.Attach(palette);
        bus.Write(0x3F00, 0x0F);
        bus.Write(0x3F11, 0x30);

        var ppu = new Rp2C02Ppu(bus, new SignalLine());
        ppu.PowerOn();
        ppu.CpuWrite(0x2003, 0x00);
        ppu.CpuWrite(0x2004, 0xFF); // hidden below the visible frame
        ppu.CpuWrite(0x2004, 0x02);
        ppu.CpuWrite(0x2004, 0x00);
        ppu.CpuWrite(0x2004, 0x00);
        ppu.CpuWrite(0x2001, 0x14);

        ppu.Clock();
        ppu.Clock();

        Assert.Equal(0xFF000000U, ppu.Framebuffer[0]);
        Assert.True((ppu.Status & 0x40) == 0);
    }

    [Fact]
    public void PpuScrollWritesPopulateTemporaryAddressAndFineX()
    {
        var (ppu, _, _) = CreatePpu();
        ppu.CpuWrite(0x2005, 0x2D);
        Assert.Equal((byte)5, ppu.FineXScroll);
        Assert.True(ppu.WriteToggle);
        Assert.Equal((ushort)5, (ushort)(ppu.TemporaryVramAddress & 0x001F));

        ppu.CpuWrite(0x2005, 0x3A);
        Assert.False(ppu.WriteToggle);
        Assert.Equal((ushort)2, (ushort)((ppu.TemporaryVramAddress >> 12) & 0x07));
        Assert.Equal((ushort)7, (ushort)((ppu.TemporaryVramAddress >> 5) & 0x1F));
    }

    [Fact]
    public void PpuMidScanlineScrollWriteDoesNotReplaceActiveRenderingAddress()
    {
        var (ppu, _, _) = CreatePpu();
        ppu.CpuWrite(0x2006, 0x20);
        ppu.CpuWrite(0x2006, 0x00);
        ppu.Clock(); // dot 0
        ppu.Clock(); // dot 1 latches active scanline address

        var activeAddress = ppu.ActiveScanlineVramAddress;
        ppu.CpuWrite(0x2005, 0xA8);
        ppu.CpuWrite(0x2005, 0x38);

        Assert.Equal((ushort)0x2000, activeAddress);
        Assert.Equal(activeAddress, ppu.ActiveScanlineVramAddress);
        Assert.NotEqual(ppu.TemporaryVramAddress, ppu.ActiveScanlineVramAddress);
    }

    [Fact]
    public void PpuAddressWritesCopyTemporaryAddressIntoCurrentAddress()
    {
        var (ppu, _, _) = CreatePpu();
        ppu.CpuWrite(0x2006, 0x23);
        Assert.True(ppu.WriteToggle);
        ppu.CpuWrite(0x2006, 0x45);
        Assert.False(ppu.WriteToggle);
        Assert.Equal((ushort)0x2345, ppu.TemporaryVramAddress);
        Assert.Equal((ushort)0x2345, ppu.VramAddress);
    }

    [Fact]
    public void ControllerPortsShiftButtonsInNesOrder()
    {
        var input = new MutableNesControllerInput();
        input.SetButtons(0, NesButtons.A | NesButtons.Start | NesButtons.Right);
        var controllers = new NesControllerPorts(input);
        controllers.PowerOn();

        controllers.CpuWrite(0x4016, 1);
        controllers.CpuWrite(0x4016, 0);

        var bits = Enumerable.Range(0, 8).Select(_ => controllers.CpuRead(0x4016) & 1).ToArray();
        Assert.Equal(new[] { 1, 0, 0, 1, 0, 0, 0, 1 }, bits);
        Assert.Equal(1, controllers.CpuRead(0x4016) & 1);
    }

    [Fact]
    public void ControllerStrobeReadsLiveAButtonAndRelatchesOnRelease()
    {
        var input = new MutableNesControllerInput();
        var controllers = new NesControllerPorts(input);
        controllers.PowerOn();

        controllers.CpuWrite(0x4016, 1);
        Assert.Equal(0, controllers.CpuRead(0x4016) & 1);
        input.SetButtons(0, NesButtons.A);
        Assert.Equal(1, controllers.CpuRead(0x4016) & 1);

        controllers.CpuWrite(0x4016, 0);
        Assert.Equal(1, controllers.CpuRead(0x4016) & 1);
    }

    [Fact]
    public void ControllerPortsExposeIndependentSecondController()
    {
        var input = new MutableNesControllerInput();
        input.SetButtons(1, NesButtons.B | NesButtons.Left);
        var controllers = new NesControllerPorts(input);
        controllers.PowerOn();
        controllers.CpuWrite(0x4016, 1);
        controllers.CpuWrite(0x4016, 0);

        Assert.Equal(0, controllers.CpuRead(0x4017) & 1); // A
        Assert.Equal(1, controllers.CpuRead(0x4017) & 1); // B
        for (var index = 0; index < 4; index++)
        {
            _ = controllers.CpuRead(0x4017);
        }
        Assert.Equal(1, controllers.CpuRead(0x4017) & 1); // Left
    }


    [Fact]
    public void ScriptedControllerInputAppliesEventsAtRequestedCpuCycles()
    {
        var input = new ScriptedNesControllerInput(
        [
            new NesInputEvent(10, NesButtons.Right, NesButtons.None),
            new NesInputEvent(20, NesButtons.A | NesButtons.Start, NesButtons.Left)
        ]);

        Assert.Equal(NesButtons.None, input.ReadButtons(0));
        input.AdvanceTo(9);
        Assert.Equal(NesButtons.None, input.ReadButtons(0));
        input.AdvanceTo(10);
        Assert.Equal(NesButtons.Right, input.ReadButtons(0));
        input.AdvanceTo(20);
        Assert.Equal(NesButtons.A | NesButtons.Start, input.ReadButtons(0));
        Assert.Equal(NesButtons.Left, input.ReadButtons(1));
    }

    [Fact]
    public void PpuCoarseXIncrementWrapsAndSwitchesHorizontalNametable()
    {
        var (ppu, _, _) = CreatePpu();
        ppu.CpuWrite(0x2006, 0x00);
        ppu.CpuWrite(0x2006, 0x1F);
        ppu.CpuWrite(0x2001, 0x08);

        for (var dot = 0; dot <= 8; dot++)
        {
            ppu.Clock();
        }

        Assert.Equal((ushort)0, (ushort)(ppu.VramAddress & 0x001F));
        Assert.Equal((ushort)0x0400, (ushort)(ppu.VramAddress & 0x0400));
    }

    [Fact]
    public void PpuVerticalIncrementWrapsFineAndCoarseYAndSwitchesNametable()
    {
        var (ppu, _, _) = CreatePpu();
        ppu.CpuWrite(0x2006, 0x73);
        ppu.CpuWrite(0x2006, 0xA0);
        ppu.CpuWrite(0x2001, 0x08);

        // PPUADDR can set only the 14-bit external VRAM address, so this starts
        // at fine Y = 3. Five scanline increments advance 3 -> 4 -> 5 -> 6 -> 7
        // and then wrap to 0 while coarse Y 29 switches the vertical nametable.
        const int clocksThroughFifthScanlineDot256 = (4 * 341) + 257;
        for (var clock = 0; clock < clocksThroughFifthScanlineDot256; clock++)
        {
            ppu.Clock();
        }

        Assert.Equal((ushort)0, (ushort)(ppu.VramAddress & 0x7000));
        Assert.Equal((ushort)0, (ushort)(ppu.VramAddress & 0x03E0));
        Assert.Equal((ushort)0x0800, (ushort)(ppu.VramAddress & 0x0800));
    }

    [Fact]
    public void ApuPulseChannelProducesSamplesThroughCpuRegisters()
    {
        var apu = new Rp2A03Apu();
        apu.PowerOn();

        apu.CpuWrite(0x4015, 0x01);
        apu.CpuWrite(0x4000, 0xBF); // 50% duty, halt length, constant volume 15
        apu.CpuWrite(0x4002, 0xFD);
        apu.CpuWrite(0x4003, 0x08);

        for (var cycle = 0; cycle < 100_000; cycle++)
        {
            apu.Clock();
        }

        Assert.True(apu.Samples.Count > 2_000);
        Assert.Contains(apu.Samples, sample => sample > 0.01f);
        Assert.Equal(0x01, apu.Status & 0x01);
    }

    [Fact]
    public void ApuStatusWriteDisablesAndClearsLengthCounters()
    {
        var apu = new Rp2A03Apu();
        apu.PowerOn();
        apu.CpuWrite(0x4015, 0x01);
        apu.CpuWrite(0x4000, 0x1F);
        apu.CpuWrite(0x4002, 0x40);
        apu.CpuWrite(0x4003, 0xF8);

        Assert.Equal(0x01, apu.CpuRead(0x4015) & 0x01);

        apu.CpuWrite(0x4015, 0x00);

        Assert.Equal(0, apu.CpuRead(0x4015) & 0x01);
    }

    [Fact]
    public void MasterClockAdvancesApuOncePerCpuCycle()
    {
        var cpu = new CountingClockedModule();
        var ppu = new CountingClockedModule();
        var apu = new CountingClockedModule();
        var clock = new NesMasterClock(cpu, ppu, apu);

        for (var tick = 0; tick < 30; tick++)
        {
            clock.Tick();
        }

        Assert.Equal(30, ppu.ClockCount);
        Assert.Equal(10, cpu.ClockCount);
        Assert.Equal(10, apu.ClockCount);
    }

    [Fact]
    public void ApuFourStepFrameCounterRaisesAndStatusReadClearsFrameIrq()
    {
        var apu = new Rp2A03Apu();
        apu.PowerOn();

        for (var cycle = 0; cycle < 29_829; cycle++)
        {
            apu.Clock();
        }

        Assert.True(apu.FrameIrqAsserted);
        Assert.NotEqual(0, apu.Status & 0x40);

        var status = apu.CpuRead(0x4015);

        Assert.NotEqual(0, status & 0x40);
        Assert.False(apu.FrameIrqAsserted);
        Assert.Equal(0, apu.Status & 0x40);
    }

    [Fact]
    public void ApuFrameIrqCanBeInhibitedThroughFrameCounterRegister()
    {
        var apu = new Rp2A03Apu();
        apu.PowerOn();
        apu.CpuWrite(0x4017, 0x40);

        for (var cycle = 0; cycle < 60_000; cycle++)
        {
            apu.Clock();
        }

        Assert.False(apu.FrameIrqAsserted);
        Assert.Equal(0, apu.Status & 0x40);
    }

    [Fact]
    public void DmcFetchesPrgRomByteStallsCpuAndRaisesIrq()
    {
        var image = CreateNromImage(16 * 1024);
        image.PrgRom[0] = 0xFF; // $C000 on an NROM-128 board
        var bus = new CpuBus();
        bus.Attach(new NromPrgRom(image));
        var stalledCycles = 0;
        var apu = new Rp2A03Apu();
        apu.AttachDmcMemory(bus, cycles => stalledCycles += cycles);
        apu.PowerOn();
        apu.CpuWrite(0x4010, 0x8F); // IRQ enabled, fastest NTSC rate
        apu.CpuWrite(0x4011, 0x00);
        apu.CpuWrite(0x4012, 0x00); // $C000
        apu.CpuWrite(0x4013, 0x00); // one byte
        apu.CpuWrite(0x4015, 0x10);

        for (var cycle = 0; cycle < 1_000; cycle++)
        {
            apu.Clock();
        }

        Assert.Equal(4, stalledCycles);
        Assert.True(apu.DmcIrqAsserted);
        Assert.NotEqual(0, apu.Status & 0x80);
        Assert.True(apu.DmcOutputLevel > 0);
    }

    [Fact]
    public void DmcLoopRestartsSampleWithoutRaisingIrq()
    {
        var image = CreateNromImage(16 * 1024);
        image.PrgRom[0] = 0xAA;
        var bus = new CpuBus();
        bus.Attach(new NromPrgRom(image));
        var fetchStalls = 0;
        var apu = new Rp2A03Apu();
        apu.AttachDmcMemory(bus, cycles => fetchStalls += cycles);
        apu.PowerOn();
        apu.CpuWrite(0x4010, 0xCF); // IRQ enabled + loop + fastest rate
        apu.CpuWrite(0x4012, 0x00);
        apu.CpuWrite(0x4013, 0x00);
        apu.CpuWrite(0x4015, 0x10);

        for (var cycle = 0; cycle < 2_000; cycle++)
        {
            apu.Clock();
        }

        Assert.True(fetchStalls >= 8);
        Assert.False(apu.DmcIrqAsserted);
        Assert.True(apu.DmcBytesRemaining > 0 || fetchStalls > 4);
    }

    private static (Rp2C02Ppu Ppu, PpuBus Bus, SignalLine Nmi) CreatePpu()
    {
        var image = CreateNromImage(16 * 1024);
        var bus = new PpuBus();
        var chr = new NromChrMemory(image);
        var ciram = new CiramNametableRam(image.Mirroring);
        var palette = new PpuPaletteRam();
        chr.PowerOn();
        ciram.PowerOn();
        palette.PowerOn();
        bus.Attach(chr);
        bus.Attach(ciram);
        bus.Attach(palette);
        var nmi = new SignalLine();
        var ppu = new Rp2C02Ppu(bus, nmi);
        ppu.PowerOn();
        return (ppu, bus, nmi);
    }

    [Fact]
    public void UxRomSwitchesLowerBankAndKeepsLastBankFixed()
    {
        var prg = new byte[4 * 16 * 1024];
        prg[0] = 0x11;
        prg[16 * 1024] = 0x22;
        prg[2 * 16 * 1024] = 0x33;
        prg[3 * 16 * 1024] = 0x44;
        var image = new NesRomImage(NesHeaderFormat.INes, 2, null, prg.Length, 0, false, false, NametableMirroring.Horizontal, NesTimingMode.Unknown, prg, Array.Empty<byte>());
        var device = new UxRomPrgRom(image);
        device.PowerOn();

        Assert.Equal(0x11, device.CpuRead(0x8000));
        Assert.Equal(0x44, device.CpuRead(0xC000));
        device.CpuWrite(0x8000, 0x02);
        Assert.Equal(0x33, device.CpuRead(0x8000));
        Assert.Equal(0x44, device.CpuRead(0xC000));
    }

    [Fact]
    public void CartridgeFactoryBuildsUxRomFromBoardDefinition()
    {
        var prg = new byte[2 * 16 * 1024];
        var image = new NesRomImage(NesHeaderFormat.INes, 2, null, prg.Length, 0, false, false, NametableMirroring.Horizontal, NesTimingMode.Unknown, prg, Array.Empty<byte>());
        const string json = """{"id":"nes.board.uxrom","name":"UxROM","mapper":2,"components":[],"connections":[],"notes":null}""";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var definition = CartridgeBoardDefinition.Load(stream);
        var hardware = CartridgeHardwareFactory.Create(image, definition);

        Assert.IsType<UxRomPrgRom>(hardware.PrgDevice);
        Assert.True(((NromChrMemory)hardware.ChrDevice).IsWritable);
        Assert.Equal("nes.board.uxrom", hardware.BoardId);
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
        if (ram is null)
        {
            ram = new CpuWorkRam();
            ram.PowerOn();
        }

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
        HeaderTiming: NesTimingMode.Unknown,
        PrgRom: new byte[prgSize],
        ChrRom: new byte[8 * 1024]);

    private sealed class CountingClockedModule : IClockedHardwareModule
    {
        public int ClockCount { get; private set; }
        public void Clock() => ClockCount++;
    }


    [Fact]
    public void TimingResolverUsesPalFilenameWhenHeaderIsUnknown()
    {
        var image = new NesRomImage(NesHeaderFormat.INes, 0, null, 16 * 1024, 8 * 1024, false, false,
            NametableMirroring.Horizontal, NesTimingMode.Unknown, new byte[16 * 1024], new byte[8 * 1024]);

        var selection = NesTimingResolver.Resolve(image, "Example (Europe).nes");

        Assert.Equal(NesTimingMode.Pal, selection.Mode);
        Assert.Equal(NesTimingSource.FileName, selection.Source);
    }

    [Fact]
    public void TimingResolverManualOverrideWins()
    {
        var image = new NesRomImage(NesHeaderFormat.Nes20, 0, 0, 16 * 1024, 8 * 1024, false, false,
            NametableMirroring.Horizontal, NesTimingMode.Pal, new byte[16 * 1024], new byte[8 * 1024]);

        var selection = NesTimingResolver.Resolve(image, "Example (Europe).nes", NesTimingMode.Ntsc);

        Assert.Equal(NesTimingMode.Ntsc, selection.Mode);
        Assert.Equal(NesTimingSource.ManualOverride, selection.Source);
    }

    [Fact]
    public void Mmc1SwitchesPrgBankAndKeepsLastBankFixed()
    {
        var prg = new byte[4 * 16 * 1024];
        for (var bank = 0; bank < 4; bank++) prg[bank * 16 * 1024] = (byte)(0x10 + bank);
        var image = new NesRomImage(NesHeaderFormat.INes, 1, null, prg.Length, 0, false, false,
            NametableMirroring.Horizontal, NesTimingMode.Unknown, prg, Array.Empty<byte>());
        var mmc1 = new Mmc1CartridgeMemory(image);

        WriteMmc1Register(mmc1, 0xE000, 2);

        Assert.Equal(0x12, mmc1.CpuRead(0x8000));
        Assert.Equal(0x13, mmc1.CpuRead(0xC000));
    }

    [Fact]
    public void Mmc1ChangesMirroringFromControlRegister()
    {
        var image = new NesRomImage(NesHeaderFormat.INes, 1, null, 32 * 1024, 0, false, false,
            NametableMirroring.Horizontal, NesTimingMode.Unknown, new byte[32 * 1024], Array.Empty<byte>());
        var mmc1 = new Mmc1CartridgeMemory(image);

        WriteMmc1Register(mmc1, 0x8000, 0x0E);

        Assert.Equal(NametableMirroring.Vertical, mmc1.Mirroring);
    }

    [Fact]
    public void Mmc1PersistsBatteryBackedPrgRam()
    {
        var image = new NesRomImage(NesHeaderFormat.INes, 1, null, 32 * 1024, 0, false, true,
            NametableMirroring.Horizontal, NesTimingMode.Unknown, new byte[32 * 1024], Array.Empty<byte>());
        var mmc1 = new Mmc1CartridgeMemory(image);
        mmc1.CpuWrite(0x6000, 0x5A);

        var saved = mmc1.SavePersistent();
        var restored = new Mmc1CartridgeMemory(image);
        restored.LoadPersistent(saved);

        Assert.Equal(0x5A, restored.CpuRead(0x6000));
    }

    private static void WriteMmc1Register(Mmc1CartridgeMemory mmc1, ushort address, byte value)
    {
        for (var bit = 0; bit < 5; bit++) mmc1.CpuWrite(address, (byte)((value >> bit) & 1));
    }
}
