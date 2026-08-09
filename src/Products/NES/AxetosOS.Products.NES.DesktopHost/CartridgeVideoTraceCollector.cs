using AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;

internal sealed class CartridgeVideoTraceCollector
{
    private const ulong HashOffset = 14_695_981_039_346_656_037UL;
    private const ulong HashPrime = 1_099_511_628_211UL;
    private const int CpuFetchTailCapacity = 16;
    private readonly Mmc1Cartridge _cartridge;
    private readonly Rp2C02 _ppu;
    private readonly Rp2A03 _cpu;
    private readonly ulong _startFrame;
    private readonly ulong _endFrame;
    private readonly Dictionary<ulong, FrameAccumulator> _frames = [];
    private readonly Queue<Mmc1ChrReadTraceEvent> _pendingChrReadTraces = new();
    private bool _fetchCaptureEnabled;
    private bool _mappingStateKnown;
    private byte _lastControl;
    private byte _lastChr0;
    private byte _lastChr1;
    private ulong _lastObservedNmiEdgeCount;
    private ulong _lastNmiCpuCycle;
    private ulong _lastNmiDmcReadCount;
    private ulong _lastNmiDmcStallCount;
    private ulong _lastNmiDmaTransferCount;
    private ulong _statusReadsFromNmi;
    private ulong _statusSpriteZeroClearReadsFromNmi;
    private ulong _statusSpriteZeroSetReadsFromNmi;
    private ulong _exactStatusReadsFromNmi;
    private ulong _exactStatusSpriteZeroClearReadsFromNmi;
    private ulong _exactStatusSpriteZeroSetReadsFromNmi;
    private ulong _exactStatusVblankClearReadsFromNmi;
    private ulong _exactStatusVblankSetReadsFromNmi;
    private Rp2C02SplitTraceEvent? _lastExactStatusRead;
    private Rp2C02SplitTraceEvent? _lastExactSpriteZeroClearStatusRead;
    private Rp2C02SplitTraceEvent? _firstExactSpriteZeroSetStatusRead;
    private bool _spriteZeroAtLastNmi;
    private ulong _lastStatusReadCpuCycle = ulong.MaxValue;
    private PpuStatusReadSample? _lastStatusRead;
    private PpuStatusReadSample? _lastSpriteZeroClearStatusRead;
    private PpuStatusReadSample? _firstSpriteZeroSetStatusRead;
    private ulong _lastOpcodeFetchCpuCycle = ulong.MaxValue;
    private readonly Queue<CpuFetchSample> _recentOpcodeFetches = new();
    private bool _previousSpriteZeroHit;
    private ulong _lastSpriteZeroCpuCycle;
    private ulong _lastSpriteZeroFrame = ulong.MaxValue;
    private int _lastSpriteZeroScanline = -1;
    private int _lastSpriteZeroDot = -1;

    public CartridgeVideoTraceCollector(Mmc1Cartridge cartridge, Rp2C02 ppu, Rp2A03 cpu, ulong startFrame, ulong endFrame)
    {
        _cartridge = cartridge;
        _ppu = ppu;
        _cpu = cpu;
        _startFrame = startFrame;
        _endFrame = endFrame;
        _lastObservedNmiEdgeCount = ppu.NmiFallingEdgeCount;
    }

    /// <summary>
    /// Samples only already-public chip diagnostic state from the host side.
    /// No chip retains a peer reference and this method never feeds execution.
    /// At precision-trace cadence it lets a cartridge commit be related to the
    /// NMI/sprite-zero/DMA timing that led the CPU to that physical write edge.
    /// </summary>
    public void ObserveTimingSample()
    {
        if (!_fetchCaptureEnabled) return;

        var cpuCycle = _cpu.ApuCpuCycleCount;
        if (_ppu.NmiFallingEdgeCount != _lastObservedNmiEdgeCount)
        {
            _lastObservedNmiEdgeCount = _ppu.NmiFallingEdgeCount;
            _lastNmiCpuCycle = cpuCycle;
            _lastNmiDmcReadCount = _cpu.DmcMemoryReadCount;
            _lastNmiDmcStallCount = _cpu.DmcCpuStallCount;
            _lastNmiDmaTransferCount = _cpu.DmaTransferCount;
            _statusReadsFromNmi = 0;
            _statusSpriteZeroClearReadsFromNmi = 0;
            _statusSpriteZeroSetReadsFromNmi = 0;
            _exactStatusReadsFromNmi = 0;
            _exactStatusSpriteZeroClearReadsFromNmi = 0;
            _exactStatusSpriteZeroSetReadsFromNmi = 0;
            _exactStatusVblankClearReadsFromNmi = 0;
            _exactStatusVblankSetReadsFromNmi = 0;
            _lastExactStatusRead = null;
            _lastExactSpriteZeroClearStatusRead = null;
            _firstExactSpriteZeroSetStatusRead = null;
            _spriteZeroAtLastNmi = _ppu.SpriteZeroHit;
            _lastStatusRead = null;
            _lastSpriteZeroClearStatusRead = null;
            _firstSpriteZeroSetStatusRead = null;
            _lastStatusReadCpuCycle = ulong.MaxValue;
        }

        ObserveOpcodeFetch(cpuCycle);
        ObservePpuStatusRead(cpuCycle);

        var spriteZeroHit = _ppu.SpriteZeroHit;
        if (spriteZeroHit && !_previousSpriteZeroHit)
        {
            _lastSpriteZeroCpuCycle = cpuCycle;
            _lastSpriteZeroFrame = _ppu.Frame;
            _lastSpriteZeroScanline = _ppu.Scanline;
            _lastSpriteZeroDot = _ppu.Dot;
        }
        _previousSpriteZeroHit = spriteZeroHit;
    }

