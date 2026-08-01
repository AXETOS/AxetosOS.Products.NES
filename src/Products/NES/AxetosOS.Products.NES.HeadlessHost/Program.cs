using AxetosOS.Products.NES.Cartridges;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: AxetosOS.Products.NES.HeadlessHost <path-to-rom.nes>");
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

Console.WriteLine("AxetosOS Products / NES — ROM inspection host");
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
return 0;
