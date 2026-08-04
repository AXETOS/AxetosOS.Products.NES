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
    private bool _vblank;

    public NesPpuTimingCore(string componentId) : base(componentId)
    {
        Clock = AddPin("CLK", PinDirection.Input);
        NmiEnable = AddPin("NMI_ENABLE", PinDirection.Input);
        ForceVblank = AddPin("FORCE_VBLANK", PinDirection.Input);
        Vblank = AddPin("VBLANK", PinDirection.Output);
        NmiBar = AddPin("/NMI", PinDirection.Output);
    }

    public DigitalPin Clock { get; }
    public DigitalPin NmiEnable { get; }
    public DigitalPin ForceVblank { get; }
    public DigitalPin Vblank { get; }
    public DigitalPin NmiBar { get; }

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