    /// <summary>
    /// The normal desktop loop advances many thousands of master clocks at once.
    /// While a cartridge/video trace is active, ask the host to drain diagnostic
    /// outputs at CPU-cycle granularity so a mapper commit can be correlated with
    /// the live PPU raster instead of being stamped several scanlines late.
    /// This changes only host observation cadence, never hardware execution.
    /// </summary>
    public bool RequiresFineTiming => _fetchCaptureEnabled;

    public void UpdateFetchCapture(ulong currentPpuFrame)
    {
        if (!_fetchCaptureEnabled && currentPpuFrame + 1 >= _startFrame && currentPpuFrame <= _endFrame)
        {
            _pendingChrReadTraces.Clear();
            _ppu.RenderingFetchTraceOutput.SetCaptureEnabled(true);
            _cartridge.RegisterTraceOutput.SetCaptureEnabled(true);
            _cartridge.ChrReadTraceOutput.SetCaptureEnabled(true);
            _fetchCaptureEnabled = true;
        }
        else if (_fetchCaptureEnabled && currentPpuFrame > _endFrame)
        {
            DrainCapturedSignals();
            _ppu.RenderingFetchTraceOutput.SetCaptureEnabled(false);
            _cartridge.RegisterTraceOutput.SetCaptureEnabled(false);
            _cartridge.ChrReadTraceOutput.SetCaptureEnabled(false);
            _fetchCaptureEnabled = false;
        }
    }

    public void DrainCapturedSignals()
    {
        if (!_fetchCaptureEnabled
            && _ppu.RenderingFetchTraceOutput.CapturedCount == 0
            && _cartridge.RegisterTraceOutput.CapturedCount == 0
            && _cartridge.ChrReadTraceOutput.CapturedCount == 0)
        {
            FlushCompletedFramesBefore(_ppu.Frame);
            return;
        }

        _cartridge.RegisterTraceOutput.Drain(OnRegister);
        _cartridge.ChrReadTraceOutput.Drain(OnChrRead);
        _ppu.RenderingFetchTraceOutput.Drain(OnFetch);
        FlushCompletedFramesBefore(_ppu.Frame);
    }

    /// <summary>
    /// Consumes the RP2C02's existing external split-trace output. PPUSTATUS
    /// events carry the exact byte produced by the PPU before the read side
    /// effects clear vblank. This is the authoritative status value; the CPU
    /// bus sampler remains useful for address/PC provenance but can be between
    /// electrical read phases when the host observes it.
    /// </summary>
    public void OnPpuSplitTrace(Rp2C02SplitTraceEvent trace)
    {
        if (!_fetchCaptureEnabled) return;

        if (trace.Operation.StartsWith("sprite-zero", StringComparison.Ordinal)
            && trace.Frame >= _startFrame
            && trace.Frame <= _endFrame)
        {
            RecordSpriteZeroTrace(trace);
        }

        if (trace.Operation != "PPUSTATUS read") return;

        _exactStatusReadsFromNmi++;
        _lastExactStatusRead = trace;

        if ((trace.Value & 0x40) == 0)
        {
            _exactStatusSpriteZeroClearReadsFromNmi++;
            _lastExactSpriteZeroClearStatusRead = trace;
        }
        else
        {
            _exactStatusSpriteZeroSetReadsFromNmi++;
            _firstExactSpriteZeroSetStatusRead ??= trace;
        }

        if ((trace.Value & 0x80) == 0) _exactStatusVblankClearReadsFromNmi++;
        else _exactStatusVblankSetReadsFromNmi++;
    }

    private void RecordSpriteZeroTrace(Rp2C02SplitTraceEvent trace)
    {
        var accumulator = GetFrame(trace.Frame);
        CaptureSpriteZeroOam(accumulator);

        switch (trace.Operation)
        {
            case "sprite-zero active":
                accumulator.SpriteZeroActiveScanlines++;
                if (trace.Value < 16) accumulator.SpriteZeroRowMask |= (ushort)(1 << trace.Value);
                accumulator.SpriteZeroFirstActiveScanline = Math.Min(accumulator.SpriteZeroFirstActiveScanline, trace.Scanline);
                accumulator.SpriteZeroLastActiveScanline = Math.Max(accumulator.SpriteZeroLastActiveScanline, trace.Scanline);
                break;
            case "sprite-zero pattern-low":
                accumulator.SpriteZeroPatternLowFetches++;
                if (trace.Value != 0) accumulator.SpriteZeroPatternLowNonZero++;
                accumulator.SpriteZeroPatternHash = Mix(accumulator.SpriteZeroPatternHash, trace.Value);
                break;
            case "sprite-zero pattern-high":
                accumulator.SpriteZeroPatternHighFetches++;
                if (trace.Value != 0) accumulator.SpriteZeroPatternHighNonZero++;
                accumulator.SpriteZeroPatternHash = Mix(accumulator.SpriteZeroPatternHash, trace.Value);
                break;
            case "sprite-zero bg-clear":
                accumulator.SpriteZeroBackgroundClearPixels++;
                accumulator.SpriteZeroFirstBackgroundClear ??= new RasterPoint(trace.Scanline, trace.Dot);
                break;
            case "sprite-zero overlap":
                accumulator.SpriteZeroOverlapPixels++;
                accumulator.SpriteZeroFirstOverlap ??= new RasterPoint(trace.Scanline, trace.Dot);
                break;
            case "sprite-zero masked":
                accumulator.SpriteZeroMaskedPixels++;
                break;
            case "sprite-zero x255":
                accumulator.SpriteZeroX255Pixels++;
                break;
            case "sprite-zero overlap-not-selected":
                accumulator.SpriteZeroNotSelectedPixels++;
                accumulator.SpriteZeroFirstNotSelected ??= new RasterPoint(trace.Scanline, trace.Dot);
                break;
            case "sprite-zero hit":
                accumulator.SpriteZeroHit ??= new RasterPoint(trace.Scanline, trace.Dot);
                break;
        }
    }

