using AxetosOS.Products.NES.VirtualHardware.Components.Clock;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Components.Logic;
using AxetosOS.Products.NES.VirtualHardware.Components.Memory;
using AxetosOS.Products.NES.VirtualHardware.Components.Passives;
using AxetosOS.Products.NES.VirtualHardware.Components.Nes;
using AxetosOS.Products.NES.VirtualHardware.Components.Power;
using AxetosOS.Products.NES.VirtualHardware.Components.Processors.Mos6502;
using AxetosOS.Products.NES.VirtualHardware.Components.Reset;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Boards.Nes;

/// <summary>
/// Pin-wired NES CPU-side motherboard slice. The board owns all wiring while
/// the CPU, RAM, ROM, decoders and control sources know only their pins.
/// </summary>
public sealed class NesCpuMotherboard
{
    public const int WorkRamSize = 2 * 1024;
    public const int PrgRomSize = 32 * 1024;

    private int _ppuHalfCycleAccumulator;

    public NesCpuMotherboard(
        ReadOnlySpan<byte> prgRom,
        NesHardwareRegion region = NesHardwareRegion.NtscNorthAmerica)
        : this(prgRom, ReadOnlySpan<byte>.Empty, NesNametableMirroring.Horizontal, region)
    {
    }

    public NesCpuMotherboard(
        ReadOnlySpan<byte> prgRom,
        ReadOnlySpan<byte> chrRom,
        NesNametableMirroring mirroring,
        NesHardwareRegion region = NesHardwareRegion.NtscNorthAmerica)
    {
        TimingProfile = NesHardwareTimingProfile.For(region);
        if (prgRom.Length > PrgRomSize)
        {
            throw new ArgumentException("PRG ROM exceeds the 32 KiB CPU window.", nameof(prgRom));
        }

        var mappedPrg = new byte[PrgRomSize];
        if (prgRom.Length == 16 * 1024)
        {
            // NROM-128 boards physically mirror the single 16 KiB PRG chip.
            prgRom.CopyTo(mappedPrg);
            prgRom.CopyTo(mappedPrg.AsSpan(16 * 1024));
        }
        else
        {
            prgRom.CopyTo(mappedPrg);
        }

        Board = new VirtualHardwareBoard("nes.cpu-motherboard");
        Vcc = Board.Add(new DigitalPowerRail("nes.vcc", DigitalLevel.High));
        Ground = Board.Add(new DigitalPowerRail("nes.ground", DigitalLevel.Low));
        Clock = Board.Add(new DigitalOscillator("nes.cpu-clock", TimingProfile.CpuClockHertz));
        PpuClock = Board.Add(new DigitalOscillator("nes.ppu-clock", TimingProfile.PpuClockHertz));
        ResetCircuit = Board.Add(new PowerOnResetCircuit("nes.reset"));
        Cpu = Board.Add(new Mos6502Processor("nes.cpu"));
        WorkRam = Board.Add(new StaticRamChip("nes.work-ram", 11));
        PrgRom = Board.Add(new ProgramRomChip("nes.prg-rom", 15, mappedPrg));
        RamDecoder = Board.Add(new BinaryAddressDecoder("nes.ram-decoder", 3));
        PrgDecoder = Board.Add(new BinaryAddressDecoder("nes.prg-decoder", 1));
        ReadInverter = Board.Add(new NotGate("nes.read-inverter"));
        IrqHigh = Board.Add(new DigitalPowerRail("nes.irq-pullup", DigitalLevel.High));
        NmiHigh = Board.Add(new DigitalPowerRail("nes.nmi-pullup", DigitalLevel.High));
        NmiPullup = Board.Add(new PullResistor("nes.nmi-resistor"));
        ReadyHigh = Board.Add(new DigitalPowerRail("nes.ready-pullup", DigitalLevel.High));
        ReadyPullup = Board.Add(new PullResistor("nes.ready-resistor"));
        BusEnableHigh = Board.Add(new DigitalPowerRail("nes.bus-enable-pullup", DigitalLevel.High));
        BusEnablePullup = Board.Add(new PullResistor("nes.bus-enable-resistor"));
        OamDma = Board.Add(new NesOamDmaController("nes.oam-dma"));
        Analyzer = Board.Add(new Mos6502BusAnalyzer("nes.cpu-bus-analyzer"));
        ControllerIo = Board.Add(new NesControllerIoPackage("nes.controller-io"));
        PpuRegisters = Board.Add(new NesPpuRegisterPackage("nes.ppu-registers"));
        PpuMemory = Board.Add(new NesPpuMemoryDevice("nes.ppu-memory", chrRom, mirroring));
        PpuTiming = Board.Add(new NesPpuTimingCore(
            "nes.ppu-timing",
            TimingProfile.DotsPerScanline,
            TimingProfile.ScanlinesPerFrame,
            TimingProfile.VblankStartScanline,
            TimingProfile.PreRenderScanline));
        PpuVblank = Board.Add(new DigitalSignalSource("nes.ppu-force-vblank", DigitalLevel.Low));
        Controller1Buttons = CreateControllerSources("nes.controller1");
        Controller2Buttons = CreateControllerSources("nes.controller2");

        WireControlSignals();
        WireAddressBus();
        WireDataBus();
        Simulator = new VirtualHardwareSimulator(Board);
    }

