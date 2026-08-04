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
    public void CpuDefersStoreBusWriteUntilTheFinalInstructionCycle()
    {
        var memory = new RecordingCpuMemory();
        memory.Bytes[0xFFFC] = 0x00;
        memory.Bytes[0xFFFD] = 0x80;
        memory.Bytes[0x8000] = 0xA9; // LDA #$42
        memory.Bytes[0x8001] = 0x42;
        memory.Bytes[0x8002] = 0x8D; // STA $0002
        memory.Bytes[0x8003] = 0x02;
        memory.Bytes[0x8004] = 0x00;
        var bus = new CpuBus();
        bus.Attach(memory);
        var cpu = new Rp2A03Cpu(bus);
        cpu.PowerOn();

        for (var cycle = 0; cycle < 9; cycle++) cpu.Clock(); // reset + LDA
        cpu.Clock(); // STA opcode fetch
        cpu.Clock(); // STA cycle 2
        cpu.Clock(); // STA cycle 3

        Assert.Empty(memory.WritesTo(0x0002));

        cpu.Clock(); // STA cycle 4: actual bus write

        Assert.Equal(new byte[] { 0x42 }, memory.WritesTo(0x0002));
    }

    [Fact]
    public void CpuDefersMemoryMappedReadUntilTheFinalInstructionCycle()
    {
        var memory = new RecordingCpuMemory();
        memory.Bytes[0xFFFC] = 0x00;
        memory.Bytes[0xFFFD] = 0x80;
        memory.Bytes[0x8000] = 0xAD; // LDA $2002
        memory.Bytes[0x8001] = 0x02;
        memory.Bytes[0x8002] = 0x20;
        memory.Bytes[0x2002] = 0x80;
        var bus = new CpuBus();
        bus.Attach(memory);
        var cpu = new Rp2A03Cpu(bus);
        cpu.PowerOn();

        for (var cycle = 0; cycle < 7; cycle++) cpu.Clock();
        cpu.Clock(); // opcode fetch
        cpu.Clock(); // cycle 2
        cpu.Clock(); // cycle 3

        Assert.Empty(memory.ReadsFrom(0x2002));

        cpu.Clock(); // cycle 4: memory-mapped read

        Assert.Single(memory.ReadsFrom(0x2002));
        Assert.Equal(0x80, cpu.Accumulator);
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


    [Theory]
    [InlineData(NesTimingMode.Ntsc)]
    [InlineData(NesTimingMode.Pal)]
    [InlineData(NesTimingMode.Dendy)]
    public void OptimizedPpuTickMatchesRawMasterClockStepping(NesTimingMode mode)
    {
        var timing = NesTimingProfile.For(mode);
        var fastCpu = new CountingClockedModule();
        var fastPpu = new CountingClockedModule();
        var fastApu = new CountingClockedModule();
        var referenceCpu = new CountingClockedModule();
        var referencePpu = new CountingClockedModule();
        var referenceApu = new CountingClockedModule();
        var fast = new NesMasterClock(fastCpu, fastPpu, fastApu, timing);
        var reference = new NesMasterClock(referenceCpu, referencePpu, referenceApu, timing);

        for (var ppuTick = 0; ppuTick < 10_000; ppuTick++)
        {
            fast.Tick();
            var targetPpuCycles = reference.PpuCycles + 1;
            while (reference.PpuCycles < targetPpuCycles)
            {
                reference.TickMaster();
            }

            Assert.Equal(reference.MasterCycles, fast.MasterCycles);
            Assert.Equal(reference.PpuCycles, fast.PpuCycles);
            Assert.Equal(reference.CpuCycles, fast.CpuCycles);
            Assert.Equal(reference.PpuMasterPhase, fast.PpuMasterPhase);
            Assert.Equal(reference.CpuMasterPhase, fast.CpuMasterPhase);
        }

        Assert.Equal(referenceCpu.ClockCount, fastCpu.ClockCount);
        Assert.Equal(referencePpu.ClockCount, fastPpu.ClockCount);
        Assert.Equal(referenceApu.ClockCount, fastApu.ClockCount);
    }


    [Fact]
    public void MasterClockCanAdvanceExactlyOnePpuFrame()
    {
        var timing = NesTimingProfile.For(NesTimingMode.Ntsc);
        var cpu = new CountingClockedModule();
        var ppuBus = new PpuBus();
        var ppu = new Rp2C02Ppu(ppuBus, new SignalLine(), timing);
        ppu.PowerOn();
        var clock = new NesMasterClock(cpu, ppu, timing: timing);
        var startingFrame = ppu.Frame;
        var startingPpuCycles = clock.PpuCycles;
        var startingCpuCycles = clock.CpuCycles;

        clock.TickFrame(ppu);

        Assert.Equal(startingFrame + 1, ppu.Frame);
        Assert.True(clock.PpuCycles > startingPpuCycles);
        Assert.True(clock.CpuCycles > startingCpuCycles);
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
    public void PpuQueuesOnlyOneNmiEdgeWhileVBlankRemainsAsserted()
    {
        var (ppu, _, nmi) = CreatePpu();
        var edges = 0;
        nmi.Asserted += () => edges++;
        ppu.CpuWrite(0x2000, 0x80);

        var clocksToVBlank = (241 * 341) + 2;
        for (var i = 0; i < clocksToVBlank; i++) ppu.Clock();

        ppu.CpuWrite(0x2000, 0x80);
        ppu.CpuWrite(0x2000, 0x80);

        Assert.Equal(1, edges);
        Assert.Equal((ulong)1, ppu.NmiEdges);
        Assert.Equal((ulong)1, ppu.VBlankStarts);
    }

    [Fact]
    public void EnablingNmiDuringVBlankCreatesExactlyOneEdge()
    {
        var (ppu, _, nmi) = CreatePpu();
        var edges = 0;
        nmi.Asserted += () => edges++;

        var clocksToVBlank = (241 * 341) + 2;
        for (var i = 0; i < clocksToVBlank; i++) ppu.Clock();
        Assert.True(ppu.InVBlank);
        Assert.Equal(0, edges);

        ppu.CpuWrite(0x2000, 0x80);
        ppu.CpuWrite(0x2000, 0x80);

        Assert.Equal(1, edges);
        Assert.Equal((ulong)1, ppu.NmiEdges);
    }

    [Fact]
    public void StatusReadSuppressesNmiOutputUntilNextVBlank()
    {
        var (ppu, _, nmi) = CreatePpu();
        ppu.CpuWrite(0x2000, 0x80);
        var clocksToVBlank = (241 * 341) + 2;
        for (var i = 0; i < clocksToVBlank; i++) ppu.Clock();

        _ = ppu.CpuRead(0x2002);

        Assert.False(nmi.IsAsserted);
        Assert.Equal((ulong)1, ppu.StatusReads);
        ppu.CpuWrite(0x2000, 0x80);
        Assert.False(nmi.IsAsserted);
    }


    [Fact]
    public void NtscPpuSkipsOneClockOnOddRenderedFrames()
    {
        var (ppu, _, _) = CreatePpu(NesTimingProfile.For(NesTimingMode.Ntsc));
        ppu.CpuWrite(0x2001, 0x18);

        var evenFrameClocks = ClockUntilFrameCompleted(ppu);
        var oddFrameClocks = ClockUntilFrameCompleted(ppu);

        Assert.Equal(262 * 341, evenFrameClocks);
        Assert.Equal((262 * 341) - 1, oddFrameClocks);
    }

    [Fact]
    public void NtscPpuKeepsFullOddFrameWhenRenderingIsDisabled()
    {
        var (ppu, _, _) = CreatePpu(NesTimingProfile.For(NesTimingMode.Ntsc));

        var evenFrameClocks = ClockUntilFrameCompleted(ppu);
        var oddFrameClocks = ClockUntilFrameCompleted(ppu);

        Assert.Equal(262 * 341, evenFrameClocks);
        Assert.Equal(262 * 341, oddFrameClocks);
    }

    [Fact]
    public void PalPpuDoesNotUseTheNtscOddFrameSkip()
    {
        var (ppu, _, _) = CreatePpu(NesTimingProfile.For(NesTimingMode.Pal));
        ppu.CpuWrite(0x2001, 0x18);

        var firstFrameClocks = ClockUntilFrameCompleted(ppu);
        var secondFrameClocks = ClockUntilFrameCompleted(ppu);

        Assert.Equal(312 * 341, firstFrameClocks);
        Assert.Equal(312 * 341, secondFrameClocks);
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

        // The hardware background pipeline is primed during the pre-render
        // scanline. Advance through the initial frame, then render the first tile.
        for (var i = 0; i < (262 * 341) + 9; i++)
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

        for (var i = 0; i < (262 * 341) + 3; i++)
        {
            ppu.Clock();
        }

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

        Assert.True(dma.TransferActive);
        Assert.Equal(0, dma.BytesTransferred);
        Assert.Equal(0x02, dma.LastPage);
        Assert.Equal(0UL, dma.Transfers);

        for (var cycle = 0; cycle < 512; cycle++)
        {
            cpu.Clock();
        }

        Assert.True(dma.TransferActive);
        Assert.Equal(255, dma.BytesTransferred);
        Assert.Equal(0UL, dma.Transfers);

        cpu.Clock();

        Assert.False(dma.TransferActive);
        Assert.Equal(256, dma.BytesTransferred);
        Assert.Equal(0x00, ppu.ReadOamByte(0xFE));
        Assert.Equal(0x01, ppu.ReadOamByte(0xFF));
        Assert.Equal(0x02, ppu.ReadOamByte(0x00));
        Assert.Equal(1UL, dma.Transfers);
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

        for (var cycle = 0; cycle < (262 * 341) + 343; cycle++)
        {
            ppu.Clock();
        }

        Assert.True((ppu.Status & 0x40) != 0);
        Assert.NotEqual(0U, ppu.Framebuffer[0]);
    }

    [Fact]
    public void PpuCanEnableSpritesAfterScanlinePreparationAndStillSetSpriteZeroHit()
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
        // Keep the entire background tile area opaque. The PPU has already advanced
        // its scrolling address by the time scanline 1 is rendered, so initializing
        // only $2000 makes this test depend on an unrelated coarse-X position.
        for (var address = 0x2000; address < 0x3000; address++)
        {
            bus.Write((ushort)address, 1);
        }

        var ppu = new Rp2C02Ppu(bus, new SignalLine());
        ppu.PowerOn();
        ppu.CpuWrite(0x2003, 0x00);
        ppu.CpuWrite(0x2004, 0x00); // visible from scanline 1
        ppu.CpuWrite(0x2004, 0x02);
        ppu.CpuWrite(0x2004, 0x00);
        ppu.CpuWrite(0x2004, 0x10);
        ppu.CpuWrite(0x2001, 0x0A); // background enabled, sprites disabled

        for (var cycle = 0; cycle < (262 * 341) + 343; cycle++)
        {
            ppu.Clock();
        }

        // Scanline 1, dot 1 has now prepared the sprite cache while sprites are off.
        // Enabling them before X=16 must allow the normal pixel-time mask gating and
        // sprite-zero hit detection to proceed.
        for (var cycle = 0; cycle < 10; cycle++)
        {
            ppu.Clock();
        }
        ppu.CpuWrite(0x2001, 0x1E);
        for (var cycle = 0; cycle < 16; cycle++)
        {
            ppu.Clock();
        }

        Assert.True((ppu.Status & 0x40) != 0);
    }

    [Fact]
    public void PpuDiagnosticTraceReportsSpriteZeroOverlapInputs()
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
        // Prime every nametable tile with the opaque background tile. The cycle-accurate
        // background pipeline fetches ahead during pre-render and advances coarse X, so
        // a single tile at $2000 is not sufficient for a deterministic overlap trace.
        for (var address = 0x2000; address < 0x3000; address++)
        {
            bus.Write((ushort)address, 1);
        }

        var ppu = new Rp2C02Ppu(bus, new SignalLine()) { DiagnosticsTraceEnabled = true };
        SpriteZeroEvaluationTraceEvent? evaluation = null;
        ppu.SpriteZeroEvaluated += evt => evaluation = evt;
        ppu.PowerOn();
        ppu.CpuWrite(0x2003, 0x00);
        ppu.CpuWrite(0x2004, 0x00);
        ppu.CpuWrite(0x2004, 0x02);
        ppu.CpuWrite(0x2004, 0x00);
        ppu.CpuWrite(0x2004, 0x00);
        ppu.CpuWrite(0x2001, 0x1E);

        // Run until the second frame has completed sprite-zero evaluation for
        // scanline 1. A fixed frame-plus-two-dot count only reached dot 2 of the
        // second scanline and therefore asserted against the unprimed first frame.
        var safetyCycles = 2 * 262 * 341;
        while ((ppu.Frame < 1 || ppu.Scanline < 1 || ppu.Dot <= 257) && safetyCycles-- > 0)
        {
            ppu.Clock();
        }

        Assert.True(safetyCycles > 0);
        Assert.NotNull(evaluation);
        Assert.True(evaluation!.SelectedForScanline);
        Assert.Equal(1, evaluation.Scanline);
        Assert.Equal(0xFF, evaluation.SpriteOpaqueMask);
        Assert.Equal(0xFF, evaluation.BackgroundOpaqueMask);
        Assert.Equal(0xFF, evaluation.OverlapMask);
        Assert.Equal("hit-possible", evaluation.RejectionReason);
    }

    [Fact]
    public void PpuDiagnosticTraceReportsOamWritesAndSpriteZeroSelection()
    {
        var image = CreateNromImage(16 * 1024);
        for (var row = 0; row < 8; row++)
        {
            image.ChrRom[16 + row] = 0xFF;
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

        var ppu = new Rp2C02Ppu(bus, new SignalLine()) { DiagnosticsTraceEnabled = true };
        var oamWrites = new List<OamWriteTraceEvent>();
        SpriteScanlineSelectionTraceEvent? selection = null;
        ppu.OamWritten += oamWrites.Add;
        ppu.SpriteScanlineSelected += evt => selection = evt;
        ppu.PowerOn();

        ppu.CpuWrite(0x2003, 0x00);
        ppu.CpuWrite(0x2004, 0x00);
        ppu.CpuWrite(0x2004, 0x01);
        ppu.CpuWrite(0x2004, 0x00);
        ppu.CpuWrite(0x2004, 0x10);
        ppu.CpuWrite(0x2001, 0x10);

        for (var cycle = 0; cycle < 343; cycle++)
        {
            ppu.Clock();
        }

        Assert.Equal(5, oamWrites.Count);
        Assert.Equal("address", oamWrites[0].Source);
        Assert.Equal("cpu", oamWrites[1].Source);
        Assert.Equal((byte)0x00, oamWrites[1].Address);
        Assert.Equal((byte)0x00, oamWrites[1].Value);
        Assert.NotNull(selection);
        Assert.True(selection!.SpriteZeroOnScanline);
        Assert.True(selection.SpriteZeroSelected);
        Assert.Equal(0, selection.SpriteZeroSelectionSlot);
        Assert.Equal((byte)0x10, selection.OamX);
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
    public void PpuDoesNotRewindTilesWhenPrefetchWasDisabled()
    {
        var (ppu, _, _) = CreatePpu();
        ppu.CpuWrite(0x2006, 0x20);
        ppu.CpuWrite(0x2006, 0x00);

        // Rendering remains disabled, so dots 328 and 336 perform no prefetch
        // coarse-X increments. The next scanline must therefore start at the
        // exact current address rather than being rewound by two tiles.
        for (var cycle = 0; cycle < 343; cycle++)
        {
            ppu.Clock();
        }

        Assert.Equal((ushort)0x2000, ppu.ActiveScanlineVramAddress);
    }

    [Fact]
    public void PpuAddressWriteAfterPrefetchCancelsPendingRewind()
    {
        var (ppu, _, _) = CreatePpu();
        ppu.CpuWrite(0x2001, 0x08);

        // Reach dot 337 after both next-scanline prefetch increments.
        for (var cycle = 0; cycle < 338; cycle++)
        {
            ppu.Clock();
        }

        ppu.CpuWrite(0x2006, 0x23);
        ppu.CpuWrite(0x2006, 0x45);

        // Finish the scanline and latch the next visible scanline. The explicit
        // $2006 address replaced v, so stale prefetch increments must not be undone.
        for (var cycle = 0; cycle < 5; cycle++)
        {
            ppu.Clock();
        }

        Assert.Equal((ushort)0x2345, ppu.ActiveScanlineVramAddress);
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
    public void ControllerReadsPreserveTheNormalNesOpenBusHighBits()
    {
        var input = new MutableNesControllerInput();
        var controllers = new NesControllerPorts(input);
        controllers.PowerOn();

        controllers.CpuWrite(0x4016, 1);
        controllers.CpuWrite(0x4016, 0);
        Assert.Equal(0x40, controllers.CpuRead(0x4016));

        input.SetButtons(0, NesButtons.A);
        controllers.CpuWrite(0x4016, 1);
        Assert.Equal(0x41, controllers.CpuRead(0x4016));
        controllers.CpuWrite(0x4016, 0);
        Assert.Equal(0x41, controllers.CpuRead(0x4016));
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

    private static int ClockUntilFrameCompleted(Rp2C02Ppu ppu)
    {
        var clocks = 0;
        do
        {
            ppu.Clock();
            clocks++;
        }
        while (!ppu.FrameCompleted);

        return clocks;
    }

    private static (Rp2C02Ppu Ppu, PpuBus Bus, SignalLine Nmi) CreatePpu(NesTimingProfile? timing = null)
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
        var ppu = new Rp2C02Ppu(bus, nmi, timing);
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
    public void Mmc1IgnoresSecondRegisterWriteFromSameCpuCycle()
    {
        var prg = new byte[4 * 16 * 1024];
        for (var bank = 0; bank < 4; bank++) prg[bank * 16 * 1024] = (byte)(0x10 + bank);
        var image = new NesRomImage(NesHeaderFormat.INes, 1, null, prg.Length, 0, false, false,
            NametableMirroring.Horizontal, NesTimingMode.Unknown, prg, Array.Empty<byte>());
        var mmc1 = new Mmc1CartridgeMemory(image);
        var bus = new CpuBus();
        bus.Attach(mmc1);

        // Five accepted low bits select PRG bank 2. Each paired write models the
        // original/final writes produced by an RMW instruction; the second must
        // not advance the MMC1 serial register.
        for (var bit = 0; bit < 5; bit++)
        {
            bus.SetCpuCycle((ulong)(100 + bit));
            var serialBit = (byte)((2 >> bit) & 1);
            bus.Write(0xE000, serialBit);
            bus.Write(0xE000, (byte)(serialBit ^ 1));
        }

        Assert.Equal(0x12, mmc1.CpuRead(0x8000));
        Assert.Equal(0x13, mmc1.CpuRead(0xC000));
    }

    [Fact]
    public void Mmc1TraceReportsSerialWritesAndRegisterCommit()
    {
        var image = new NesRomImage(NesHeaderFormat.INes, 1, null, 4 * 16 * 1024, 0, false, false,
            NametableMirroring.Horizontal, NesTimingMode.Unknown, new byte[4 * 16 * 1024], Array.Empty<byte>());
        var mmc1 = new Mmc1CartridgeMemory(image) { DiagnosticsTraceEnabled = true };
        var events = new List<Mmc1TraceEvent>();
        mmc1.TraceEvent += events.Add;

        for (var bit = 0; bit < 5; bit++)
            mmc1.CpuWrite(0xE000, (byte)((2 >> bit) & 1), (ulong)(200 + bit));

        Assert.Equal(5, events.Count);
        Assert.Equal("serial", events[0].Kind);
        Assert.Equal("commit-prg", events[^1].Kind);
        Assert.Equal((byte)2, events[^1].RegisterValue);
        Assert.Equal((byte)2, events[^1].PrgBank);
        Assert.Equal((ulong)204, events[^1].CpuCycle);
    }

    [Fact]
    public void CpuReadModifyWriteEmitsOriginalThenModifiedBusWrites()
    {
        var bus = new CpuBus();
        var memory = new RecordingCpuMemory();
        bus.Attach(memory);
        memory.Bytes[0xFFFC] = 0x00;
        memory.Bytes[0xFFFD] = 0x80;
        memory.Bytes[0x8000] = 0xEE; // INC $9000
        memory.Bytes[0x8001] = 0x00;
        memory.Bytes[0x8002] = 0x90;
        memory.Bytes[0x9000] = 0x7F;
        var cpu = new Rp2A03Cpu(bus);
        cpu.PowerOn();

        while (!cpu.IsInstructionBoundary) cpu.Clock();
        cpu.Clock();
        while (!cpu.IsInstructionBoundary) cpu.Clock();

        Assert.Equal([(byte)0x7F, (byte)0x80], memory.WritesTo(0x9000));
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


    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(11)]
    [InlineData(66)]
    [InlineData(71)]
    [InlineData(79)]
    public void DiscreteMapperFamiliesCanBeConstructed(int mapper)
    {
        var image = CreateMapperImage(mapper, prgBanks16K: 8, chrBanks8K: 8);
        var definition = new CartridgeBoardDefinition($"mapper-{mapper}", $"Mapper {mapper}", mapper, [], [], null);
        var hardware = CartridgeHardwareFactory.Create(image, definition);
        Assert.NotNull(hardware.PrgDevice);
        Assert.NotNull(hardware.ChrDevice);
    }

    [Fact]
    public void Mmc3SwitchesPrgBanksAndExposesIrqProvider()
    {
        var image = CreateMapperImage(4, prgBanks16K: 8, chrBanks8K: 8);
        var memory = new Mmc3CartridgeMemory(image, hasIrq: true);
        memory.CpuWrite(0x8000, 0x06);
        memory.CpuWrite(0x8001, 0x03);
        Assert.Equal(0x03, memory.CpuRead(0x8000));
        Assert.IsAssignableFrom<ICartridgeIrqProvider>(memory);
    }

    [Fact]
    public void Mmc3ScanlineClockReloadsCountsAndAssertsIrq()
    {
        var image = CreateMapperImage(4, prgBanks16K: 8, chrBanks8K: 8);
        var memory = new Mmc3CartridgeMemory(image, hasIrq: true);
        memory.CpuWrite(0xC000, 2);
        memory.CpuWrite(0xC001, 0);
        memory.CpuWrite(0xE001, 0);

        memory.ClockScanline();
        Assert.False(memory.IrqAsserted);
        memory.ClockScanline();
        Assert.False(memory.IrqAsserted);
        memory.ClockScanline();
        Assert.True(memory.IrqAsserted);

        memory.CpuWrite(0xE000, 0);
        Assert.False(memory.IrqAsserted);
    }

    [Fact]
    public void Mmc3PrgRamHonorsEnableAndWriteProtection()
    {
        var image = CreateMapperImage(4, prgBanks16K: 8, chrBanks8K: 8);
        var memory = new Mmc3CartridgeMemory(image, hasIrq: true);
        memory.CpuWrite(0x6000, 0x11);
        Assert.Equal(0x11, memory.CpuRead(0x6000));

        memory.CpuWrite(0xA001, 0xC0);
        memory.CpuWrite(0x6000, 0x22);
        Assert.Equal(0x11, memory.CpuRead(0x6000));

        memory.CpuWrite(0xA001, 0x00);
        Assert.Equal(0xFF, memory.CpuRead(0x6000));
    }

    [Fact]
    public void Mmc3MapsEveryPrgSlotInBothBankModes()
    {
        var image = CreateMapperImage(4, prgBanks16K: 8, chrBanks8K: 8);
        var memory = new Mmc3CartridgeMemory(image, hasIrq: true);
        memory.CpuWrite(0x8000, 0x06);
        memory.CpuWrite(0x8001, 0x03);
        memory.CpuWrite(0x8000, 0x07);
        memory.CpuWrite(0x8001, 0x05);

        Assert.Equal(new[] { 3, 5, 14, 15 }, memory.GetDiagnostics().PrgBanks);

        memory.CpuWrite(0x8000, 0x46);
        Assert.Equal(new[] { 14, 5, 3, 15 }, memory.GetDiagnostics().PrgBanks);
    }

    [Fact]
    public void Mmc3MapsEveryChrWindowInBothInversionModes()
    {
        var image = CreateMapperImage(4, prgBanks16K: 8, chrBanks8K: 8);
        var memory = new Mmc3CartridgeMemory(image, hasIrq: true);
        var values = new byte[] { 4, 8, 12, 13, 14, 15 };
        for (var register = 0; register < values.Length; register++)
        {
            memory.CpuWrite(0x8000, (byte)register);
            memory.CpuWrite(0x8001, values[register]);
        }

        Assert.Equal(new[] { 4, 5, 8, 9, 12, 13, 14, 15 }, memory.GetDiagnostics().ChrBanks);

        memory.CpuWrite(0x8000, 0x80);
        Assert.Equal(new[] { 12, 13, 14, 15, 4, 5, 8, 9 }, memory.GetDiagnostics().ChrBanks);
    }

    [Fact]
    public void Mmc3DiagnosticsExposeRegisterAndIrqState()
    {
        var image = CreateMapperImage(4, prgBanks16K: 8, chrBanks8K: 8);
        var memory = new Mmc3CartridgeMemory(image, hasIrq: true);
        memory.CpuWrite(0x8000, 0x86);
        memory.CpuWrite(0x8001, 0x07);
        memory.CpuWrite(0xC000, 0x02);
        memory.CpuWrite(0xC001, 0x00);
        memory.CpuWrite(0xE001, 0x00);
        memory.ClockScanline();

        var diagnostics = memory.GetDiagnostics();
        Assert.Equal(0x86, diagnostics.BankSelect);
        Assert.Equal(0x07, diagnostics.Registers[6]);
        Assert.Equal(0x02, diagnostics.IrqLatch);
        Assert.Equal(0x02, diagnostics.IrqCounter);
        Assert.True(diagnostics.IrqEnabled);
        Assert.Equal(1, diagnostics.ScanlineClocks);
        Assert.True(diagnostics.RegisterWrites >= 5);
    }

    [Fact]
    public void IrqLineCombinerKeepsOutputAssertedUntilEverySourceClears()
    {
        var output = false;
        var combiner = new IrqLineCombiner(value => output = value);
        var first = combiner.CreateSource();
        var second = combiner.CreateSource();

        first(true);
        second(true);
        first(false);
        Assert.True(output);
        second(false);
        Assert.False(output);
    }


    [Fact]
    public void CpuBusBroadcastsWritesToOverlappingDevicesButReadsFromFirstMatch()
    {
        var bus = new CpuBus();
        var first = new OverlappingCpuDevice(0x11);
        var second = new OverlappingCpuDevice(0x22);
        bus.Attach(first);
        bus.Attach(second);

        Assert.Equal(0x11, bus.Read(0x4017));

        bus.Write(0x4017, 0xC0);

        Assert.Equal(1, first.WriteCount);
        Assert.Equal(1, second.WriteCount);
        Assert.Equal(0xC0, first.LastWrite);
        Assert.Equal(0xC0, second.LastWrite);
    }

    private sealed class OverlappingCpuDevice(byte readValue) : ICpuBusDevice
    {
        public int WriteCount { get; private set; }
        public byte LastWrite { get; private set; }

        public bool HandlesCpuAddress(ushort address) => address == 0x4017;
        public byte CpuRead(ushort address) => readValue;
        public void CpuWrite(ushort address, byte value)
        {
            WriteCount++;
            LastWrite = value;
        }
    }

    private static NesRomImage CreateMapperImage(int mapper, int prgBanks16K, int chrBanks8K)
    {
        var prg = new byte[prgBanks16K * 16 * 1024];
        for (var bank = 0; bank < prg.Length / 0x2000; bank++)
            Array.Fill(prg, (byte)bank, bank * 0x2000, 0x2000);
        var chr = new byte[chrBanks8K * 8 * 1024];
        for (var bank = 0; bank < chr.Length / 0x2000; bank++)
            Array.Fill(chr, (byte)bank, bank * 0x2000, 0x2000);
        return new NesRomImage(NesHeaderFormat.INes, mapper, 0, prg.Length, chr.Length, false, false,
            NametableMirroring.Horizontal, NesTimingMode.Unknown, prg, chr);
    }
    private sealed class RecordingCpuMemory : ICpuBusDevice
    {
        public byte[] Bytes { get; } = new byte[ushort.MaxValue + 1];
        private readonly List<(ushort Address, byte Value)> _writes = [];
        private readonly List<ushort> _reads = [];

        public bool HandlesCpuAddress(ushort address) => true;
        public byte CpuRead(ushort address)
        {
            _reads.Add(address);
            return Bytes[address];
        }

        public void CpuWrite(ushort address, byte value)
        {
            _writes.Add((address, value));
            Bytes[address] = value;
        }

        public byte[] WritesTo(ushort address) => _writes
            .Where(item => item.Address == address)
            .Select(item => item.Value)
            .ToArray();

        public ushort[] ReadsFrom(ushort address) => _reads
            .Where(item => item == address)
            .ToArray();
    }

    [Fact]
    public void PpuSpriteEvaluatorClearsSecondaryOamOnEvenDots()
    {
        var evaluator = new Rp2C02SpriteEvaluator();
        var primaryOam = new byte[256];
        evaluator.BeginScanline(1, 8);

        for (var dot = 1; dot <= 64; dot++)
        {
            evaluator.Clock(dot, primaryOam);
        }

        Assert.All(evaluator.SecondaryOam.ToArray(), value => Assert.Equal((byte)0xFF, value));
    }

    [Fact]
    public void PpuSpriteEvaluatorSelectsTheFirstEightSpritesAndPreservesSpriteZeroIdentity()
    {
        var evaluator = new Rp2C02SpriteEvaluator();
        var primaryOam = Enumerable.Repeat((byte)0xFF, 256).ToArray();
        for (var sprite = 0; sprite < 9; sprite++)
        {
            var offset = sprite * 4;
            primaryOam[offset] = 0x00;
            primaryOam[offset + 1] = (byte)(0x20 + sprite);
            primaryOam[offset + 2] = 0x00;
            primaryOam[offset + 3] = (byte)(sprite * 8);
        }

        evaluator.BeginScanline(1, 8);
        for (var dot = 1; dot <= 256; dot++)
        {
            evaluator.Clock(dot, primaryOam);
        }

        Assert.Equal(8, evaluator.SelectedSpriteCount);
        Assert.True(evaluator.SpriteZeroSelected);
        Assert.True(evaluator.OverflowDetected);
        Assert.Equal((byte)0, evaluator.GetSelectedSprite(0).PrimaryOamIndex);
        Assert.Equal((byte)7, evaluator.GetSelectedSprite(7).PrimaryOamIndex);
    }

    [Fact]
    public void PpuSpriteEvaluatorUsesTheDiagonalOverflowComparatorPath()
    {
        var evaluator = new Rp2C02SpriteEvaluator();
        var primaryOam = Enumerable.Repeat((byte)0xFF, 256).ToArray();
        for (var sprite = 0; sprite < 8; sprite++)
        {
            primaryOam[sprite * 4] = 0x00;
        }

        // The ninth sprite's Y is out of range, but its tile byte is in range.
        // After secondary OAM fills, the hardware bug advances m diagonally and
        // eventually compares non-Y bytes as if they were Y coordinates.
        primaryOam[8 * 4] = 0xF0;
        primaryOam[8 * 4 + 1] = 0x00;
        primaryOam[9 * 4] = 0x00;

        evaluator.BeginScanline(1, 8);
        for (var dot = 1; dot <= 256; dot++)
        {
            evaluator.Clock(dot, primaryOam);
        }

        Assert.True(evaluator.OverflowDetected);
    }

    [Fact]
    public void ApuCanDrainIntoReusableBufferWithoutAllocatingResultArrays()
    {
        var apu = new Rp2A03Apu(sampleRate: 44_100);
        apu.PowerOn();

        for (var cycle = 0; cycle < 100_000; cycle++)
        {
            apu.Clock();
        }

        var available = apu.Samples.Count;
        Assert.True(available > 0);
        var buffer = new float[available];

        var drained = apu.DrainSamples(buffer);

        Assert.Equal(available, drained);
        Assert.Empty(apu.Samples);
        Assert.Equal(0, apu.DrainSamples(buffer));
    }

}

public sealed class InspectableBusComponentTests
{
    [Fact]
    public void CpuBusPublishesLiveReadAndWriteTransactionsWithoutChangingRouting()
    {
        var bus = new CpuBus();
        var ram = new CpuWorkRam();
        bus.Attach(ram);
        bus.SetCpuCycle(123);

        bus.Write(0x0007, 0x5A);
        var write = bus.LastTransaction;

        Assert.Equal("nes.bus.cpu", bus.ModuleId);
        Assert.Equal(16, bus.AddressWidthBits);
        Assert.Equal(BusAccessDirection.Write, write.Direction);
        Assert.Equal((ushort)0x0007, write.Address);
        Assert.Equal((byte)0x5A, write.Data);
        Assert.Same(ram, write.PrimaryDevice);
        Assert.Equal(1, write.ParticipantCount);
        Assert.Equal((ulong)123, write.ClockCycle);

        Assert.Equal((byte)0x5A, bus.Read(0x0807));
        var read = bus.LastTransaction;
        Assert.Equal(BusAccessDirection.Read, read.Direction);
        Assert.Equal((ushort)0x0807, read.Address);
        Assert.Same(ram, read.PrimaryDevice);
        Assert.True(read.Sequence > write.Sequence);
    }

    [Fact]
    public void PpuBusPublishesNormalizedLiveTransactions()
    {
        var bus = new PpuBus();
        var palette = new PpuPaletteRam();
        bus.Attach(palette);

        bus.Write(0x7F10, 0x2C);
        var write = bus.LastTransaction;

        Assert.Equal("nes.bus.ppu", bus.ModuleId);
        Assert.Equal(14, bus.AddressWidthBits);
        Assert.Equal((ushort)0x3F10, write.Address);
        Assert.Equal(BusAccessDirection.Write, write.Direction);
        Assert.Same(palette, write.PrimaryDevice);
        Assert.Equal((byte)0x2C, bus.Read(0x3F00));
        Assert.Equal(BusAccessDirection.Read, bus.LastTransaction.Direction);
    }
}