    private void CaptureSpriteZeroOam(FrameAccumulator accumulator)
    {
        if (accumulator.SpriteZeroOamKnown) return;
        accumulator.SpriteZeroOamKnown = true;
        accumulator.SpriteZeroY = _ppu.InspectOam(0);
        accumulator.SpriteZeroTile = _ppu.InspectOam(1);
        accumulator.SpriteZeroAttributes = _ppu.InspectOam(2);
        accumulator.SpriteZeroX = _ppu.InspectOam(3);
        accumulator.PpuControl = _ppu.ControlRegister;
        accumulator.PpuMask = _ppu.MaskRegister;
    }

    public void OnRegister(Mmc1RegisterTraceEvent trace)
    {
        // This raster value is observed externally when the buffered package
        // output is drained. v2.16.3 makes the host drain at one CPU bus-cycle
        // period while capture is active, bounding this observation to only a few
        // PPU dots after the real cartridge-local commit. PpuReadCountAtCommit is
        // captured by the cartridge itself at the exact commit and exposes the
        // remaining observation lag explicitly.
        var frame = _ppu.Frame;
        if (frame < _startFrame || frame > _endFrame) return;

        var accumulator = GetFrame(frame);
        accumulator.MapperEvents++;
        if (trace.Operation != Mmc1RegisterOperation.PrgBank)
            accumulator.VideoMapperEvents++;

        var mappingChanged = !_mappingStateKnown
            || trace.Control != _lastControl
            || trace.ChrBank0 != _lastChr0
            || trace.ChrBank1 != _lastChr1;
        _mappingStateKnown = true;
        _lastControl = trace.Control;
        _lastChr0 = trace.ChrBank0;
        _lastChr1 = trace.ChrBank1;

        // Print only changes that can alter PPU-visible cartridge wiring. PRG-only
        // commits are still counted in the per-frame summary but do not flood a
        // video trace that is specifically looking for CHR/CIRAM corruption.
        if (!mappingChanged || trace.Operation == Mmc1RegisterOperation.PrgBank) return;

        var ppuReadsNow = _cartridge.PpuReadCount;
        var ppuReadLag = ppuReadsNow >= trace.PpuReadCountAtCommit
            ? ppuReadsNow - trace.PpuReadCountAtCommit
            : 0;

        var cpuCycle = _cpu.ApuCpuCycleCount;
        var cyclesFromNmi = _lastNmiCpuCycle == 0 || cpuCycle < _lastNmiCpuCycle
            ? 0
            : cpuCycle - _lastNmiCpuCycle;
        var dmcReadsFromNmi = _cpu.DmcMemoryReadCount >= _lastNmiDmcReadCount
            ? _cpu.DmcMemoryReadCount - _lastNmiDmcReadCount
            : 0;
        var dmcStallsFromNmi = _cpu.DmcCpuStallCount >= _lastNmiDmcStallCount
            ? _cpu.DmcCpuStallCount - _lastNmiDmcStallCount
            : 0;
        var dmaFromNmi = _cpu.DmaTransferCount >= _lastNmiDmaTransferCount
            ? _cpu.DmaTransferCount - _lastNmiDmaTransferCount
            : 0;
        var spriteZeroAge = _lastSpriteZeroFrame == frame && cpuCycle >= _lastSpriteZeroCpuCycle
            ? cpuCycle - _lastSpriteZeroCpuCycle
            : ulong.MaxValue;
        var spriteZeroText = spriteZeroAge == ulong.MaxValue
            ? "none-this-frame"
            : $"{spriteZeroAge:N0}cy-after@{_lastSpriteZeroScanline}:{_lastSpriteZeroDot}";

        Console.WriteLine(
            $"CART MMC1: frame={frame:N0}; scanline~={_ppu.Scanline}; dot~={_ppu.Dot}; op={trace.Operation}; " +
            $"write=${trace.Address:X4}:${trace.Data:X2}; control=${trace.Control:X2}; chr0=${trace.ChrBank0:X2}; " +
            $"chr1=${trace.ChrBank1:X2}; prg=${trace.PrgBank:X2}; mapper-write={trace.MapperWriteCount:N0}; " +
            $"ppu-read-at-commit={trace.PpuReadCountAtCommit:N0}; ppu-write-at-commit={trace.PpuWriteCountAtCommit:N0}; " +
            $"ppu-read-lag={ppuReadLag:N0}; cpu-cycle={cpuCycle:N0}; from-nmi={cyclesFromNmi:N0}; " +
            $"pc=${_cpu.ProgramCounter:X4}; op=${_cpu.CurrentOpcode:X2}; cpu-state={_cpu.CurrentCycleState}; " +
            $"bus={(_cpu.CurrentBusIsRead ? 'R' : 'W')}${_cpu.CurrentBusAddress:X4}; insn={_cpu.CompletedInstructionCount:N0}; " +
            $"nmi-pending={_cpu.NmiPending}; interrupts={_cpu.CompletedInterruptCount:N0}; sprite0={spriteZeroText}; " +
            $"dmc-from-nmi={dmcReadsFromNmi:N0}/{dmcStallsFromNmi:N0}; oam-dma-bytes-from-nmi={dmaFromNmi:N0}; " +
            $"ppustatus-bus-from-nmi={_statusReadsFromNmi:N0}/{_statusSpriteZeroClearReadsFromNmi:N0}/{_statusSpriteZeroSetReadsFromNmi:N0}; " +
            $"ppustatus-exact-from-nmi={_exactStatusReadsFromNmi:N0}/{_exactStatusSpriteZeroClearReadsFromNmi:N0}/{_exactStatusSpriteZeroSetReadsFromNmi:N0}; " +
            $"ppustatus-vblank-exact={_exactStatusVblankClearReadsFromNmi:N0}/{_exactStatusVblankSetReadsFromNmi:N0}; s0-at-nmi={_spriteZeroAtLastNmi}; " +
            $"last-ppustatus={FormatExactStatusTrace(_lastExactStatusRead)}; " +
            $"last-s0-clear-status={FormatExactStatusTrace(_lastExactSpriteZeroClearStatusRead)}; " +
            $"first-s0-set-status={FormatExactStatusTrace(_firstExactSpriteZeroSetStatusRead)}; " +
            $"last-ppustatus-cpu={FormatStatusSample(_lastStatusRead, cpuCycle)}; " +
            $"fetch-tail={FormatOpcodeFetchTail()}");
    }

