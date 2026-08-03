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

var nmi = new SignalLine();
var ppu = new Rp2C02Ppu(ppuBus, nmi, timing);
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
var cpu = new Rp2A03Cpu(cpuBus);
apu.AttachDmcMemory(cpuBus, cpu.RequestDmaStall);
var irqLines = new IrqLineCombiner(cpu.SetIrqLine);
apu.IrqLineChanged += irqLines.CreateSource();
if (cartridge.PrgDevice is ICartridgeIrqProvider cartridgeIrq)
    cartridgeIrq.IrqLineChanged += irqLines.CreateSource();
var oamDma = new OamDmaController(cpuBus, ppu, cpu);
cpuBus.Attach(oamDma);
oamDma.PowerOn();
nmi.Asserted += cpu.RequestNmi;
ppu.PowerOn();
cpu.PowerOn();
var clock = new NesMasterClock(cpu, ppu, apu, timing);

using var presenter = new Win32FramePresenter(
    $"AxetosOS NES — {Path.GetFileNameWithoutExtension(romPath)}",
    Rp2C02Ppu.ScreenWidth * 3,
    Rp2C02Ppu.ScreenHeight * 3);
var surface = new FrameSurface(Rp2C02Ppu.ScreenWidth, Rp2C02Ppu.ScreenHeight);
using var audio = new Win32WaveOutAudioSink(apu.SampleRate);
audio.Start();
var buttons = NesButtons.None;
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
Console.WriteLine("Controls:  Arrows=D-pad, Z=A, X=B, Enter=Start, Right Shift=Select, Esc=Exit");
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

        var startingFrame = ppu.Frame;
        while (presenter.IsOpen && ppu.Frame == startingFrame)
        {
            clock.Tick();
        }

        if (!presenter.IsOpen)
        {
            break;
        }

        ppu.Framebuffer.AsSpan().CopyTo(surface.PixelSpan);
        presenter.Present(surface, ScalingMode.IntegerNearest);

        var audioSamples = apu.DrainSamples();
        if (audioSamples.Length > 0)
        {
            audio.Submit(audioSamples);
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
