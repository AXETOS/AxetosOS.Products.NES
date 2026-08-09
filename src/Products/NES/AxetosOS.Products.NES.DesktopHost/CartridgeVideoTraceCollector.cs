using AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;

internal sealed class CartridgeVideoTraceCollector
{
    private const ulong HashOffset = 14_695_981_039_346_656_037UL;
    private const ulong HashPrime = 1_099_511_628_211UL;
    private readonly Mmc1Cartridge _cartridge;
    private readonly Rp2C02 _ppu;
    private readonly ulong _startFrame;
    private readonly ulong _endFrame;
    private readonly Dictionary<ulong, FrameAccumulator> _frames = [];
    private bool _fetchCaptureEnabled;
    private bool _mappingStateKnown;
    private byte _lastControl;
    private byte _lastChr0;
    private byte _lastChr1;

    public CartridgeVideoTraceCollector(Mmc1Cartridge cartridge, Rp2C02 ppu, ulong startFrame, ulong endFrame)
    {
        _cartridge = cartridge;
        _ppu = ppu;
        _startFrame = startFrame;
        _endFrame = endFrame;
    }

    public void UpdateFetchCapture(ulong currentPpuFrame)
    {
        if (!_fetchCaptureEnabled && currentPpuFrame + 1 >= _startFrame && currentPpuFrame <= _endFrame)
        {
            _ppu.RenderingFetchTraceOutput.SetCaptureEnabled(true);
            _cartridge.RegisterTraceOutput.SetCaptureEnabled(true);
            _fetchCaptureEnabled = true;
        }
        else if (_fetchCaptureEnabled && currentPpuFrame > _endFrame)
        {
            DrainCapturedSignals();
            _ppu.RenderingFetchTraceOutput.SetCaptureEnabled(false);
            _cartridge.RegisterTraceOutput.SetCaptureEnabled(false);
            _fetchCaptureEnabled = false;
        }
    }

    public void DrainCapturedSignals()
    {
        if (!_fetchCaptureEnabled
            && _ppu.RenderingFetchTraceOutput.CapturedCount == 0
            && _cartridge.RegisterTraceOutput.CapturedCount == 0) return;

        _cartridge.RegisterTraceOutput.Drain(OnRegister);
        _ppu.RenderingFetchTraceOutput.Drain(OnFetch);
    }

    public void OnRegister(Mmc1RegisterTraceEvent trace)
    {
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

        Console.WriteLine(
            $"CART MMC1: frame={frame:N0}; scanline={_ppu.Scanline}; dot={_ppu.Dot}; op={trace.Operation}; " +
            $"write=${trace.Address:X4}:${trace.Data:X2}; control=${trace.Control:X2}; chr0=${trace.ChrBank0:X2}; " +
            $"chr1=${trace.ChrBank1:X2}; prg=${trace.PrgBank:X2}; mapper-write={trace.MapperWriteCount:N0}");
    }

    public void OnCompletedFrame(ulong frame, ReadOnlySpan<uint> pixels)
    {
        if (frame < _startFrame || frame > _endFrame) return;
        var accumulator = GetFrame(frame);
        accumulator.PixelHash = HashPixels(pixels);
        for (var quarter = 0; quarter < 4; quarter++)
            accumulator.PixelQuarterHashes[quarter] = HashPixelRows(pixels, quarter * 60, 60);
        PrintAndClear(accumulator);
        _frames.Remove(frame);
    }

    public void Finish()
    {
        DrainCapturedSignals();
        _ppu.RenderingFetchTraceOutput.SetCaptureEnabled(false);
        _cartridge.RegisterTraceOutput.SetCaptureEnabled(false);
        _fetchCaptureEnabled = false;
        foreach (var frame in _frames.Values.OrderBy(static frame => frame.Frame))
            if (!frame.Printed) PrintAndClear(frame);
        _frames.Clear();
    }

    private void OnFetch(Rp2C02RenderingFetchTraceEvent trace)
    {
        if (trace.Frame < _startFrame || trace.Frame > _endFrame) return;
        var accumulator = GetFrame(trace.Frame);
        var mapping = _cartridge.InspectPpuMapping(trace.Address);
        accumulator.Fetches++;

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

    private FrameAccumulator GetFrame(ulong frame)
    {
        if (_frames.TryGetValue(frame, out var existing)) return existing;
        var created = new FrameAccumulator(frame);
        _frames.Add(frame, created);
        return created;
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
            $"pixels=${frame.PixelHash:X16}; pixel-q=${frame.PixelQuarterHashes[0]:X16}/${frame.PixelQuarterHashes[1]:X16}/${frame.PixelQuarterHashes[2]:X16}/${frame.PixelQuarterHashes[3]:X16}");
    }

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
        public ulong MappingHash { get; set; }
        public ulong[] MappingQuarterHashes { get; } = new ulong[4];
        public ulong PixelHash { get; set; } = HashOffset;
        public ulong[] PixelQuarterHashes { get; } = new ulong[4];
    }
}