    private void ObserveOpcodeFetch(ulong cpuCycle)
    {
        if (!_cpu.SyncState || !_cpu.CurrentBusIsRead || cpuCycle == _lastOpcodeFetchCpuCycle) return;
        _lastOpcodeFetchCpuCycle = cpuCycle;

        var dataKnown = _cpu.TryInspectDataBus(out var opcode);
        _recentOpcodeFetches.Enqueue(new CpuFetchSample(
            _cpu.CurrentBusAddress,
            dataKnown,
            opcode));

        while (_recentOpcodeFetches.Count > CpuFetchTailCapacity)
            _recentOpcodeFetches.Dequeue();
    }

    private void ObservePpuStatusRead(ulong cpuCycle)
    {
        if (!_cpu.CurrentBusIsRead
            || !IsPpuStatusAddress(_cpu.CurrentBusAddress)
            || cpuCycle == _lastStatusReadCpuCycle)
        {
            return;
        }

        _lastStatusReadCpuCycle = cpuCycle;
        var dataKnown = _cpu.TryInspectDataBus(out var status);
        var sample = new PpuStatusReadSample(
            cpuCycle,
            _ppu.Frame,
            _ppu.Scanline,
            _ppu.Dot,
            _cpu.CurrentBusAddress,
            _cpu.ProgramCounter,
            _cpu.CurrentOpcode,
            dataKnown,
            status);

        _statusReadsFromNmi++;
        _lastStatusRead = sample;
        if (!dataKnown) return;

        if ((status & 0x40) == 0)
        {
            _statusSpriteZeroClearReadsFromNmi++;
            _lastSpriteZeroClearStatusRead = sample;
        }
        else
        {
            _statusSpriteZeroSetReadsFromNmi++;
            _firstSpriteZeroSetStatusRead ??= sample;
        }
    }

    private static bool IsPpuStatusAddress(ushort address) =>
        address is >= 0x2000 and <= 0x3FFF && (address & 0x0007) == 2;

    private static string FormatExactStatusTrace(Rp2C02SplitTraceEvent? trace)
    {
        if (trace is not { } value) return "none";
        var spriteZero = (value.Value & 0x40) != 0 ? 1 : 0;
        var vblank = (value.Value & 0x80) != 0 ? 1 : 0;
        return $"${value.Value:X2}@f{value.Frame:N0}:{value.Scanline}:{value.Dot}:s0={spriteZero}:vb={vblank}";
    }

    private static string FormatStatusSample(PpuStatusReadSample? sample, ulong currentCpuCycle)
    {
        if (sample is not { } value) return "none";
        var age = currentCpuCycle >= value.CpuCycle ? currentCpuCycle - value.CpuCycle : 0;
        var status = value.DataKnown ? $"${value.Status:X2}" : "??";
        return $"{age:N0}cy-ago@f{value.Frame:N0}:{value.Scanline}:{value.Dot}:a=${value.Address:X4}:v={status}:pc=${value.ProgramCounter:X4}:op=${value.Opcode:X2}";
    }

    private string FormatOpcodeFetchTail()
    {
        if (_recentOpcodeFetches.Count == 0) return "none";
        return string.Join(
            ",",
            _recentOpcodeFetches.Select(static sample =>
                sample.DataKnown
                    ? $"${sample.Address:X4}:${sample.Opcode:X2}"
                    : $"${sample.Address:X4}:??"));
    }

