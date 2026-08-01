using AxetosOS.Products.NES.Cartridges;
using AxetosOS.Products.NES.Hardware;

if (args.Length is not 1 and not 3)
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
if (args.Length == 3)
{
    if (!string.Equals(args[1], "--cycles", StringComparison.OrdinalIgnoreCase) ||
        !int.TryParse(args[2], out requestedCycles) ||
        requestedCycles < 0)
    {
        WriteUsage();
        return 2;
    }
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

if (requestedCycles == 0)
{
    return 0;
}

if (image.MapperNumber != 0)
{
    Console.Error.WriteLine("CPU boot execution currently supports NROM/mapper 0 only.");
    return 4;
}

var ram = new CpuWorkRam();
ram.PowerOn();
var bus = new CpuBus();
bus.Attach(ram);
bus.Attach(new NromPrgRom(image));
var cpu = new Rp2A03Cpu(bus);
cpu.PowerOn();

Console.WriteLine();
Console.WriteLine($"Reset vector: ${cpu.ProgramCounter:X4}");

try
{
    for (var cycle = 0; cycle < requestedCycles; cycle++)
    {
        cpu.Clock();
    }
}
catch (UnsupportedCpuOpcodeException exception)
{
    Console.Error.WriteLine(exception.Message);
    PrintCpuState(cpu);
    return 5;
}

PrintCpuState(cpu);
return 0;

static void PrintCpuState(Rp2A03Cpu cpu)
{
    Console.WriteLine($"CPU cycles:  {cpu.TotalCycles:N0}");
    Console.WriteLine($"PC:          ${cpu.ProgramCounter:X4}");
    Console.WriteLine($"A:           ${cpu.Accumulator:X2}");
    Console.WriteLine($"X:           ${cpu.X:X2}");
    Console.WriteLine($"Y:           ${cpu.Y:X2}");
    Console.WriteLine($"SP:          ${cpu.StackPointer:X2}");
    Console.WriteLine($"P:           ${cpu.Status:X2}");
    Console.WriteLine($"Last opcode: ${cpu.LastOpcode:X2}");
}

static void WriteUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  AxetosOS.Products.NES.HeadlessHost <path-to-rom.nes>");
    Console.Error.WriteLine("  AxetosOS.Products.NES.HeadlessHost <path-to-rom.nes> --cycles <count>");
}
