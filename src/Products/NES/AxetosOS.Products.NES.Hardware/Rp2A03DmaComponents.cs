using AxetosOS.Products.NES.Abstractions;

namespace AxetosOS.Products.NES.Hardware;

/// <summary>
/// Inspectable view of the RP2A03 OAM DMA channel. The view owns no duplicate
/// state; every property is read directly from the live DMA controller.
/// </summary>
public sealed class OamDmaChannelModule : INesHardwareModule
{
    private readonly OamDmaController _controller;

    internal OamDmaChannelModule(OamDmaController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    public string ModuleId => "nes.rp2a03.dma.oam-channel";
    public bool Active => _controller.TransferActive;
    public byte SourcePage => _controller.LastPage;
    public int ByteOffset => _controller.BytesTransferred;
    public ushort CurrentSourceAddress => _controller.CurrentOamSourceAddress;
    public bool ReadPhase => _controller.OamReadPhase;
    public byte DataLatch => _controller.DataLatch;
    public int PendingDummyCycles => _controller.PendingOamDummyCycles;
    public ulong CompletedTransfers => _controller.Transfers;

    public void PowerOn() { }
    public void Reset() { }
}

/// <summary>
/// Inspectable view of the RP2A03 DMC sample DMA channel.
/// </summary>
public sealed class DmcDmaChannelModule : INesHardwareModule
{
    private readonly OamDmaController _controller;

    internal DmcDmaChannelModule(OamDmaController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    public string ModuleId => "nes.rp2a03.dma.dmc-channel";
    public bool Active => _controller.DmcTransferActive;
    public ushort SourceAddress => _controller.DmcSourceAddress;
    public int PendingStandaloneCycles => _controller.PendingDmcStandaloneCycles;
    public int PendingOverlapCycles => _controller.PendingDmcOverlapCycles;

    public void PowerOn() { }
    public void Reset() { }
}

/// <summary>
/// Inspectable view of DMA arbitration and CPU RDY ownership.
/// </summary>
public sealed class DmaBusArbiterModule : INesHardwareModule
{
    private readonly OamDmaController _controller;

    internal DmaBusArbiterModule(OamDmaController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    public string ModuleId => "nes.rp2a03.dma.bus-arbiter";
    public bool OwnsCpuBus => _controller.BusOwned;
    public string ActiveOwner => _controller.TransferActive
        ? (_controller.DmcTransferActive ? "OAM+DMC" : "OAM")
        : (_controller.DmcTransferActive ? "DMC" : "CPU");
    public int PendingOamRealignCycles => _controller.PendingOamRealignCycles;

    public void PowerOn() { }
    public void Reset() { }
}
