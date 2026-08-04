using System.Diagnostics;
using AxetosOS.Audio.Windows;
using AxetosOS.Products.NES.Abstractions;
using AxetosOS.Products.NES.Cartridges;
using AxetosOS.Products.NES.Hardware;
using AxetosOS.Rendering.Abstractions;
using AxetosOS.Rendering.Windows;

NesTimingMode? manualTiming = null;
bool mapperTraceEnabled = false;
string? mapperTracePath = null;
string? romArgument = null;
for (var index = 0; index < args.Length; index++)
{
    if (args[index].Equals("--mapper-trace", StringComparison.OrdinalIgnoreCase))
    {
        mapperTraceEnabled = true;
        if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            mapperTracePath = args[++index];
        continue;
    }

    if (args[index].Equals("--timing", StringComparison.OrdinalIgnoreCase))
    {
        if (++index >= args.Length || !TryParseTiming(args[index], out manualTiming))
        {
            Console.Error.WriteLine("Timing must be auto, ntsc, pal or dendy.");
            return 2;
        }
        continue;
    }

    if (romArgument is not null)
    {
        Console.Error.WriteLine("Usage: DesktopHost [rom-path] [--timing auto|ntsc|pal|dendy] [--mapper-trace [path]]");
        return 2;
    }
    romArgument = args[index];
}

var selectedRomPath = romArgument ?? NativeFileDialog.OpenFile(
    "Open NES cartridge image",
    "NES cartridge images (*.nes)|*.nes|All files (*.*)|*.*",
    defaultExtension: "nes");

if (string.IsNullOrWhiteSpace(selectedRomPath)) return 0;

var romPath = Path.GetFullPath(selectedRomPath);
if (!File.Exists(romPath))
{
    Console.Error.WriteLine($"ROM file not found: {romPath}");
    return 3;
}

await using var romStream = File.OpenRead(romPath);
var image = NesRomReader.Read(romStream);
var timingSelection = NesTimingResolver.Resolve(image, romPath, manualTiming);
var timing = NesTimingProfile.For(timingSelection.Mode);
var catalogPath = Path.Combine(AppContext.BaseDirectory, "hardware", "mapper-catalog.json");
await using var catalogStream = File.OpenRead(catalogPath);
var catalog = MapperCatalog.Load(catalogStream);
MapperCatalogEntry mapper;
CartridgeHardware cartridge;
try
{
    mapper = catalog.Resolve(image.MapperNumber, image.SubmapperNumber);
    var boardPath = Path.Combine(AppContext.BaseDirectory, "hardware", mapper.Definition.Replace('/', Path.DirectorySeparatorChar));
    await using var boardStream = File.OpenRead(boardPath);
    var boardDefinition = CartridgeBoardDefinition.Load(boardStream);
    cartridge = CartridgeHardwareFactory.Create(image, boardDefinition);
}
catch (UnsupportedMapperException exception)
{
    var submapper = exception.Submapper?.ToString() ?? "unspecified";
    var message = $"Mapper {exception.Mapper}, submapper {submapper}, is not supported yet.\n\nSupported mappers: 0, 1, 2, 3, 4, 7, 11, 66, 71, 79 and 206.";
    NativeMessageDialog.ShowError("Unsupported cartridge hardware", message);
    Console.Error.WriteLine(message);
    return 4;
}

var ram = new CpuWorkRam();
var ciram = new CiramNametableRam(image.Mirroring);
var palette = new PpuPaletteRam();
ram.PowerOn();
ciram.PowerOn();
palette.PowerOn();
if (cartridge.ChrDevice is INesHardwareModule chrModule) chrModule.PowerOn();
if (cartridge.PrgDevice is ICartridgeMirroringProvider mirroringProvider)
{
    ciram.SetMirroring(mirroringProvider.Mirroring);
    mirroringProvider.MirroringChanged += ciram.SetMirroring;
}

var savePath = Path.ChangeExtension(romPath, ".sav");
var persistentMemory = cartridge.PrgDevice as IBatteryBackedMemory;
if (persistentMemory is { HasBattery: true } && File.Exists(savePath))
    persistentMemory.LoadPersistent(await File.ReadAllBytesAsync(savePath));

var ppuBus = new PpuBus();
ppuBus.Attach(cartridge.ChrDevice);
ppuBus.Attach(ciram);
ppuBus.Attach(palette);

var cpuSignals = new Rp2A03SignalLines();
var ppu = new Rp2C02Ppu(ppuBus, cpuSignals.Nmi, timing);
var cpuBus = new CpuBus();
cpuBus.Attach(ram);
cpuBus.Attach(ppu);
cpuBus.Attach(cartridge.PrgDevice);

var controllerInput = new MutableNesControllerInput();
var controllers = new NesControllerPorts(controllerInput);
controllers.PowerOn();
cpuBus.Attach(controllers);