    public void OnCompletedFrame(ulong frame, ReadOnlySpan<uint> pixels)
    {
        if (frame < _startFrame || frame > _endFrame) return;
        var accumulator = GetFrame(frame);
        accumulator.PixelHash = HashPixels(pixels);
        for (var quarter = 0; quarter < 4; quarter++)
            accumulator.PixelQuarterHashes[quarter] = HashPixelRows(pixels, quarter * 60, 60);
        accumulator.PixelComplete = true;

        // RP2C0x still performs the pre-render scanline after the framebuffer has
        // completed. Keep this frame accumulator alive until the PPU frame counter
        // advances so those real pre-render cartridge fetches remain in the same
        // single frame summary instead of creating a misleading duplicate line.
        FlushCompletedFramesBefore(_ppu.Frame);
    }

    public void Finish()
    {
        DrainCapturedSignals();
        _ppu.RenderingFetchTraceOutput.SetCaptureEnabled(false);
        _cartridge.RegisterTraceOutput.SetCaptureEnabled(false);
        _cartridge.ChrReadTraceOutput.SetCaptureEnabled(false);
        _fetchCaptureEnabled = false;
        _pendingChrReadTraces.Clear();
        foreach (var frame in _frames.Values.OrderBy(static frame => frame.Frame))
            if (!frame.Printed) PrintAndClear(frame);
        _frames.Clear();
    }

    private void OnChrRead(Mmc1ChrReadTraceEvent trace) =>
        _pendingChrReadTraces.Enqueue(trace);

    private bool TryMatchChrRead(
        Rp2C02RenderingFetchTraceEvent trace,
        FrameAccumulator accumulator,
        out Mmc1ChrReadTraceEvent matched)
    {
        while (_pendingChrReadTraces.Count != 0)
        {
            var candidate = _pendingChrReadTraces.Dequeue();
            if (candidate.PpuAddress == trace.Address && candidate.Data == trace.Data)
            {
                matched = candidate;
                return true;
            }

            // CPU $2007 CHR reads are visible to the mapper but are not part of
            // RenderingFetchTraceOutput. Discard any such unmatched read and
            // retain the count in the frame diagnostic rather than forcing a
            // false pairing onto the next rendering fetch.
            accumulator.UnmatchedChrReadTraces++;
        }

        matched = default;
        return false;
    }

    private static bool IsChrRenderingFetch(Rp2C02RenderingFetchKind kind) =>
        kind is Rp2C02RenderingFetchKind.BackgroundPatternLow
            or Rp2C02RenderingFetchKind.BackgroundPatternHigh
            or Rp2C02RenderingFetchKind.SpritePatternLow
            or Rp2C02RenderingFetchKind.SpritePatternHigh;

    private static bool IsBackgroundPatternFetch(Rp2C02RenderingFetchKind kind) =>
        kind is Rp2C02RenderingFetchKind.BackgroundPatternLow
            or Rp2C02RenderingFetchKind.BackgroundPatternHigh;

    private void OnFetch(Rp2C02RenderingFetchTraceEvent trace)
    {
        if (trace.Frame < _startFrame || trace.Frame > _endFrame) return;
        var accumulator = GetFrame(trace.Frame);
        CaptureSpriteZeroOam(accumulator);

        Mmc1ChrReadTraceEvent exactChrRead = default;
        var hasExactChrRead = IsChrRenderingFetch(trace.Kind)
            && TryMatchChrRead(trace, accumulator, out exactChrRead);

        // For compiled CHR reads, v2.22.0 consumes the mapper's own read-complete
        // trace. That event is emitted at the exact CompleteRead callback and
        // therefore carries the physical CHR address/data and retained register
        // state that actually supplied this PPU fetch. CIRAM and a reference-path
        // fallback continue to use the inspection-only cartridge projection.
        var mapping = hasExactChrRead
            ? new Mmc1PpuMappingDiagnostic(
                exactChrRead.PpuAddress,
                Mmc1PpuMappingKind.Chr,
                exactChrRead.PhysicalAddress,
                exactChrRead.Bank4K,
                -1,
                exactChrRead.Control,
                exactChrRead.ChrBank0,
                exactChrRead.ChrBank1,
                exactChrRead.PrgBank)
            : _cartridge.InspectPpuMapping(trace.Address);
        accumulator.Fetches++;

        if (IsBackgroundPatternFetch(trace.Kind) && trace.Scanline is >= 17 and <= 24)
            RecordSpriteZeroBackgroundChrFetch(accumulator, trace, hasExactChrRead ? exactChrRead : null);

        var quarter = trace.Scanline is >= 0 and < 240 ? trace.Scanline / 60 : -1;
        accumulator.MappingHash = HashFetch(accumulator.MappingHash, trace, mapping);
        if (quarter >= 0)
            accumulator.MappingQuarterHashes[quarter] = HashFetch(accumulator.MappingQuarterHashes[quarter], trace, mapping);

        if (mapping.Kind == Mmc1PpuMappingKind.Chr)
        {
            accumulator.ChrFetches++;
            if ((uint)mapping.Bank4K < 32)
                accumulator.ChrBankMask |= 1u << mapping.Bank4K;
        }
        else if (mapping.Kind == Mmc1PpuMappingKind.Ciram)
        {
            accumulator.CiramFetches++;
            if ((uint)mapping.CiramPage < 2)
                accumulator.CiramPageMask |= 1u << mapping.CiramPage;
        }

        accumulator.Control = mapping.Control;
        accumulator.Chr0 = mapping.ChrBank0;
        accumulator.Chr1 = mapping.ChrBank1;
        accumulator.Prg = mapping.PrgBank;
    }

