using AxetosOS.Products.NES.Cartridges;
using AxetosOS.Products.NES.Hardware;

if (args.Length == 0)
{
    WriteUsage();
    return 2;
}

var romPath = Path.GetFullPath(args[0]);
if (!File.Exists(romPath))
{
    Console.Error.WriteLine($"ROM file not found: {romPath}");
    return 3;
}

var requestedCycles = 0;
string? framePath = null;
var controller1Buttons = NesButtons.None;
var controller2Buttons = NesButtons.None;
string? inputScriptPath = null;
for (var index = 1; index < args.Length; index++)
{
    if (string.Equals(args[index], "--cycles", StringComparison.OrdinalIgnoreCase) &&
        index + 1 < args.Length && int.TryParse(args[++index], out var cycles) && cycles >= 0)
    {
        requestedCycles = cycles;
        continue;
    }

    if (string.Equals(args[index], "--frame", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
    {
        framePath = Path.GetFullPath(args[++index]);
        continue;
    }

    if (string.Equals(args[index], "--controller1", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
    {
        controller1Buttons = ParseButtons(args[++index]);
        continue;
    }

    if (string.Equals(args[index], "--controller2", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
    {
        controller2Buttons = ParseButtons(args[++index]);
        continue;
    }

    if (string.Equals(args[index], "--input-script", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
    {
        inputScriptPath = Path.GetFullPath(args[++index]);
        continue;
    }

    WriteUsage();
    return 2;
}

await using var romStream = File.OpenRead(romPath);
var image = NesRomReader.Read(romStream);

var catalogPath = Path.Combine(AppContext.BaseDirectory, "hardware", "mapper-catalog.json");
await using var catalogStream = File.OpenRead(catalogPath);
var catalog = MapperCatalog.Load(catalogStream);
var mapper = catalog.Resolve(image.MapperNumber, image.SubmapperNumber);

Console.WriteLine("AxetosOS Products / NES — headless hardware host");
Console.WriteLine($"Header:      {image.HeaderFormat}");
Console.WriteLine($"Mapper:      {image.MapperNumber}");
Console.WriteLine($"Submapper:   {image.SubmapperNumber?.ToString() ?? "n/a"}");
Console.WriteLine($"Board:       {mapper.Name}");
Console.WriteLine($"Definition:  {mapper.Definition}");
Console.WriteLine($"PRG ROM:     {image.PrgRomSizeBytes:N0} bytes");
Console.WriteLine($"CHR ROM:     {image.ChrRomSizeBytes:N0} bytes");
Console.WriteLine($"Mirroring:   {image.Mirroring}");
Console.WriteLine($"Trainer:     {image.HasTrainer}");
Console.WriteLine($"Battery RAM: {image.HasBatteryBackedMemory}");

if (requestedCycles == 0 && framePath is null)
{
    return 0;
}

if (image.MapperNumber != 0)
{
    Console.Error.WriteLine("CPU boot execution currently supports NROM/mapper 0 only.");
    return 4;
}

var ram = new CpuWorkRam();
var ciram = new CiramNametableRam(image.Mirroring);
var palette = new PpuPaletteRam();
var chr = new NromChrMemory(image);
ram.PowerOn();
ciram.PowerOn();
palette.PowerOn();
chr.PowerOn();

var ppuBus = new PpuBus();
ppuBus.Attach(chr);
ppuBus.Attach(ciram);
ppuBus.Attach(palette);

var nmi = new SignalLine();
var ppu = new Rp2C02Ppu(ppuBus, nmi);
var bus = new CpuBus();
bus.Attach(ram);
bus.Attach(ppu);
bus.Attach(new NromPrgRom(image));
ScriptedNesControllerInput? scriptedInput = null;
INesControllerInput input;
if (inputScriptPath is not null)
{
    if (!File.Exists(inputScriptPath))
    {
        Console.Error.WriteLine($"Input script not found: {inputScriptPath}");
        return 6;
    }

    var events = LoadInputScript(inputScriptPath, controller1Buttons, controller2Buttons);
    scriptedInput = new ScriptedNesControllerInput(events);
    input = scriptedInput;
}
else
{
    var mutableInput = new MutableNesControllerInput();
    mutableInput.SetButtons(0, controller1Buttons);
    mutableInput.SetButtons(1, controller2Buttons);
    input = mutableInput;
}

var controllers = new NesControllerPorts(input);
controllers.PowerOn();
bus.Attach(controllers);
var cpu = new Rp2A03Cpu(bus);
var oamDma = new OamDmaController(bus, ppu, cpu);
bus.Attach(oamDma);
oamDma.PowerOn();
nmi.Asserted += cpu.RequestNmi;
ppu.PowerOn();
cpu.PowerOn();
var clock = new NesMasterClock(cpu, ppu);

Console.WriteLine();
Console.WriteLine($"Reset vector: ${cpu.ProgramCounter:X4}");

var targetCycles = requestedCycles > 0 ? (ulong)requestedCycles : 100_000UL;
try
{
    while (clock.CpuCycles < targetCycles)
    {
        scriptedInput?.AdvanceTo(clock.CpuCycles);
        clock.Tick();
    }
}
catch (UnsupportedCpuOpcodeException exception)
{
    Console.Error.WriteLine(exception.Message);
    PrintCpuState(cpu);
    PrintPpuState(ppu, clock);
    return 5;
}

PrintCpuState(cpu);
PrintPpuState(ppu, clock);
Console.WriteLine($"Sprite 0:    x={ppu.ReadOamByte(3),3}, y={unchecked((byte)(ppu.ReadOamByte(0) + 1)),3}");

if (framePath is not null)
{
    WritePpm(framePath, ppu.Framebuffer);
    Console.WriteLine($"Frame image:  {framePath}");
}

var finalController1 = input.ReadButtons(0);
var finalController2 = input.ReadButtons(1);
Console.WriteLine($"Controller 1:{FormatButtons(finalController1),12}");
Console.WriteLine($"Controller 2:{FormatButtons(finalController2),12}");
if (inputScriptPath is not null)
{
    Console.WriteLine($"Input script: {inputScriptPath}");
}

return 0;

static void PrintCpuState(Rp2A03Cpu cpu)
{
    Console.WriteLine($"CPU cycles:  {cpu.TotalCycles:N0}");
    Console.WriteLine($"Instructions:{cpu.InstructionsExecuted,12:N0}");
    Console.WriteLine($"PC:          ${cpu.ProgramCounter:X4}");
    Console.WriteLine($"A:           ${cpu.Accumulator:X2}");
    Console.WriteLine($"X:           ${cpu.X:X2}");
    Console.WriteLine($"Y:           ${cpu.Y:X2}");
    Console.WriteLine($"SP:          ${cpu.StackPointer:X2}");
    Console.WriteLine($"P:           ${cpu.Status:X2}");
    Console.WriteLine($"Last opcode: ${cpu.LastOpcode:X2}");
}

static void PrintPpuState(Rp2C02Ppu ppu, NesMasterClock clock)
{
    Console.WriteLine($"PPU cycles:  {clock.PpuCycles:N0}");
    Console.WriteLine($"PPU position:{ppu.Scanline,8}, {ppu.Dot}");
    Console.WriteLine($"PPU frame:   {ppu.Frame,12:N0}");
    Console.WriteLine($"VBlank:      {ppu.InVBlank}");
}

static void WritePpm(string path, IReadOnlyList<uint> framebuffer)
{
    var directory = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }

    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    writer.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{Rp2C02Ppu.ScreenWidth} {Rp2C02Ppu.ScreenHeight}\n255\n"));
    foreach (var pixel in framebuffer)
    {
        writer.Write((byte)((pixel >> 16) & 0xFF));
        writer.Write((byte)((pixel >> 8) & 0xFF));
        writer.Write((byte)(pixel & 0xFF));
    }
}

static IReadOnlyList<NesInputEvent> LoadInputScript(string path, NesButtons initialController1, NesButtons initialController2)
{
    using var stream = File.OpenRead(path);
    using var document = System.Text.Json.JsonDocument.Parse(stream);
    if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
    {
        throw new InvalidDataException("Input script root must be a JSON array.");
    }

    var events = new List<NesInputEvent>
    {
        new(0, initialController1, initialController2)
    };
    var controller1 = initialController1;
    var controller2 = initialController2;
    foreach (var element in document.RootElement.EnumerateArray())
    {
        if (!element.TryGetProperty("cycle", out var cycleElement) || !cycleElement.TryGetUInt64(out var cycle))
        {
            throw new InvalidDataException("Every input event requires an unsigned 'cycle' value.");
        }

        if (element.TryGetProperty("controller1", out var controller1Element))
        {
            controller1 = ParseButtons(controller1Element.GetString() ?? "None");
        }

        if (element.TryGetProperty("controller2", out var controller2Element))
        {
            controller2 = ParseButtons(controller2Element.GetString() ?? "None");
        }

        events.Add(new NesInputEvent(cycle, controller1, controller2));
    }

    return events;
}

static NesButtons ParseButtons(string value)
{
    if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
    {
        return NesButtons.None;
    }

    var buttons = NesButtons.None;
    foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (!Enum.TryParse<NesButtons>(token, ignoreCase: true, out var parsed) || parsed == NesButtons.None)
        {
            throw new ArgumentException($"Unknown NES button '{token}'.");
        }

        buttons |= parsed;
    }

    return buttons;
}

static string FormatButtons(NesButtons buttons) => buttons == NesButtons.None ? "None" : buttons.ToString();

static void WriteUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  AxetosOS.Products.NES.HeadlessHost <path-to-rom.nes>");
    Console.Error.WriteLine("  AxetosOS.Products.NES.HeadlessHost <path-to-rom.nes> [--cycles <count>] [--frame <output.ppm>] [--controller1 <buttons>] [--controller2 <buttons>] [--input-script <events.json>]");
    Console.Error.WriteLine("  Buttons are comma-separated: A,B,Select,Start,Up,Down,Left,Right");
}