var apu = new Rp2A03Apu();
apu.PowerOn();
cpuBus.Attach(apu);
var cpu = new Rp2A03Cpu(cpuBus, cpuSignals);
var irqLines = new IrqLineCombiner(asserted =>
{
    if (asserted) cpuSignals.Irq.Assert();
    else cpuSignals.Irq.Release();
});
apu.IrqLineChanged += irqLines.CreateSource();
if (cartridge.PrgDevice is ICartridgeIrqProvider cartridgeIrq)
    cartridgeIrq.IrqLineChanged += irqLines.CreateSource();
var oamDma = new OamDmaController(cpuBus, ppu, cpu);
oamDma.AttachDmc(apu);
cpuBus.Attach(oamDma);
oamDma.PowerOn();
ppu.PowerOn();
cpu.PowerOn();
var clock = new NesMasterClock(cpu, ppu, apu, timing);
Mmc1CartridgeMemory? mmc1 = cartridge.PrgDevice as Mmc1CartridgeMemory;

using var presenter = new Win32FramePresenter(
    $"AxetosOS NES — {Path.GetFileNameWithoutExtension(romPath)}",
    Rp2C02Ppu.ScreenWidth * 3,
    Rp2C02Ppu.ScreenHeight * 3);
var surface = new FrameSurface(Rp2C02Ppu.ScreenWidth, Rp2C02Ppu.ScreenHeight);
using var audio = new Win32WaveOutAudioSink(apu.SampleRate);
const int AudioTransferBufferSize = 2_048;
var audioTransferBuffer = new float[AudioTransferBufferSize];
audio.Start();
var buttons = NesButtons.None;
var diagnosticCaptureRequested = false;
var diagnosticRecordingToggleRequested = false;
var diagnosticCaptureNumber = 0;
var diagnosticRecordingNumber = 0;
var diagnosticLogPath = Path.ChangeExtension(romPath, ".nes-diagnostics.log");
const int DiagnosticRecordingCapacity = 120_000;
var diagnosticRecordingFrames = new string[DiagnosticRecordingCapacity];
var diagnosticRecordingStart = 0;
var diagnosticRecordingCount = 0;
var diagnosticRecordingActive = false;
DateTimeOffset diagnosticRecordingStartedUtc = default;
string? diagnosticRecordingPath = null;
const int DiagnosticEventCapacity = 100_000;
var diagnosticEvents = new string[DiagnosticEventCapacity];
var diagnosticEventStart = 0;
var diagnosticEventCount = 0;
ulong statusPollFrame = ulong.MaxValue;
int statusPollReads = 0;
ushort statusPollFirstPc = 0;
ushort statusPollLastPc = 0;
byte statusPollObservedBits = 0;
ulong statusPollFirstCycle = 0;

void AddDiagnosticEvent(string row)
{
    if (!diagnosticRecordingActive) return;
    var writeIndex = (diagnosticEventStart + diagnosticEventCount) % DiagnosticEventCapacity;
    diagnosticEvents[writeIndex] = row;
    if (diagnosticEventCount < DiagnosticEventCapacity)
        diagnosticEventCount++;
    else
        diagnosticEventStart = (diagnosticEventStart + 1) % DiagnosticEventCapacity;
}

void FlushStatusPollFrame()
{
    if (statusPollReads >= 32)
    {
        AddDiagnosticEvent(string.Join(',',
            "status-poll-frame", statusPollFrame, statusPollFirstCycle, clock.CpuCycles,
            $"{statusPollFirstPc:X4}", $"{statusPollLastPc:X4}", $"{cpu.StackPointer:X2}",
            statusPollReads, $"{statusPollObservedBits:X2}", "", "", "", "", "", "", "", "", ""));
    }
    statusPollReads = 0;
    statusPollObservedBits = 0;
}

ram.Written += evt =>
{
    if (!diagnosticRecordingActive || (evt.PhysicalAddress & 0x00FF) >= 4) return;
    AddDiagnosticEvent(string.Join(',',
        "cpu-ram-write", ppu.Frame, clock.CpuCycles, clock.CpuCycles,
        $"{cpu.ProgramCounter:X4}", $"{cpu.ProgramCounter:X4}", $"{cpu.StackPointer:X2}",
        "", "", $"{evt.CpuAddress:X4}", $"{evt.Value:X2}", "", "", "", "", "", "", "",
        $"physical={evt.PhysicalAddress:X4};previous={evt.PreviousValue:X2};mirrored={(evt.CpuAddress != evt.PhysicalAddress ? 1 : 0)}"));
};

oamDma.TraceEvent += evt =>
{
    AddDiagnosticEvent(string.Join(',',
        evt.Kind, ppu.Frame, evt.CpuCycle, evt.CpuCycle,
        $"{cpu.ProgramCounter:X4}", $"{cpu.ProgramCounter:X4}", $"{cpu.StackPointer:X2}",
        "", "", $"{evt.SourceAddress:X4}", $"{evt.Value:X2}", "", "", "", "", "", "", "",
        $"page={evt.Page:X2};offset={evt.Offset};physical={(evt.SourceAddress & 0x07FF):X4}"));
};