    private static void RecordSpriteZeroBackgroundChrFetch(
        FrameAccumulator accumulator,
        Rp2C02RenderingFetchTraceEvent trace,
        Mmc1ChrReadTraceEvent? exactRead)
    {
        accumulator.SpriteZeroBackgroundPatternFetches++;
        if (exactRead is not { } read)
        {
            accumulator.SpriteZeroBackgroundExactMisses++;
            return;
        }

        accumulator.SpriteZeroBackgroundExactReads++;
        if (!accumulator.SpriteZeroBackgroundPpuReadRangeKnown)
        {
            accumulator.SpriteZeroBackgroundPpuReadRangeKnown = true;
            accumulator.SpriteZeroBackgroundFirstPpuReadCount = read.PpuReadCount;
        }
        accumulator.SpriteZeroBackgroundLastPpuReadCount = read.PpuReadCount;

        if ((uint)read.Bank4K < 32)
            accumulator.SpriteZeroBackgroundBankMask |= 1u << read.Bank4K;

        accumulator.SpriteZeroBackgroundExactHash = Mix(accumulator.SpriteZeroBackgroundExactHash, (byte)trace.Kind);
        accumulator.SpriteZeroBackgroundExactHash = Mix(accumulator.SpriteZeroBackgroundExactHash, (byte)read.PpuAddress);
        accumulator.SpriteZeroBackgroundExactHash = Mix(accumulator.SpriteZeroBackgroundExactHash, (byte)(read.PpuAddress >> 8));
        accumulator.SpriteZeroBackgroundExactHash = Mix(accumulator.SpriteZeroBackgroundExactHash, (byte)read.PhysicalAddress);
        accumulator.SpriteZeroBackgroundExactHash = Mix(accumulator.SpriteZeroBackgroundExactHash, (byte)(read.PhysicalAddress >> 8));
        accumulator.SpriteZeroBackgroundExactHash = Mix(accumulator.SpriteZeroBackgroundExactHash, (byte)(read.PhysicalAddress >> 16));
        accumulator.SpriteZeroBackgroundExactHash = Mix(accumulator.SpriteZeroBackgroundExactHash, read.Data);
        accumulator.SpriteZeroBackgroundExactHash = Mix(accumulator.SpriteZeroBackgroundExactHash, read.ChrBank1);

        if (!accumulator.SpriteZeroBackgroundFirstChr1Known)
        {
            accumulator.SpriteZeroBackgroundFirstChr1Known = true;
            accumulator.SpriteZeroBackgroundFirstChr1 = read.ChrBank1;
            accumulator.SpriteZeroBackgroundLastChr1 = read.ChrBank1;
        }
        else if (read.ChrBank1 != accumulator.SpriteZeroBackgroundLastChr1)
        {
            accumulator.SpriteZeroBackgroundChr1Switch ??= new RasterPoint(trace.Scanline, trace.Dot);
            accumulator.SpriteZeroBackgroundChr1SwitchFrom = accumulator.SpriteZeroBackgroundLastChr1;
            accumulator.SpriteZeroBackgroundChr1SwitchTo = read.ChrBank1;
            accumulator.SpriteZeroBackgroundLastChr1 = read.ChrBank1;
        }

        if (!accumulator.SpriteZeroBackgroundBanks.TryGetValue(read.Bank4K, out var bank))
        {
            bank = new ChrBankWindowAccumulator();
            accumulator.SpriteZeroBackgroundBanks.Add(read.Bank4K, bank);
        }

        bank.Reads++;
        bank.Hash = Mix(bank.Hash, (byte)trace.Kind);
        bank.Hash = Mix(bank.Hash, (byte)read.PpuAddress);
        bank.Hash = Mix(bank.Hash, (byte)(read.PpuAddress >> 8));
        bank.Hash = Mix(bank.Hash, read.Data);
    }

    private FrameAccumulator GetFrame(ulong frame)
    {
        if (_frames.TryGetValue(frame, out var existing)) return existing;
        var created = new FrameAccumulator(frame);
        _frames.Add(frame, created);
        return created;
    }

    private void FlushCompletedFramesBefore(ulong currentFrame)
    {
        if (_frames.Count == 0) return;

        var completed = _frames.Values
            .Where(frame => frame.PixelComplete && frame.Frame < currentFrame)
            .OrderBy(static frame => frame.Frame)
            .ToArray();

        foreach (var frame in completed)
        {
            PrintAndClear(frame);
            _frames.Remove(frame.Frame);
        }
    }

