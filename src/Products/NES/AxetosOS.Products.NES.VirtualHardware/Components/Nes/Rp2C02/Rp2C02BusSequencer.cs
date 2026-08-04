using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Nes.Rp2C02;

/// <summary>
/// Pin-level arbiter/sequencer for the RP2C02 external address and data bus.
/// CPU requests have priority over render requests. A transaction is held
/// until the external data bus resolves, then a one-evaluation completion
/// pulse is emitted. No memory array is accessed directly.
/// </summary>
public sealed class Rp2C02BusSequencer : VirtualHardwareComponent
{
    private Owner _owner;
    private bool _cpuRequestLast;
    private bool _renderRequestLast;
    private bool _completionPending;

    public Rp2C02BusSequencer(string componentId) : base(componentId)
    {
        CpuAddress = CreateBus("CPU_A", 14, PinDirection.Input);
        CpuWriteData = CreateBus("CPU_WD", 8, PinDirection.Input);
        CpuReadData = CreateBus("CPU_RD", 8, PinDirection.Output);
        CpuRequest = AddPin("CPU_REQ", PinDirection.Input);
        CpuWrite = AddPin("CPU_WRITE", PinDirection.Input);
        CpuComplete = AddPin("CPU_COMPLETE", PinDirection.Output);

        RenderAddress = CreateBus("RENDER_A", 14, PinDirection.Input);
        RenderReadData = CreateBus("RENDER_RD", 8, PinDirection.Output);
        RenderRequest = AddPin("RENDER_REQ", PinDirection.Input);
        RenderComplete = AddPin("RENDER_COMPLETE", PinDirection.Output);

        ExternalAddress = CreateBus("PPU_A", 14, PinDirection.Output);
        ExternalData = CreateBus("PPU_D", 8, PinDirection.Bidirectional);
        ReadBar = AddPin("/RD", PinDirection.Output);
        WriteBar = AddPin("/WR", PinDirection.Output);
        Busy = AddPin("BUSY", PinDirection.Output);
    }

    public DigitalBus CpuAddress { get; }
    public DigitalBus CpuWriteData { get; }
    public DigitalBus CpuReadData { get; }
    public DigitalPin CpuRequest { get; }
    public DigitalPin CpuWrite { get; }
    public DigitalPin CpuComplete { get; }
    public DigitalBus RenderAddress { get; }
    public DigitalBus RenderReadData { get; }
    public DigitalPin RenderRequest { get; }
    public DigitalPin RenderComplete { get; }
    public DigitalBus ExternalAddress { get; }
    public DigitalBus ExternalData { get; }
    public DigitalPin ReadBar { get; }
    public DigitalPin WriteBar { get; }
    public DigitalPin Busy { get; }
    public ulong CompletedReadCount { get; private set; }
    public ulong CompletedWriteCount { get; private set; }

    public override void PowerOn()
    {
        _owner = Owner.None;
        _cpuRequestLast = _renderRequestLast = false;
        _completionPending = false;
        CompletedReadCount = CompletedWriteCount = 0;
        ReleaseBus();
    }

    public override void Reset() { _owner = Owner.None; _completionPending = false; ReleaseBus(); }

    public override void Evaluate()
    {
        CpuComplete.Drive(DigitalLevel.Low);
        RenderComplete.Drive(DigitalLevel.Low);

        if (_completionPending)
        {
            if (_owner == Owner.Cpu) CpuComplete.Drive(DigitalLevel.High);
            else if (_owner == Owner.Render) RenderComplete.Drive(DigitalLevel.High);
            _completionPending = false;
            _owner = Owner.None;
            ReleaseBus();
            return;
        }

        var cpuRequest = CpuRequest.SampledLevel == DigitalLevel.High;
        var renderRequest = RenderRequest.SampledLevel == DigitalLevel.High;
        if (_owner == Owner.None)
        {
            if (cpuRequest && !_cpuRequestLast) _owner = Owner.Cpu;
            else if (renderRequest && !_renderRequestLast) _owner = Owner.Render;
        }
        _cpuRequestLast = cpuRequest;
        _renderRequestLast = renderRequest;

        switch (_owner)
        {
            case Owner.None:
                ReleaseBus();
                break;
            case Owner.Cpu:
                DriveCpuTransaction();
                break;
            case Owner.Render:
                DriveRenderTransaction();
                break;
        }
    }

    private void DriveCpuTransaction()
    {
        if (!CpuAddress.TrySample(out var address)) return;
        ExternalAddress.Drive(address & 0x3FFF);
        Busy.Drive(DigitalLevel.High);
        var write = CpuWrite.SampledLevel == DigitalLevel.High;
        if (write)
        {
            ReadBar.Drive(DigitalLevel.High);
            WriteBar.Drive(DigitalLevel.Low);
            if (!CpuWriteData.TrySample(out var data)) return;
            ExternalData.Drive(data);
            CompletedWriteCount++;
            _completionPending = true;
        }
        else
        {
            ExternalData.Release();
            WriteBar.Drive(DigitalLevel.High);
            ReadBar.Drive(DigitalLevel.Low);
            if (!ExternalData.TrySample(out var data)) return;
            CpuReadData.Drive(data);
            CompletedReadCount++;
            _completionPending = true;
        }
    }

    private void DriveRenderTransaction()
    {
        if (!RenderAddress.TrySample(out var address)) return;
        ExternalAddress.Drive(address & 0x3FFF);
        ExternalData.Release();
        WriteBar.Drive(DigitalLevel.High);
        ReadBar.Drive(DigitalLevel.Low);
        Busy.Drive(DigitalLevel.High);
        if (!ExternalData.TrySample(out var data)) return;
        RenderReadData.Drive(data);
        CompletedReadCount++;
        _completionPending = true;
    }

    private void ReleaseBus()
    {
        ExternalAddress.Release();
        ExternalData.Release();
        ReadBar.Drive(DigitalLevel.High);
        WriteBar.Drive(DigitalLevel.High);
        Busy.Drive(DigitalLevel.Low);
    }

    private DigitalBus CreateBus(string name, int width, PinDirection direction)
    {
        var pins = new DigitalPin[width];
        for (var bit = 0; bit < width; bit++) pins[bit] = AddPin($"{name}{bit}", direction);
        return new DigitalBus($"{ComponentId}.{name}", pins);
    }

    private enum Owner { None, Cpu, Render }
}
