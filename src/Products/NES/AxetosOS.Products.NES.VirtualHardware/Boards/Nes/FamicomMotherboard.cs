using AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Logic;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Memory;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;
using AxetosOS.Products.NES.VirtualHardware.Components.Clock;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Components.Nes;
using AxetosOS.Products.NES.VirtualHardware.Components.Passives;
using AxetosOS.Products.NES.VirtualHardware.Components.Power;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Boards.Nes;

/// <summary>
/// Japanese Famicom motherboard assembled from standalone package models.
/// The board owns only components and nets. Cartridge signals are exposed as
/// normalized virtual-slot nets and are not backed by a ROM or mapper here.
/// </summary>
public sealed class FamicomMotherboard
{
    public const long MasterClockHertz = 21_477_272;

    private CompiledClockExecutionPlan _executionPlan = null!;
    private CompiledFamicomNromExecutionPlan? _compiledNromExecutionPlan;

    public FamicomMotherboard()
    {
        Board = new VirtualHardwareBoard("famicom.mainboard");

        Vcc = Board.Add(new DigitalPowerRail("famicom.vcc", DigitalLevel.High));
        Ground = Board.Add(new DigitalPowerRail("famicom.ground", DigitalLevel.Low));
        MasterClock = Board.Add(new DigitalOscillator("famicom.master-clock", MasterClockHertz));
        ResetSource = Board.Add(new DigitalSignalSource("famicom.reset", DigitalLevel.Low));
        IrqPullup = Board.Add(new PullResistor("famicom.irq-pullup"));
        NmiPullup = Board.Add(new PullResistor("famicom.nmi-pullup"));

        Cpu = Board.Add(new Rp2A03("U1.RP2A03"));
        Ppu = Board.Add(new Rp2C02("U2.RP2C02"));
        CpuRam = Board.Add(new Hm6116("U3.HM6116.CPU"));
        Ciram = Board.Add(new Hm6116("U4.HM6116.CIRAM"));
        AddressDecoder = Board.Add(new Sn74Ls139A("U5.SN74LS139A"));
        PpuAddressLatch = Board.Add(new Sn74Ls373("U6.SN74LS373"));
        BusInverter = Board.Add(new Sn74Ls368A("U7.SN74LS368A"));
        Controller1 = Board.Add(new NesStandardController("J1.CONTROLLER1"));
        Controller2 = Board.Add(new NesStandardController("J2.CONTROLLER2"));

        TiePackagePower();
        WireClockAndReset();
        WireCpuBusAndDecode();
        WirePpuMemoryBus();
        WireInterruptsAndIo();

        Simulator = new VirtualHardwareSimulator(Board);
        _executionPlan = new CompiledClockExecutionPlan(MasterClock, Simulator);
    }

    public VirtualHardwareBoard Board { get; }
    public VirtualHardwareSimulator Simulator { get; }
    public DigitalPowerRail Vcc { get; }
    public DigitalPowerRail Ground { get; }
    public DigitalOscillator MasterClock { get; }
    public DigitalSignalSource ResetSource { get; }
    public PullResistor IrqPullup { get; }
    public PullResistor NmiPullup { get; }

    public Rp2A03 Cpu { get; }
    public Rp2C02 Ppu { get; }
    public Hm6116 CpuRam { get; }
    public Hm6116 Ciram { get; }
    public Sn74Ls139A AddressDecoder { get; }
    public Sn74Ls373 PpuAddressLatch { get; }
    public Sn74Ls368A BusInverter { get; }
    public NesStandardController Controller1 { get; }
    public NesStandardController Controller2 { get; }

    public IReadOnlyList<DigitalNet> CpuAddressNets { get; private set; } = [];
    public IReadOnlyList<DigitalNet> CpuDataNets { get; private set; } = [];
    public IReadOnlyList<DigitalNet> PpuAddressDataNets { get; private set; } = [];
    public IReadOnlyList<DigitalNet> PpuHighAddressNets { get; private set; } = [];
    public DigitalNet CpuReadWriteNet { get; private set; } = null!;
    public DigitalNet CpuM2Net { get; private set; } = null!;
    public DigitalNet CartridgeIrqNet { get; private set; } = null!;
    public DigitalNet PpuReadBarNet { get; private set; } = null!;
    public DigitalNet PpuWriteBarNet { get; private set; } = null!;
    public DigitalNet PpuAleNet { get; private set; } = null!;
    public DigitalNet CiramChipEnableBarNet { get; private set; } = null!;
    public DigitalNet CiramA10Net { get; private set; } = null!;
    public DigitalNet AudioNet { get; private set; } = null!;

    public void PowerOn()
    {
        Board.PowerOn();
        _compiledNromExecutionPlan?.SynchronizePowerOn();
    }

    public void ReleaseReset()
    {
        ResetSource.Set(DigitalLevel.High);
        _compiledNromExecutionPlan?.SetResetAsserted(false);
    }