    private void PrintAndClear(FrameAccumulator frame)
    {
        if (frame.Printed) return;
        frame.Printed = true;
        Console.WriteLine(
            $"CART FRAME: frame={frame.Frame:N0}; control=${frame.Control:X2}; chr0=${frame.Chr0:X2}; chr1=${frame.Chr1:X2}; prg=${frame.Prg:X2}; " +
            $"mapper-events={frame.MapperEvents:N0}; video-map-events={frame.VideoMapperEvents:N0}; fetches={frame.Fetches:N0}; " +
            $"chr={frame.ChrFetches:N0}; ciram={frame.CiramFetches:N0}; chr-banks=${frame.ChrBankMask:X8}; ciram-pages=${frame.CiramPageMask:X1}; " +
            $"map=${frame.MappingHash:X16}; map-q=${frame.MappingQuarterHashes[0]:X16}/${frame.MappingQuarterHashes[1]:X16}/${frame.MappingQuarterHashes[2]:X16}/${frame.MappingQuarterHashes[3]:X16}; " +
            $"pixels=${frame.PixelHash:X16}; pixel-q=${frame.PixelQuarterHashes[0]:X16}/${frame.PixelQuarterHashes[1]:X16}/${frame.PixelQuarterHashes[2]:X16}/${frame.PixelQuarterHashes[3]:X16}; " +
            $"ppu=${frame.PpuControl:X2}/${frame.PpuMask:X2}; s0-oam={FormatSpriteZeroOam(frame)}; " +
            $"s0-active={frame.SpriteZeroActiveScanlines:N0}@{FormatActiveRange(frame)}:rows=${frame.SpriteZeroRowMask:X4}; " +
            $"s0-pattern={frame.SpriteZeroPatternLowFetches:N0}/{frame.SpriteZeroPatternLowNonZero:N0}," +
            $"{frame.SpriteZeroPatternHighFetches:N0}/{frame.SpriteZeroPatternHighNonZero:N0}:${frame.SpriteZeroPatternHash:X16}; " +
            $"s0-pixels=bg0:{frame.SpriteZeroBackgroundClearPixels:N0}@{FormatRaster(frame.SpriteZeroFirstBackgroundClear)}," +
            $"overlap:{frame.SpriteZeroOverlapPixels:N0}@{FormatRaster(frame.SpriteZeroFirstOverlap)}," +
            $"masked:{frame.SpriteZeroMaskedPixels:N0},x255:{frame.SpriteZeroX255Pixels:N0}," +
            $"not-selected:{frame.SpriteZeroNotSelectedPixels:N0}@{FormatRaster(frame.SpriteZeroFirstNotSelected)}; " +
            $"s0-hit={FormatRaster(frame.SpriteZeroHit)}; " +
            $"s0-bg-chr={FormatSpriteZeroBackgroundChr(frame)}");
    }

    private static string FormatSpriteZeroBackgroundChr(FrameAccumulator frame)
    {
        var chr1 = !frame.SpriteZeroBackgroundFirstChr1Known
            ? "none"
            : frame.SpriteZeroBackgroundChr1Switch is { } transition
                ? $"${frame.SpriteZeroBackgroundChr1SwitchFrom:X2}->${frame.SpriteZeroBackgroundChr1SwitchTo:X2}@{transition.Scanline}:{transition.Dot}"
                : $"${frame.SpriteZeroBackgroundFirstChr1:X2}";

        var banks = frame.SpriteZeroBackgroundBanks.Count == 0
            ? "none"
            : string.Join(
                ",",
                frame.SpriteZeroBackgroundBanks
                    .OrderBy(static pair => pair.Key)
                    .Select(static pair => $"{pair.Key}:{pair.Value.Reads:N0}:${pair.Value.Hash:X16}"));

        var readRange = frame.SpriteZeroBackgroundPpuReadRangeKnown
            ? $"{frame.SpriteZeroBackgroundFirstPpuReadCount:N0}-{frame.SpriteZeroBackgroundLastPpuReadCount:N0}"
            : "none";

        return $"fetch={frame.SpriteZeroBackgroundPatternFetches:N0}/exact={frame.SpriteZeroBackgroundExactReads:N0}/" +
            $"miss={frame.SpriteZeroBackgroundExactMisses:N0}/orphan={frame.UnmatchedChrReadTraces:N0}," +
            $"seq={readRange},banks=${frame.SpriteZeroBackgroundBankMask:X8},chr1={chr1}," +
            $"bank-reads={banks},hash=${frame.SpriteZeroBackgroundExactHash:X16}";
    }

    private static string FormatSpriteZeroOam(FrameAccumulator frame) =>
        frame.SpriteZeroOamKnown
            ? $"${frame.SpriteZeroY:X2}/${frame.SpriteZeroTile:X2}/${frame.SpriteZeroAttributes:X2}/${frame.SpriteZeroX:X2}"
            : "unknown";

    private static string FormatActiveRange(FrameAccumulator frame) =>
        frame.SpriteZeroActiveScanlines == 0
            ? "none"
            : $"{frame.SpriteZeroFirstActiveScanline}-{frame.SpriteZeroLastActiveScanline}";

    private static string FormatRaster(RasterPoint? point) =>
        point is { } value ? $"{value.Scanline}:{value.Dot}" : "none";

    private static ulong HashFetch(ulong hash, Rp2C02RenderingFetchTraceEvent trace, Mmc1PpuMappingDiagnostic mapping)
    {
        hash = Mix(hash, (byte)trace.Kind);
        hash = Mix(hash, (byte)trace.Address);
        hash = Mix(hash, (byte)(trace.Address >> 8));
        hash = Mix(hash, trace.Data);
        hash = Mix(hash, (byte)mapping.Kind);
        hash = Mix(hash, (byte)mapping.PhysicalAddress);
        hash = Mix(hash, (byte)(mapping.PhysicalAddress >> 8));
        hash = Mix(hash, (byte)(mapping.PhysicalAddress >> 16));
        hash = Mix(hash, mapping.Control);
        hash = Mix(hash, mapping.ChrBank0);
        hash = Mix(hash, mapping.ChrBank1);
        return hash;
    }

    private static ulong HashPixels(ReadOnlySpan<uint> pixels)
    {
        var hash = HashOffset;
        foreach (var pixel in pixels)
        {
            hash = Mix(hash, (byte)pixel);
            hash = Mix(hash, (byte)(pixel >> 8));
            hash = Mix(hash, (byte)(pixel >> 16));
            hash = Mix(hash, (byte)(pixel >> 24));
        }
        return hash;
    }

