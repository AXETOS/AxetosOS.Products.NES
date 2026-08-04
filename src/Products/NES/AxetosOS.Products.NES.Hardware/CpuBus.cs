using AxetosOS.Products.NES.Abstractions;

namespace AxetosOS.Products.NES.Hardware;

public sealed class CpuBus : IInspectableBusModule
{
    private readonly List<ICpuBusDevice> _devices = [];
    private byte _openBus;
    private ulong _transactionSequence;
    private BusTransactionSnapshot _lastTransaction;

    public string ModuleId => "nes.bus.cpu";
    public int AddressWidthBits => 16;
    public int DataWidthBits => 8;
    public IReadOnlyList<ICpuBusDevice> Devices => _devices;
    public IReadOnlyList<object> AttachedDevices => _devices;
    public byte OpenBus => _openBus;
    public ulong CpuCycle { get; private set; }
    public BusTransactionSnapshot LastTransaction => _lastTransaction;

    public void SetCpuCycle(ulong cpuCycle) => CpuCycle = cpuCycle;

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
        object? primaryDevice = null;
        var participantCount = 0;

        foreach (var device in _devices)
        {
            if (!device.HandlesCpuAddress(address))
            {
                continue;
            }

            primaryDevice = device;
            participantCount = 1;
            _openBus = device.CpuRead(address);
            break;
        }

        _lastTransaction = new BusTransactionSnapshot(
            ++_transactionSequence,
            address,
            _openBus,
            BusAccessDirection.Read,
            primaryDevice,
            participantCount,
            CpuCycle);
        return _openBus;
    }

    public void Write(ushort address, byte value)
    {
        _openBus = value;
        object? primaryDevice = null;
        var participantCount = 0;

        // CPU reads are selected by the first matching device, but writes may target
        // multiple chips on the same decoded address. The NES notably uses $4017
        // for controller-port reads and APU frame-counter writes.
        foreach (var device in _devices)
        {
            if (!device.HandlesCpuAddress(address))
                continue;

            primaryDevice ??= device;
            participantCount++;
            if (device is ICpuCycleAwareBusDevice cycleAware)
                cycleAware.CpuWrite(address, value, CpuCycle);
            else
                device.CpuWrite(address, value);
        }

        _lastTransaction = new BusTransactionSnapshot(
            ++_transactionSequence,
            address,
            value,
            BusAccessDirection.Write,
            primaryDevice,
            participantCount,
            CpuCycle);
    }

    public void PowerOn() => ClearInspectionState();

    public void Reset() => ClearInspectionState();

    private void ClearInspectionState()
    {
        _openBus = 0;
        CpuCycle = 0;
        _transactionSequence = 0;
        _lastTransaction = default;
    }
}
