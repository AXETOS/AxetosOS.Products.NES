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

        Assert.Equal(new[] { 1, 0, 1, 0 }, outputBits);
        Assert.Equal((byte)0b1101, fixture.Chip.LastReceivedNibble);
        Assert.Equal(1UL, fixture.Chip.CompletedSerialNibbleCount);
    }


    [Fact]
    public void Four_valid_response_nibbles_authenticate_through_the_serial_pins()
    {
        var fixture = new Fixture();
        fixture.Pulse(4);

        Assert.Equal(Cic3193AuthenticationState.Authenticating, fixture.Chip.AuthenticationState);

        for (var round = 0; round < 4; round++)
        {
            var challenge = (byte)((0x0A ^ ((round * 0x03) & 0x0F)) & 0x0F);
            var rotated = ((challenge << 1) | (challenge >> 3)) & 0x0F;
            var response = (byte)((rotated ^ 0x09 ^ round) & 0x0F);
            fixture.SendNibble(response);
        }

        Assert.Equal(Cic3193AuthenticationState.Authenticated, fixture.Chip.AuthenticationState);
        Assert.Equal(1UL, fixture.Chip.SuccessfulAuthenticationCount);
        Assert.Equal(0UL, fixture.Chip.FailedAuthenticationCount);
        Assert.Equal(DigitalLevel.High, fixture.Chip.HostResetBar.DriveLevel);
    }

    [Fact]
    public void Invalid_response_asserts_host_reset_then_restarts_authentication()
    {
        var fixture = new Fixture();
        fixture.Pulse(4);

        fixture.SendNibble(0x00);

        Assert.Equal(Cic3193AuthenticationState.RetryHold, fixture.Chip.AuthenticationState);
        Assert.Equal(1UL, fixture.Chip.FailedAuthenticationCount);
        Assert.Equal(1UL, fixture.Chip.HostResetPulseCount);
        Assert.Equal(DigitalLevel.Low, fixture.Chip.HostResetBar.DriveLevel);
        Assert.Equal(DigitalLevel.High, fixture.Chip.SlaveResetBar.DriveLevel);

        fixture.Pulse(7);
        Assert.Equal(Cic3193AuthenticationState.RetryHold, fixture.Chip.AuthenticationState);
        Assert.Equal(DigitalLevel.Low, fixture.Chip.HostResetBar.DriveLevel);

        fixture.Pulse(1);
        Assert.Equal(Cic3193AuthenticationState.Authenticating, fixture.Chip.AuthenticationState);
        Assert.Equal(0, fixture.Chip.AuthenticationRound);
        Assert.Equal(DigitalLevel.High, fixture.Chip.HostResetBar.DriveLevel);
    }

    [Fact]
    public void Power_loss_releases_every_output_pin()
    {
        var fixture = new Fixture();
        fixture.Pulse(4);

        fixture.Vcc.Set(DigitalLevel.Low);
        fixture.Settle();

        Assert.False(fixture.Chip.Powered);
        Assert.Equal(DigitalLevel.HighImpedance, fixture.Chip.DataOut.DriveLevel);
        Assert.Equal(DigitalLevel.HighImpedance, fixture.Chip.HostResetBar.DriveLevel);
        Assert.Equal(DigitalLevel.HighImpedance, fixture.Chip.SlaveResetBar.DriveLevel);
    }

    private sealed class Fixture
    {
        private readonly VirtualHardwareSimulator _sim;
        private readonly DigitalSignalSource _clock;

        public Fixture()
        {
            var board = new VirtualHardwareBoard("cic3193-standalone");
            Chip = board.Add(new Cic3193("U1"));
            Vcc = board.Add(new DigitalSignalSource("vcc", DigitalLevel.High));
            var gnd = board.Add(new DigitalSignalSource("gnd", DigitalLevel.Low));
            var reset = board.Add(new DigitalSignalSource("reset", DigitalLevel.High));
            _clock = board.Add(new DigitalSignalSource("clock", DigitalLevel.Low));
            DataIn = board.Add(new DigitalSignalSource("data-in", DigitalLevel.Low));
            var seed = board.Add(new DigitalSignalSource("seed", DigitalLevel.High));
            var config = board.Add(new DigitalSignalSource("config", DigitalLevel.Low));

            board.Connect("VCC", Vcc.Output, Chip.Vcc);
            board.Connect("GND", gnd.Output, Chip.Gnd);
            board.Connect("RESET", reset.Output, Chip.ResetBar);
            board.Connect("CLK", _clock.Output, Chip.Clock);
            board.Connect("DATA-IN", DataIn.Output, Chip.DataIn);
            board.Connect("SEED", seed.Output, Chip.Seed);
            board.Connect("CONFIG", config.Output, Chip.Config);
            board.Connect("DATA-OUT", Chip.DataOut);
            board.Connect("HOST-RESET", Chip.HostResetBar);
            board.Connect("SLAVE-RESET", Chip.SlaveResetBar);

            _sim = new VirtualHardwareSimulator(board);
            board.PowerOn();
            _sim.Settle();
        }

        public Cic3193 Chip { get; }
        public DigitalSignalSource Vcc { get; }
        public DigitalSignalSource DataIn { get; }

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
