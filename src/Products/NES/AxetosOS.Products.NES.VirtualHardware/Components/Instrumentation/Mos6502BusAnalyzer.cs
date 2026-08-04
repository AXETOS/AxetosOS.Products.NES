using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;

/// <summary>
/// Passive pin-level analyzer for a 6502-style bus. It observes resolved nets
/// only and records one sample for each PHI2 rising edge.
/// </summary>
public sealed class Mos6502BusAnalyzer : VirtualHardwareComponent
{
    private readonly List<Mos6502BusCycle> _cycles = [];
    private DigitalLevel _previousClock = DigitalLevel.Low;
    private int _pendingDataCycleIndex = -1;
    private bool _pendingDataSettled;
    private bool _pendingOwnedByDma;

    public Mos6502BusAnalyzer(string componentId, int capacity = 4_096)
        : base(componentId)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        Capacity = capacity;
        var addressPins = new DigitalPin[16];
        var dataPins = new DigitalPin[8];
        for (var bit = 0; bit < addressPins.Length; bit++)
        {
            addressPins[bit] = AddPin($"A{bit}", PinDirection.Input);
        }

        for (var bit = 0; bit < dataPins.Length; bit++)
        {
            dataPins[bit] = AddPin($"D{bit}", PinDirection.Input);
        }

        Address = new DigitalBus($"{componentId}.A", addressPins);
        Data = new DigitalBus($"{componentId}.D", dataPins);
        ReadWrite = AddPin("R/W", PinDirection.Input);
        Sync = AddPin("SYNC", PinDirection.Input);
        Clock = AddPin("PHI2", PinDirection.Input);
        CpuBusEnable = AddPin("CPU_BUS_ENABLE", PinDirection.Input);
    }

    public int Capacity { get; }
    public DigitalBus Address { get; }
    public DigitalBus Data { get; }
    public DigitalPin ReadWrite { get; }
    public DigitalPin Sync { get; }
    public DigitalPin Clock { get; }
    public DigitalPin CpuBusEnable { get; }
    public IReadOnlyList<Mos6502BusCycle> Cycles => _cycles;
    public ulong ObservedRisingEdges { get; private set; }
    public ulong DroppedCycleCount { get; private set; }

    public override void PowerOn()
    {
        _cycles.Clear();
        _previousClock = DigitalLevel.Low;
        _pendingDataCycleIndex = -1;
        _pendingDataSettled = false;
        _pendingOwnedByDma = false;
        ObservedRisingEdges = 0;
        DroppedCycleCount = 0;
    }

    public override void Reset() => PowerOn();

    public override void Evaluate()
    {
        var currentClock = Clock.SampledLevel;
        var rising = _previousClock == DigitalLevel.Low && currentClock == DigitalLevel.High;
        if (currentClock is DigitalLevel.Low or DigitalLevel.High)
        {
            _previousClock = currentClock;
        }

        if (rising)
        {
            CaptureRisingEdge();
            return;
        }

        // Preserve the established edge-time address/control trace, but allow
        // memory another propagation pass to resolve read data. This enriches
        // the already-recorded cycle rather than shifting every observation by
        // one bus phase.
        if (_pendingDataCycleIndex < 0)
        {
            return;
        }

        // Keep the observation open for the complete PHI2-high interval. DMA
        // acquires and drives the bus through several simulator propagation
        // passes after the rising edge, so closing the sample after the first
        // pass can preserve the preceding CPU address instead of the settled
        // DMA address. Finalize only when PHI2 falls.
        if (currentClock == DigitalLevel.Low)
        {
            _pendingDataCycleIndex = -1;
            return;
        }

        if (currentClock != DigitalLevel.High)
        {
            return;
        }

        var cycle = _cycles[_pendingDataCycleIndex];

        // Bus ownership may change after PHI2 rises. Once DMA has electrically
        // disabled the CPU, follow the resolved DMA address/control/data for the
        // remainder of the high phase. Until then, preserve the CPU's edge-time
        // address/control and freeze the first valid data value. This prevents a
        // later CPU microstate or controller shift from rewriting an established
        // normal cycle while still allowing DMA several propagation passes to
        // acquire and settle the bus.
        if (CpuBusEnable.SampledLevel == DigitalLevel.Low)
        {
            _pendingOwnedByDma = true;
        }

        if (_pendingOwnedByDma)
        {
            if (Address.TrySample(out var settledAddress))
            {
                cycle = cycle with
                {
                    Address = (ushort)settledAddress,
                    IsRead = ReadWrite.SampledLevel == DigitalLevel.High,
                    IsOpcodeFetch = false
                };
            }

            if (Data.TrySample(out var dmaData))
            {
                cycle = cycle with { Data = (byte)dmaData };
            }
        }
        else if (!_pendingDataSettled && Data.TrySample(out var settledData))
        {
            cycle = cycle with { Data = (byte)settledData };
            _pendingDataSettled = true;
        }

        _cycles[_pendingDataCycleIndex] = cycle;
    }

    private void CaptureRisingEdge()
    {
        ObservedRisingEdges++;
        _pendingDataCycleIndex = -1;
        _pendingDataSettled = false;
        _pendingOwnedByDma = false;

        if (!Address.TrySample(out var rawAddress))
        {
            return;
        }

        var hasData = Data.TrySample(out var rawData);
        var cycle = new Mos6502BusCycle(
            ObservedRisingEdges,
            (ushort)rawAddress,
            hasData ? (byte)rawData : null,
            ReadWrite.SampledLevel == DigitalLevel.High,
            Sync.SampledLevel == DigitalLevel.High);

        if (_cycles.Count == Capacity)
        {
            _cycles.RemoveAt(0);
            DroppedCycleCount++;
        }

        _cycles.Add(cycle);
        _pendingDataCycleIndex = _cycles.Count - 1;
        _pendingDataSettled = hasData;
    }

    public void Clear()
    {
        _cycles.Clear();
        _pendingDataCycleIndex = -1;
        _pendingDataSettled = false;
        _pendingOwnedByDma = false;
        DroppedCycleCount = 0;
    }
}

public readonly record struct Mos6502BusCycle(
    ulong Sequence,
    ushort Address,
    byte? Data,
    bool IsRead,
    bool IsOpcodeFetch);
