using AxetosOS.Products.NES.Abstractions;

namespace AxetosOS.Products.NES.Hardware;

/// <summary>
/// Passive, allocation-free views over the live RP2C02 functional blocks.
/// The PPU remains the sole owner of timing and state transitions.
/// </summary>
public sealed class Rp2C02VramAddressUnit : INesHardwareModule
{
    private readonly Rp2C02Ppu _ppu;

    public Rp2C02VramAddressUnit(Rp2C02Ppu ppu) =>
        _ppu = ppu ?? throw new ArgumentNullException(nameof(ppu));

    public string ModuleId => "nes.rp2c02.vram-address-unit";
    public ushort CurrentAddress => _ppu.VramAddress;
    public ushort TemporaryAddress => _ppu.TemporaryVramAddress;
    public ushort ActiveScanlineAddress => _ppu.ActiveScanlineVramAddress;
    public byte FineXScroll => _ppu.FineXScroll;
    public bool WriteToggle => _ppu.WriteToggle;

    public void PowerOn() { }
    public void Reset() { }
}

public sealed class Rp2C02BackgroundPipeline : INesHardwareModule
{
    private readonly Rp2C02Ppu _ppu;

    public Rp2C02BackgroundPipeline(Rp2C02Ppu ppu) =>
        _ppu = ppu ?? throw new ArgumentNullException(nameof(ppu));

    public string ModuleId => "nes.rp2c02.background-pipeline";
    public ushort PatternLowShift => _ppu.BackgroundPatternLowShift;
    public ushort PatternHighShift => _ppu.BackgroundPatternHighShift;
    public ushort AttributeLowShift => _ppu.BackgroundAttributeLowShift;
    public ushort AttributeHighShift => _ppu.BackgroundAttributeHighShift;
    public byte NextTile => _ppu.NextBackgroundTile;
    public byte NextAttribute => _ppu.NextBackgroundAttribute;
    public byte NextLowPlane => _ppu.NextBackgroundLowPlane;
    public byte NextHighPlane => _ppu.NextBackgroundHighPlane;
    public bool Enabled => _ppu.BackgroundRenderingEnabled;

    public void PowerOn() { }
    public void Reset() { }
}

public sealed class Rp2C02PixelCompositor : INesHardwareModule
{
    private readonly Rp2C02Ppu _ppu;

    public Rp2C02PixelCompositor(Rp2C02Ppu ppu) =>
        _ppu = ppu ?? throw new ArgumentNullException(nameof(ppu));

    public string ModuleId => "nes.rp2c02.pixel-compositor";
    public int Scanline => _ppu.Scanline;
    public int Dot => _ppu.Dot;
    public bool BackgroundEnabled => _ppu.BackgroundRenderingEnabled;
    public bool SpritesEnabled => _ppu.SpriteRenderingEnabled;
    public int LatchedSpriteCount => _ppu.LatchedSpriteCount;
    public bool SpriteZeroSelected => _ppu.LatchedSpriteZeroSelected;
    public int SpriteZeroSlot => _ppu.LatchedSpriteZeroSlot;
    public ulong SpriteZeroHits => _ppu.SpriteZeroHits;

    public void PowerOn() { }
    public void Reset() { }
}

public sealed class Rp2C02NmiController : INesHardwareModule
{
    private readonly Rp2C02Ppu _ppu;
    private readonly ISignalLine _output;

    public Rp2C02NmiController(Rp2C02Ppu ppu, ISignalLine output)
    {
        _ppu = ppu ?? throw new ArgumentNullException(nameof(ppu));
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    public string ModuleId => "nes.rp2c02.nmi-controller";
    public bool VblankFlag => _ppu.InVBlank;
    public bool NmiEnabled => (_ppu.Control & 0x80) != 0;
    public bool OutputActive => _ppu.NmiOutputActive;
    public bool PinAsserted => _output.IsAsserted;
    public bool VblankStartSuppressed => _ppu.VblankStartSuppressed;
    public ulong EdgeCount => _ppu.NmiEdges;

    public void PowerOn() { }
    public void Reset() { }
}
