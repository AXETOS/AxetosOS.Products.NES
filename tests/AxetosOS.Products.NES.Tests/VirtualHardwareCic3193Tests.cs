using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Simulation;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareCic3193Tests
{
    [Fact]
    public void Package_releases_slave_then_host_reset_from_external_clock_edges()
    {
        var fixture = new Fixture();

        Assert.Equal(DigitalLevel.Low, fixture.Chip.SlaveResetBar.DriveLevel);
        Assert.Equal(DigitalLevel.Low, fixture.Chip.HostResetBar.DriveLevel);

        fixture.Pulse(2);
        Assert.Equal(DigitalLevel.High, fixture.Chip.SlaveResetBar.DriveLevel);
        Assert.Equal(DigitalLevel.Low, fixture.Chip.HostResetBar.DriveLevel);

        fixture.Pulse(2);
        Assert.True(fixture.Chip.StartupComplete);
        Assert.Equal(DigitalLevel.High, fixture.Chip.HostResetBar.DriveLevel);
    }

    [Fact]
    public void Serial_interface_shifts_one_nibble_only_from_package_pins()
    {
        var fixture = new Fixture();
        fixture.Pulse(4);

        var outputBits = new List<int>();
        foreach (var inputBit in new[] { 1, 1, 0, 1 })
        {
            outputBits.Add(fixture.Chip.DataOut.DriveLevel == DigitalLevel.High ? 1 : 0);
            fixture.DataIn.Set(inputBit == 1 ? DigitalLevel.High : DigitalLevel.Low);
            fixture.Pulse(1);
        }

        Assert.Equal(BitsOf(fixture.FirstChallenge), outputBits);
        Assert.Equal((byte)0b1101, fixture.Chip.LastReceivedNibble);
        Assert.Equal(1UL, fixture.Chip.CompletedSerialNibbleCount);
    }

    [Fact]
    public void Sixteen_valid_response_nibbles_complete_initial_authentication()
    {
        var fixture = new Fixture();
        fixture.Pulse(4);

        Assert.Equal(Cic3193AuthenticationState.Authenticating, fixture.Chip.AuthenticationState);

        for (var round = 0; round < 16; round++)
        {
            fixture.SendExpectedResponse();
        }

        Assert.Equal(Cic3193AuthenticationState.Authenticated, fixture.Chip.AuthenticationState);
        Assert.Equal(1UL, fixture.Chip.SuccessfulAuthenticationCount);
        Assert.Equal(16UL, fixture.Chip.CompletedAuthenticationRoundCount);
        Assert.Equal(0UL, fixture.Chip.FailedAuthenticationCount);
        Assert.Equal(DigitalLevel.High, fixture.Chip.HostResetBar.DriveLevel);
    }

    [Fact]
    public void Authenticated_link_is_verified_continuously_for_many_rounds()
    {
        var fixture = new Fixture();
        fixture.Pulse(4);

        for (var round = 0; round < 16 + 128; round++)
        {
            fixture.SendExpectedResponse();
        }

        Assert.Equal(Cic3193AuthenticationState.Authenticated, fixture.Chip.AuthenticationState);
        Assert.Equal(144UL, fixture.Chip.CompletedAuthenticationRoundCount);
        Assert.Equal(1UL, fixture.Chip.SuccessfulAuthenticationCount);
        Assert.Equal(0UL, fixture.Chip.FailedAuthenticationCount);
        Assert.Equal(0UL, fixture.Chip.HostResetPulseCount);
    }

    [Fact]
    public void Invalid_response_asserts_host_reset_then_restarts_from_captured_seed()
    {
        var fixture = new Fixture();
        fixture.Pulse(4);
        var firstChallenge = fixture.Chip.CurrentChallengeNibble;

        fixture.SendNibble((byte)(fixture.Chip.ExpectedResponseNibble ^ 0x01));

        Assert.Equal(Cic3193AuthenticationState.RetryHold, fixture.Chip.AuthenticationState);
        Assert.Equal(1UL, fixture.Chip.FailedAuthenticationCount);
        Assert.Equal(1UL, fixture.Chip.HostResetPulseCount);
        Assert.Equal(DigitalLevel.Low, fixture.Chip.HostResetBar.DriveLevel);
        Assert.Equal(DigitalLevel.High, fixture.Chip.SlaveResetBar.DriveLevel);

        fixture.Pulse(7);
        Assert.Equal(Cic3193AuthenticationState.RetryHold, fixture.Chip.AuthenticationState);

        fixture.Pulse(1);
        Assert.Equal(Cic3193AuthenticationState.Authenticating, fixture.Chip.AuthenticationState);
        Assert.Equal(0, fixture.Chip.AuthenticationRound);
        Assert.Equal(firstChallenge, fixture.Chip.CurrentChallengeNibble);
        Assert.Equal(DigitalLevel.High, fixture.Chip.HostResetBar.DriveLevel);
    }

    [Fact]
    public void Failure_during_continuous_verification_reasserts_host_reset()
    {
        var fixture = new Fixture();
        fixture.Pulse(4);
        for (var round = 0; round < 20; round++)
        {
            fixture.SendExpectedResponse();
        }

        Assert.Equal(Cic3193AuthenticationState.Authenticated, fixture.Chip.AuthenticationState);
        fixture.SendNibble((byte)(fixture.Chip.ExpectedResponseNibble ^ 0x08));

        Assert.Equal(Cic3193AuthenticationState.RetryHold, fixture.Chip.AuthenticationState);
        Assert.Equal(DigitalLevel.Low, fixture.Chip.HostResetBar.DriveLevel);
        Assert.Equal(1UL, fixture.Chip.FailedAuthenticationCount);
    }

    [Fact]
    public void Seed_and_configuration_are_sampled_during_startup_not_continuously()
    {
        var fixture = new Fixture(seed: DigitalLevel.High, config: DigitalLevel.Low);
        fixture.Pulse(1);

        Assert.True(fixture.Chip.SeedHigh);
        Assert.True(fixture.Chip.NtscOnlyMode);

        fixture.Seed.Set(DigitalLevel.Low);
        fixture.Config.Set(DigitalLevel.High);
        fixture.Pulse(3);

        Assert.True(fixture.Chip.SeedHigh);
        Assert.True(fixture.Chip.NtscOnlyMode);
        var originalChallenge = fixture.Chip.CurrentChallengeNibble;

        fixture.SendNibble((byte)(fixture.Chip.ExpectedResponseNibble ^ 1));
        fixture.Pulse(8);

        Assert.Equal(originalChallenge, fixture.Chip.CurrentChallengeNibble);
    }

    [Fact]
    public void External_reset_immediately_owns_both_reset_outputs_and_restarts_startup()
    {
        var fixture = new Fixture();
        fixture.Pulse(4);
        fixture.SendExpectedResponse();

        fixture.Reset.Set(DigitalLevel.Low);
        fixture.Settle();

        Assert.True(fixture.Chip.ResetAsserted);
        Assert.False(fixture.Chip.StartupComplete);
        Assert.Equal(DigitalLevel.Low, fixture.Chip.HostResetBar.DriveLevel);
        Assert.Equal(DigitalLevel.Low, fixture.Chip.SlaveResetBar.DriveLevel);
        Assert.Equal(1UL, fixture.Chip.ExternalResetCount);

        fixture.Reset.Set(DigitalLevel.High);
        fixture.Settle();
        fixture.Pulse(4);

        Assert.True(fixture.Chip.StartupComplete);
        Assert.Equal(Cic3193AuthenticationState.Authenticating, fixture.Chip.AuthenticationState);
        Assert.Equal(0, fixture.Chip.AuthenticationRound);
    }

    [Fact]
    public void Power_loss_releases_every_output_pin_and_repower_requires_startup_again()
    {
        var fixture = new Fixture();
        fixture.Pulse(4);

        fixture.Vcc.Set(DigitalLevel.Low);
        fixture.Settle();

        Assert.False(fixture.Chip.Powered);
        Assert.Equal(DigitalLevel.HighImpedance, fixture.Chip.DataOut.DriveLevel);
        Assert.Equal(DigitalLevel.HighImpedance, fixture.Chip.HostResetBar.DriveLevel);
        Assert.Equal(DigitalLevel.HighImpedance, fixture.Chip.SlaveResetBar.DriveLevel);

        fixture.Vcc.Set(DigitalLevel.High);
        fixture.Settle();

        Assert.Equal(DigitalLevel.Low, fixture.Chip.HostResetBar.DriveLevel);
        Assert.Equal(DigitalLevel.Low, fixture.Chip.SlaveResetBar.DriveLevel);
    }

    private static int[] BitsOf(byte value) =>
        new[]
        {
            (value >> 3) & 1,
            (value >> 2) & 1,
            (value >> 1) & 1,
            value & 1
        };

    private sealed class Fixture
    {
        private readonly VirtualHardwareSimulator _sim;
        private readonly DigitalSignalSource _clock;

        public Fixture(
            DigitalLevel seed = DigitalLevel.High,
            DigitalLevel config = DigitalLevel.Low)
        {
            var board = new VirtualHardwareBoard("cic3193-standalone");
            Chip = board.Add(new Cic3193("U1"));
            Vcc = board.Add(new DigitalSignalSource("vcc", DigitalLevel.High));
            var gnd = board.Add(new DigitalSignalSource("gnd", DigitalLevel.Low));
            Reset = board.Add(new DigitalSignalSource("reset", DigitalLevel.High));
            _clock = board.Add(new DigitalSignalSource("clock", DigitalLevel.Low));
            DataIn = board.Add(new DigitalSignalSource("data-in", DigitalLevel.Low));
            Seed = board.Add(new DigitalSignalSource("seed", seed));
            Config = board.Add(new DigitalSignalSource("config", config));

            board.Connect("VCC", Vcc.Output, Chip.Vcc);
            board.Connect("GND", gnd.Output, Chip.Gnd);
            board.Connect("RESET", Reset.Output, Chip.ResetBar);
            board.Connect("CLK", _clock.Output, Chip.Clock);
            board.Connect("DATA-IN", DataIn.Output, Chip.DataIn);
            board.Connect("SEED", Seed.Output, Chip.Seed);
            board.Connect("CONFIG", Config.Output, Chip.Config);
            board.Connect("DATA-OUT", Chip.DataOut);
            board.Connect("HOST-RESET", Chip.HostResetBar);
            board.Connect("SLAVE-RESET", Chip.SlaveResetBar);

            _sim = new VirtualHardwareSimulator(board);
            board.PowerOn();
            _sim.Settle();
        }

        public Cic3193 Chip { get; }
        public DigitalSignalSource Vcc { get; }
        public DigitalSignalSource Reset { get; }
        public DigitalSignalSource DataIn { get; }
        public DigitalSignalSource Seed { get; }
        public DigitalSignalSource Config { get; }
        public byte FirstChallenge => Chip.CurrentChallengeNibble;

        public void Pulse(int count)
        {
            for (var index = 0; index < count; index++)
            {
                _clock.Set(DigitalLevel.High);
                _sim.Settle();
                _clock.Set(DigitalLevel.Low);
                _sim.Settle();
            }
        }

        public void SendExpectedResponse() => SendNibble(Chip.ExpectedResponseNibble);

        public void SendNibble(byte value)
        {
            for (var bit = 3; bit >= 0; bit--)
            {
                DataIn.Set(((value >> bit) & 1) != 0 ? DigitalLevel.High : DigitalLevel.Low);
                Pulse(1);
            }
        }

        public void Settle() => _sim.Settle();
    }
}