if (mmc1 is not null)
{
    mmc1.TraceEvent += evt =>
    {
        AddDiagnosticEvent(string.Join(',',
            evt.Kind, ppu.Frame, evt.CpuCycle, evt.CpuCycle,
            $"{cpu.ProgramCounter:X4}", $"{cpu.ProgramCounter:X4}", $"{cpu.StackPointer:X2}",
            "", "", $"{evt.Address:X4}", $"{evt.Value:X2}", $"{evt.ShiftBefore:X2}",
            $"{evt.ShiftAfter:X2}", evt.RegisterValue is null ? "" : $"{evt.RegisterValue.Value:X2}",
            $"{evt.Control:X2}", $"{evt.ChrBank0:X2}", $"{evt.ChrBank1:X2}", $"{evt.PrgBank:X2}", evt.Mirroring));
    };
}

ppu.SpriteZeroHit += evt =>
{
    AddDiagnosticEvent(string.Join(',',
        "sprite-zero-hit", evt.Frame, clock.CpuCycles, clock.CpuCycles,
        $"{cpu.ProgramCounter:X4}", $"{cpu.ProgramCounter:X4}", $"{cpu.StackPointer:X2}",
        "", "", "", "", "", "", "", "", "", "", "",
        $"x={evt.ScreenX};y={evt.ScreenY};scanline={evt.Scanline};dot={evt.Dot};v={evt.VramAddress:X4};scanlineV={evt.ScanlineVramAddress:X4};fineX={evt.FineXScroll}"));
};

ppu.OamWritten += evt =>
{
    AddDiagnosticEvent(string.Join(',',
        "oam-write", evt.Frame, clock.CpuCycles, clock.CpuCycles,
        $"{cpu.ProgramCounter:X4}", $"{cpu.ProgramCounter:X4}", $"{cpu.StackPointer:X2}",
        "", "", "", "", "", "", "", "", "", "", "",
        $"source={evt.Source};scanline={evt.Scanline};dot={evt.Dot};address={evt.Address:X2};previous={evt.PreviousValue:X2};value={evt.Value:X2};nextAddress={evt.NextAddress:X2}"));
};

ppu.SpriteScanlineSelected += evt =>
{
    AddDiagnosticEvent(string.Join(',',
        "sprite-scanline-selection", evt.Frame, clock.CpuCycles, clock.CpuCycles,
        $"{cpu.ProgramCounter:X4}", $"{cpu.ProgramCounter:X4}", $"{cpu.StackPointer:X2}",
        "", "", "", "", "", "", "", "", "", "", "",
        $"scanline={evt.Scanline};height={evt.SpriteHeight};spritesOnScanline={evt.SpritesOnScanline};evaluated={evt.EvaluatedSprites};spriteZeroOnScanline={(evt.SpriteZeroOnScanline ? 1 : 0)};spriteZeroSelected={(evt.SpriteZeroSelected ? 1 : 0)};slot={evt.SpriteZeroSelectionSlot};oamY={evt.OamY:X2};tile={evt.TileIndex:X2};attr={evt.Attributes:X2};oamX={evt.OamX:X2}"));
};

ppu.SpriteZeroEvaluated += evt =>
{
    if (!diagnosticRecordingActive || !evt.SelectedForScanline) return;
    AddDiagnosticEvent(string.Join(',',
        "sprite-zero-evaluation", evt.Frame, clock.CpuCycles, clock.CpuCycles,
        $"{cpu.ProgramCounter:X4}", $"{cpu.ProgramCounter:X4}", $"{cpu.StackPointer:X2}",
        "", "", "", "", "", "", "", "", "", "", "",
        $"scanline={evt.Scanline};oamY={evt.OamY:X2};tile={evt.TileIndex:X2};attr={evt.Attributes:X2};oamX={evt.OamX};height={evt.SpriteHeight};sourceRow={evt.SourceRow};patternRow={evt.PatternRow};patternAddress={evt.PatternAddress:X4};low={evt.LowPlane:X2};high={evt.HighPlane:X2};spriteMask={evt.SpriteOpaqueMask:X2};backgroundMask={evt.BackgroundOpaqueMask:X2};overlapMask={evt.OverlapMask:X2};selected={(evt.SelectedForScanline ? 1 : 0)};bg={(evt.BackgroundEnabled ? 1 : 0)};sprites={(evt.SpritesEnabled ? 1 : 0)};bgLeft={(evt.BackgroundLeftEnabled ? 1 : 0)};spriteLeft={(evt.SpritesLeftEnabled ? 1 : 0)};scanlineV={evt.ScanlineVramAddress:X4};fineX={evt.FineXScroll};reason={evt.RejectionReason}"));
};

