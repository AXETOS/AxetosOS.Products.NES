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

    public T Add<T>(T component) where T : IVirtualHardwareComponent
    {
        ArgumentNullException.ThrowIfNull(component);
        if (_components.Any(existing => existing.ComponentId == component.ComponentId))
        {
            throw new InvalidOperationException($"Component ID '{component.ComponentId}' already exists on board '{BoardId}'.");
        }

        _components.Add(component);
        return component;
    }

    public bool Remove(IVirtualHardwareComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (!_components.Contains(component)) return false;
        foreach (var pin in component.Pins) pin.Net?.Disconnect(pin);
        return _components.Remove(component);
    }

    public DigitalNet AddNet(string name)
    {
        if (_nets.Any(existing => existing.Name == name))
        {
            throw new InvalidOperationException($"Net '{name}' already exists on board '{BoardId}'.");
        }

        var net = new DigitalNet(name);
        _nets.Add(net);
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
        foreach (var pin in pins)
        {
            net.Connect(pin);
        }
        return net;
    }

    /// <summary>
    /// Applies the board's external rail, clock and switch source drives. Chip
    /// packages are never called; they can react only when the resulting net
    /// levels reach their input pins through the simulator.
    /// </summary>
    public void PowerOn()
    {
        foreach (var source in _components.OfType<IExternalBoardSource>())
        {
            source.ApplyPowerOnDrive();
        }
    }

}
