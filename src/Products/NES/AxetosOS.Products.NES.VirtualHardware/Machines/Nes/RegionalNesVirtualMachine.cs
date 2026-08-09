using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;
using AxetosOS.Products.NES.VirtualHardware.Simulation;
using AxetosOS.Products.NES.VirtualHardware.Loading;

namespace AxetosOS.Products.NES.VirtualHardware.Machines.Nes;

public enum ActiveNesMotherboard
{
    None,
    Famicom,
    NtscNes,
    PalNes
}

/// <summary>
/// Host-level three-board virtual console. The boards remain independent
/// circuits; one shared logical ROM slot selects and activates exactly one.
/// </summary>
public sealed class RegionalNesVirtualMachine
{
    private PalCicVariant _constructedPalVariant;

    public RegionalNesVirtualMachine()
    {
        Slot = new SharedVirtualRomSlot();
        Famicom = new FamicomMotherboard();
        NtscNes = new NtscNesMotherboard();
        _constructedPalVariant = PalCicVariant.PalA3195;
        PalNes = new PalNesMotherboard(_constructedPalVariant);
    }

    public SharedVirtualRomSlot Slot { get; }
    public FamicomMotherboard Famicom { get; }
    public NtscNesMotherboard NtscNes { get; }
    public PalNesMotherboard PalNes { get; private set; }
    public ActiveNesMotherboard ActiveMotherboard { get; private set; }
    public bool IsPowered { get; private set; }
    public ulong SelectionCount { get; private set; }
    public bool CompiledLabExecutionRequested { get; private set; }

    public object? ActiveBoard => ActiveMotherboard switch
    {
        ActiveNesMotherboard.Famicom => Famicom,
        ActiveNesMotherboard.NtscNes => NtscNes,
        ActiveNesMotherboard.PalNes => PalNes,
        _ => null
    };

    public void InsertRom(
        VirtualHardwareNesRomImage image,
        string? sourceName = null,
        NesRegionSelection regionSelection = NesRegionSelection.Auto,
        PalCicVariant palCicVariant = PalCicVariant.PalA3195)
    {
        if (IsPowered)
            throw new InvalidOperationException("Power off the virtual machine before replacing the ROM.");

        var resolved = NesHardwareRegionResolver.Resolve(image, sourceName, regionSelection);
        SelectResolvedMotherboard(resolved, palCicVariant);

        // In compiled-lab mode the fixed motherboard is compiled before ROM
        // metadata constructs mapper/cartridge hardware. Cartridge hardware is
        // then inserted and bound as a separate replaceable unit.
        if (CompiledLabExecutionRequested && ActiveMotherboard == ActiveNesMotherboard.Famicom)
            Famicom.SetCompiledLabMotherboardEnabled(true);

        Slot.Insert(image, sourceName, regionSelection, palCicVariant);
        AttachInsertedCartridge();
    }

    public void EjectRom()
    {
        if (IsPowered)
            throw new InvalidOperationException("Power off the virtual machine before ejecting the ROM.");

        DetachInsertedCartridge();
        Slot.Eject();
        ActiveMotherboard = ActiveNesMotherboard.None;
    }

    public void PowerOn()
    {
        if (!Slot.IsOccupied || ActiveMotherboard == ActiveNesMotherboard.None)
            throw new InvalidOperationException("Insert a ROM before powering on the virtual machine.");
        if (IsPowered)
            return;

        switch (ActiveMotherboard)
        {
            case ActiveNesMotherboard.Famicom:
                Famicom.PowerOn();
                break;
            case ActiveNesMotherboard.NtscNes:
                NtscNes.PowerOn();
                break;
            case ActiveNesMotherboard.PalNes:
                PalNes.PowerOn();
                break;
            default:
                throw new InvalidOperationException("No motherboard is selected.");
        }

        IsPowered = true;
    }

    public void PowerOff()
    {
        // Board packages currently model power application, not analog rail
        // discharge. Host power-off prevents all further clock advancement and
        // permits ROM/board reselection.
        IsPowered = false;
    }

