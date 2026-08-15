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
/// North American NTSC NES motherboard assembled from standalone package models.
/// The board owns only components and nets. Cartridge and CIC-key signals are
/// exposed as normalized virtual-slot nets and are not backed by a ROM or mapper here.
/// </summary>
public sealed class NtscNesMotherboard
{
    public const long MasterClockHertz = 21_477_272;
    public const long CicClockHertz = 4_000_000;

    private CompiledClockExecutionPlan _executionPlan = null!;
    private CompiledClockExecutionPlan _cicExecutionPlan = null!;
    private CompiledLabMotherboardExecutionPlan? _compiledLabMotherboardExecutionPlan;

    public NtscNesMotherboard()
    {
        Board = new VirtualHardwareBoard("nes.ntsc.mainboard");

        Vcc = Board.Add(new DigitalPowerRail("nes.ntsc.vcc", DigitalLevel.High));
        Ground = Board.Add(new DigitalPowerRail("nes.ntsc.ground", DigitalLevel.Low));
        MasterClock = Board.Add(new DigitalOscillator("nes.ntsc.master-clock", MasterClockHertz));
        ResetSource = Board.Add(new DigitalSignalSource("nes.ntsc.reset", DigitalLevel.Low));
        IrqPullup = Board.Add(new PullResistor("nes.ntsc.irq-pullup"));
        NmiPullup = Board.Add(new PullResistor("nes.ntsc.nmi-pullup"));
        CicClock = Board.Add(new DigitalOscillator("nes.ntsc.cic-clock", CicClockHertz));
        CicSeed = Board.Add(new DigitalSignalSource("nes.ntsc.cic-seed", DigitalLevel.High));
        CicConfig = Board.Add(new DigitalSignalSource("nes.ntsc.cic-config", DigitalLevel.High));

        Cpu = Board.Add(new Rp2A03("U1.RP2A03"));
        Ppu = Board.Add(new Rp2C02("U2.RP2C02"));
        CpuRam = Board.Add(new Hm6116("U3.HM6116.CPU"));
        Ciram = Board.Add(new Hm6116("U4.HM6116.CIRAM"));
        AddressDecoder = Board.Add(new Sn74Ls139A("U5.SN74LS139A"));
        PpuAddressLatch = Board.Add(new Sn74Ls373("U6.SN74LS373"));
        BusInverter = Board.Add(new Sn74Ls368A("U7.SN74LS368A"));
        Controller1 = Board.Add(new NesStandardController("J1.CONTROLLER1"));
        Controller2 = Board.Add(new NesStandardController("J2.CONTROLLER2"));
        Cic = Board.Add(new Cic3193("U8.CIC3193"));

        TiePackagePower();
        WireClockAndReset();
        WireCpuBusAndDecode();
        WirePpuMemoryBus();
        WireInterruptsAndIo();
        WireCicAndResetChain();

        Simulator = new VirtualHardwareSimulator(Board);
        _executionPlan = new CompiledClockExecutionPlan(MasterClock, Simulator);
        _cicExecutionPlan = new CompiledClockExecutionPlan(CicClock, Simulator);
    }

    public VirtualHardwareBoard Board { get; }
    public VirtualHardwareSimulator Simulator { get; }
    public DigitalPowerRail Vcc { get; }
    public DigitalPowerRail Ground { get; }
    public DigitalOscillator MasterClock { get; }
    public DigitalSignalSource ResetSource { get; }
    public PullResistor IrqPullup { get; }
    public PullResistor NmiPullup { get; }
    public DigitalOscillator CicClock { get; }
    public DigitalSignalSource CicSeed { get; }
    public DigitalSignalSource CicConfig { get; }

    public Rp2A03 Cpu { get; }
    public Rp2C02 Ppu { get; }
    public Hm6116 CpuRam { get; }
    public Hm6116 Ciram { get; }
    public Sn74Ls139A AddressDecoder { get; }
    public Sn74Ls373 PpuAddressLatch { get; }
    public Sn74Ls368A BusInverter { get; }
    public NesStandardController Controller1 { get; }
    public NesStandardController Controller2 { get; }
    public Cic3193 Cic { get; }

