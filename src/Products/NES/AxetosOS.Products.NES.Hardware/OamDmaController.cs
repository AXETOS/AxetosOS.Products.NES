using AxetosOS.Products.NES.Abstractions;

namespace AxetosOS.Products.NES.Hardware;

public sealed record OamDmaTraceEvent(
    string Kind,
    ulong CpuCycle,
    byte Page,
    int Offset,
    ushort SourceAddress,
    byte Value);

public sealed class OamDmaController : INesHardwareModule, ICpuBusDevice, IHardwareCompositeModule
{
    private readonly CpuBus _bus;
    private readonly Rp2C02Ppu _ppu;
    private readonly Rp2A03Cpu _cpu;
    private int _dummyCyclesRemaining;
    private int _offset;
    private bool _readPhase;
    private byte _latchedValue;
    private bool _transferActive;
    private Rp2A03Apu? _apu;
    private bool _dmcPending;
    private ushort _dmcAddress;
    private int _dmcStandaloneCycles;
    private int _dmcOverlapCycles;
    private int _oamRealignCycles;

    public OamDmaController(CpuBus bus, Rp2C02Ppu ppu, Rp2A03Cpu cpu)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _ppu = ppu ?? throw new ArgumentNullException(nameof(ppu));
        _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));
        _cpu.StallCycle += ClockDmaCycle;
        OamChannel = new OamDmaChannelModule(this);
        DmcChannel = new DmcDmaChannelModule(this);
        BusArbiter = new DmaBusArbiterModule(this);

        HardwareComponents =
        [
            new(ModuleId, "RP2A03 DMA controller", HardwareComponentKind.DmaController, this),
            new(OamChannel.ModuleId, "OAM DMA channel", HardwareComponentKind.DmaController, OamChannel),
            new(DmcChannel.ModuleId, "DMC sample DMA channel", HardwareComponentKind.DmaController, DmcChannel),
            new(BusArbiter.ModuleId, "DMA bus arbiter", HardwareComponentKind.Internal, BusArbiter)
        ];

        HardwareConnections =
        [
            new(ModuleId, OamChannel.ModuleId, HardwareConnectionKind.Internal, "$4014 OAM transfer state"),
            new(ModuleId, DmcChannel.ModuleId, HardwareConnectionKind.Internal, "DMC sample fetch state"),
            new(OamChannel.ModuleId, BusArbiter.ModuleId, HardwareConnectionKind.Dma, "OAM bus request"),
            new(DmcChannel.ModuleId, BusArbiter.ModuleId, HardwareConnectionKind.Dma, "DMC bus request"),
            new(BusArbiter.ModuleId, _cpu.ModuleId, HardwareConnectionKind.Signal, "RDY/bus ownership")
        ];
    }

    public string ModuleId => "nes.dma.oam";
    public OamDmaChannelModule OamChannel { get; }
    public DmcDmaChannelModule DmcChannel { get; }
    public DmaBusArbiterModule BusArbiter { get; }
    public IReadOnlyList<HardwareComponentDescriptor> HardwareComponents { get; }
    public IReadOnlyList<HardwareConnectionDescriptor> HardwareConnections { get; }
    public byte LastPage { get; private set; }
    public ulong Transfers { get; private set; }
    public bool TransferActive => _transferActive;
    public bool DmcTransferActive => _dmcPending;
    public bool BusOwned => _transferActive || _dmcPending;
    public int BytesTransferred => _offset;
    public ushort CurrentOamSourceAddress => (ushort)((LastPage << 8) | (_offset & 0xFF));
    public bool OamReadPhase => _readPhase;
    public byte DataLatch => _latchedValue;
    public int PendingOamDummyCycles => _dummyCyclesRemaining;
    public ushort DmcSourceAddress => _dmcAddress;
    public int PendingDmcStandaloneCycles => _dmcStandaloneCycles;
    public int PendingDmcOverlapCycles => _dmcOverlapCycles;
    public int PendingOamRealignCycles => _oamRealignCycles;
    public bool DiagnosticsTraceEnabled { get; set; }
    public event Action<OamDmaTraceEvent>? TraceEvent;

    public void AttachDmc(Rp2A03Apu apu)
    {
        _apu = apu ?? throw new ArgumentNullException(nameof(apu));
        _apu.AttachDmcDma(RequestDmcDma);
    }

    public void PowerOn()
    {
        LastPage = 0;
        Transfers = 0;
        ResetTransferState();
    }

    public void Reset() => ResetTransferState();

    public bool HandlesCpuAddress(ushort address) => address == 0x4014;

    public byte CpuRead(ushort address) => LastPage;

    public void CpuWrite(ushort address, byte value)
    {
        LastPage = value;
        _offset = 0;
        _latchedValue = 0;
        _readPhase = true;
        _transferActive = true;

        // OAM DMA consumes one halt cycle, one additional alignment cycle when
        // initiated on an odd CPU cycle, and 256 alternating read/write pairs.
        var alignmentCycles = (int)(_cpu.TotalCycles & 1);
        _dummyCyclesRemaining = 1 + alignmentCycles;

        // OAM DMA becomes the external CPU-bus owner. Pulling RDY low freezes
        // the CPU micro-operation while PPU/APU clocks continue. Each stalled
        // CPU edge advances one DMA phase through the StallCycle event.
        _cpu.Signals.Rdy.Release();
        EmitTrace("dma-start", _cpu.TotalCycles, 0, (ushort)(LastPage << 8), 0);
    }

    private void ClockDmaCycle(ulong cpuCycle)
    {
        if (_dmcPending && !_transferActive)
        {
            ClockStandaloneDmc(cpuCycle);
            return;
        }

        if (!_transferActive) return;

        if (_dummyCyclesRemaining > 0)
        {
            _dummyCyclesRemaining--;
            ClockDmcOverlapCountdown();
            return;
        }

        if (_oamRealignCycles > 0)
        {
            _oamRealignCycles--;
            return;
        }

        // While OAM DMA already owns the bus, DMC halt/dummy/alignment cycles
        // overlap the OAM transfer. On the next OAM get cycle DMC wins bus
        // arbitration, then OAM spends one cycle restoring get/put alignment.
        if (_dmcPending && _dmcOverlapCycles == 0 && _readPhase)
        {
            CompleteDmcRead(cpuCycle);
            _oamRealignCycles = 1;
            return;
        }

        if (_readPhase)
        {
            var sourceAddress = (ushort)((LastPage << 8) | _offset);
            _latchedValue = _bus.Read(sourceAddress);
            if (_offset < 4)
                EmitTrace("dma-source-read", cpuCycle, _offset, sourceAddress, _latchedValue);
            _readPhase = false;
            ClockDmcOverlapCountdown();
            return;
        }

        _ppu.WriteOamDmaByte(_latchedValue);
        _offset++;
        _readPhase = true;
        ClockDmcOverlapCountdown();

        if (_offset < 256) return;

        _transferActive = false;
        Transfers++;
        EmitTrace("dma-complete", cpuCycle, 255, (ushort)((LastPage << 8) | 0xFF), _latchedValue);
        if (!_dmcPending) _cpu.Signals.Rdy.Assert();
    }

    private void RequestDmcDma(ushort address)
    {
        if (_dmcPending) return;

        _dmcPending = true;
        _dmcAddress = address;
        if (_transferActive)
        {
            _dmcOverlapCycles = 3;
        }
        else
        {
            // Halt, dummy and optional alignment/setup precede the get. The
            // current CPU does not yet expose read/write microcycle type, so
            // reload DMA uses the conservative four-cycle sequence.
            _dmcStandaloneCycles = 3;
            _cpu.Signals.Rdy.Release();
        }

        EmitTrace("dmc-request", _cpu.TotalCycles, 0, address, 0);
    }

    private void ClockStandaloneDmc(ulong cpuCycle)
    {
        if (_dmcStandaloneCycles > 0)
        {
            _dmcStandaloneCycles--;
            return;
        }

        CompleteDmcRead(cpuCycle);
        _cpu.Signals.Rdy.Assert();
    }

    private void ClockDmcOverlapCountdown()
    {
        if (_dmcPending && _dmcOverlapCycles > 0) _dmcOverlapCycles--;
    }

    private void CompleteDmcRead(ulong cpuCycle)
    {
        var value = _bus.Read(_dmcAddress);
        EmitTrace("dmc-source-read", cpuCycle, 0, _dmcAddress, value);
        _dmcPending = false;
        _dmcStandaloneCycles = 0;
        _dmcOverlapCycles = 0;
        _apu?.CompleteDmcDma(value);
    }

    private void EmitTrace(string kind, ulong cpuCycle, int offset, ushort sourceAddress, byte value)
    {
        if (DiagnosticsTraceEnabled)
            TraceEvent?.Invoke(new OamDmaTraceEvent(kind, cpuCycle, LastPage, offset, sourceAddress, value));
    }

    private void ResetTransferState()
    {
        _dummyCyclesRemaining = 0;
        _offset = 0;
        _readPhase = true;
        _latchedValue = 0;
        _transferActive = false;
        _dmcPending = false;
        _dmcAddress = 0;
        _dmcStandaloneCycles = 0;
        _dmcOverlapCycles = 0;
        _oamRealignCycles = 0;
        _cpu.Signals.Rdy.Assert();
    }
}
