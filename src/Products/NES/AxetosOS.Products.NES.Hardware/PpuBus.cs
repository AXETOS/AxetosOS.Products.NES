using AxetosOS.Products.NES.Abstractions;

namespace AxetosOS.Products.NES.Hardware;

public sealed class PpuBus : IInspectableBusModule
{
    private readonly List<IPpuBusDevice> _devices = [];
    private readonly List<IPpuScanlineClock> _scanlineClocks = [];
    private byte _openBus;
    private ulong _transactionSequence;
    private BusTransactionSnapshot _lastTransaction;

    public string ModuleId => "nes.bus.ppu";
    public int AddressWidthBits => 14;
    public int DataWidthBits => 8;
    public IReadOnlyList<IPpuBusDevice> Devices => _devices;
    public IReadOnlyList<object> AttachedDevices => _devices;
    public byte OpenBus => _openBus;
    public BusTransactionSnapshot LastTransaction => _lastTransaction;

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
        object? primaryDevice = null;
        var participantCount = 0;

        foreach (var device in _devices)
        {
            if (!device.HandlesPpuAddress(address))
            {
                continue;
            }

            primaryDevice = device;
            participantCount = 1;
            _openBus = device.PpuRead(address);
            break;
        }

        _lastTransaction = new BusTransactionSnapshot(
            ++_transactionSequence,
            address,
            _openBus,
            BusAccessDirection.Read,
            primaryDevice,
            participantCount,
            0);
        return _openBus;
    }

    public void Write(ushort address, byte value)
    {
        address = Normalize(address);
        _openBus = value;
        object? primaryDevice = null;
        var participantCount = 0;

        foreach (var device in _devices)
        {
            if (!device.HandlesPpuAddress(address))
                continue;

            primaryDevice = device;
            participantCount = 1;
            device.PpuWrite(address, value);
            break;
        }

        _lastTransaction = new BusTransactionSnapshot(
            ++_transactionSequence,
            address,
            value,
            BusAccessDirection.Write,
            primaryDevice,
            participantCount,
            0);
    }

    public void ClockScanline()
    {
        for (var index = 0; index < _scanlineClocks.Count; index++)
            _scanlineClocks[index].ClockScanline();
    }

    public void PowerOn() => ClearInspectionState();

    public void Reset() => ClearInspectionState();

    private void ClearInspectionState()
    {
        _openBus = 0;
        _transactionSequence = 0;
        _lastTransaction = default;
    }

    private static ushort Normalize(ushort address) => (ushort)(address & 0x3FFF);
}