ppu.StatusRead += evt =>
{
    if (!diagnosticRecordingActive) return;
    if (statusPollFrame != evt.Frame)
    {
        FlushStatusPollFrame();
        statusPollFrame = evt.Frame;
        statusPollFirstCycle = clock.CpuCycles;
        statusPollFirstPc = cpu.ProgramCounter;
    }
    statusPollReads++;
    statusPollLastPc = cpu.ProgramCounter;
    statusPollObservedBits |= evt.Value;
};

presenter.KeyStateChanged += (key, pressed) =>
{
    var button = key switch
    {
        NativeKey.Z => NesButtons.A,
        NativeKey.X => NesButtons.B,
        NativeKey.RightShift => NesButtons.Select,
        NativeKey.Enter => NesButtons.Start,
        NativeKey.Up => NesButtons.Up,
        NativeKey.Down => NesButtons.Down,
        NativeKey.Left => NesButtons.Left,
        NativeKey.Right => NesButtons.Right,
        _ => NesButtons.None
    };

    if (pressed && (buttons & NesButtons.Select) != 0)
    {
        if (key == NativeKey.Enter)
        {
            diagnosticCaptureRequested = true;
            return;
        }

        if (key == NativeKey.Down)
        {
            diagnosticRecordingToggleRequested = true;
            return;
        }
    }

    if (key == NativeKey.Escape && pressed)
    {
        presenter.Close();
        return;
    }

    if (button != NesButtons.None)
    {
        buttons = pressed ? buttons | button : buttons & ~button;
        controllerInput.SetButtons(0, buttons);
    }
};

Console.WriteLine("AxetosOS Products / NES — native desktop host");
Console.WriteLine("Launch:    Native ROM picker available when no path is supplied");
Console.WriteLine($"ROM:       {romPath}");
Console.WriteLine($"Mapper:    {image.MapperNumber} ({mapper.Name})");
Console.WriteLine($"Timing:    {timing.Name} ({timingSelection.Source})");
Console.WriteLine($"Save RAM:  {(persistentMemory is { HasBattery: true } ? savePath : "not battery-backed")}");
Console.WriteLine("Controls:  Arrows=D-pad, Z=A, X=B, Enter=Start, Right Shift=Select, Right Shift+Enter=Capture, Right Shift+Down=Start/stop recording, Esc=Exit");
Console.WriteLine($"Diagnostics:{diagnosticLogPath}");
Console.WriteLine("Video:     AxetosOS native framebuffer presenter");
Console.WriteLine($"Audio:     AxetosOS native PCM output ({apu.SampleRate:N0} Hz mono)");

StreamWriter? mapperTrace = null;
Mmc3CartridgeMemory? mmc3 = cartridge.PrgDevice as Mmc3CartridgeMemory;
if (mapperTraceEnabled && mmc3 is not null)
{
    mapperTracePath ??= Path.ChangeExtension(romPath, ".mmc3-trace.log");
    mapperTracePath = Path.GetFullPath(mapperTracePath);
    mapperTrace = new StreamWriter(mapperTracePath, append: false) { AutoFlush = true };
    mapperTrace.WriteLine($"# AxetosOS NES MMC3 trace");
    mapperTrace.WriteLine($"# ROM={romPath}");
    mapperTrace.WriteLine($"# Mapper={image.MapperNumber} Submapper={image.SubmapperNumber?.ToString() ?? "unspecified"} Timing={timing.Name}");
    mapperTrace.WriteLine("# frame,cpuCycles,pc,nmiServiced,irqServiced,brk,rti,vblankStarts,nmiEdges,statusReads,bankSelect,registers,prgBanks,chrBanks,irqLatch,irqCounter,reload,enabled,asserted,scanlineClocks,irqAssertions,mirroring");
    Console.WriteLine($"Trace:     {mapperTracePath}");
}
else if (mapperTraceEnabled)
{
    Console.WriteLine("Trace:     requested, but the loaded cartridge is not MMC3-family hardware");
}

var framesPerSecond = timing.FramesPerSecond;
var cpuClockHz = timing.CpuClockHz;
var frameDuration = TimeSpan.FromSeconds(1.0 / framesPerSecond);
var timer = Stopwatch.StartNew();
var nextFrame = TimeSpan.Zero;
var diagnosticsStart = timer.Elapsed;
var diagnosticsCpuCycles = clock.CpuCycles;
var diagnosticsPpuFrame = ppu.Frame;
var lastTitleUpdate = TimeSpan.Zero;
var romTitle = Path.GetFileNameWithoutExtension(romPath);
double? smoothedEmulationPercent = null;
double? smoothedFps = null;
double? smoothedAudioMilliseconds = null;
const double diagnosticsSmoothing = 0.20;
ulong lastTracedFrame = ulong.MaxValue;
ushort lastObservedPc = cpu.ProgramCounter;
long unchangedPcFrames = 0;

