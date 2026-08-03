using AxetosOS.Products.NES.Abstractions;

namespace AxetosOS.Products.NES.Hardware;

public sealed class PpuBus
{
    private readonly List<IPpuBusDevice> _devices = [];
    private readonly List<IPpuScanlineClock> _scanlineClocks = [];
    private byte _openBus;

    public IReadOnlyList<IPpuBusDevice> Devices => _devices;
    public byte OpenBus => _openBus;

    public void Attach(IPpuBusDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (_devices.Contains(device))
        {
            throw new InvalidOperationException("The PPU bus device is already attached.");
        }

        _devices.Add(device);
        if (device is IPpuScanlineClock scanlineClock)
            _scanlineClocks.Add(scanlineClock);
    }

    public byte Read(ushort address)
    {
        address = Normalize(address);
        foreach (var device in _devices)
        {
            if (!device.HandlesPpuAddress(address))
            {
                continue;
            }

            _openBus = device.PpuRead(address);
            return _openBus;
        }

        return _openBus;
    }

    public void Write(ushort address, byte value)
    {
        address = Normalize(address);
        _openBus = value;
        foreach (var device in _devices)
        {
            if (device.HandlesPpuAddress(address))
            {
                device.PpuWrite(address, value);
                return;
            }
        }
    }

    public void ClockScanline()
    {
        for (var index = 0; index < _scanlineClocks.Count; index++)
            _scanlineClocks[index].ClockScanline();
    }

    private static ushort Normalize(ushort address) => (ushort)(address & 0x3FFF);
}