    public void AssertReset()
    {
        ResetSource.Set(DigitalLevel.Low);
        _compiledNromExecutionPlan?.SetResetAsserted(true);
    }

    public bool CompiledPhysicalMachineEnabled => _compiledNromExecutionPlan is not null;
    public int CompiledRuntimeUnitCount => _compiledNromExecutionPlan?.RuntimeUnits ?? 0;
    public int CompiledFoldedPhysicalTraceCount => _compiledNromExecutionPlan?.FoldedPhysicalTraces ?? 0;

    public void SetCompiledPhysicalMachineEnabled(bool enabled)
    {
        if (!enabled)
        {
            _compiledNromExecutionPlan?.Dispose();
            _compiledNromExecutionPlan = null;
            return;
        }

        if (_compiledNromExecutionPlan is not null) return;
        if (!Board.Components.OfType<NromCartridge>().Any())
            throw new InvalidOperationException("A physical NROM cartridge must be attached before compiling the Famicom machine.");
        _compiledNromExecutionPlan = new CompiledFamicomNromExecutionPlan(this);
    }

    public void AdvanceMasterHalfCycle()
    {
        var compiled = _compiledNromExecutionPlan;
        if (compiled is not null) compiled.AdvanceHalfCycle();
        else _executionPlan.AdvanceHalfCycle();
    }

    public void AdvanceMasterCycles(int cycles)
    {
        var compiled = _compiledNromExecutionPlan;
        if (compiled is not null) compiled.AdvanceCycles(cycles);
        else _executionPlan.AdvanceCycles(cycles);
    }

    internal void RecompileTopology()
    {
        _compiledNromExecutionPlan?.Dispose();
        _compiledNromExecutionPlan = null;
        _executionPlan.RecompileTopology();

        // Cartridge insertion fixes the complete Famicom/NROM wiring. From this
        // point the physical description can be compiled into one fused runtime
        // circuit; an unpopulated motherboard deliberately stays on the generic
        // model for standalone board/chip tests.
        if (Board.Components.OfType<NromCartridge>().Any())
            _compiledNromExecutionPlan = new CompiledFamicomNromExecutionPlan(this);
    }


    private void TiePackagePower()
    {
        ConnectPower(Cpu.Vcc, Cpu.Gnd);
        ConnectPower(Ppu.Vcc, Ppu.Gnd);
        ConnectPower(CpuRam.Vcc, CpuRam.Gnd);
        ConnectPower(Ciram.Vcc, Ciram.Gnd);
        ConnectPower(AddressDecoder.Vcc, AddressDecoder.Gnd);
        ConnectPower(PpuAddressLatch.Vcc, PpuAddressLatch.Gnd);
        ConnectPower(BusInverter.Vcc, BusInverter.Gnd);
        ConnectPower(Controller1.Vcc, Controller1.Gnd);
        ConnectPower(Controller2.Vcc, Controller2.Gnd);
    }

    private void ConnectPower(DigitalPin vcc, DigitalPin gnd)
    {
        Board.Connect("VCC", Vcc.Output, vcc);
        Board.Connect("GND", Ground.Output, gnd);
    }

    private void WireClockAndReset()
    {
        Board.Connect("MASTER.CLK", MasterClock.Output, Cpu.MasterClock, Ppu.Clock);
        Board.Connect("SYSTEM.RESET_BAR", ResetSource.Output, Cpu.ResetBar, Ppu.ResetBar);
    }

    private void WireCpuBusAndDecode()
    {
        var addressNets = new DigitalNet[16];
        for (var bit = 0; bit < 16; bit++)
        {
            addressNets[bit] = Board.Connect($"CPU.A{bit}", Cpu.Address.Pins[bit]);
        }
        CpuAddressNets = addressNets;

        var dataNets = new DigitalNet[8];
        for (var bit = 0; bit < 8; bit++)
        {
            dataNets[bit] = Board.Connect(
                $"CPU.D{bit}",
                Cpu.Data.Pins[bit],
                CpuRam.Data.Pins[bit],
                Ppu.CpuData.Pins[bit]);
        }
        CpuDataNets = dataNets;

        for (var bit = 0; bit < 11; bit++)
        {
            Board.Connect($"CPU.A{bit}", CpuRam.Address.Pins[bit]);
        }

        for (var bit = 0; bit < 3; bit++)
        {
            Board.Connect($"CPU.A{bit}", Ppu.RegisterSelect.Pins[bit]);
        }

        CpuReadWriteNet = Board.Connect("CPU.RW", Cpu.ReadWrite, CpuRam.WriteEnableBar, Ppu.CpuReadWrite, BusInverter.A[0]);
        CpuM2Net = Board.Connect("CPU.M2", Cpu.M2);

        // A15 enables both decoder halves. A14:A13 select the mirrored 8 KiB
        // CPU regions: Y0 selects internal RAM and Y1 selects PPU registers.
        Board.Connect("CPU.A15", AddressDecoder.Enable1Bar, AddressDecoder.Enable2Bar);
        Board.Connect("CPU.A13", AddressDecoder.A1, AddressDecoder.A2);
        Board.Connect("CPU.A14", AddressDecoder.B1, AddressDecoder.B2);
        Board.Connect("DECODE.RAM_BAR", AddressDecoder.Y10Bar, CpuRam.ChipSelectBar);
        Board.Connect("DECODE.PPU_BAR", AddressDecoder.Y21Bar, Ppu.ChipSelectBar);

        // One LS368 channel supplies active-low CPU read enable to work RAM.
        Board.Connect("GND", BusInverter.Enable1Bar);
        Board.Connect("VCC", BusInverter.Enable2Bar);
        Board.Connect("CPU.RD_BAR", BusInverter.YBar[0], CpuRam.OutputEnableBar);
        for (var channel = 1; channel < 6; channel++)
        {
            Board.Connect("GND", BusInverter.A[channel]);
        }
    }

