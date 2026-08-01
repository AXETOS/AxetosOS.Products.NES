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
        foreach (var device in _devices)
        {
            if (device.HandlesCpuAddress(address))
            {
                device.CpuWrite(address, value);
                return;
            }
        }
    }
}