    public IReadOnlyList<DigitalNet> CpuAddressNets { get; private set; } = [];
    public IReadOnlyList<DigitalNet> CpuDataNets { get; private set; } = [];
    public IReadOnlyList<DigitalNet> PpuDataNets { get; private set; } = [];
    public IReadOnlyList<DigitalNet> PpuLowAddressNets { get; private set; } = [];
    public IReadOnlyList<DigitalNet> PpuHighAddressNets { get; private set; } = [];
    public DigitalNet CpuReadWriteNet { get; private set; } = null!;
    public DigitalNet CpuM2Net { get; private set; } = null!;
    public DigitalNet CpuRomSelectBarNet { get; private set; } = null!;
    public DigitalNet CartridgeIrqNet { get; private set; } = null!;
    public DigitalNet PpuReadBarNet { get; private set; } = null!;
    public DigitalNet PpuWriteBarNet { get; private set; } = null!;
    public DigitalNet PpuAleNet { get; private set; } = null!;
    public DigitalNet CiramChipEnableBarNet { get; private set; } = null!;
    public DigitalNet CiramA10Net { get; private set; } = null!;
    public DigitalNet AudioNet { get; private set; } = null!;
    public DigitalNet CicDataToCartridgeNet { get; private set; } = null!;
    public DigitalNet CicDataFromCartridgeNet { get; private set; } = null!;
    public DigitalNet CicSlaveResetNet { get; private set; } = null!;
    public DigitalNet HostResetNet { get; private set; } = null!;

    public void PowerOn()
    {
        Board.PowerOn();
        _compiledLabMotherboardExecutionPlan?.SynchronizePowerOn();
    }

    public void ReleaseReset()
    {
        ResetSource.Set(DigitalLevel.High);
        _compiledLabMotherboardExecutionPlan?.RefreshExternalSource(Cic.HostResetBar);
    }

    public void AssertReset()
    {
        ResetSource.Set(DigitalLevel.Low);
        _compiledLabMotherboardExecutionPlan?.RefreshExternalSource(Cic.HostResetBar);
    }

    public bool CompiledLabMotherboardEnabled => _compiledLabMotherboardExecutionPlan is not null;
    public int CompiledLabRuntimeUnitCount => _compiledLabMotherboardExecutionPlan?.RuntimeUnits ?? 0;
    public int CompiledLabInternalComponentCount => _compiledLabMotherboardExecutionPlan?.InternalComponentCount ?? 0;
    public int CompiledLabFoldedInternalTraceCount => _compiledLabMotherboardExecutionPlan?.FoldedInternalTraceCount ?? 0;
    public int CompiledLabBoundaryTraceCount => _compiledLabMotherboardExecutionPlan?.BoundaryTraceCount ?? 0;
    public Guid? CompiledLabCompilationId => _compiledLabMotherboardExecutionPlan?.CompilationId;

    public void SetCompiledLabMotherboardEnabled(bool enabled)
    {
        if (!enabled)
        {
            _compiledLabMotherboardExecutionPlan?.Dispose();
            _compiledLabMotherboardExecutionPlan = null;
            return;
        }

        if (_compiledLabMotherboardExecutionPlan is not null) return;

        _compiledLabMotherboardExecutionPlan = new CompiledLabMotherboardExecutionPlan(
            Board,
            (ICompiledClockSource)MasterClock);
    }

    public void AdvanceMasterHalfCycle()
    {
        if (_compiledLabMotherboardExecutionPlan is { } compiled)
            compiled.AdvanceHalfCycle();
        else
            _executionPlan.AdvanceHalfCycle();
    }

    public void AdvanceCicHalfCycle()
    {
        _cicExecutionPlan.AdvanceHalfCycle();
        _compiledLabMotherboardExecutionPlan?.RefreshExternalSource(Cic.HostResetBar);
    }