    private void WirePpuMemoryBus()
    {
        var adNets = new DigitalNet[8];
        for (var bit = 0; bit < 8; bit++)
        {
            adNets[bit] = Board.Connect(
                $"PPU.AD{bit}",
                Ppu.MultiplexedAddressData.Pins[bit],
                PpuAddressLatch.D.Pins[bit],
                Ciram.Data.Pins[bit]);
            Board.Connect($"CIRAM.A{bit}", PpuAddressLatch.Q.Pins[bit], Ciram.Address.Pins[bit]);
        }
        PpuAddressDataNets = adNets;

        var highNets = new DigitalNet[6];
        for (var bit = 0; bit < 6; bit++)
        {
            highNets[bit] = Board.Connect($"PPU.A{bit + 8}", Ppu.HighAddress.Pins[bit]);
            if (bit < 2)
            {
                Board.Connect($"PPU.A{bit + 8}", Ciram.Address.Pins[bit + 8]);
            }
        }
        PpuHighAddressNets = highNets;

        PpuAleNet = Board.Connect("PPU.ALE", Ppu.AddressLatchEnable, PpuAddressLatch.LatchEnable);
        Board.Connect("GND", PpuAddressLatch.OutputEnableBar);
        CiramChipEnableBarNet = Board.Connect("CIRAM.CE_BAR", Ciram.ChipSelectBar);
        CiramA10Net = Board.Connect("CIRAM.A10", Ciram.Address.Pins[10]);
        PpuReadBarNet = Board.Connect("PPU.RD_BAR", Ppu.VramReadBar, Ciram.OutputEnableBar);
        PpuWriteBarNet = Board.Connect("PPU.WR_BAR", Ppu.VramWriteBar, Ciram.WriteEnableBar);
    }

    private void WireInterruptsAndIo()
    {
        Board.Connect("VCC", IrqPullup.Rail, NmiPullup.Rail);
        CartridgeIrqNet = Board.Connect("CPU.IRQ_BAR", IrqPullup.Node, Cpu.IrqBar);
        Board.Connect("CPU.NMI_BAR", NmiPullup.Node, Ppu.NmiBar, Cpu.NmiBar);

        WireStandardControllers();

        // OUT1/OUT2 remain available for Famicom expansion hardware.
        Board.Connect("CTRL.OUT1", Cpu.ControllerOut1);
        Board.Connect("CTRL.OUT2", Cpu.ControllerOut2);
        AudioNet = Board.Connect("AUDIO.OUT", Cpu.AudioOut);

        for (var bit = 0; bit < Ppu.Extension.Width; bit++)
        {
            Board.Connect($"PPU.EXT{bit}", Ppu.Extension.Pins[bit]);
        }
    }

    private void WireStandardControllers()
    {
        Board.Connect("CTRL.STROBE", Cpu.ControllerOut0, Controller1.Strobe, Controller2.Strobe);
        Board.Connect("CTRL.OE1_BAR", Cpu.ControllerRead1Bar, Controller1.ClockBar);
        Board.Connect("CTRL.OE2_BAR", Cpu.ControllerRead2Bar, Controller2.ClockBar);
        Board.Connect("CTRL.DATA1", Controller1.Data, Cpu.ControllerData1);
        Board.Connect("CTRL.DATA2", Controller2.Data, Cpu.ControllerData2);

        // Button pins are exposed as board nets. With no external driver they
        // are sampled as unpressed; the controller still drives a determinate
        // serial zero onto the CPU input.
        for (var button = 0; button < 8; button++)
        {
            Board.Connect($"CTRL1.BUTTON{button}", Controller1.Buttons.Pins[button]);
            Board.Connect($"CTRL2.BUTTON{button}", Controller2.Buttons.Pins[button]);
        }
    }

}
