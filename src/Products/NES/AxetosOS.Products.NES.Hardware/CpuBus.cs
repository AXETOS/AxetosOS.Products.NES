using AxetosOS.Products.NES.Abstractions;

namespace AxetosOS.Products.NES.Hardware;

public sealed class CpuBus
{
    private readonly List<ICpuBusDevice> _devices = [];
    private byte _openBus;

    public IReadOnlyList<ICpuBusDevice> Devices => _devices;
    public byte OpenBus => _openBus;

    public void Attach(ICpuBusDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (_devices.Contains(device))
        {
            throw new InvalidOperationException("The CPU bus device is already attached.");
        }

        _devices.Add(device);
    }

    public byte Read(ushort address)
    {
        foreach (var device in _devices)
        {
            if (!device.HandlesCpuAddress(address))
            {
                continue;
            }

            _openBus = device.CpuRead(address);
            return _openBus;
        }

        return _openBus;
    }

    public void Write(ushort address, byte value)
    {
        _openBus = value;
        // CPU reads are selected by the first matching device, but writes may target
        // multiple chips on the same decoded address. The NES notably uses $4017
        // for controller-port reads and APU frame-counter writes.
        foreach (var device in _devices)
        {
            if (device.HandlesCpuAddress(address))
            {
                device.CpuWrite(address, value);
            }
        }
    }
}
