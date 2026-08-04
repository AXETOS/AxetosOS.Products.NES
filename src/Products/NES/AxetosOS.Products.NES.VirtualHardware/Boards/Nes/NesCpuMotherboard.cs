using AxetosOS.Products.NES.VirtualHardware.Components.Clock;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Components.Logic;
using AxetosOS.Products.NES.VirtualHardware.Components.Memory;
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
    public const long NtscCpuClockHertz = 1_789_773;
    public const int WorkRamSize = 2 * 1024;
    public const int PrgRomSize = 32 * 1024;

    public NesCpuMotherboard(ReadOnlySpan<byte> prgRom)
    {
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
        Clock = Board.Add(new DigitalOscillator("nes.cpu-clock", NtscCpuClockHertz));
        ResetCircuit = Board.Add(new PowerOnResetCircuit("nes.reset"));
        Cpu = Board.Add(new Mos6502Processor("nes.cpu"));
        WorkRam = Board.Add(new StaticRamChip("nes.work-ram", 11));
        PrgRom = Board.Add(new ProgramRomChip("nes.prg-rom", 15, mappedPrg));
        RamDecoder = Board.Add(new BinaryAddressDecoder("nes.ram-decoder", 3));
        PrgDecoder = Board.Add(new BinaryAddressDecoder("nes.prg-decoder", 1));
        ReadInverter = Board.Add(new NotGate("nes.read-inverter"));
        IrqHigh = Board.Add(new DigitalPowerRail("nes.irq-pullup", DigitalLevel.High));
        NmiHigh = Board.Add(new DigitalPowerRail("nes.nmi-pullup", DigitalLevel.High));
        ReadyHigh = Board.Add(new DigitalPowerRail("nes.ready-pullup", DigitalLevel.High));
        Analyzer = Board.Add(new Mos6502BusAnalyzer("nes.cpu-bus-analyzer"));

        WireControlSignals();
        WireAddressBus();
        WireDataBus();
        Simulator = new VirtualHardwareSimulator(Board);
    }

    public VirtualHardwareBoard Board { get; }
    public VirtualHardwareSimulator Simulator { get; }
    public DigitalPowerRail Vcc { get; }
    public DigitalPowerRail Ground { get; }
    public DigitalOscillator Clock { get; }
    public PowerOnResetCircuit ResetCircuit { get; }
    public Mos6502Processor Cpu { get; }
    public StaticRamChip WorkRam { get; }
    public ProgramRomChip PrgRom { get; }
    public BinaryAddressDecoder RamDecoder { get; }
    public BinaryAddressDecoder PrgDecoder { get; }
    public NotGate ReadInverter { get; }
    public DigitalPowerRail IrqHigh { get; }
    public DigitalPowerRail NmiHigh { get; }
    public DigitalPowerRail ReadyHigh { get; }
    public Mos6502BusAnalyzer Analyzer { get; }

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
        Clock.AdvanceHalfCycle();
        Simulator.Settle();
    }

    public void AdvanceCycle()
    {
        AdvanceHalfCycle();
        AdvanceHalfCycle();
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

    private void WireControlSignals()
    {
        Board.Connect("VCC", Vcc.Output, ResetCircuit.Vcc);
        Board.Connect("GND", Ground.Output, RamDecoder.EnableBar, PrgDecoder.EnableBar);
        Board.Connect("PHI2", Clock.Output, Cpu.Clock, Analyzer.Clock);
        Board.Connect("/RESET", ResetCircuit.ResetBar, Cpu.ResetBar);
        Board.Connect("/IRQ", IrqHigh.Output, Cpu.IrqBar);
        Board.Connect("/NMI", NmiHigh.Output, Cpu.NmiBar);
        Board.Connect("RDY", ReadyHigh.Output, Cpu.Ready);
        Board.Connect("R/W", Cpu.ReadWrite, WorkRam.WriteEnableBar, ReadInverter.Input, Analyzer.ReadWrite);
        Board.Connect("/READ", ReadInverter.Output, WorkRam.OutputEnableBar, PrgRom.OutputEnableBar);
        Board.Connect("SYNC", Cpu.Sync, Analyzer.Sync);

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
            Board.Connect($"A{bit}", Cpu.Address.Pins[bit], Analyzer.Address.Pins[bit]);
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
            Board.Connect($"D{bit}", Cpu.Data.Pins[bit], WorkRam.Data.Pins[bit], PrgRom.Data.Pins[bit], Analyzer.Data.Pins[bit]);
        }
    }
}
