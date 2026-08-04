using AxetosOS.Products.NES.Abstractions;

namespace AxetosOS.Products.NES.Hardware;

/// <summary>
/// Inspectable RP2C02 package boundary. It groups the live PPU core, its
/// dot-clocked sprite evaluation circuit, internal palette RAM, and NMI output
/// without creating an alternate execution path.
/// </summary>
public sealed class Rp2C02Package : INesHardwareModule, IHardwareCompositeModule
{
    private readonly HardwareComponentDescriptor[] _components;
    private readonly HardwareConnectionDescriptor[] _connections;

    public Rp2C02Package(
        Rp2C02Ppu ppu,
        Rp2C02SpriteEvaluator spriteEvaluator,
        PpuPaletteRam paletteRam,
        ISignalLine nmiOutput)
    {
        ArgumentNullException.ThrowIfNull(ppu);
        ArgumentNullException.ThrowIfNull(spriteEvaluator);
        ArgumentNullException.ThrowIfNull(paletteRam);
        ArgumentNullException.ThrowIfNull(nmiOutput);

        Ppu = ppu;
        SpriteEvaluator = spriteEvaluator;
        PaletteRam = paletteRam;
        PrimaryOam = new Rp2C02PrimaryOamMemory(ppu);
        SecondaryOam = new Rp2C02SecondaryOamMemory(spriteEvaluator);
        VramAddressUnit = new Rp2C02VramAddressUnit(ppu);
        BackgroundPipeline = new Rp2C02BackgroundPipeline(ppu);
        PixelCompositor = new Rp2C02PixelCompositor(ppu);
        NmiController = new Rp2C02NmiController(ppu, nmiOutput);
        NmiOutput = nmiOutput;

        _components =
        [
            new(ModuleId, "RP2C02 package", HardwareComponentKind.Chip, this),
            new(ppu.ModuleId, "RP2C02 timing, fetch and pixel core", HardwareComponentKind.Internal, ppu),
            new(spriteEvaluator.ModuleId, "Sprite evaluation circuit", HardwareComponentKind.Internal, spriteEvaluator),
            new(PrimaryOam.ModuleId, "256-byte primary OAM", HardwareComponentKind.Memory, PrimaryOam),
            new(SecondaryOam.ModuleId, "32-byte secondary OAM", HardwareComponentKind.Memory, SecondaryOam),
            new(paletteRam.ModuleId, "Internal palette RAM", HardwareComponentKind.Memory, paletteRam),
            new(VramAddressUnit.ModuleId, "VRAM address and scroll unit", HardwareComponentKind.Internal, VramAddressUnit),
            new(BackgroundPipeline.ModuleId, "Background fetch and shift pipeline", HardwareComponentKind.Internal, BackgroundPipeline),
            new(PixelCompositor.ModuleId, "Background/sprite pixel compositor", HardwareComponentKind.Internal, PixelCompositor),
            new(NmiController.ModuleId, "VBlank and NMI controller", HardwareComponentKind.Internal, NmiController),
            new("nes.signal.rp2c02.nmi", "NMI output pin", HardwareComponentKind.SignalBundle, nmiOutput)
        ];

        _connections =
        [
            new(ModuleId, ppu.ModuleId, HardwareConnectionKind.Internal, "timing/fetch/pixel core"),
            new(ppu.ModuleId, PrimaryOam.ModuleId, HardwareConnectionKind.Internal, "primary OAM address/data path"),
            new(PrimaryOam.ModuleId, spriteEvaluator.ModuleId, HardwareConnectionKind.Internal, "sprite evaluation reads"),
            new(spriteEvaluator.ModuleId, SecondaryOam.ModuleId, HardwareConnectionKind.Internal, "secondary OAM clear/copy"),
            new(ppu.ModuleId, paletteRam.ModuleId, HardwareConnectionKind.Internal, "palette address/data path"),
            new(VramAddressUnit.ModuleId, BackgroundPipeline.ModuleId, HardwareConnectionKind.Internal, "tile/attribute/pattern fetch addresses"),
            new(BackgroundPipeline.ModuleId, PixelCompositor.ModuleId, HardwareConnectionKind.Internal, "background pixel planes"),
            new(SecondaryOam.ModuleId, PixelCompositor.ModuleId, HardwareConnectionKind.Internal, "sprite pixel units"),
            new(ppu.ModuleId, NmiController.ModuleId, HardwareConnectionKind.Internal, "VBlank state"),
            new(NmiController.ModuleId, "nes.signal.rp2c02.nmi", HardwareConnectionKind.Signal, "NMI output")
        ];
    }

    public string ModuleId => "nes.package.rp2c02";
    public Rp2C02Ppu Ppu { get; }
    public Rp2C02SpriteEvaluator SpriteEvaluator { get; }
    public Rp2C02PrimaryOamMemory PrimaryOam { get; }
    public Rp2C02SecondaryOamMemory SecondaryOam { get; }
    public PpuPaletteRam PaletteRam { get; }
    public Rp2C02VramAddressUnit VramAddressUnit { get; }
    public Rp2C02BackgroundPipeline BackgroundPipeline { get; }
    public Rp2C02PixelCompositor PixelCompositor { get; }
    public Rp2C02NmiController NmiController { get; }
    public ISignalLine NmiOutput { get; }
    public IReadOnlyList<HardwareComponentDescriptor> HardwareComponents => _components;
    public IReadOnlyList<HardwareConnectionDescriptor> HardwareConnections => _connections;

    public void PowerOn()
    {
        // The motherboard powers the live PPU exactly once. The package is an
        // ownership/inspection boundary and must not duplicate lifecycle calls.
    }

    public void Reset()
    {
        // See PowerOn: lifecycle remains owned by the motherboard power order.
    }
}
