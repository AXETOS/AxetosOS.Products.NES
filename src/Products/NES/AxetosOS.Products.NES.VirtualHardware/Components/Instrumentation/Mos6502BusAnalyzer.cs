using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Instrumentation;

/// <summary>
/// Passive pin-level analyzer for a 6502-style bus. It observes resolved nets
/// only and records one sample for each PHI2 rising edge.
/// </summary>
public sealed class Mos6502BusAnalyzer : VirtualHardwareComponent
{
    private readonly Mos6502BusCycle[] _cycles;
    private readonly IReadOnlyList<Mos6502BusCycle> _cycleView;
    private readonly ulong _clockInputMask;
    private int _cycleStart;
    private int _cycleCount;
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
        _cycles = new Mos6502BusCycle[capacity];
        _cycleView = new CycleView(this);
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
        _clockInputMask = Clock.InputChangeMask;

        InitializePackageState();
    }

    public int Capacity { get; }
    public DigitalBus Address { get; }
    public DigitalBus Data { get; }
    public DigitalPin ReadWrite { get; }
    public DigitalPin Sync { get; }
    public DigitalPin Clock { get; }
    public DigitalPin CpuBusEnable { get; }
    public IReadOnlyList<Mos6502BusCycle> Cycles => _cycleView;
    public ulong ObservedRisingEdges { get; private set; }
    public ulong DroppedCycleCount { get; private set; }

    private void InitializePackageState()
    {
        _cycleStart = 0;
        _cycleCount = 0;
        _previousClock = DigitalLevel.Low;
        _pendingDataCycleIndex = -1;
        _pendingDataSettled = false;
        _pendingOwnedByDma = false;
        ObservedRisingEdges = 0;
        DroppedCycleCount = 0;
    }

    protected override void OnInputChanges(ulong changedInputMask)
    {
        var clockChanged = (changedInputMask & _clockInputMask) != 0;

        // Until PHI2 creates a cycle there is nothing for address/data traffic
        // to enrich. The pins still receive every electrical transition.
        if (_pendingDataCycleIndex < 0 && !clockChanged) return;

        var currentClock = Clock.SampledLevel;
        if (clockChanged)
        {
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

            // Keep the observation open for the complete PHI2-high interval;
            // the falling edge is the exact point at which it can be closed.
            if (currentClock == DigitalLevel.Low)
            {
                _pendingDataCycleIndex = -1;
                return;
            }
        }

        if (_pendingDataCycleIndex < 0 || currentClock != DigitalLevel.High) return;

        var cycle = _cycles[_pendingDataCycleIndex];

        // Bus ownership may change after PHI2 rises. Once DMA has electrically
        // disabled the CPU, follow the resolved DMA address/control/data for the
        // remainder of the high phase. Until then preserve edge-time control and
        // freeze the first valid read datum.
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
        var isRead = ReadWrite.SampledLevel == DigitalLevel.High;
        var cycle = new Mos6502BusCycle(
            ObservedRisingEdges,
            (ushort)rawAddress,
            hasData ? (byte)rawData : null,
            isRead,
            Sync.SampledLevel == DigitalLevel.High);

        int physicalIndex;
        if (_cycleCount < Capacity)
        {
            physicalIndex = (_cycleStart + _cycleCount) % Capacity;
            _cycleCount++;
        }
        else
        {
            // Fixed ring: overwriting the oldest cycle is O(1). The previous
            // List.RemoveAt(0) shifted 4,095 records for every captured cycle
            // once the analyzer filled.
            physicalIndex = _cycleStart;
            _cycleStart = (_cycleStart + 1) % Capacity;
            DroppedCycleCount++;
        }

        _cycles[physicalIndex] = cycle;
        _pendingDataCycleIndex = physicalIndex;

        // Write data is already driven by the CPU at the rising edge. Read data
        // belongs to the responding package and may settle later in PHI2 high.
        _pendingDataSettled = !isRead && hasData;
    }

    public void Clear()
    {
        _cycleStart = 0;
        _cycleCount = 0;
        _pendingDataCycleIndex = -1;
        _pendingDataSettled = false;
        _pendingOwnedByDma = false;
        DroppedCycleCount = 0;
    }

    private sealed class CycleView : IReadOnlyList<Mos6502BusCycle>
    {
        private readonly Mos6502BusAnalyzer _owner;

        public CycleView(Mos6502BusAnalyzer owner)
        {
            _owner = owner;
        }

        public int Count => _owner._cycleCount;

        public Mos6502BusCycle this[int index]
        {
            get
            {
                if ((uint)index >= (uint)_owner._cycleCount) throw new ArgumentOutOfRangeException(nameof(index));
                return _owner._cycles[(_owner._cycleStart + index) % _owner.Capacity];
            }
        }

        public IEnumerator<Mos6502BusCycle> GetEnumerator()
        {
            for (var index = 0; index < _owner._cycleCount; index++) yield return this[index];
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

public readonly record struct Mos6502BusCycle(
    ulong Sequence,
    ushort Address,
    byte? Data,
    bool IsRead,
    bool IsOpcodeFetch);
