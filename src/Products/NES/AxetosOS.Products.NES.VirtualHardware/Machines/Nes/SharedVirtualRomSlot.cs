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
    private IReplaceableCartridgeHardware? _cartridge;
    public const int CpuAddressWidth = 15;
    public const int CpuDataWidth = 8;
    public const int PpuAddressWidth = 14;
    public const int PpuDataWidth = 8;

    public VirtualHardwareNesRomImage? InsertedImage { get; private set; }
    public string? SourceName { get; private set; }
    public NesResolvedRegion? ResolvedRegion { get; private set; }
    public PalCicVariant PalCicVariant { get; private set; } = PalCicVariant.PalA3195;
    public bool IsOccupied => InsertedImage is not null;
    public IReplaceableCartridgeHardware? Cartridge => _cartridge;
    public ulong InsertCount { get; private set; }
    public ulong EjectCount { get; private set; }

    public void Insert(
        VirtualHardwareNesRomImage image,
        string? sourceName = null,
        NesRegionSelection regionSelection = NesRegionSelection.Auto,
        PalCicVariant palCicVariant = PalCicVariant.PalA3195)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (IsOccupied) throw new InvalidOperationException("Eject the current cartridge before inserting another one.");
        var cartridge = VirtualCartridgeHardwareFactory.Create(image);
        InsertedImage = image;
        SourceName = sourceName;
        ResolvedRegion = NesHardwareRegionResolver.Resolve(image, sourceName, regionSelection);
        PalCicVariant = palCicVariant;
        _cartridge = cartridge;
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
        _cartridge = null;
        EjectCount++;
    }
    public void AttachTo(FamicomMotherboard board) => Attach(
        board.Board, board.Vcc.Output, board.Ground.Output, board.CpuAddressNets, board.CpuDataNets,
        board.CpuReadWriteNet, board.CpuM2Net, board.CpuRomSelectBarNet, board.PpuLowAddressNets, board.PpuHighAddressNets,
        board.PpuDataNets, board.PpuReadBarNet, board.PpuWriteBarNet, board.CiramChipEnableBarNet,
        board.CiramA10Net, board.CartridgeIrqNet);

    public void AttachTo(NtscNesMotherboard board) => Attach(
        board.Board, board.Vcc.Output, board.Ground.Output, board.CpuAddressNets, board.CpuDataNets,
        board.CpuReadWriteNet, board.CpuM2Net, board.CpuRomSelectBarNet, board.PpuLowAddressNets, board.PpuHighAddressNets,
        board.PpuDataNets, board.PpuReadBarNet, board.PpuWriteBarNet, board.CiramChipEnableBarNet,
        board.CiramA10Net, board.CartridgeIrqNet);

    public void AttachTo(PalNesMotherboard board) => Attach(
        board.Board, board.Vcc.Output, board.Ground.Output, board.CpuAddressNets, board.CpuDataNets,
        board.CpuReadWriteNet, board.CpuM2Net, board.CpuRomSelectBarNet, board.PpuLowAddressNets, board.PpuHighAddressNets,
        board.PpuDataNets, board.PpuReadBarNet, board.PpuWriteBarNet, board.CiramChipEnableBarNet,
        board.CiramA10Net, board.CartridgeIrqNet);

    private void Attach(
        AxetosOS.Products.NES.VirtualHardware.Boards.VirtualHardwareBoard board, DigitalPin vcc, DigitalPin gnd,
        IReadOnlyList<DigitalNet> cpuAddress, IReadOnlyList<DigitalNet> cpuData, DigitalNet cpuRw, DigitalNet cpuM2, DigitalNet cpuRomSelBar,
        IReadOnlyList<DigitalNet> ppuLow, IReadOnlyList<DigitalNet> ppuHigh, IReadOnlyList<DigitalNet> ppuData,
        DigitalNet ppuRd, DigitalNet ppuWr, DigitalNet ciramCe, DigitalNet ciramA10, DigitalNet irq)
    {
        var cartridge = _cartridge ?? throw new InvalidOperationException("No cartridge is inserted.");
        if (!board.Components.Contains(cartridge)) board.Add(cartridge);
        board.Connect("VCC", vcc, cartridge.Vcc);
        board.Connect("GND", gnd, cartridge.Gnd);
        for (var bit = 0; bit < CpuAddressWidth; bit++) cpuAddress[bit].Connect(cartridge.CpuAddress.Pins[bit]);
        for (var bit = 0; bit < 8; bit++) cpuData[bit].Connect(cartridge.CpuData.Pins[bit]);
        cpuRw.Connect(cartridge.CpuReadWrite);
        cpuM2.Connect(cartridge.CpuM2);
        cpuRomSelBar.Connect(cartridge.CpuRomSelectBar);
        for (var bit = 0; bit < 8; bit++) ppuLow[bit].Connect(cartridge.PpuAddress.Pins[bit]);
        for (var bit = 0; bit < 6; bit++) ppuHigh[bit].Connect(cartridge.PpuAddress.Pins[bit + 8]);
        for (var bit = 0; bit < 8; bit++) ppuData[bit].Connect(cartridge.PpuData.Pins[bit]);
        ppuRd.Connect(cartridge.PpuReadBar);
        ppuWr.Connect(cartridge.PpuWriteBar);
        ciramCe.Connect(cartridge.CiramChipEnableBar);
        ciramA10.Connect(cartridge.CiramA10);
        irq.Connect(cartridge.IrqBar);
    }

}
