using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Boards;

/// <summary>
/// Owns components and wiring only. Machine behavior is produced by connected
/// components reacting to resolved pin levels.
/// </summary>
public sealed class VirtualHardwareBoard
{
    private readonly List<IVirtualHardwareComponent> _components = [];
    private readonly List<DigitalNet> _nets = [];

    public VirtualHardwareBoard(string boardId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(boardId);
        BoardId = boardId;
    }

    public string BoardId { get; }
    public IReadOnlyList<IVirtualHardwareComponent> Components => _components;
    public IReadOnlyList<DigitalNet> Nets => _nets;
    public ulong TopologyRevision { get; private set; }

    public T Add<T>(T component) where T : IVirtualHardwareComponent
    {
        ArgumentNullException.ThrowIfNull(component);
        if (_components.Any(existing => existing.ComponentId == component.ComponentId))
        {
            throw new InvalidOperationException($"Component ID '{component.ComponentId}' already exists on board '{BoardId}'.");
        }

        _components.Add(component);
        TopologyRevision++;
        return component;
    }

    public DigitalNet AddNet(string name)
    {
        if (_nets.Any(existing => existing.Name == name))
        {
            throw new InvalidOperationException($"Net '{name}' already exists on board '{BoardId}'.");
        }

        var net = new DigitalNet(name);
        _nets.Add(net);
        TopologyRevision++;
        return net;
    }

    public DigitalNet Connect(string netName, params DigitalPin[] pins)
    {
        ArgumentNullException.ThrowIfNull(pins);
        if (pins.Length == 0)
        {
            throw new ArgumentException("At least one pin is required.", nameof(pins));
        }

        var net = _nets.FirstOrDefault(existing => existing.Name == netName) ?? AddNet(netName);
        var previousPinCount = net.Pins.Count;
        foreach (var pin in pins)
        {
            net.Connect(pin);
        }

        if (net.Pins.Count != previousPinCount)
        {
            TopologyRevision++;
        }

        return net;
    }

    public void PowerOn()
    {
        foreach (var component in _components)
        {
            component.PowerOn();
        }
    }

    public void Reset()
    {
        foreach (var component in _components)
        {
            component.Reset();
        }
    }
}
