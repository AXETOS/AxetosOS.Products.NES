using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components.Clock;
using AxetosOS.Products.NES.VirtualHardware.Components.Logic;
using AxetosOS.Products.NES.VirtualHardware.Components.Memory;
using AxetosOS.Products.NES.VirtualHardware.Components.Power;
using AxetosOS.Products.NES.VirtualHardware.Components.Processors.Tiny8;
using AxetosOS.Products.NES.VirtualHardware.Components.Reset;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Boards.Examples;

/// <summary>
/// A complete pin-wired demonstration computer. No component receives direct
/// references to another component; all interaction occurs over resolved nets.
/// </summary>
public sealed class PinDrivenMicrocomputer
{
    private CompiledLabMotherboardExecutionPlan? _compiledExecutionPlan;
    public PinDrivenMicrocomputer(ReadOnlySpan<byte> program)
    {
        Board = new VirtualHardwareBoard("virtual-hardware.example.microcomputer");
        Vcc = Board.Add(new DigitalPowerRail("example.vcc", DigitalLevel.High));
        Ground = Board.Add(new DigitalPowerRail("example.ground", DigitalLevel.Low));
        Oscillator = Board.Add(new DigitalOscillator("example.oscillator", 1_000_000));
        ResetCircuit = Board.Add(new PowerOnResetCircuit("example.reset"));
        Cpu = Board.Add(new Tiny8Processor("example.cpu"));
        Ram = Board.Add(new StaticRamChip("example.ram", 7));
        Rom = Board.Add(new ProgramRomChip("example.rom", 7, program));
        Decoder = Board.Add(new BinaryAddressDecoder("example.decoder", 1));
        ReadInverter = Board.Add(new NotGate("example.read-inverter"));

        WireSingleSignals();
        WireAddressBus();
        WireDataBus();

        Simulator = new VirtualHardwareSimulator(Board);
    }

    public VirtualHardwareBoard Board { get; }
    public VirtualHardwareSimulator Simulator { get; }
    public DigitalPowerRail Vcc { get; }
    public DigitalPowerRail Ground { get; }
    public DigitalOscillator Oscillator { get; }
    public PowerOnResetCircuit ResetCircuit { get; }
    public Tiny8Processor Cpu { get; }
    public StaticRamChip Ram { get; }
    public ProgramRomChip Rom { get; }
    public BinaryAddressDecoder Decoder { get; }
    public NotGate ReadInverter { get; }

    public void PowerOn()
    {
        Board.PowerOn();
        _compiledExecutionPlan?.SynchronizePowerOn();
    }

    public void ReleaseReset()
    {
        ResetCircuit.Release();
        _compiledExecutionPlan?.RefreshExternalSource(ResetCircuit.ResetBar);
    }

    public void AdvanceHalfCycle()
    {
        if (_compiledExecutionPlan is not null) _compiledExecutionPlan.AdvanceHalfCycle();
        else Oscillator.AdvanceHalfCycle();
    }

    public void AdvanceCycle()
    {
        AdvanceHalfCycle();
        AdvanceHalfCycle();
    }

    public bool CompiledHardwareEnabled => _compiledExecutionPlan is not null;

    public void SetCompiledHardwareEnabled(bool enabled)
    {
        if (!enabled)
        {
            _compiledExecutionPlan?.Dispose();
            _compiledExecutionPlan = null;
            return;
        }
        if (_compiledExecutionPlan is not null) return;
        _compiledExecutionPlan = new CompiledLabMotherboardExecutionPlan(Board, Oscillator);
    }

    public void RunUntilHalted(int maximumCycles = 1_000)
    {
        for (var cycle = 0; cycle < maximumCycles && !Cpu.IsHalted; cycle++)
        {
            AdvanceCycle();
        }

        if (!Cpu.IsHalted)
        {
            throw new InvalidOperationException($"Tiny8 program did not halt after {maximumCycles} cycles.");
        }
    }

    private void WireSingleSignals()
    {
        Board.Connect("VCC", Vcc.Output, ResetCircuit.Vcc);
        Board.Connect("GND", Ground.Output, Decoder.EnableBar);
        Board.Connect("CLK", Oscillator.Output, Cpu.Clock);
        Board.Connect("/RESET", ResetCircuit.ResetBar, Cpu.ResetBar);
        Board.Connect("R/W", Cpu.ReadWrite, Ram.WriteEnableBar, ReadInverter.Input);
        Board.Connect("/READ", ReadInverter.Output, Ram.OutputEnableBar, Rom.OutputEnableBar);
        Board.Connect("RAM_/CS", Decoder.Outputs[0], Ram.ChipSelectBar);
        Board.Connect("ROM_/CS", Decoder.Outputs[1], Rom.ChipSelectBar);
        Board.Connect("DECODER_A0", Cpu.Address.Pins[7], Decoder.Address.Pins[0]);
    }

    private void WireAddressBus()
    {
        for (var bit = 0; bit < 7; bit++)
        {
            Board.Connect($"A{bit}", Cpu.Address.Pins[bit], Ram.Address.Pins[bit], Rom.Address.Pins[bit]);
        }
    }

    private void WireDataBus()
    {
        for (var bit = 0; bit < 8; bit++)
        {
            Board.Connect($"D{bit}", Cpu.Data.Pins[bit], Ram.Data.Pins[bit], Rom.Data.Pins[bit]);
        }
    }
}