    public void AdvanceCicCycles(int cycles)
    {
        if (cycles < 0) throw new ArgumentOutOfRangeException(nameof(cycles));
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            AdvanceCicHalfCycle();
            AdvanceCicHalfCycle();
        }
    }

    public void AdvanceMasterCycles(int cycles)
    {
        if (_compiledLabMotherboardExecutionPlan is { } compiled)
            compiled.AdvanceCycles(cycles);
        else
            _executionPlan.AdvanceCycles(cycles);
    }

    internal void RecompileTopology() => _executionPlan.RecompileTopology();

    internal void AttachCompiledExternalDevice(ICompiledExternalDevice device) =>
        _compiledLabMotherboardExecutionPlan?.AttachExternalDevice(device);

    internal void DetachCompiledExternalDevice(ICompiledExternalDevice device) =>
        _compiledLabMotherboardExecutionPlan?.DetachExternalDevice(device);


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
        ConnectPower(Cic.Vcc, Cic.Gnd);
    }

    private void ConnectPower(DigitalPin vcc, DigitalPin gnd)
    {
        Board.Connect("VCC", Vcc.Output, vcc);
        Board.Connect("GND", Ground.Output, gnd);
    }

    private void WireClockAndReset()
    {
        Board.Connect("MASTER.CLK", MasterClock.Output, Cpu.MasterClock, Ppu.Clock);
        Board.Connect("CIC.RESET_BAR", ResetSource.Output, Cic.ResetBar);
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

        // The first half of the LS139 qualifies the CPU map with M2. With 1A=M2
        // and 1B=A15, 1Y1 is active only for M2=1/A15=0 (/M07) and 1Y3
        // is active only for M2=1/A15=1 (/ROMSEL). /M07 then enables the
        // second half, where A14:A13 select the mirrored 8 KiB internal regions.
        Board.Connect("GND", AddressDecoder.Enable1Bar);
        Board.Connect("CPU.M2", AddressDecoder.A1);
        Board.Connect("CPU.A15", AddressDecoder.B1);
        Board.Connect("DECODE.M07_BAR", AddressDecoder.Y11Bar, AddressDecoder.Enable2Bar);
        CpuRomSelectBarNet = Board.Connect("CPU.ROMSEL_BAR", AddressDecoder.Y13Bar);
        Board.Connect("CPU.A13", AddressDecoder.A2);
        Board.Connect("CPU.A14", AddressDecoder.B2);
        Board.Connect("DECODE.RAM_BAR", AddressDecoder.Y20Bar, CpuRam.ChipSelectBar);
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
        // RP2C0x AD0-AD7 are multiplexed only on the PPU package side.
        // The motherboard's 74LS373 demultiplexes the low address byte. The
        // cartridge connector therefore sees separate PPU A0-A7 and D0-D7
        // conductors, just like the physical console.
        var dataNets = new DigitalNet[8];
        var lowAddressNets = new DigitalNet[8];
        for (var bit = 0; bit < 8; bit++)
        {
            dataNets[bit] = Board.Connect(
                $"PPU.D{bit}",
                Ppu.MultiplexedAddressData.Pins[bit],
                PpuAddressLatch.D.Pins[bit],
                Ciram.Data.Pins[bit]);
            lowAddressNets[bit] = Board.Connect(
                $"PPU.A{bit}",
                PpuAddressLatch.Q.Pins[bit],
                Ciram.Address.Pins[bit]);
        }
        PpuDataNets = dataNets;
        PpuLowAddressNets = lowAddressNets;

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

        // NES controller inputs are future connector nets.
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

    private void WireCicAndResetChain()
    {
        Board.Connect("CIC.CLK", CicClock.Output, Cic.Clock);
        Board.Connect("CIC.SEED", CicSeed.Output, Cic.Seed);
        Board.Connect("CIC.CONFIG", CicConfig.Output, Cic.Config);

        HostResetNet = Board.Connect("SYSTEM.RESET_BAR", Cic.HostResetBar, Cpu.ResetBar, Ppu.ResetBar);
        CicSlaveResetNet = Board.Connect("SLOT.CIC.SLAVE_RESET_BAR", Cic.SlaveResetBar);
        CicDataToCartridgeNet = Board.Connect("SLOT.CIC.DATA_OUT", Cic.DataOut);
        CicDataFromCartridgeNet = Board.Connect("SLOT.CIC.DATA_IN", Cic.DataIn);

        Board.Connect("GND", Cic.Nc5, Cic.Nc11, Cic.Nc12, Cic.Nc13, Cic.Nc14, Cic.Nc15);
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