try
{
    while (presenter.IsOpen)
    {
        presenter.PumpEvents();
        if (!presenter.IsOpen)
        {
            break;
        }

        clock.TickFrame(ppu);

        if (!presenter.IsOpen)
        {
            break;
        }

        ppu.Framebuffer.AsSpan().CopyTo(surface.PixelSpan);
        presenter.Present(surface, ScalingMode.IntegerNearest);

        if (diagnosticRecordingToggleRequested)
        {
            diagnosticRecordingToggleRequested = false;
            if (!diagnosticRecordingActive)
            {
                diagnosticRecordingNumber++;
                diagnosticRecordingPath = Path.Combine(
                    Path.GetDirectoryName(diagnosticLogPath) ?? Environment.CurrentDirectory,
                    $"{Path.GetFileNameWithoutExtension(romPath)}.diagnostic-recording-{diagnosticRecordingNumber:D3}.csv");
                diagnosticRecordingStart = 0;
                diagnosticRecordingCount = 0;
                diagnosticEventStart = 0;
                diagnosticEventCount = 0;
                statusPollFrame = ulong.MaxValue;
                statusPollReads = 0;
                statusPollObservedBits = 0;
                diagnosticRecordingStartedUtc = DateTimeOffset.UtcNow;
                diagnosticRecordingActive = true;
                ppu.DiagnosticsTraceEnabled = true;
                ram.DiagnosticsTraceEnabled = true;
                oamDma.DiagnosticsTraceEnabled = true;
                if (mmc1 is not null) mmc1.DiagnosticsTraceEnabled = true;
                Console.WriteLine($"Diagnostic recording started in memory: {diagnosticRecordingPath}");
            }
            else
            {
                FlushStatusPollFrame();
                diagnosticRecordingActive = false;
                ppu.DiagnosticsTraceEnabled = false;
                ram.DiagnosticsTraceEnabled = false;
                oamDma.DiagnosticsTraceEnabled = false;
                if (mmc1 is not null) mmc1.DiagnosticsTraceEnabled = false;
                FlushDiagnosticRecording(
                    diagnosticRecordingPath!,
                    diagnosticRecordingStartedUtc,
                    romPath,
                    image.MapperNumber,
                    image.SubmapperNumber,
                    timing.Name,
                    diagnosticRecordingFrames,
                    diagnosticRecordingStart,
                    diagnosticRecordingCount,
                    diagnosticEvents,
                    diagnosticEventStart,
                    diagnosticEventCount);
                Console.WriteLine($"Diagnostic recording stopped and written: {diagnosticRecordingPath}");
            }
        }

        if (diagnosticRecordingActive)
        {
            var frameRow = CreateDiagnosticRecordingFrame(clock.CpuCycles, cpu, ppu, ciram, mmc1);
            var writeIndex = (diagnosticRecordingStart + diagnosticRecordingCount) % DiagnosticRecordingCapacity;
            diagnosticRecordingFrames[writeIndex] = frameRow;
            if (diagnosticRecordingCount < DiagnosticRecordingCapacity)
            {
                diagnosticRecordingCount++;
            }
            else
            {
                diagnosticRecordingStart = (diagnosticRecordingStart + 1) % DiagnosticRecordingCapacity;
            }
        }

        if (diagnosticCaptureRequested)
        {
            diagnosticCaptureRequested = false;
            diagnosticCaptureNumber++;
            var imagePath = Path.Combine(
                Path.GetDirectoryName(diagnosticLogPath) ?? Environment.CurrentDirectory,
                $"{Path.GetFileNameWithoutExtension(romPath)}.diagnostic-{diagnosticCaptureNumber:D3}.ppm");

            WriteDiagnosticCapture(
                diagnosticLogPath,
                imagePath,
                diagnosticCaptureNumber,
                romPath,
                image.MapperNumber,
                image.SubmapperNumber,
                timing.Name,
                clock.CpuCycles,
                cpu,
                ppu,
                ciram,
                mmc1,
                mmc3);
            WritePpm(imagePath, ppu.Framebuffer, Rp2C02Ppu.ScreenWidth, Rp2C02Ppu.ScreenHeight);
            Console.WriteLine($"Diagnostic capture {diagnosticCaptureNumber}: {diagnosticLogPath}");
            Console.WriteLine($"Diagnostic frame:       {imagePath}");
        }

        int drainedAudioSamples;
        while ((drainedAudioSamples = apu.DrainSamples(audioTransferBuffer)) > 0)
        {
            audio.Submit(audioTransferBuffer.AsSpan(0, drainedAudioSamples));
        }

        if (mapperTrace is { } trace && mmc3 is { } activeMmc3 && ppu.Frame != lastTracedFrame)
        {
            var snapshot = activeMmc3.GetDiagnostics();
            trace.WriteLine(string.Join(',',
                ppu.Frame,
                clock.CpuCycles,
                $"{cpu.ProgramCounter:X4}",
                cpu.NmiServiced,
                cpu.IrqServiced,
                cpu.BrkExecuted,
                cpu.RtiExecuted,
                ppu.VBlankStarts,
                ppu.NmiEdges,
                ppu.StatusReads,
                $"{snapshot.BankSelect:X2}",
                string.Join('-', snapshot.Registers.Select(value => value.ToString("X2"))),
                string.Join('-', snapshot.PrgBanks),
                string.Join('-', snapshot.ChrBanks),
                snapshot.IrqLatch,
                snapshot.IrqCounter,
                snapshot.IrqReloadPending ? 1 : 0,
                snapshot.IrqEnabled ? 1 : 0,
                snapshot.IrqAsserted ? 1 : 0,
                snapshot.ScanlineClocks,
                snapshot.IrqAssertions,
                snapshot.Mirroring));
            lastTracedFrame = ppu.Frame;

            if (cpu.ProgramCounter == lastObservedPc) unchangedPcFrames++;
            else
            {
                lastObservedPc = cpu.ProgramCounter;
                unchangedPcFrames = 0;
            }
            if (unchangedPcFrames == 120)
                trace.WriteLine($"# STALL-SUSPECT frame={ppu.Frame} pc=${cpu.ProgramCounter:X4} unchangedForFrames={unchangedPcFrames}");
        }

        var now = timer.Elapsed;
        if (now - lastTitleUpdate >= TimeSpan.FromMilliseconds(500))
        {
            var diagnosticSeconds = (now - diagnosticsStart).TotalSeconds;
            if (diagnosticSeconds > 0)
            {
                var cpuDelta = clock.CpuCycles - diagnosticsCpuCycles;
                var frameDelta = ppu.Frame - diagnosticsPpuFrame;
                var emulationPercent = cpuDelta / (cpuClockHz * diagnosticSeconds) * 100.0;
                var measuredFps = frameDelta / diagnosticSeconds;
                var bufferedAudio = audio.BufferedMilliseconds;

                smoothedEmulationPercent = smoothedEmulationPercent is null
                    ? emulationPercent
                    : smoothedEmulationPercent.Value + ((emulationPercent - smoothedEmulationPercent.Value) * diagnosticsSmoothing);
                smoothedFps = smoothedFps is null
                    ? measuredFps
                    : smoothedFps.Value + ((measuredFps - smoothedFps.Value) * diagnosticsSmoothing);
                smoothedAudioMilliseconds = smoothedAudioMilliseconds is null
                    ? bufferedAudio
                    : smoothedAudioMilliseconds.Value + ((bufferedAudio - smoothedAudioMilliseconds.Value) * diagnosticsSmoothing);

                presenter.SetTitle($"{romTitle} | {timing.Name} | Emulation {smoothedEmulationPercent:F1}% | FPS {smoothedFps:F1} | Audio {smoothedAudioMilliseconds:F0} ms");
            }

            diagnosticsStart = now;
            diagnosticsCpuCycles = clock.CpuCycles;
            diagnosticsPpuFrame = ppu.Frame;
            lastTitleUpdate = now;
        }

        nextFrame += frameDuration;
        while (presenter.IsOpen && timer.Elapsed < nextFrame)
        {
            presenter.PumpEvents();
            var remaining = nextFrame - timer.Elapsed;
            if (remaining > TimeSpan.FromMilliseconds(2)) Thread.Sleep(1);
            else Thread.SpinWait(64);
        }

        if (timer.Elapsed - nextFrame > TimeSpan.FromMilliseconds(250))
        {
            nextFrame = timer.Elapsed;
        }
    }
}
catch (UnsupportedCpuOpcodeException exception)
{
    Console.Error.WriteLine(exception.Message);
    Console.Error.WriteLine($"PC=${cpu.ProgramCounter:X4} opcode=${cpu.LastOpcode:X2} frame={ppu.Frame:N0}");
    return 5;
}

