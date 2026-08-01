using System.Text.Json;

namespace AxetosOS.Products.NES.Cartridges;

public sealed record MapperCatalog(IReadOnlyList<MapperCatalogEntry> Definitions)
{
    public static MapperCatalog Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var catalog = JsonSerializer.Deserialize<MapperCatalog>(stream, JsonOptions)
            ?? throw new InvalidDataException("Mapper catalog is empty or invalid.");

        return catalog;
    }

    public MapperCatalogEntry Resolve(int mapper, int? submapper)
    {
        var exact = Definitions.FirstOrDefault(entry =>
            entry.Mapper == mapper && entry.Submapper == submapper);

        if (exact is not null)
        {
            return exact;
        }

        var fallback = Definitions.FirstOrDefault(entry =>
            entry.Mapper == mapper && entry.Submapper is null);

        return fallback ?? throw new UnsupportedMapperException(mapper, submapper);
    }

    private static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true
    };
}

public sealed record MapperCatalogEntry(
    int Mapper,
    int? Submapper,
    string Name,
    string Definition);

public sealed class UnsupportedMapperException(int mapper, int? submapper)
    : NotSupportedException($"Mapper {mapper}, submapper {submapper?.ToString() ?? "unspecified"}, is not defined.")
{
    public int Mapper { get; } = mapper;
    public int? Submapper { get; } = submapper;
}