    public void ReleaseReset()
    {
        EnsurePowered();
        switch (ActiveMotherboard)
        {
            case ActiveNesMotherboard.Famicom: Famicom.ReleaseReset(); break;
            case ActiveNesMotherboard.NtscNes: NtscNes.ReleaseReset(); break;
            case ActiveNesMotherboard.PalNes: PalNes.ReleaseReset(); break;
        }
    }

    public void AdvanceMasterCycles(int cycles)
    {
        EnsurePowered();
        switch (ActiveMotherboard)
        {
            case ActiveNesMotherboard.Famicom: Famicom.AdvanceMasterCycles(cycles); break;
            case ActiveNesMotherboard.NtscNes: NtscNes.AdvanceMasterCycles(cycles); break;
            case ActiveNesMotherboard.PalNes: PalNes.AdvanceMasterCycles(cycles); break;
        }
    }

    public void SetCompiledLabExecutionEnabled(bool enabled)
    {
        if (IsPowered) throw new InvalidOperationException("Change execution mode only while powered off.");
        CompiledLabExecutionRequested = enabled;

        if (!enabled)
        {
            Famicom.SetCompiledLabMotherboardEnabled(false);
            return;
        }

        if (ActiveMotherboard == ActiveNesMotherboard.Famicom)
            Famicom.SetCompiledLabMotherboardEnabled(true);
    }

    private void SelectResolvedMotherboard(NesResolvedRegion resolved, PalCicVariant palCicVariant)
    {
        ActiveMotherboard = resolved.Region switch
        {
            NesHardwareRegion.NtscJapan => ActiveNesMotherboard.Famicom,
            NesHardwareRegion.NtscNorthAmerica => ActiveNesMotherboard.NtscNes,
            NesHardwareRegion.Pal => ActiveNesMotherboard.PalNes,
            _ => throw new ArgumentOutOfRangeException(nameof(resolved.Region), resolved.Region, null)
        };

        if (ActiveMotherboard == ActiveNesMotherboard.PalNes && _constructedPalVariant != palCicVariant)
        {
            _constructedPalVariant = palCicVariant;
            PalNes = new PalNesMotherboard(_constructedPalVariant);
        }

        SelectionCount++;
    }


    private void AttachInsertedCartridge()
    {
        var cartridge = Slot.Cartridge ?? throw new InvalidOperationException("No cartridge hardware was constructed.");
        switch (ActiveMotherboard)
        {
            case ActiveNesMotherboard.Famicom:
                Slot.AttachTo(Famicom);
                Famicom.RecompileTopology();
                Famicom.AttachCompiledExternalDevice(cartridge);
                break;
            case ActiveNesMotherboard.NtscNes:
                Slot.AttachTo(NtscNes);
                NtscNes.RecompileTopology();
                break;
            case ActiveNesMotherboard.PalNes:
                Slot.AttachTo(PalNes);
                PalNes.RecompileTopology();
                break;
        }
    }

    private void DetachInsertedCartridge()
    {
        var cartridge = Slot.Cartridge;
        if (cartridge is null) return;
        switch (ActiveMotherboard)
        {
            case ActiveNesMotherboard.Famicom:
                // Remove the package from the physical netlist first, then rebuild
                // the compiled external binding. Bind-time topology proofs and
                // cached signal samplers must observe the post-ejection circuit,
                // not the connector state that existed one instruction earlier.
                Famicom.Board.Remove(cartridge);
                Famicom.RecompileTopology();
                Famicom.DetachCompiledExternalDevice(cartridge);
                break;
            case ActiveNesMotherboard.NtscNes:
                NtscNes.Board.Remove(cartridge);
                NtscNes.RecompileTopology();
                break;
            case ActiveNesMotherboard.PalNes:
                PalNes.Board.Remove(cartridge);
                PalNes.RecompileTopology();
                break;
        }
    }

    private void EnsurePowered()
    {
        if (!IsPowered)
            throw new InvalidOperationException("The virtual machine is not powered.");
    }
}
