using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Nes;

/// <summary>
/// Clocked regional RP2C02 timing foundation. It advances scanline/dot counters
/// on PPU clock rising edges, raises vblank at the configured boundary, clears
/// it on the configured pre-render line, and asserts open-drain /NMI while
/// vblank and the CPU-visible NMI-enable pin are both active.
/// </summary>
public sealed class NesPpuTimingCore : VirtualHardwareComponent
{
    private bool _dotTickHigh;
    private readonly ulong _clockInputMask;
    private readonly ulong _resetInputMask;
    private readonly ulong _outputControlInputMask;
    private bool _vblank;
    private bool _resetAsserted;

    public NesPpuTimingCore(
        string componentId,
        int dotsPerScanline = 341,
        int scanlinesPerFrame = 262,
        int vblankStartScanline = 241,
        int preRenderScanline = 261) : base(componentId)
    {
        if (dotsPerScanline <= 0) throw new ArgumentOutOfRangeException(nameof(dotsPerScanline));
        if (scanlinesPerFrame <= 0) throw new ArgumentOutOfRangeException(nameof(scanlinesPerFrame));
        if (vblankStartScanline < 0 || vblankStartScanline >= scanlinesPerFrame) throw new ArgumentOutOfRangeException(nameof(vblankStartScanline));
        if (preRenderScanline < 0 || preRenderScanline >= scanlinesPerFrame) throw new ArgumentOutOfRangeException(nameof(preRenderScanline));

        DotsPerScanline = dotsPerScanline;
        ScanlinesPerFrame = scanlinesPerFrame;
        VblankStartScanline = vblankStartScanline;
        PreRenderScanline = preRenderScanline;
        Clock = AddPin("CLK", PinDirection.Input, DigitalInputActivation.RisingEdge);
        ResetBar = AddPin("/RESET", PinDirection.Input);
        NmiEnable = AddPin("NMI_ENABLE", PinDirection.Input);
        ForceVblank = AddPin("FORCE_VBLANK", PinDirection.Input);
        Vblank = AddPin("VBLANK", PinDirection.Output);
        NmiBar = AddPin("/NMI", PinDirection.Output);
        DotTick = AddPin("DOT_TICK", PinDirection.Output);
        var scanlinePins = new DigitalPin[9];
        var dotPins = new DigitalPin[9];
        for (var bit = 0; bit < 9; bit++)
        {
            scanlinePins[bit] = AddPin($"SCANLINE{bit}", PinDirection.Output);
            dotPins[bit] = AddPin($"DOT{bit}", PinDirection.Output);
        }
        ScanlineBus = new DigitalBus($"{componentId}.SCANLINE", scanlinePins);
        DotBus = new DigitalBus($"{componentId}.DOT", dotPins);
        _clockInputMask = Clock.InputChangeMask;
        _resetInputMask = ResetBar.InputChangeMask;
        _outputControlInputMask = NmiEnable.InputChangeMask | ForceVblank.InputChangeMask;
    
        InitializePackageState();
    }

    public int DotsPerScanline { get; }
    public int ScanlinesPerFrame { get; }
    public int VblankStartScanline { get; }
    public int PreRenderScanline { get; }

    public DigitalPin Clock { get; }
    public DigitalPin ResetBar { get; }
    public DigitalPin NmiEnable { get; }
    public DigitalPin ForceVblank { get; }
    public DigitalPin Vblank { get; }
    public DigitalPin NmiBar { get; }
    public DigitalPin DotTick { get; }
    public DigitalBus ScanlineBus { get; }
    public DigitalBus DotBus { get; }

    public int Scanline { get; private set; }
    public int Dot { get; private set; }
    public ulong Frame { get; private set; }
    public bool IsVblank => _vblank || ForceVblank.SampledLevel == DigitalLevel.High;

    private void InitializePackageState()
    {
        Scanline = 0;
        Dot = 0;
        Frame = 0;
        _vblank = false;
        _dotTickHigh = false;
        DotTick.Drive(DigitalLevel.Low);
        DriveOutputs();
    }

    protected override void OnInputChanges(ulong changedInputMask)
    {
        var resetChanged = (changedInputMask & _resetInputMask) != 0;
        var clockRising = (changedInputMask & _clockInputMask) != 0;
        var outputControlChanged = (changedInputMask & _outputControlInputMask) != 0;
        if (!resetChanged && !clockRising && !outputControlChanged) return;

        if (ResetBar.SampledLevel == DigitalLevel.Low)
        {
            if (!_resetAsserted) InitializePackageState();
            _resetAsserted = true;
            return;
        }

        _resetAsserted = false;
        if (clockRising && Clock.SampledLevel == DigitalLevel.High) AdvanceDot();
        DriveOutputs();
    }

    private void AdvanceDot()
    {
        _dotTickHigh = !_dotTickHigh;
        DotTick.Drive(_dotTickHigh ? DigitalLevel.High : DigitalLevel.Low);
        Dot++;
        if (Dot >= DotsPerScanline)
        {
            Dot = 0;
            Scanline++;
            if (Scanline >= ScanlinesPerFrame)
            {
                Scanline = 0;
                Frame++;
            }
        }

        if (Scanline == VblankStartScanline && Dot == 1)
        {
            _vblank = true;
        }
        else if (Scanline == PreRenderScanline && Dot == 1)
        {
            _vblank = false;
        }
    }

    private void DriveOutputs()
    {
        ScanlineBus.Drive((ulong)Scanline);
        DotBus.Drive((ulong)Dot);
        var active = IsVblank;
        Vblank.Drive(active ? DigitalLevel.High : DigitalLevel.Low);

        // The RP2C02 NMI output is represented as an open-drain line. The
        // motherboard supplies the weak pull-up; the PPU only pulls low.
        if (active && NmiEnable.SampledLevel == DigitalLevel.High)
        {
            NmiBar.Drive(DigitalLevel.Low);
        }
        else
        {
            NmiBar.Release();
        }
    }
}
