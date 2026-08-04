using AxetosOS.Products.NES.Abstractions;
using AxetosOS.Products.NES.Cartridges;
using AxetosOS.Products.NES.Hardware;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class MotherboardCompositionTests
{
    [Fact]
    public void MotherboardPublishesInspectableComponentsAndConnections()
    {
        var image = new NesRomImage(
            NesHeaderFormat.INes,
            MapperNumber: 0,
            SubmapperNumber: null,
            PrgRomSizeBytes: 16 * 1024,
            ChrRomSizeBytes: 8 * 1024,
            HasTrainer: false,
            HasBatteryBackedMemory: false,
            Mirroring: NametableMirroring.Horizontal,
            HeaderTiming: NesTimingMode.Ntsc,
            PrgRom: new byte[16 * 1024],
            ChrRom: new byte[8 * 1024]);
        var cartridge = new CartridgeHardware(new NromPrgRom(image), new NromChrMemory(image), "nrom");
        var board = new NesMotherboard(cartridge, new NullControllerInput(), image.Mirroring);

        Assert.Contains(board.HardwareComponents, component => component.Id == board.Rp2A03.ModuleId);
        Assert.Contains(board.HardwareComponents, component => component.Id == board.Cpu.ModuleId);
        Assert.Contains(board.HardwareComponents, component => component.Id == board.Rp2C02.ModuleId);
        Assert.Contains(board.HardwareComponents, component => component.Id == board.Ppu.ModuleId);
        Assert.Contains(board.HardwareComponents, component => component.Id == board.Ppu.SpriteEvaluator.ModuleId);
        Assert.Contains(board.HardwareComponents, component => component.Id == board.PrimaryOam.ModuleId);
        Assert.Contains(board.HardwareComponents, component => component.Id == board.SecondaryOam.ModuleId);
        Assert.Contains(board.HardwareComponents, component => component.Id == "nes.bus.cpu");
        Assert.Contains(board.HardwareComponents, component => component.Id == "nes.bus.ppu");
        Assert.Contains(board.HardwareConnections, connection =>
            connection.SourceId == board.Rp2A03.ModuleId &&
            connection.TargetId == board.Cpu.ModuleId &&
            connection.Kind == HardwareConnectionKind.Internal);
        Assert.Contains(board.HardwareConnections, connection =>
            connection.SourceId == board.Ppu.ModuleId &&
            connection.TargetId == board.SignalNetwork.Nmi.ModuleId &&
            connection.Name == "NMI");
        Assert.Contains(board.HardwareConnections, connection =>
            connection.TargetId == "nes.cartridge.nrom" &&
            connection.Kind == HardwareConnectionKind.CartridgeConnector);
    }

    [Fact]
    public void Rp2A03PackagePublishesOwnedFunctionalBlocks()
    {
        var bus = new CpuBus();
        var signals = new Rp2A03SignalLines();
        var cpu = new Rp2A03Cpu(bus, signals);
        var apu = new Rp2A03Apu();
        var controllers = new NesControllerPorts(new NullControllerInput());
        var ppuBus = new PpuBus();
        ppuBus.Attach(new CiramNametableRam(NametableMirroring.Horizontal));
        ppuBus.Attach(new PpuPaletteRam());
        var ppu = new Rp2C02Ppu(ppuBus, signals.Nmi, NesTimingProfile.For(NesTimingMode.Ntsc));
        var dma = new OamDmaController(bus, ppu, cpu);
        dma.AttachDmc(apu);

        var package = new Rp2A03Package(cpu, apu, controllers, dma, signals);

        Assert.Same(cpu, package.Cpu);
        Assert.Same(apu, package.Apu);
        Assert.Contains(package.HardwareComponents, component => component.Id == cpu.ModuleId);
        Assert.Contains(package.HardwareComponents, component => component.Id == apu.ModuleId);
        Assert.Contains(package.HardwareConnections, connection =>
            connection.SourceId == package.ModuleId &&
            connection.TargetId == cpu.ModuleId &&
            connection.Kind == HardwareConnectionKind.Internal);
    }


    [Fact]
    public void Rp2A03PackagePublishesLiveDmaChannelsAndArbiter()
    {
        var bus = new CpuBus();
        var signals = new Rp2A03SignalLines();
        var cpu = new Rp2A03Cpu(bus, signals);
        var apu = new Rp2A03Apu();
        var controllers = new NesControllerPorts(new NullControllerInput());
        var ppuBus = new PpuBus();
        ppuBus.Attach(new CiramNametableRam(NametableMirroring.Horizontal));
        ppuBus.Attach(new PpuPaletteRam());
        var ppu = new Rp2C02Ppu(ppuBus, signals.Nmi, NesTimingProfile.For(NesTimingMode.Ntsc));
        var dma = new OamDmaController(bus, ppu, cpu);
        dma.AttachDmc(apu);
        var package = new Rp2A03Package(cpu, apu, controllers, dma, signals);

        Assert.Same(dma.OamChannel, package.DmaController.OamChannel);
        Assert.Same(dma.DmcChannel, package.DmaController.DmcChannel);
        Assert.Same(dma.BusArbiter, package.DmaController.BusArbiter);
        Assert.Contains(package.HardwareComponents, component => component.Id == dma.OamChannel.ModuleId);
        Assert.Contains(package.HardwareComponents, component => component.Id == dma.DmcChannel.ModuleId);
        Assert.Contains(package.HardwareConnections, connection =>
            connection.SourceId == dma.OamChannel.ModuleId &&
            connection.TargetId == dma.BusArbiter.ModuleId &&
            connection.Kind == HardwareConnectionKind.Dma);

        dma.CpuWrite(0x4014, 0x02);
        Assert.True(dma.OamChannel.Active);
        Assert.Equal((byte)0x02, dma.OamChannel.SourcePage);
        Assert.True(dma.BusArbiter.OwnsCpuBus);
        Assert.Equal("OAM", dma.BusArbiter.ActiveOwner);
    }


    [Fact]
    public void Rp2A03PackagePublishesLiveApuChannelsAndFrameSequencer()
    {
        var bus = new CpuBus();
        var signals = new Rp2A03SignalLines();
        var cpu = new Rp2A03Cpu(bus, signals);
        var apu = new Rp2A03Apu();
        var controllers = new NesControllerPorts(new NullControllerInput());
        var ppuBus = new PpuBus();
        ppuBus.Attach(new CiramNametableRam(NametableMirroring.Horizontal));
        ppuBus.Attach(new PpuPaletteRam());
        var ppu = new Rp2C02Ppu(ppuBus, signals.Nmi, NesTimingProfile.For(NesTimingMode.Ntsc));
        var dma = new OamDmaController(bus, ppu, cpu);
        dma.AttachDmc(apu);
        var package = new Rp2A03Package(cpu, apu, controllers, dma, signals);

        apu.PowerOn();
        apu.CpuWrite(0x4015, 0x01);
        apu.CpuWrite(0x4000, 0xDF);
        apu.CpuWrite(0x4002, 0x34);
        apu.CpuWrite(0x4003, 0x08);
        apu.Clock();

        Assert.Contains(package.HardwareComponents, component => component.Id == apu.FrameSequencer.ModuleId);
        Assert.Contains(package.HardwareComponents, component => component.Id == apu.Pulse1.ModuleId);
        Assert.Contains(package.HardwareComponents, component => component.Id == apu.Dmc.ModuleId);
        Assert.Contains(package.HardwareConnections, connection =>
            connection.SourceId == apu.FrameSequencer.ModuleId &&
            connection.TargetId == apu.Pulse1.ModuleId &&
            connection.Kind == HardwareConnectionKind.Clock);
        Assert.Equal(1, apu.FrameSequencer.Cycle);
        Assert.True(apu.Pulse1.State.Enabled);
        Assert.Equal((ushort)0x34, apu.Pulse1.State.TimerPeriod);
    }


    [Fact]
    public void Rp2A03PackagePublishesLiveCpuInternalComponents()
    {
        var bus = new CpuBus();
        var signals = new Rp2A03SignalLines();
        bus.Attach(new CpuWorkRam());
        var cpu = new Rp2A03Cpu(bus, signals);
        var apu = new Rp2A03Apu();
        var controllers = new NesControllerPorts(new NullControllerInput());
        var ppuBus = new PpuBus();
        ppuBus.Attach(new CiramNametableRam(NametableMirroring.Horizontal));
        ppuBus.Attach(new PpuPaletteRam());
        var ppu = new Rp2C02Ppu(ppuBus, signals.Nmi, NesTimingProfile.For(NesTimingMode.Ntsc));
        var dma = new OamDmaController(bus, ppu, cpu);
        dma.AttachDmc(apu);
        var package = new Rp2A03Package(cpu, apu, controllers, dma, signals);

        cpu.PowerOn();

        Assert.Equal(cpu.Accumulator, package.RegisterFile.Accumulator);
        Assert.Equal(cpu.ProgramCounter, package.RegisterFile.ProgramCounter);
        Assert.Equal(cpu.LastOpcode, package.ExecutionUnit.LastOpcode);
        Assert.Equal(cpu.CyclesRemaining, package.ExecutionUnit.CyclesRemaining);
        Assert.Equal(cpu.NmiPending, package.InterruptController.NmiPending);
        Assert.Same(signals, package.Signals);
        Assert.Contains(package.HardwareComponents, component => component.Id == package.RegisterFile.ModuleId);
        Assert.Contains(package.HardwareComponents, component => component.Id == package.ExecutionUnit.ModuleId);
        Assert.Contains(package.HardwareComponents, component => component.Id == package.InterruptController.ModuleId);
        Assert.Contains(package.HardwareConnections, connection =>
            connection.SourceId == package.RegisterFile.ModuleId &&
            connection.TargetId == package.ExecutionUnit.ModuleId &&
            connection.Kind == HardwareConnectionKind.Internal);
    }

    [Fact]
    public void Rp2C02PackagePublishesLiveInternalComponents()
    {
        var signals = new Rp2A03SignalLines();
        var ppuBus = new PpuBus();
        var ciram = new CiramNametableRam(NametableMirroring.Horizontal);
        var palette = new PpuPaletteRam();
        ppuBus.Attach(ciram);
        ppuBus.Attach(palette);
        var ppu = new Rp2C02Ppu(ppuBus, signals.Nmi, NesTimingProfile.For(NesTimingMode.Ntsc));

        var package = new Rp2C02Package(ppu, ppu.SpriteEvaluator, palette, signals.Nmi);

        Assert.Same(ppu, package.Ppu);
        Assert.Same(ppu.SpriteEvaluator, package.SpriteEvaluator);
        Assert.Same(palette, package.PaletteRam);
        Assert.Equal(256, package.PrimaryOam.CapacityBytes);
        Assert.Equal(32, package.SecondaryOam.CapacityBytes);
        Assert.Contains(package.HardwareComponents, component => component.Id == ppu.SpriteEvaluator.ModuleId);
        Assert.Contains(package.HardwareComponents, component => component.Id == package.PrimaryOam.ModuleId);
        Assert.Contains(package.HardwareComponents, component => component.Id == package.SecondaryOam.ModuleId);
        Assert.Contains(package.HardwareConnections, connection =>
            connection.SourceId == package.PrimaryOam.ModuleId &&
            connection.TargetId == ppu.SpriteEvaluator.ModuleId &&
            connection.Kind == HardwareConnectionKind.Internal);
    }

    [Fact]
    public void Rp2C02PackagePublishesLiveRenderingAndNmiBlocks()
    {
        var signals = new Rp2A03SignalLines();
        var ppuBus = new PpuBus();
        var palette = new PpuPaletteRam();
        ppuBus.Attach(new CiramNametableRam(NametableMirroring.Horizontal));
        ppuBus.Attach(palette);
        var ppu = new Rp2C02Ppu(ppuBus, signals.Nmi, NesTimingProfile.For(NesTimingMode.Ntsc));
        var package = new Rp2C02Package(ppu, ppu.SpriteEvaluator, palette, signals.Nmi);

        ppu.PowerOn();
        ppu.CpuWrite(0x2000, 0x80);
        ppu.CpuWrite(0x2001, 0x18);
        ppu.CpuWrite(0x2005, 0x05);

        Assert.Equal(ppu.VramAddress, package.VramAddressUnit.CurrentAddress);
        Assert.Equal((byte)5, package.VramAddressUnit.FineXScroll);
        Assert.True(package.BackgroundPipeline.Enabled);
        Assert.True(package.PixelCompositor.SpritesEnabled);
        Assert.True(package.NmiController.NmiEnabled);
        Assert.Contains(package.HardwareComponents, component => component.Id == package.BackgroundPipeline.ModuleId);
        Assert.Contains(package.HardwareComponents, component => component.Id == package.PixelCompositor.ModuleId);
        Assert.Contains(package.HardwareConnections, connection =>
            connection.SourceId == package.BackgroundPipeline.ModuleId &&
            connection.TargetId == package.PixelCompositor.ModuleId &&
            connection.Kind == HardwareConnectionKind.Internal);
    }

    [Fact]
    public void InspectableMemoryComponentsExposeTheLiveStorage()
    {
        var signals = new Rp2A03SignalLines();
        var ppuBus = new PpuBus();
        var ciram = new CiramNametableRam(NametableMirroring.Horizontal);
        var palette = new PpuPaletteRam();
        ppuBus.Attach(ciram);
        ppuBus.Attach(palette);
        var ppu = new Rp2C02Ppu(ppuBus, signals.Nmi, NesTimingProfile.For(NesTimingMode.Ntsc));
        var package = new Rp2C02Package(ppu, ppu.SpriteEvaluator, palette, signals.Nmi);

        ppu.CpuWrite(0x2003, 0x10);
        ppu.CpuWrite(0x2004, 0xAB);
        palette.PpuWrite(0x3F05, 0x7F);

        Assert.Equal(0xAB, package.PrimaryOam.ReadPhysicalByte(0x10));
        Assert.Equal(0x3F, palette.ReadPhysicalByte(0x05));
        Assert.Equal(2 * 1024, ciram.CapacityBytes);
    }


    [Fact]
    public void ControllerPackagePublishesLivePortsAndStrobeLine()
    {
        var input = new MutableNesControllerInput();
        input.SetButtons(0, NesButtons.A | NesButtons.Start);
        input.SetButtons(1, NesButtons.B);
        var controllers = new NesControllerPorts(input);

        controllers.PowerOn();
        controllers.CpuWrite(0x4016, 0x01);

        Assert.True(controllers.StrobeLine.IsAsserted);
        Assert.Equal(NesButtons.A | NesButtons.Start, controllers.Port1.LatchedButtons);
        Assert.Equal(NesButtons.B, controllers.Port2.LatchedButtons);
        Assert.Contains(controllers.HardwareComponents, component => component.Id == controllers.Port1.ModuleId);
        Assert.Contains(controllers.HardwareComponents, component => component.Id == controllers.Port2.ModuleId);
        Assert.Contains(controllers.HardwareConnections, connection =>
            connection.SourceId == controllers.StrobeLine.ModuleId &&
            connection.TargetId == controllers.Port1.ModuleId &&
            connection.Kind == HardwareConnectionKind.Signal);

        Assert.Equal(0x41, controllers.CpuRead(0x4016));
        Assert.Equal(1UL, controllers.Port1.SerialReadCount);

        controllers.CpuWrite(0x4016, 0x00);
        Assert.False(controllers.StrobeLine.IsAsserted);
        Assert.Equal((byte)(NesButtons.A | NesButtons.Start), controllers.Port1.ShiftRegister);
    }

    private sealed class NullControllerInput : INesControllerInput
    {
        public NesButtons ReadButtons(int port) => NesButtons.None;
    }
    [Fact]
    public void MotherboardPublishesLiveCartridgeBoardComponents()
    {
        var image = new NesRomImage(
            NesHeaderFormat.INes,
            MapperNumber: 0,
            SubmapperNumber: null,
            PrgRomSizeBytes: 16 * 1024,
            ChrRomSizeBytes: 8 * 1024,
            HasTrainer: false,
            HasBatteryBackedMemory: false,
            Mirroring: NametableMirroring.Vertical,
            HeaderTiming: NesTimingMode.Ntsc,
            PrgRom: Enumerable.Repeat((byte)0xA5, 16 * 1024).ToArray(),
            ChrRom: Enumerable.Repeat((byte)0x3C, 8 * 1024).ToArray());
        var cartridge = CartridgeHardwareFactory.Create(image, new CartridgeBoardDefinition("nrom", "NROM", 0, [], [], null));
        var board = new NesMotherboard(cartridge, new NullControllerInput(), image.Mirroring);

        Assert.Equal("nes.cartridge.nrom", board.CartridgeBoard.ModuleId);
        Assert.Same(cartridge.PrgDevice, board.CartridgeBoard.Prg.LiveDevice);
        Assert.Same(cartridge.ChrDevice, board.CartridgeBoard.Chr.LiveDevice);
        Assert.Equal((byte)0xA5, board.CartridgeBoard.Prg.ReadMappedByte(0x8000));
        Assert.Equal((byte)0x3C, board.CartridgeBoard.Chr.ReadMappedByte(0x0000));
        Assert.Equal(NametableMirroring.Vertical, board.CartridgeBoard.Mirroring.Current);
        Assert.False(board.CartridgeBoard.IrqOutput.IsConnected);
        Assert.Contains(board.HardwareComponents, component => component.Id == board.CartridgeBoard.Mapper.ModuleId);
        Assert.Contains(board.HardwareConnections, connection =>
            connection.SourceId == board.CartridgeConnector.Cpu.ModuleId &&
            connection.TargetId == board.CartridgeBoard.Prg.ModuleId &&
            connection.Kind == HardwareConnectionKind.CartridgeConnector);
    }

    [Fact]
    public void MotherboardPublishesLiveClockAndSignalNetworks()
    {
        var image = new NesRomImage(
            NesHeaderFormat.INes, 0, null, 16 * 1024, 8 * 1024, false, false,
            NametableMirroring.Horizontal, NesTimingMode.Ntsc,
            new byte[16 * 1024], new byte[8 * 1024]);
        var cartridge = new CartridgeHardware(new NromPrgRom(image), new NromChrMemory(image), "nrom");
        var board = new NesMotherboard(cartridge, new NullControllerInput(), image.Mirroring);

        Assert.Equal(21_477_272, board.ClockNetwork.Oscillator.FrequencyHz);
        Assert.Equal(4, board.ClockNetwork.PpuDivider.Divisor);
        Assert.Equal(12, board.ClockNetwork.CpuDivider.Divisor);
        Assert.Same(board.CpuSignals.Nmi, board.SignalNetwork.Nmi.Line);
        Assert.True(board.SignalNetwork.Nmi.ActiveLow);
        Assert.True(board.SignalNetwork.Rdy.IsAsserted);
        Assert.Contains(board.HardwareComponents, component => component.Id == board.ClockNetwork.Oscillator.ModuleId);
        Assert.Contains(board.HardwareComponents, component => component.Id == board.SignalNetwork.Irq.ModuleId);
        Assert.Contains(board.HardwareConnections, connection =>
            connection.SourceId == board.ClockNetwork.PpuDivider.ModuleId &&
            connection.TargetId == board.Rp2C02.ModuleId &&
            connection.Kind == HardwareConnectionKind.Clock);

        board.Clock.TickMaster();
        Assert.Equal((ulong)1, board.ClockNetwork.Oscillator.EdgeCount);
    }


    [Fact]
    public void MotherboardPublishesLiveAddressDecodersAndCartridgeConnector()
    {
        var image = new NesRomImage(
            NesHeaderFormat.INes, 0, null, 16 * 1024, 8 * 1024, false, false,
            NametableMirroring.Horizontal, NesTimingMode.Ntsc,
            Enumerable.Repeat((byte)0xA5, 16 * 1024).ToArray(),
            Enumerable.Repeat((byte)0x3C, 8 * 1024).ToArray());
        var cartridge = new CartridgeHardware(new NromPrgRom(image), new NromChrMemory(image), "nrom");
        var board = new NesMotherboard(cartridge, new NullControllerInput(), image.Mirroring);

        Assert.Same(board.CartridgeBoard, board.CartridgeConnector.Cpu.InsertedBoard);
        Assert.Same(board.CartridgeBoard, board.CartridgeConnector.Ppu.InsertedBoard);

        Assert.Equal(0xA5, board.CpuBus.Read(0x8000));
        Assert.Equal(NesCpuAddressRegion.Cartridge, board.CpuAddressDecoder.Region);
        Assert.True(board.CartridgeConnector.Cpu.CartridgeSelected);

        Assert.Equal(0x3C, board.PpuBus.Read(0x0000));
        Assert.Equal(NesPpuAddressRegion.PatternTable, board.PpuAddressDecoder.Region);
        Assert.True(board.CartridgeConnector.Ppu.CartridgeSelected);

        Assert.Contains(board.HardwareConnections, connection =>
            connection.SourceId == board.CpuAddressDecoder.ModuleId &&
            connection.TargetId == board.CartridgeConnector.Cpu.ModuleId &&
            connection.Kind == HardwareConnectionKind.CartridgeConnector);
    }


}

