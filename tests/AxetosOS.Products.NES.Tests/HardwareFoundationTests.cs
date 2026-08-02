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
        Assert.NotEqual(0U, ppu.Framebuffer[0]);
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
        ppu.CpuWrite(0x2004, 0xFF); // Y wraps to first visible scanline
        ppu.CpuWrite(0x2004, 0x02);
        ppu.CpuWrite(0x2004, 0x00);
        ppu.CpuWrite(0x2004, 0x00);
        ppu.CpuWrite(0x2001, 0x1E);

        ppu.Clock();
        ppu.Clock();

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
            ppu.CpuWrite(0x2004, 0xFF);
            ppu.CpuWrite(0x2004, 0x02);
            ppu.CpuWrite(0x2004, 0x00);
            ppu.CpuWrite(0x2004, (byte)(sprite * 8));
        }

        ppu.CpuWrite(0x2001, 0x14);
        ppu.Clock();
        ppu.Clock();

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
        backgroundOnly.Clock();
        backgroundOnly.Clock();
        var expectedBackground = backgroundOnly.Framebuffer[0];

        var ppu = new Rp2C02Ppu(bus, new SignalLine());
        ppu.PowerOn();
        ppu.CpuWrite(0x2003, 0x00);
        ppu.CpuWrite(0x2004, 0xFF);
        ppu.CpuWrite(0x2004, 0x02);
        ppu.CpuWrite(0x2004, 0x20); // behind background
        ppu.CpuWrite(0x2004, 0x00);
        ppu.CpuWrite(0x2001, 0x1E);
        ppu.Clock();
        ppu.Clock();

        Assert.Equal(expectedBackground, ppu.Framebuffer[0]);
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
        PrgRom: new byte[prgSize],
        ChrRom: new byte[8 * 1024]);

    private sealed class CountingClockedModule : IClockedHardwareModule
    {
        public int ClockCount { get; private set; }
        public void Clock() => ClockCount++;
    }
}