if (diagnosticRecordingActive && diagnosticRecordingPath is not null)
{
    FlushStatusPollFrame();
    FlushDiagnosticRecording(
        diagnosticRecordingPath,
        diagnosticRecordingStartedUtc,
        romPath,
        image.MapperNumber,
        image.SubmapperNumber,
        timing.Name,
        diagnosticRecordingFrames,
        diagnosticRecordingStart,
        diagnosticRecordingCount,
        diagnosticEvents,
        diagnosticEventStart,
        diagnosticEventCount);
    Console.WriteLine($"Diagnostic recording written during shutdown: {diagnosticRecordingPath}");
}

mapperTrace?.Dispose();

if (persistentMemory is { HasBattery: true })
    await File.WriteAllBytesAsync(savePath, persistentMemory.SavePersistent());

return 0;

static bool TryParseTiming(string value, out NesTimingMode? mode)
{
    mode = value.ToLowerInvariant() switch
    {
        "auto" => null,
        "ntsc" => NesTimingMode.Ntsc,
        "pal" => NesTimingMode.Pal,
        "dendy" => NesTimingMode.Dendy,
        _ => (NesTimingMode?)NesTimingMode.Unknown
    };
    if (mode == NesTimingMode.Unknown)
    {
        mode = null;
        return false;
    }
    return true;
}