public sealed class ConsoleIoComponentTests
{
    [Fact]
    public void MotherboardPublishesLiveConsoleIoConnectorsAndControls()
    {
        var image = new NesRomImage(
            NesHeaderFormat.INes,
            MapperNumber: 0,
            SubmapperNumber: null,
            PrgRomSizeBytes: 16 * 1024,
            ChrRomSizeBytes: 8 * 1024,
            HasTrainer: false,
            HasBatteryBackedMemory: false,
            Mirroring: NametableMirroring.Horizontal,
            HeaderTiming: NesTimingMode.Ntsc,
            PrgRom: new byte[16 * 1024],
            ChrRom: new byte[8 * 1024]);
        var cartridge = new CartridgeHardware(new NromPrgRom(image), new NromChrMemory(image), "nrom");
        var motherboard = new NesMotherboard(cartridge, new MutableNesControllerInput(), image.Mirroring);

        Assert.Equal(motherboard.Ppu.Framebuffer.Length, motherboard.ConsoleIo.Video.Framebuffer.Length);
        Assert.Equal(motherboard.Apu.SampleRate, motherboard.ConsoleIo.Audio.SampleRate);
        Assert.Same(motherboard.Controllers.Port1, motherboard.ConsoleIo.Controller1.Port);
        Assert.Contains(motherboard.HardwareComponents, component => component.Id == motherboard.ConsoleIo.Video.ModuleId);
        Assert.Contains(motherboard.HardwareConnections, connection =>
            connection.SourceId == motherboard.Ppu.ModuleId &&
            connection.TargetId == motherboard.ConsoleIo.Video.ModuleId);

        motherboard.PowerOn();
        Assert.True(motherboard.ConsoleIo.PowerSwitch.IsPowered);
    }
}

