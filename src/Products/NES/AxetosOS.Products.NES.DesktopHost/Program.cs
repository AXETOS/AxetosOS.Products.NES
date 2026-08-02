using System.Diagnostics;
using AxetosOS.Products.NES.Abstractions;
using AxetosOS.Products.NES.Cartridges;
using AxetosOS.Products.NES.Hardware;
using AxetosOS.Rendering.Abstractions;
using AxetosOS.Rendering.Windows;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: dotnet run --project .\\src\\Products\\NES\\AxetosOS.Products.NES.DesktopHost -- <rom-path>");
    return 2;
}

var romPath = Path.GetFullPath(args[0]);
if (!File.Exists(romPath))
{
    Console.Error.WriteLine($"ROM file not found: {romPath}");
    return 3;
}

await using var romStream = File.OpenRead(romPath);
var image = NesRomReader.Read(romStream);
var catalogPath = Path.Combine(AppContext.BaseDirectory, "hardware", "mapper-catalog.json");
await using var catalogStream = File.OpenRead(catalogPath);
var catalog = MapperCatalog.Load(catalogStream);
var mapper = catalog.Resolve(image.MapperNumber, image.SubmapperNumber);
var boardPath = Path.Combine(AppContext.BaseDirectory, "hardware", mapper.Definition.Replace('/', Path.DirectorySeparatorChar));
await using var boardStream = File.OpenRead(boardPath);
var boardDefinition = CartridgeBoardDefinition.Load(boardStream);
var cartridge = CartridgeHardwareFactory.Create(image, boardDefinition);

var ram = new CpuWorkRam();
var ciram = new CiramNametableRam(image.Mirroring);
var palette = new PpuPaletteRam();
ram.PowerOn();
ciram.PowerOn();
palette.PowerOn();
if (cartridge.ChrDevice is INesHardwareModule chrModule) chrModule.PowerOn();

var ppuBus = new PpuBus();
ppuBus.Attach(cartridge.ChrDevice);
ppuBus.Attach(ciram);
ppuBus.Attach(palette);

var nmi = new SignalLine();
var ppu = new Rp2C02Ppu(ppuBus, nmi);
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
apu.IrqLineChanged += cpu.SetIrqLine;
var oamDma = new OamDmaController(cpuBus, ppu, cpu);
cpuBus.Attach(oamDma);
oamDma.PowerOn();
nmi.Asserted += cpu.RequestNmi;
ppu.PowerOn();
cpu.PowerOn();
var clock = new NesMasterClock(cpu, ppu, apu);

using var presenter = new Win32FramePresenter(
    $"AxetosOS NES — {Path.GetFileNameWithoutExtension(romPath)}",
    Rp2C02Ppu.ScreenWidth * 3,
    Rp2C02Ppu.ScreenHeight * 3);
var surface = new FrameSurface(Rp2C02Ppu.ScreenWidth, Rp2C02Ppu.ScreenHeight);
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
Console.WriteLine($"ROM:       {romPath}");
Console.WriteLine($"Mapper:    {image.MapperNumber} ({mapper.Name})");
Console.WriteLine("Controls:  Arrows=D-pad, Z=A, X=B, Enter=Start, Right Shift=Select, Esc=Exit");
Console.WriteLine("Video:     AxetosOS native framebuffer presenter");

const double framesPerSecond = 60.0988;
var frameDuration = TimeSpan.FromSeconds(1.0 / framesPerSecond);
var timer = Stopwatch.StartNew();
var nextFrame = TimeSpan.Zero;

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

return 0;
