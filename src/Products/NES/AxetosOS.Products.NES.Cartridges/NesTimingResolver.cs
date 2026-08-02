namespace AxetosOS.Products.NES.Cartridges;

public enum NesTimingSource
{
    ManualOverride,
    Nes20Header,
    INesHeader,
    FileName,
    Default
}

public sealed record NesTimingSelection(NesTimingMode Mode, NesTimingSource Source)
{
    public string DisplayName => Mode switch
    {
        NesTimingMode.Pal => "PAL",
        NesTimingMode.Dendy => "Dendy",
        _ => "NTSC"
    };
}

public static class NesTimingResolver
{
    public static NesTimingSelection Resolve(NesRomImage image, string? fileName, NesTimingMode? manualOverride = null)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (manualOverride is { } forced && forced is not (NesTimingMode.Unknown or NesTimingMode.MultiRegion))
            return new NesTimingSelection(forced, NesTimingSource.ManualOverride);

        if (image.HeaderFormat == NesHeaderFormat.Nes20 && image.HeaderTiming is not (NesTimingMode.Unknown or NesTimingMode.MultiRegion))
            return new NesTimingSelection(image.HeaderTiming, NesTimingSource.Nes20Header);

        if (image.HeaderFormat == NesHeaderFormat.INes && image.HeaderTiming == NesTimingMode.Pal)
            return new NesTimingSelection(NesTimingMode.Pal, NesTimingSource.INesHeader);

        var name = Path.GetFileNameWithoutExtension(fileName ?? string.Empty);
        if (ContainsAny(name, "(Europe)", "(Australia)", "(PAL)", "[PAL]"))
            return new NesTimingSelection(NesTimingMode.Pal, NesTimingSource.FileName);
        if (ContainsAny(name, "(Dendy)", "[Dendy]"))
            return new NesTimingSelection(NesTimingMode.Dendy, NesTimingSource.FileName);
        if (ContainsAny(name, "(USA)", "(Japan)", "(World)", "(North America)", "(NTSC)", "[NTSC]"))
            return new NesTimingSelection(NesTimingMode.Ntsc, NesTimingSource.FileName);

        return new NesTimingSelection(NesTimingMode.Ntsc, NesTimingSource.Default);
    }

    private static bool ContainsAny(string value, params string[] markers) =>
        markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