static void FlushDiagnosticRecording(
    string path,
    DateTimeOffset startedUtc,
    string romPath,
    int mapperNumber,
    int? submapperNumber,
    string timingName,
    string[] frames,
    int start,
    int count,
    string[] events,
    int eventStart,
    int eventCount)
{
    using var writer = new StreamWriter(path, append: false);
    writer.WriteLine("# AxetosOS NES continuous diagnostic recording");
    writer.WriteLine($"# UTC={startedUtc:O}");
    writer.WriteLine($"# ROM={romPath}");
    writer.WriteLine($"# Mapper={mapperNumber}; Submapper={submapperNumber?.ToString() ?? "unspecified"}; Timing={timingName}");
    writer.WriteLine($"# BufferedFrames={count}; Capacity={frames.Length}; DroppedOldest={(count == frames.Length ? 1 : 0)}");
    writer.WriteLine($"# BufferedEvents={eventCount}; EventCapacity={events.Length}; DroppedOldestEvents={(eventCount == events.Length ? 1 : 0)}");
    writer.WriteLine("frame,cpuCycles,pc,opcode,sp,nmi,irq,brk,rti,scanline,dot,control,mask,status,v,t,scanlineV,fineX,statusReads,spriteZeroHits,lastSpriteZeroFrame,lastSpriteZeroX,lastSpriteZeroY,ciramMirroring,ciramLowerHash,ciramUpperHash,mmc1Shift,mmc1Control,mmc1Chr0,mmc1Chr1,mmc1Prg,mmc1PrgBanks,mmc1ChrBanks");

    for (var index = 0; index < count; index++)
    {
        var frameIndex = (start + index) % frames.Length;
        writer.WriteLine(frames[frameIndex]);
    }

    writer.WriteLine();
    writer.WriteLine("# critical-events");
    writer.WriteLine("kind,frame,firstCpuCycle,lastCpuCycle,firstPc,lastPc,sp,statusReadCount,statusObservedBits,address,value,shiftBefore,shiftAfter,registerValue,control,chr0,chr1,prg,details");
    for (var index = 0; index < eventCount; index++)
    {
        var eventIndex = (eventStart + index) % events.Length;
        writer.WriteLine(events[eventIndex]);
    }
}

static string CreateDiagnosticRecordingFrame(
    ulong cpuCycles,
    Rp2A03Cpu cpu,
    Rp2C02Ppu ppu,
    CiramNametableRam ciram,
    Mmc1CartridgeMemory? mmc1)
{
    var ppuState = ppu.GetDiagnostics();
    var ciramState = ciram.GetDiagnostics();
    var mmc1State = mmc1?.GetDiagnostics();
    return string.Join(',',
        ppuState.Frame, cpuCycles, $"{cpu.ProgramCounter:X4}", $"{cpu.LastOpcode:X2}", $"{cpu.StackPointer:X2}",
        cpu.NmiServiced, cpu.IrqServiced, cpu.BrkExecuted, cpu.RtiExecuted,
        ppuState.Scanline, ppuState.Dot, $"{ppuState.Control:X2}", $"{ppuState.Mask:X2}", $"{ppuState.Status:X2}",
        $"{ppuState.VramAddress:X4}", $"{ppuState.TemporaryVramAddress:X4}", $"{ppuState.ActiveScanlineVramAddress:X4}",
        ppuState.FineXScroll, ppuState.StatusReads,
        ppuState.SpriteZeroHits, ppuState.LastSpriteZeroHitFrame, ppuState.LastSpriteZeroHitX, ppuState.LastSpriteZeroHitY,
        ciramState.Mirroring, $"{ciramState.LowerHash:X8}", $"{ciramState.UpperHash:X8}",
        mmc1State is null ? "" : $"{mmc1State.ShiftRegister:X2}",
        mmc1State is null ? "" : $"{mmc1State.Control:X2}",
        mmc1State is null ? "" : $"{mmc1State.ChrBank0:X2}",
        mmc1State is null ? "" : $"{mmc1State.ChrBank1:X2}",
        mmc1State is null ? "" : $"{mmc1State.PrgBank:X2}",
        mmc1State is null ? "" : string.Join('-', mmc1State.PrgBanks),
        mmc1State is null ? "" : string.Join('-', mmc1State.ChrBanks));
}

