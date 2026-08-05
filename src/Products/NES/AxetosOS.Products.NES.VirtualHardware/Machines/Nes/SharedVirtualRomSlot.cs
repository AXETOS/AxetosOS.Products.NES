using AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;
using AxetosOS.Products.NES.VirtualHardware.Loading;

namespace AxetosOS.Products.NES.VirtualHardware.Machines.Nes;

/// <summary>
/// One normalized digital ROM insertion point shared by all regional
/// motherboard assemblies. Physical 60-pin and 72-pin connector differences
/// deliberately do not exist at this host boundary.
/// </summary>
public sealed class SharedVirtualRomSlot
{
    private NromCartridge? _cartridge;
    public const int CpuAddressWidth = 16;
    public const int CpuDataWidth = 8;
    public const int PpuAddressWidth = 14;
    public const int PpuDataWidth = 8;

    public VirtualHardwareNesRomImage? InsertedImage { get; private set; }
    public string? SourceName { get; private set; }
    public NesResolvedRegion? ResolvedRegion { get; private set; }
    public PalCicVariant PalCicVariant { get; private set; } = PalCicVariant.PalA3195;
    public bool IsOccupied => InsertedImage is not null;
    public NromCartridge? Cartridge => _cartridge;
    public ulong InsertCount { get; private set; }
    public ulong EjectCount { get; private set; }

    public void Insert(
        VirtualHardwareNesRomImage image,
        string? sourceName = null,
        NesRegionSelection regionSelection = NesRegionSelection.Auto,
        PalCicVariant palCicVariant = PalCicVariant.PalA3195)
    {
        ArgumentNullException.ThrowIfNull(image);
        InsertedImage = image;
        SourceName = sourceName;
        ResolvedRegion = NesHardwareRegionResolver.Resolve(image, sourceName, regionSelection);
        PalCicVariant = palCicVariant;
        if (image.MapperNumber != 0)
            throw new NotSupportedException($"Mapper {image.MapperNumber} is not yet implemented as virtual cartridge hardware.");
        _cartridge ??= new NromCartridge("SLOT.NROM");
        _cartridge.LoadImage(image);
        InsertCount++;
    }

    public void Eject()
    {
        if (!IsOccupied)
            return;

        InsertedImage = null;
        SourceName = null;
        ResolvedRegion = null;
        _cartridge?.Eject();
        EjectCount++;
    }
    public void AttachTo(FamicomMotherboard board) => Attach(
        board.Board, board.Vcc.Output, board.Ground.Output, board.CpuAddressNets, board.CpuDataNets,
        board.CpuReadWriteNet, board.CpuM2Net, board.PpuAddressDataNets, board.PpuHighAddressNets,
        board.PpuAleNet, board.PpuReadBarNet, board.PpuWriteBarNet, board.CiramChipEnableBarNet,
        board.CiramA10Net, board.CartridgeIrqNet);

    public void AttachTo(NtscNesMotherboard board) => Attach(
        board.Board, board.Vcc.Output, board.Ground.Output, board.CpuAddressNets, board.CpuDataNets,
        board.CpuReadWriteNet, board.CpuM2Net, board.PpuAddressDataNets, board.PpuHighAddressNets,
        board.PpuAleNet, board.PpuReadBarNet, board.PpuWriteBarNet, board.CiramChipEnableBarNet,
        board.CiramA10Net, board.CartridgeIrqNet);

    public void AttachTo(PalNesMotherboard board) => Attach(
        board.Board, board.Vcc.Output, board.Ground.Output, board.CpuAddressNets, board.CpuDataNets,
        board.CpuReadWriteNet, board.CpuM2Net, board.PpuAddressDataNets, board.PpuHighAddressNets,
        board.PpuAleNet, board.PpuReadBarNet, board.PpuWriteBarNet, board.CiramChipEnableBarNet,
        board.CiramA10Net, board.CartridgeIrqNet);

    private void Attach(
        AxetosOS.Products.NES.VirtualHardware.Boards.VirtualHardwareBoard board, DigitalPin vcc, DigitalPin gnd,
        IReadOnlyList<DigitalNet> cpuAddress, IReadOnlyList<DigitalNet> cpuData, DigitalNet cpuRw, DigitalNet cpuM2,
        IReadOnlyList<DigitalNet> ppuAd, IReadOnlyList<DigitalNet> ppuHigh, DigitalNet ppuAle, DigitalNet ppuRd,
        DigitalNet ppuWr, DigitalNet ciramCe, DigitalNet ciramA10, DigitalNet irq)
    {
        var cartridge = _cartridge ?? throw new InvalidOperationException("No cartridge is inserted.");
        if (!board.Components.Contains(cartridge)) board.Add(cartridge);
        board.Connect("VCC", vcc, cartridge.Vcc);
        board.Connect("GND", gnd, cartridge.Gnd);
        for (var bit = 0; bit < 16; bit++) board.Connect($"CPU.A{bit}", cartridge.CpuAddress.Pins[bit]);
        for (var bit = 0; bit < 8; bit++) board.Connect($"CPU.D{bit}", cartridge.CpuData.Pins[bit]);
        board.Connect("CPU.RW", cartridge.CpuReadWrite);
        board.Connect("CPU.M2", cartridge.CpuM2);
        for (var bit = 0; bit < 8; bit++) board.Connect($"PPU.AD{bit}", cartridge.PpuAddressData.Pins[bit]);
        for (var bit = 0; bit < 6; bit++) board.Connect($"PPU.A{bit + 8}", cartridge.PpuHighAddress.Pins[bit]);
        board.Connect("PPU.ALE", cartridge.PpuAle);
        board.Connect("PPU.RD_BAR", cartridge.PpuReadBar);
        board.Connect("PPU.WR_BAR", cartridge.PpuWriteBar);
        board.Connect("CIRAM.CE_BAR", cartridge.CiramChipEnableBar);
        board.Connect("CIRAM.A10", cartridge.CiramA10);
        board.Connect("CPU.IRQ_BAR", cartridge.IrqBar);
    }

}
