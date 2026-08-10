using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;
using AxetosOS.Products.NES.VirtualHardware.Components.Nes;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Machines.Nes;

/// <summary>
/// Product-host boundary for two standard controller ports.  The adapter has no
/// access to CPU registers or controller internals: it owns generic external
/// digital sources whose package pins are physically connected to the existing
/// controller button traces.
/// </summary>
internal sealed class NesControllerHostInputAdapter
{
    private readonly DigitalExternalInputBank[] _ports;

    public NesControllerHostInputAdapter(
        string adapterId,
        VirtualHardwareBoard board,
        VirtualHardwareSimulator simulator,
        NesStandardController controller1,
        NesStandardController controller2)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(simulator);
        ArgumentNullException.ThrowIfNull(controller1);
        ArgumentNullException.ThrowIfNull(controller2);

        _ports =
        [
            board.Add(new DigitalExternalInputBank($"{adapterId}.PORT1", 8)),
            board.Add(new DigitalExternalInputBank($"{adapterId}.PORT2", 8))
        ];

        ConnectPort(board, _ports[0], controller1);
        ConnectPort(board, _ports[1], controller2);

        // The regional motherboard simulators are constructed before host-side
        // peripherals are attached. Recompile once here so raw propagation,
        // profiling and later whole-circuit compilation all see the same final
        // physical topology before any cartridge is inserted or power applied.
        simulator.RecompileTopology();
    }

    public void SetButton(int port, NesControllerButton button, bool pressed)
    {
        if ((uint)port >= (uint)_ports.Length) throw new ArgumentOutOfRangeException(nameof(port));
        var bit = (int)button;
        if ((uint)bit >= 8) throw new ArgumentOutOfRangeException(nameof(button));
        _ports[port].SetBit(bit, pressed);
    }

    public byte InspectButtons(int port)
    {
        if ((uint)port >= (uint)_ports.Length) throw new ArgumentOutOfRangeException(nameof(port));
        return (byte)_ports[port].Value;
    }

    private static void ConnectPort(
        VirtualHardwareBoard board,
        DigitalExternalInputBank source,
        NesStandardController controller)
    {
        for (var bit = 0; bit < 8; bit++)
        {
            var buttonPin = controller.Buttons.Pins[bit];
            var net = buttonPin.Net
                ?? throw new InvalidOperationException($"Controller button pin '{buttonPin.Name}' is not attached to a physical trace.");
            board.Connect(net.Name, source.Outputs.Pins[bit]);
        }
    }
}