static void WriteDiagnosticCapture(
    string logPath,
    string imagePath,
    int captureNumber,
    string romPath,
    int mapperNumber,
    int? submapperNumber,
    string timingName,
    ulong cpuCycles,
    Rp2A03Cpu cpu,
    Rp2C02Ppu ppu,
    CiramNametableRam ciram,
    Mmc1CartridgeMemory? mmc1,
    Mmc3CartridgeMemory? mmc3)
{
    var ppuState = ppu.GetDiagnostics();
    var ciramState = ciram.GetDiagnostics();

    using var writer = new StreamWriter(logPath, append: true);
    writer.WriteLine("============================================================");
    writer.WriteLine($"Capture: {captureNumber}");
    writer.WriteLine($"UTC: {DateTimeOffset.UtcNow:O}");
    writer.WriteLine($"ROM: {romPath}");
    writer.WriteLine($"Mapper: {mapperNumber}; Submapper: {submapperNumber?.ToString() ?? "unspecified"}; Timing: {timingName}");
    writer.WriteLine($"Frame image: {imagePath}");
    writer.WriteLine($"CPU: cycles={cpuCycles}; PC=${cpu.ProgramCounter:X4}; opcode=${cpu.LastOpcode:X2}; SP=${cpu.StackPointer:X2}; NMI={cpu.NmiServiced}; IRQ={cpu.IrqServiced}; BRK={cpu.BrkExecuted}; RTI={cpu.RtiExecuted}");
    writer.WriteLine($"PPU: frame={ppuState.Frame}; scanline={ppuState.Scanline}; dot={ppuState.Dot}; control=${ppuState.Control:X2}; mask=${ppuState.Mask:X2}; status=${ppuState.Status:X2}");
    writer.WriteLine($"PPU address: v=${ppuState.VramAddress:X4}; t=${ppuState.TemporaryVramAddress:X4}; scanlineV=${ppuState.ActiveScanlineVramAddress:X4}; fineX={ppuState.FineXScroll}; writeToggle={(ppuState.WriteToggle ? 1 : 0)}");
    writer.WriteLine($"PPU rendering: background={(ppuState.BackgroundEnabled ? 1 : 0)}; sprites={(ppuState.SpritesEnabled ? 1 : 0)}; backgroundPattern=${ppuState.BackgroundPatternTable:X4}; spritePattern=${ppuState.SpritePatternTable:X4}; vblank={(ppuState.InVBlank ? 1 : 0)}; nmiOutput={(ppuState.NmiOutput ? 1 : 0)}");
    writer.WriteLine($"PPU counters: vblankStarts={ppuState.VBlankStarts}; nmiEdges={ppuState.NmiEdges}; statusReads={ppuState.StatusReads}; spriteZeroHits={ppuState.SpriteZeroHits}; lastSpriteZeroFrame={ppuState.LastSpriteZeroHitFrame}; lastSpriteZero=({ppuState.LastSpriteZeroHitX},{ppuState.LastSpriteZeroHitY})");
    writer.WriteLine($"CIRAM: mirroring={ciramState.Mirroring}; lowerNonZero={ciramState.LowerNonZeroBytes}; upperNonZero={ciramState.UpperNonZeroBytes}; lowerHash={ciramState.LowerHash:X8}; upperHash={ciramState.UpperHash:X8}");

    if (mmc1 is not null)
    {
        var state = mmc1.GetDiagnostics();
        writer.WriteLine($"MMC1: shift=${state.ShiftRegister:X2}; control=${state.Control:X2}; chr0=${state.ChrBank0:X2}; chr1=${state.ChrBank1:X2}; prg=${state.PrgBank:X2}; prgRam={(state.PrgRamEnabled ? 1 : 0)}");
        writer.WriteLine($"MMC1 mapping: mirroring={state.Mirroring}; prgMode={state.PrgMode}; chrMode={state.ChrMode}; prgBanks={string.Join('-', state.PrgBanks)}; chrBanks={string.Join('-', state.ChrBanks)}");
    }

    if (mmc3 is not null)
    {
        var state = mmc3.GetDiagnostics();
        writer.WriteLine($"MMC3: select=${state.BankSelect:X2}; registers={string.Join('-', state.Registers.Select(value => value.ToString("X2")))}; prgBanks={string.Join('-', state.PrgBanks)}; chrBanks={string.Join('-', state.ChrBanks)}");
        writer.WriteLine($"MMC3 IRQ: latch={state.IrqLatch}; counter={state.IrqCounter}; reload={(state.IrqReloadPending ? 1 : 0)}; enabled={(state.IrqEnabled ? 1 : 0)}; asserted={(state.IrqAsserted ? 1 : 0)}; scanlineClocks={state.ScanlineClocks}; assertions={state.IrqAssertions}; mirroring={state.Mirroring}");
    }

    writer.WriteLine();
}

static void WritePpm(string path, ReadOnlySpan<uint> pixels, int width, int height)
{
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
    writer.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n"));
    foreach (var pixel in pixels)
    {
        writer.Write((byte)((pixel >> 16) & 0xFF));
        writer.Write((byte)((pixel >> 8) & 0xFF));
        writer.Write((byte)(pixel & 0xFF));
    }
}
