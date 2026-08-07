using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Nes;

/// <summary>
/// RP2A03 OAM-DMA controller. A CPU write to $4014 latches the source page,
/// requests the CPU bus, performs 256 alternating memory-read/OAM-write
/// transfers, and then releases the processor. All traffic is represented by
/// pins; the controller never references RAM or PPU storage directly.
/// </summary>
public sealed class NesOamDmaController : VirtualHardwareComponent
{
    private enum DmaPhase { Idle, Alignment, Read, Write }

    private DmaPhase _phase;
    private DigitalLevel _previousClock;
    private bool _triggerConsumed;
    private byte _page;
    private byte _index;
    private byte _latchedData;
    private bool _alignmentRisingSeen;
    private readonly ulong _clockInputMask;
    private readonly ulong _triggerInputMask;

    public NesOamDmaController(string componentId) : base(componentId)
    {
        var addressPins = new DigitalPin[16];
        var dataPins = new DigitalPin[8];
        var oamDataPins = new DigitalPin[8];
        for (var bit = 0; bit < 16; bit++) addressPins[bit] = AddPin($"A{bit}", PinDirection.Bidirectional);
        for (var bit = 0; bit < 8; bit++)
        {
            dataPins[bit] = AddPin($"D{bit}", PinDirection.Bidirectional);
            oamDataPins[bit] = AddPin($"OAM_D{bit}", PinDirection.Output);
        }

        Address = new DigitalBus($"{componentId}.A", addressPins);
        Data = new DigitalBus($"{componentId}.D", dataPins);
        OamData = new DigitalBus($"{componentId}.OAM_D", oamDataPins);
        ReadWrite = AddPin("R/W", PinDirection.Bidirectional);
        Clock = AddPin("PHI2", PinDirection.Input);
        CpuBusEnable = AddPin("CPU_BUS_ENABLE", PinDirection.Output);
        Ready = AddPin("RDY", PinDirection.Output);
        OamWrite = AddPin("OAM_WRITE", PinDirection.Output);
        _clockInputMask = Clock.InputChangeMask;
        _triggerInputMask = Address.InputChangeMask | Data.InputChangeMask | ReadWrite.InputChangeMask;
    
        InitializePackageState();
    }

    public DigitalBus Address { get; }
    public DigitalBus Data { get; }
    public DigitalBus OamData { get; }
    public DigitalPin ReadWrite { get; }
    public DigitalPin Clock { get; }
    public DigitalPin CpuBusEnable { get; }
    public DigitalPin Ready { get; }
    public DigitalPin OamWrite { get; }

    public bool IsActive => _phase != DmaPhase.Idle;
    public byte SourcePage => _page;
    public byte CurrentIndex => _index;
    public ulong TransferCount { get; private set; }
    public ulong CompletedDmaCount { get; private set; }
    public ulong CpuStallCycleCount { get; private set; }

    private void InitializePackageState()
    {
        _phase = DmaPhase.Idle;
        _previousClock = DigitalLevel.Low;
        _triggerConsumed = false;
        _page = _index = _latchedData = 0;
        _alignmentRisingSeen = false;
        TransferCount = CompletedDmaCount = CpuStallCycleCount = 0;
        ReleaseBus();
    }

    protected override void OnInputChanges(ulong changedInputMask)
    {
        var clockChanged = (changedInputMask & _clockInputMask) != 0;
        var triggerPinsChanged = (changedInputMask & _triggerInputMask) != 0;

        if (_phase == DmaPhase.Idle)
        {
            // While idle, PHI2 activity by itself cannot start DMA.  Address,
            // data and R/W still arrive at the pins and only those trigger-side
            // changes need to inspect $4014.
            if (clockChanged) _previousClock = Clock.SampledLevel;
            if (triggerPinsChanged) DetectTrigger();
            return;
        }

        // Once DMA owns the bus, its state machine changes only on PHI2 edges.
        // External bus-data settling between edges is merely sampled later.
        if (!clockChanged) return;
        var clock = Clock.SampledLevel;
        var rising = _previousClock == DigitalLevel.Low && clock == DigitalLevel.High;
        var falling = _previousClock == DigitalLevel.High && clock == DigitalLevel.Low;
        _previousClock = clock;

        CpuBusEnable.Drive(DigitalLevel.Low);
        Ready.Drive(DigitalLevel.Low);
        CpuStallCycleCount += rising ? 1UL : 0UL;

        switch (_phase)
        {
            case DmaPhase.Alignment:
                Address.Release();
                Data.Release();
                ReadWrite.Release();
                OamWrite.Drive(DigitalLevel.Low);
                OamData.Release();
                if (rising) _alignmentRisingSeen = true;
                if (falling && _alignmentRisingSeen) _phase = DmaPhase.Read;
                break;

            case DmaPhase.Read:
                Address.Drive((ushort)((_page << 8) | _index));
                Data.Release();
                ReadWrite.Drive(DigitalLevel.High);
                OamWrite.Drive(DigitalLevel.Low);
                OamData.Release();
                if (falling && Data.TrySample(out var value))
                {
                    _latchedData = (byte)value;
                    _phase = DmaPhase.Write;
                }
                break;

            case DmaPhase.Write:
                Address.Release();
                Data.Release();
                ReadWrite.Release();
                OamData.Drive(_latchedData);
                OamWrite.Drive(DigitalLevel.High);
                if (falling)
                {
                    TransferCount++;
                    if (_index == byte.MaxValue)
                    {
                        CompletedDmaCount++;
                        _phase = DmaPhase.Idle;
                        ReleaseBus();
                    }
                    else
                    {
                        _index++;
                        _phase = DmaPhase.Read;
                    }
                }
                break;
        }
    }

    private void DetectTrigger()
    {
        var isWrite = ReadWrite.SampledLevel == DigitalLevel.Low;
        var validAddress = Address.TrySample(out var address) && address == 0x4014;
        if (!isWrite || !validAddress)
        {
            _triggerConsumed = false;
            return;
        }

        if (_triggerConsumed || !Data.TrySample(out var data)) return;
        _triggerConsumed = true;
        _page = (byte)data;
        _index = 0;
        _alignmentRisingSeen = false;
        _phase = DmaPhase.Alignment;
    }

    private void ReleaseBus()
    {
        Address.Release();
        Data.Release();
        ReadWrite.Release();
        CpuBusEnable.Release();
        Ready.Release();
        OamData.Release();
        OamWrite.Drive(DigitalLevel.Low);
    }
}
