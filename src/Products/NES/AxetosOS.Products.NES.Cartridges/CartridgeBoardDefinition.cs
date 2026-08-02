using System.Text.Json;

namespace AxetosOS.Products.NES.Cartridges;

public sealed record CartridgeBoardDefinition(
    string Id,
    string Name,
    int Mapper,
    IReadOnlyList<CartridgeComponentDefinition> Components,
    IReadOnlyList<string> Connections,
    string? Notes)
{
    public static CartridgeBoardDefinition Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return JsonSerializer.Deserialize<CartridgeBoardDefinition>(stream, Options)
            ?? throw new InvalidDataException("Cartridge board definition is empty or invalid.");
    }

    private static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true
    };
}

public sealed record CartridgeComponentDefinition(
    string Id,
    string Type,
    string? Source,
    int? FallbackSize,
    int? Width,
    string? Role);
