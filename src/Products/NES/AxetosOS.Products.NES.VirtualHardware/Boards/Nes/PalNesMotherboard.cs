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

/// <summary>Identifies the PAL lock-chip population installed on the board.</summary>
public enum PalCicVariant
{
    PalA3195,
    PalB3197
}

/// <summary>
/// European/Australian PAL NES motherboard assembled from standalone package models.
/// The board owns only components and nets. Cartridge and CIC-key signals are
/// exposed as normalized virtual-slot nets and are not backed by a ROM or mapper here.
/// </summary>
public sealed class PalNesMotherboard
{
    public const long MasterClockHertz = 26_601_712;
    public const long CicClockHertz = 4_000_000;

    public PalNesMotherboard(PalCicVariant cicVariant = PalCicVariant.PalA3195)
    {
        CicVariant = cicVariant;
        Board = new VirtualHardwareBoard("nes.pal.mainboard");

        Vcc = Board.Add(new DigitalPowerRail("nes.pal.vcc", DigitalLevel.High));
        Ground = Board.Add(new DigitalPowerRail("nes.pal.ground", DigitalLevel.Low));
        MasterClock = Board.Add(new DigitalOscillator("nes.pal.master-clock", MasterClockHertz));
        ResetSource = Board.Add(new DigitalSignalSource("nes.pal.reset", DigitalLevel.Low));
        IrqPullup = Board.Add(new PullResistor("nes.pal.irq-pullup"));
        NmiPullup = Board.Add(new PullResistor("nes.pal.nmi-pullup"));
        CicClock = Board.Add(new DigitalOscillator("nes.pal.cic-clock", CicClockHertz));
        CicSeed = Board.Add(new DigitalSignalSource("nes.pal.cic-seed", DigitalLevel.High));
        CicConfig = Board.Add(new DigitalSignalSource("nes.pal.cic-config", DigitalLevel.High));

        Cpu = Board.Add(new Rp2A07("U1.RP2A07"));
        Ppu = Board.Add(new Rp2C07("U2.RP2C07"));
        CpuRam = Board.Add(new Hm6116("U3.HM6116.CPU"));
        Ciram = Board.Add(new Hm6116("U4.HM6116.CIRAM"));
        AddressDecoder = Board.Add(new Sn74Ls139A("U5.SN74LS139A"));
        PpuAddressLatch = Board.Add(new Sn74Ls373("U6.SN74LS373"));
        BusInverter = Board.Add(new Sn74Ls368A("U7.SN74LS368A"));
        Controller1 = Board.Add(new NesStandardController("J1.CONTROLLER1"));
        Controller2 = Board.Add(new NesStandardController("J2.CONTROLLER2"));
        if (cicVariant == PalCicVariant.PalA3195)
        {
            Cic3195 = Board.Add(new Cic3195("U8.CIC3195"));
        }
        else
        {
            Cic3197 = Board.Add(new Cic3197("U8.CIC3197"));
        }

        TiePackagePower();
        WireClockAndReset();
        WireCpuBusAndDecode();
        WirePpuMemoryBus();
        WireInterruptsAndIo();
        WireCicAndResetChain();

        Simulator = new VirtualHardwareSimulator(Board);
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

    public Rp2A07 Cpu { get; }
    public Rp2C07 Ppu { get; }
    public Hm6116 CpuRam { get; }
    public Hm6116 Ciram { get; }
    public Sn74Ls139A AddressDecoder { get; }
    public Sn74Ls373 PpuAddressLatch { get; }
    public Sn74Ls368A BusInverter { get; }
    public NesStandardController Controller1 { get; }
    public NesStandardController Controller2 { get; }
    public PalCicVariant CicVariant { get; }
    public Cic3195? Cic3195 { get; }
    public Cic3197? Cic3197 { get; }
    public object Cic => (object?)Cic3195 ?? Cic3197!;

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
    public DigitalNet CicDataToCartridgeNet { get; private set; } = null!;
    public DigitalNet CicDataFromCartridgeNet { get; private set; } = null!;
    public DigitalNet CicSlaveResetNet { get; private set; } = null!;
    public DigitalNet HostResetNet { get; private set; } = null!;

    public void PowerOn()
    {
        Board.PowerOn();
        Simulator.Settle();
    }

    public void ReleaseReset()
    {
        ResetSource.Set(DigitalLevel.High);
        Simulator.Settle();
    }

    public void AssertReset()
    {
        ResetSource.Set(DigitalLevel.Low);
        Simulator.Settle();
    }

    public void AdvanceMasterHalfCycle()
    {
        MasterClock.AdvanceHalfCycle();
        Simulator.Settle();
    }

    public void AdvanceCicHalfCycle()
    {
        CicClock.AdvanceHalfCycle();
        Simulator.Settle();
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
        if (cycles < 0) throw new ArgumentOutOfRangeException(nameof(cycles));
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            AdvanceMasterHalfCycle();
            AdvanceMasterHalfCycle();
        }
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
        ConnectPower(CicVcc, CicGnd);
    }