    public NesHardwareTimingProfile TimingProfile { get; }
    public NesHardwareRegion Region => TimingProfile.Region;
    public VirtualHardwareBoard Board { get; }
    public VirtualHardwareSimulator Simulator { get; }
    public DigitalPowerRail Vcc { get; }
    public DigitalPowerRail Ground { get; }
    public DigitalOscillator Clock { get; }
    public DigitalOscillator PpuClock { get; }
    public PowerOnResetCircuit ResetCircuit { get; }
    public Mos6502Processor Cpu { get; }
    public StaticRamChip WorkRam { get; }
    public ProgramRomChip PrgRom { get; }
    public BinaryAddressDecoder RamDecoder { get; }
    public BinaryAddressDecoder PrgDecoder { get; }
    public NotGate ReadInverter { get; }
    public DigitalPowerRail IrqHigh { get; }
    public DigitalPowerRail NmiHigh { get; }
    public PullResistor NmiPullup { get; }
    public DigitalPowerRail ReadyHigh { get; }
    public PullResistor ReadyPullup { get; }
    public DigitalPowerRail BusEnableHigh { get; }
    public PullResistor BusEnablePullup { get; }
    public NesOamDmaController OamDma { get; }
    public Mos6502BusAnalyzer Analyzer { get; }
    public NesControllerIoPackage ControllerIo { get; }
    public NesPpuRegisterPackage PpuRegisters { get; }
    public NesPpuMemoryDevice PpuMemory { get; }
    public NesPpuTimingCore PpuTiming { get; }
    public DigitalSignalSource PpuVblank { get; }
    public IReadOnlyList<DigitalSignalSource> Controller1Buttons { get; }
    public IReadOnlyList<DigitalSignalSource> Controller2Buttons { get; }

    public void PowerOn()
    {
        Board.PowerOn();
        Simulator.Settle();
    }

    public void ReleaseReset()
    {
        ResetCircuit.Release();
        Simulator.Settle();
    }

    public void AdvanceHalfCycle()
    {
        // A phase accumulator preserves non-integer regional clock ratios.
        // NTSC advances exactly 3 PPU half-cycles per CPU half-cycle; PAL
        // advances 16/5, producing the repeating 3,3,3,3,4 sequence.
        _ppuHalfCycleAccumulator += TimingProfile.PpuHalfCyclesPerCpuHalfCycleNumerator;
        var ppuHalfCycles = _ppuHalfCycleAccumulator / TimingProfile.PpuHalfCyclesPerCpuHalfCycleDenominator;
        _ppuHalfCycleAccumulator %= TimingProfile.PpuHalfCyclesPerCpuHalfCycleDenominator;

        for (var phase = 0; phase < ppuHalfCycles; phase++)
        {
            PpuClock.AdvanceHalfCycle();
            Simulator.Settle();
        }

        Clock.AdvanceHalfCycle();
        Simulator.Settle();
    }

    public void AdvanceCycle()
    {
        AdvanceHalfCycle();
        AdvanceHalfCycle();
    }

    public void AdvancePpuDot()
    {
        PpuClock.AdvanceHalfCycle();
        Simulator.Settle();
        PpuClock.AdvanceHalfCycle();
        Simulator.Settle();
    }

    public void AdvancePpuDots(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        for (var dot = 0; dot < count; dot++) AdvancePpuDot();
    }

    public void SetPpuVblank(bool active)
    {
        PpuVblank.Set(active ? DigitalLevel.High : DigitalLevel.Low);
        Simulator.Settle();
    }

    public void SetControllerButtons(int port, byte buttons)
    {
        var sources = port switch
        {
            1 => Controller1Buttons,
            2 => Controller2Buttons,
            _ => throw new ArgumentOutOfRangeException(nameof(port), "Controller port must be 1 or 2.")
        };

        for (var bit = 0; bit < 8; bit++)
        {
            sources[bit].Set((buttons & (1 << bit)) != 0 ? DigitalLevel.High : DigitalLevel.Low);
        }

        Simulator.Settle();
    }

    public void RunUntilHalted(int maximumCycles = 100_000)
    {
        for (var cycle = 0; cycle < maximumCycles && !Cpu.IsHalted; cycle++)
        {
            AdvanceCycle();
        }

        if (!Cpu.IsHalted)
        {
            throw new InvalidOperationException($"MOS 6502 did not halt after {maximumCycles} cycles.");
        }
    }


    private IReadOnlyList<DigitalSignalSource> CreateControllerSources(string prefix)
    {
        var sources = new DigitalSignalSource[8];
        for (var bit = 0; bit < sources.Length; bit++)
        {
            var source = Board.Add(new DigitalSignalSource($"{prefix}.button{bit}", DigitalLevel.Low));
            sources[bit] = source;
            var target = prefix.EndsWith("1", StringComparison.Ordinal)
                ? ControllerIo.Controller1.Pins[bit]
                : ControllerIo.Controller2.Pins[bit];
            Board.Connect($"{prefix}.B{bit}", source.Output, target);
        }

        return sources;
    }