    private static ulong HashPixelRows(ReadOnlySpan<uint> pixels, int startRow, int rowCount)
    {
        const int width = 256;
        var hash = HashOffset;
        var start = startRow * width;
        var length = Math.Min(rowCount * width, pixels.Length - start);
        if (start < 0 || start >= pixels.Length || length <= 0) return hash;
        var region = pixels.Slice(start, length);
        foreach (var pixel in region)
        {
            hash = Mix(hash, (byte)pixel);
            hash = Mix(hash, (byte)(pixel >> 8));
            hash = Mix(hash, (byte)(pixel >> 16));
            hash = Mix(hash, (byte)(pixel >> 24));
        }
        return hash;
    }

    private static ulong Mix(ulong hash, byte value)
    {
        hash ^= value;
        return hash * HashPrime;
    }

    private readonly record struct RasterPoint(int Scanline, int Dot);

    private readonly record struct PpuStatusReadSample(
        ulong CpuCycle,
        ulong Frame,
        int Scanline,
        int Dot,
        ushort Address,
        ushort ProgramCounter,
        byte Opcode,
        bool DataKnown,
        byte Status);

    private readonly record struct CpuFetchSample(
        ushort Address,
        bool DataKnown,
        byte Opcode);

    private sealed class ChrBankWindowAccumulator
    {
        public ulong Reads { get; set; }
        public ulong Hash { get; set; } = HashOffset;
    }

    private sealed class FrameAccumulator
    {
        public FrameAccumulator(ulong frame)
        {
            Frame = frame;
            MappingHash = HashOffset;
            for (var i = 0; i < MappingQuarterHashes.Length; i++) MappingQuarterHashes[i] = HashOffset;
            for (var i = 0; i < PixelQuarterHashes.Length; i++) PixelQuarterHashes[i] = HashOffset;
        }

        public ulong Frame { get; }
        public bool Printed { get; set; }
        public bool PixelComplete { get; set; }
        public ulong Fetches { get; set; }
        public ulong ChrFetches { get; set; }
        public ulong CiramFetches { get; set; }
        public ulong MapperEvents { get; set; }
        public ulong VideoMapperEvents { get; set; }
        public uint ChrBankMask { get; set; }
        public uint CiramPageMask { get; set; }
        public byte Control { get; set; }
        public byte Chr0 { get; set; }
        public byte Chr1 { get; set; }
        public byte Prg { get; set; }
        public bool SpriteZeroOamKnown { get; set; }
        public byte SpriteZeroY { get; set; }
        public byte SpriteZeroTile { get; set; }
        public byte SpriteZeroAttributes { get; set; }
        public byte SpriteZeroX { get; set; }
        public byte PpuControl { get; set; }
        public byte PpuMask { get; set; }
        public ulong SpriteZeroActiveScanlines { get; set; }
        public int SpriteZeroFirstActiveScanline { get; set; } = int.MaxValue;
        public int SpriteZeroLastActiveScanline { get; set; } = -1;
        public ushort SpriteZeroRowMask { get; set; }
        public ulong SpriteZeroPatternLowFetches { get; set; }
        public ulong SpriteZeroPatternLowNonZero { get; set; }
        public ulong SpriteZeroPatternHighFetches { get; set; }
        public ulong SpriteZeroPatternHighNonZero { get; set; }
        public ulong SpriteZeroPatternHash { get; set; } = HashOffset;
        public ulong SpriteZeroBackgroundClearPixels { get; set; }
        public ulong SpriteZeroOverlapPixels { get; set; }
        public ulong SpriteZeroMaskedPixels { get; set; }
        public ulong SpriteZeroX255Pixels { get; set; }
        public ulong SpriteZeroNotSelectedPixels { get; set; }
        public RasterPoint? SpriteZeroFirstBackgroundClear { get; set; }
        public RasterPoint? SpriteZeroFirstOverlap { get; set; }
        public RasterPoint? SpriteZeroFirstNotSelected { get; set; }
        public RasterPoint? SpriteZeroHit { get; set; }
        public ulong UnmatchedChrReadTraces { get; set; }
        public ulong SpriteZeroBackgroundPatternFetches { get; set; }
        public ulong SpriteZeroBackgroundExactReads { get; set; }
        public ulong SpriteZeroBackgroundExactMisses { get; set; }
        public bool SpriteZeroBackgroundPpuReadRangeKnown { get; set; }
        public ulong SpriteZeroBackgroundFirstPpuReadCount { get; set; }
        public ulong SpriteZeroBackgroundLastPpuReadCount { get; set; }
        public uint SpriteZeroBackgroundBankMask { get; set; }
        public bool SpriteZeroBackgroundFirstChr1Known { get; set; }
        public byte SpriteZeroBackgroundFirstChr1 { get; set; }
        public byte SpriteZeroBackgroundLastChr1 { get; set; }
        public byte SpriteZeroBackgroundChr1SwitchFrom { get; set; }
        public byte SpriteZeroBackgroundChr1SwitchTo { get; set; }
        public RasterPoint? SpriteZeroBackgroundChr1Switch { get; set; }
        public ulong SpriteZeroBackgroundExactHash { get; set; } = HashOffset;
        public Dictionary<int, ChrBankWindowAccumulator> SpriteZeroBackgroundBanks { get; } = [];
        public ulong MappingHash { get; set; }
        public ulong[] MappingQuarterHashes { get; } = new ulong[4];
        public ulong PixelHash { get; set; } = HashOffset;
        public ulong[] PixelQuarterHashes { get; } = new ulong[4];
    }
}
