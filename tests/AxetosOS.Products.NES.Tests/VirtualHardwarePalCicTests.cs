using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwarePalCicTests
{
    [Fact]
    public void Cic3195_completes_startup_and_sixteen_round_authentication_from_package_pins()
    {
        var fixture = new Cic3195Fixture();
        fixture.Pulse(4);

        Assert.True(fixture.Chip.StartupComplete);
        Assert.True(fixture.Chip.PalAOnlyMode);
        Assert.Equal(Cic3195AuthenticationState.Authenticating, fixture.Chip.AuthenticationState);

        for (var round = 0; round < 16; round++)
        {
            fixture.SendExpectedResponse();
        }

        Assert.Equal(Cic3195AuthenticationState.Authenticated, fixture.Chip.AuthenticationState);
        Assert.Equal(1UL, fixture.Chip.SuccessfulAuthenticationCount);
        Assert.Equal(16UL, fixture.Chip.CompletedAuthenticationRoundCount);
        Assert.Equal(DigitalLevel.High, fixture.Chip.HostResetBar.DriveLevel);
    }

    [Fact]
    public void Cic3197_completes_startup_and_sixteen_round_authentication_from_package_pins()
    {
        var fixture = new Cic3197Fixture();
        fixture.Pulse(4);

        Assert.True(fixture.Chip.StartupComplete);
        Assert.True(fixture.Chip.PalBOnlyMode);
        Assert.Equal(Cic3197AuthenticationState.Authenticating, fixture.Chip.AuthenticationState);

        for (var round = 0; round < 16; round++)
        {
            fixture.SendExpectedResponse();
        }

        Assert.Equal(Cic3197AuthenticationState.Authenticated, fixture.Chip.AuthenticationState);
        Assert.Equal(1UL, fixture.Chip.SuccessfulAuthenticationCount);
        Assert.Equal(16UL, fixture.Chip.CompletedAuthenticationRoundCount);
        Assert.Equal(DigitalLevel.High, fixture.Chip.HostResetBar.DriveLevel);
    }

    [Fact]
    public void Pal_A_and_Pal_B_packages_generate_distinct_region_streams()
    {
        var palA = new Cic3195Fixture();
        var palB = new Cic3197Fixture();
        palA.Pulse(4);
        palB.Pulse(4);

        Assert.NotEqual(palA.Chip.CurrentChallengeNibble, palB.Chip.CurrentChallengeNibble);
        Assert.NotEqual(palA.Chip.ExpectedResponseNibble, palB.Chip.ExpectedResponseNibble);
    }

    [Fact]
    public void Cic3195_rejects_a_Cic3197_response_and_holds_host_reset_for_eight_clocks()
    {
        var palA = new Cic3195Fixture();
        var palB = new Cic3197Fixture();
        palA.Pulse(4);
        palB.Pulse(4);

        palA.SendNibble(palB.Chip.ExpectedResponseNibble);

        Assert.Equal(Cic3195AuthenticationState.RetryHold, palA.Chip.AuthenticationState);
        Assert.Equal(DigitalLevel.Low, palA.Chip.HostResetBar.DriveLevel);
        Assert.Equal(1UL, palA.Chip.FailedAuthenticationCount);

        palA.Pulse(7);
        Assert.Equal(Cic3195AuthenticationState.RetryHold, palA.Chip.AuthenticationState);
        palA.Pulse(1);
        Assert.Equal(Cic3195AuthenticationState.Authenticating, palA.Chip.AuthenticationState);
    }

    [Fact]
    public void Cic3197_external_reset_and_power_loss_restart_the_protocol_electrically()
    {
        var fixture = new Cic3197Fixture();
        fixture.Pulse(4);
        fixture.SendExpectedResponse();

        fixture.Reset.Set(DigitalLevel.Low);
        fixture.Settle();
        Assert.Equal(DigitalLevel.Low, fixture.Chip.HostResetBar.DriveLevel);
        Assert.Equal(DigitalLevel.Low, fixture.Chip.SlaveResetBar.DriveLevel);
        Assert.False(fixture.Chip.StartupComplete);

        fixture.Reset.Set(DigitalLevel.High);
        fixture.Settle();
        fixture.Pulse(4);
        Assert.True(fixture.Chip.StartupComplete);

        fixture.Vcc.Set(DigitalLevel.Low);
        fixture.Settle();
        Assert.Equal(DigitalLevel.HighImpedance, fixture.Chip.DataOut.DriveLevel);
        Assert.Equal(DigitalLevel.HighImpedance, fixture.Chip.HostResetBar.DriveLevel);
        Assert.Equal(DigitalLevel.HighImpedance, fixture.Chip.SlaveResetBar.DriveLevel);
    }

    private abstract class FixtureBase
    {
        protected FixtureBase(
            VirtualHardwareBoard board,
            DigitalSignalSource clock,
            DigitalSignalSource dataIn,
            DigitalSignalSource reset,
            DigitalSignalSource vcc)
        {
            Clock = clock;
            DataIn = dataIn;
            Reset = reset;
            Vcc = vcc;
            Simulator = new VirtualHardwareSimulator(board);
            board.PowerOn();
            Simulator.Settle();
        }

        protected VirtualHardwareSimulator Simulator { get; }
        protected DigitalSignalSource Clock { get; }
        protected DigitalSignalSource DataIn { get; }
        public DigitalSignalSource Reset { get; }
        public DigitalSignalSource Vcc { get; }

        public void Pulse(int count)
        {
            for (var index = 0; index < count; index++)
            {
                Clock.Set(DigitalLevel.High);
                Simulator.Settle();
                Clock.Set(DigitalLevel.Low);
                Simulator.Settle();
            }
        }

        public void SendNibble(byte value)
        {
            for (var bit = 3; bit >= 0; bit--)
            {
                DataIn.Set(((value >> bit) & 1) != 0 ? DigitalLevel.High : DigitalLevel.Low);
                Pulse(1);
            }
        }

        public void Settle() => Simulator.Settle();
    }

    private sealed class Cic3195Fixture : FixtureBase
    {
        public Cic3195Fixture() : this(Create()) { }

        private Cic3195Fixture(Parts parts)
            : base(parts.Board, parts.Clock, parts.DataIn, parts.Reset, parts.Vcc)
        {
            Chip = parts.Chip;
        }

        public Cic3195 Chip { get; }
        public void SendExpectedResponse() => SendNibble(Chip.ExpectedResponseNibble);

        private static Parts Create()
        {
            var board = new VirtualHardwareBoard("cic3195-standalone");
            var chip = board.Add(new Cic3195("U1"));
            var vcc = board.Add(new DigitalSignalSource("vcc", DigitalLevel.High));
            var gnd = board.Add(new DigitalSignalSource("gnd", DigitalLevel.Low));
            var reset = board.Add(new DigitalSignalSource("reset", DigitalLevel.High));
            var clock = board.Add(new DigitalSignalSource("clock", DigitalLevel.Low));
            var dataIn = board.Add(new DigitalSignalSource("data-in", DigitalLevel.Low));
            var seed = board.Add(new DigitalSignalSource("seed", DigitalLevel.High));
            var config = board.Add(new DigitalSignalSource("config", DigitalLevel.Low));

            Connect(board, chip.Vcc, chip.Gnd, chip.ResetBar, chip.Clock, chip.DataIn, chip.Seed, chip.Config,
                chip.DataOut, chip.HostResetBar, chip.SlaveResetBar,
                vcc, gnd, reset, clock, dataIn, seed, config);
            return new Parts(board, chip, clock, dataIn, reset, vcc);
        }

        private sealed record Parts(VirtualHardwareBoard Board, Cic3195 Chip, DigitalSignalSource Clock,
            DigitalSignalSource DataIn, DigitalSignalSource Reset, DigitalSignalSource Vcc);
    }

    private sealed class Cic3197Fixture : FixtureBase
    {
        public Cic3197Fixture() : this(Create()) { }

        private Cic3197Fixture(Parts parts)
            : base(parts.Board, parts.Clock, parts.DataIn, parts.Reset, parts.Vcc)
        {
            Chip = parts.Chip;
        }

        public Cic3197 Chip { get; }
        public void SendExpectedResponse() => SendNibble(Chip.ExpectedResponseNibble);

        private static Parts Create()
        {
            var board = new VirtualHardwareBoard("cic3197-standalone");
            var chip = board.Add(new Cic3197("U1"));
            var vcc = board.Add(new DigitalSignalSource("vcc", DigitalLevel.High));
            var gnd = board.Add(new DigitalSignalSource("gnd", DigitalLevel.Low));
            var reset = board.Add(new DigitalSignalSource("reset", DigitalLevel.High));
            var clock = board.Add(new DigitalSignalSource("clock", DigitalLevel.Low));
            var dataIn = board.Add(new DigitalSignalSource("data-in", DigitalLevel.Low));
            var seed = board.Add(new DigitalSignalSource("seed", DigitalLevel.High));
            var config = board.Add(new DigitalSignalSource("config", DigitalLevel.Low));

            Connect(board, chip.Vcc, chip.Gnd, chip.ResetBar, chip.Clock, chip.DataIn, chip.Seed, chip.Config,
                chip.DataOut, chip.HostResetBar, chip.SlaveResetBar,
                vcc, gnd, reset, clock, dataIn, seed, config);
            return new Parts(board, chip, clock, dataIn, reset, vcc);
        }

        private sealed record Parts(VirtualHardwareBoard Board, Cic3197 Chip, DigitalSignalSource Clock,
            DigitalSignalSource DataIn, DigitalSignalSource Reset, DigitalSignalSource Vcc);
    }

    private static void Connect(
        VirtualHardwareBoard board,
        DigitalPin chipVcc,
        DigitalPin chipGnd,
        DigitalPin chipReset,
        DigitalPin chipClock,
        DigitalPin chipDataIn,
        DigitalPin chipSeed,
        DigitalPin chipConfig,
        DigitalPin chipDataOut,
        DigitalPin chipHostReset,
        DigitalPin chipSlaveReset,
        DigitalSignalSource vcc,
        DigitalSignalSource gnd,
        DigitalSignalSource reset,
        DigitalSignalSource clock,
        DigitalSignalSource dataIn,
        DigitalSignalSource seed,
        DigitalSignalSource config)
    {
        board.Connect("VCC", vcc.Output, chipVcc);
        board.Connect("GND", gnd.Output, chipGnd);
        board.Connect("RESET", reset.Output, chipReset);
        board.Connect("CLK", clock.Output, chipClock);
        board.Connect("DATA-IN", dataIn.Output, chipDataIn);
        board.Connect("SEED", seed.Output, chipSeed);
        board.Connect("CONFIG", config.Output, chipConfig);
        board.Connect("DATA-OUT", chipDataOut);
        board.Connect("HOST-RESET", chipHostReset);
        board.Connect("SLAVE-RESET", chipSlaveReset);
    }
}
