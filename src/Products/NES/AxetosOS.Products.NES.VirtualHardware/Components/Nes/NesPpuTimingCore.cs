using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Nes;

/// <summary>
/// Clocked NTSC RP2C02 timing foundation. It advances scanline/dot counters on
/// PPU clock rising edges, raises vblank at scanline 241 dot 1, clears it on
/// the pre-render line at dot 1, and asserts the open-drain /NMI output while
/// vblank and the CPU-visible NMI-enable pin are both active.
/// </summary>
public sealed class NesPpuTimingCore : VirtualHardwareComponent
{
    public const int DotsPerScanline = 341;
    public const int ScanlinesPerFrame = 262;
    public const int VblankStartScanline = 241;
    public const int PreRenderScanline = 261;

    private bool _clockWasHigh;
    private bool _dotTickHigh;
    private bool _vblank;

    public NesPpuTimingCore(string componentId) : base(componentId)
    {
        Clock = AddPin("CLK", PinDirection.Input);
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
    }

    public DigitalPin Clock { get; }
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

    public override void PowerOn()
    {
        Scanline = 0;
        Dot = 0;
        Frame = 0;
        _vblank = false;
        _clockWasHigh = false;
        _dotTickHigh = false;
        DotTick.Drive(DigitalLevel.Low);
        DriveOutputs();
    }

    public override void Reset() => PowerOn();

    public override void Evaluate()
    {
        var clockHigh = Clock.SampledLevel == DigitalLevel.High;
        if (clockHigh && !_clockWasHigh)
        {
            AdvanceDot();
        }

        _clockWasHigh = clockHigh;
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
