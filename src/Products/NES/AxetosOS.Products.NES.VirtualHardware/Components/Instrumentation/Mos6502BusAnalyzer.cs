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
    }

    public int Capacity { get; }
    public DigitalBus Address { get; }
    public DigitalBus Data { get; }
    public DigitalPin ReadWrite { get; }
    public DigitalPin Sync { get; }
    public DigitalPin Clock { get; }
    public IReadOnlyList<Mos6502BusCycle> Cycles => _cycles;
    public ulong ObservedRisingEdges { get; private set; }
    public ulong DroppedCycleCount { get; private set; }

    public override void PowerOn()
    {
        _cycles.Clear();
        _previousClock = DigitalLevel.Low;
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

        if (!rising)
        {
            return;
        }

        ObservedRisingEdges++;
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
    }

    public void Clear()
    {
        _cycles.Clear();
        DroppedCycleCount = 0;
    }
}

public readonly record struct Mos6502BusCycle(
    ulong Sequence,
    ushort Address,
    byte? Data,
    bool IsRead,
    bool IsOpcodeFetch);