    private void ConnectPower(DigitalPin vcc, DigitalPin gnd)
    {
        Board.Connect("VCC", Vcc.Output, vcc);
        Board.Connect("GND", Ground.Output, gnd);
    }

    private void WireClockAndReset()
    {
        Board.Connect("MASTER.CLK", MasterClock.Output, Cpu.MasterClock, Ppu.Clock);
        Board.Connect("CIC.RESET_BAR", ResetSource.Output, CicResetBar);
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
        Board.Connect("CIC.CLK", CicClock.Output, CicClockPin);
        Board.Connect("CIC.SEED", CicSeed.Output, CicSeedPin);
        Board.Connect("CIC.CONFIG", CicConfig.Output, CicConfigPin);

        HostResetNet = Board.Connect("SYSTEM.RESET_BAR", CicHostResetBar, Cpu.ResetBar, Ppu.ResetBar);
        CicSlaveResetNet = Board.Connect("SLOT.CIC.SLAVE_RESET_BAR", CicSlaveResetBar);
        CicDataToCartridgeNet = Board.Connect("SLOT.CIC.DATA_OUT", CicDataOut);
        CicDataFromCartridgeNet = Board.Connect("SLOT.CIC.DATA_IN", CicDataIn);

        Board.Connect("GND", CicNc5, CicNc11, CicNc12, CicNc13, CicNc14, CicNc15);
    }

    private DigitalPin CicVcc => Cic3195?.Vcc ?? Cic3197!.Vcc;
    private DigitalPin CicGnd => Cic3195?.Gnd ?? Cic3197!.Gnd;
    private DigitalPin CicClockPin => Cic3195?.Clock ?? Cic3197!.Clock;
    private DigitalPin CicResetBar => Cic3195?.ResetBar ?? Cic3197!.ResetBar;
    private DigitalPin CicSeedPin => Cic3195?.Seed ?? Cic3197!.Seed;
    private DigitalPin CicConfigPin => Cic3195?.Config ?? Cic3197!.Config;
    private DigitalPin CicHostResetBar => Cic3195?.HostResetBar ?? Cic3197!.HostResetBar;
    private DigitalPin CicSlaveResetBar => Cic3195?.SlaveResetBar ?? Cic3197!.SlaveResetBar;
    private DigitalPin CicDataOut => Cic3195?.DataOut ?? Cic3197!.DataOut;
    private DigitalPin CicDataIn => Cic3195?.DataIn ?? Cic3197!.DataIn;
    private DigitalPin CicNc5 => Cic3195?.Nc5 ?? Cic3197!.Nc5;
    private DigitalPin CicNc11 => Cic3195?.Nc11 ?? Cic3197!.Nc11;
    private DigitalPin CicNc12 => Cic3195?.Nc12 ?? Cic3197!.Nc12;
    private DigitalPin CicNc13 => Cic3195?.Nc13 ?? Cic3197!.Nc13;
    private DigitalPin CicNc14 => Cic3195?.Nc14 ?? Cic3197!.Nc14;
    private DigitalPin CicNc15 => Cic3195?.Nc15 ?? Cic3197!.Nc15;

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