    private void WireControlSignals()
    {
        Board.Connect("VCC", Vcc.Output, ResetCircuit.Vcc);
        Board.Connect("GND", Ground.Output, RamDecoder.EnableBar, PrgDecoder.EnableBar);
        Board.Connect("PHI2", Clock.Output, Cpu.Clock, Analyzer.Clock, OamDma.Clock);
        Board.Connect("/RESET", ResetCircuit.ResetBar, Cpu.ResetBar);
        Board.Connect("/IRQ", IrqHigh.Output, Cpu.IrqBar);
        Board.Connect("NMI_PULLUP_RAIL", NmiHigh.Output, NmiPullup.Rail);
        Board.Connect("/NMI", NmiPullup.Node, PpuTiming.NmiBar, Cpu.NmiBar);
        Board.Connect("RDY_PULLUP_RAIL", ReadyHigh.Output, ReadyPullup.Rail);
        Board.Connect("RDY", ReadyPullup.Node, OamDma.Ready, Cpu.Ready);
        Board.Connect("BUS_ENABLE_PULLUP_RAIL", BusEnableHigh.Output, BusEnablePullup.Rail);
        Board.Connect("CPU_BUS_ENABLE", BusEnablePullup.Node, OamDma.CpuBusEnable, Cpu.BusEnable, Analyzer.CpuBusEnable);
        Board.Connect("R/W", Cpu.ReadWrite, OamDma.ReadWrite, WorkRam.WriteEnableBar, ReadInverter.Input, Analyzer.ReadWrite, ControllerIo.ReadWrite, PpuRegisters.ReadWrite);
        Board.Connect("/READ", ReadInverter.Output, WorkRam.OutputEnableBar, PrgRom.OutputEnableBar);
        Board.Connect("SYNC", Cpu.Sync, Analyzer.Sync);
        Board.Connect("PPU_CLK", PpuClock.Output, PpuTiming.Clock);
        Board.Connect("PPU_NMI_ENABLE", PpuRegisters.NmiEnable, PpuTiming.NmiEnable);
        Board.Connect("PPU_FORCE_VBLANK", PpuVblank.Output, PpuTiming.ForceVblank);
        Board.Connect("PPU_VBLANK", PpuTiming.Vblank, PpuRegisters.Vblank);
        Board.Connect("PPU_DOT_TICK", PpuTiming.DotTick, PpuRegisters.DotTick);
        Board.Connect("PPU_OAM_DMA_WRITE", OamDma.OamWrite, PpuRegisters.DmaWrite);
        for (var bit = 0; bit < 8; bit++)
        {
            Board.Connect($"PPU_OAM_DMA_D{bit}", OamDma.OamData.Pins[bit], PpuRegisters.DmaData.Pins[bit]);
        }
        for (var bit = 0; bit < 9; bit++)
        {
            Board.Connect($"PPU_SCANLINE{bit}", PpuTiming.ScanlineBus.Pins[bit], PpuRegisters.Scanline.Pins[bit]);
            Board.Connect($"PPU_DOT{bit}", PpuTiming.DotBus.Pins[bit], PpuRegisters.Dot.Pins[bit]);
        }

        // A15 selects the cartridge PRG region at $8000-$FFFF.
        Board.Connect("A15", PrgDecoder.Address.Pins[0]);
        Board.Connect("PRG_/CS", PrgDecoder.Outputs[1], PrgRom.ChipSelectBar);

        // A13-A15 decode eight 8 KiB regions; region zero contains the internal
        // 2 KiB RAM mirrored four times because A11 and A12 are not connected.
        Board.Connect("A13", RamDecoder.Address.Pins[0]);
        Board.Connect("A14", RamDecoder.Address.Pins[1]);
        Board.Connect("A15", RamDecoder.Address.Pins[2]);
        Board.Connect("RAM_/CS", RamDecoder.Outputs[0], WorkRam.ChipSelectBar);
    }

    private void WireAddressBus()
    {
        for (var bit = 0; bit < 16; bit++)
        {
            Board.Connect($"A{bit}", Cpu.Address.Pins[bit], OamDma.Address.Pins[bit], Analyzer.Address.Pins[bit], ControllerIo.Address.Pins[bit], PpuRegisters.Address.Pins[bit]);
        }

        for (var bit = 0; bit < 11; bit++)
        {
            Board.Connect($"A{bit}", WorkRam.Address.Pins[bit]);
        }

        for (var bit = 0; bit < 15; bit++)
        {
            Board.Connect($"A{bit}", PrgRom.Address.Pins[bit]);
        }
    }

    private void WireDataBus()
    {
        for (var bit = 0; bit < 8; bit++)
        {
            Board.Connect($"D{bit}", Cpu.Data.Pins[bit], OamDma.Data.Pins[bit], WorkRam.Data.Pins[bit], PrgRom.Data.Pins[bit], Analyzer.Data.Pins[bit], ControllerIo.Data.Pins[bit], PpuRegisters.Data.Pins[bit]);
        }
    }
}