public sealed class HardwareInspectionRegistryTests
{
    [Fact]
    public void MotherboardInspectionIndexesLiveComponentsAndCapturesMachineState()
    {
        var image = new NesRomImage(
            NesHeaderFormat.INes, 0, null, 16 * 1024, 8 * 1024, false, false,
            NametableMirroring.Horizontal, NesTimingMode.Ntsc,
            new byte[16 * 1024], new byte[8 * 1024]);
        var cartridge = new CartridgeHardware(new NromPrgRom(image), new NromChrMemory(image), "nrom");
        var motherboard = new NesMotherboard(cartridge, new MutableNesControllerInput(), image.Mirroring);

        Assert.Equal(motherboard.HardwareComponents.Count, motherboard.Inspection.ComponentCount);
        Assert.Equal(motherboard.HardwareConnections.Count, motherboard.Inspection.ConnectionCount);
        Assert.True(motherboard.Inspection.TryGetComponent(motherboard.Cpu.ModuleId, out var cpu));
        Assert.Same(motherboard.Cpu, cpu.Instance);
        Assert.Same(motherboard.PpuBus, motherboard.Inspection.GetRequiredComponent(motherboard.PpuBus.ModuleId).Instance);

        motherboard.PowerOn();
        motherboard.Clock.TickMaster();
        motherboard.CpuBus.Read(0x0000);
        motherboard.PpuBus.Read(0x3F00);

        var snapshot = motherboard.Inspection.CaptureSnapshot();
        Assert.True(snapshot.IsPowered);
        Assert.Equal((ulong)1, snapshot.MasterCycles);
        Assert.Equal(motherboard.Rp2A03.RegisterFile.ProgramCounter, snapshot.ProgramCounter);
        Assert.Equal(motherboard.Ppu.Scanline, snapshot.PpuScanline);
        Assert.Equal((ushort)0x0000, snapshot.CpuBus.Address);
        Assert.Equal((ushort)0x3F00, snapshot.PpuBus.Address);
        Assert.True(snapshot.RdyAsserted);
    }
}

