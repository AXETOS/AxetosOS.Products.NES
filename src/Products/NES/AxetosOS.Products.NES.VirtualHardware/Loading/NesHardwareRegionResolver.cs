using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;

namespace AxetosOS.Products.NES.VirtualHardware.Loading;

/// <summary>
/// Software-side policy that selects which physical motherboard profile to
/// construct. The motherboard itself never examines filenames or ROM headers.
/// </summary>
public static class NesHardwareRegionResolver
{
    public static NesResolvedRegion Resolve(
        VirtualHardwareNesRomImage image,
        string? fileName,
        NesRegionSelection selection = NesRegionSelection.Auto)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (selection != NesRegionSelection.Auto)
        {
            return new NesResolvedRegion(
                ToHardwareRegion(selection),
                NesRegionSelectionSource.ManualOverride,
                $"Explicit {selection} override.");
        }

        if (image.HeaderFormat == VirtualHardwareNesHeaderFormat.Nes20)
        {
            if (image.HeaderTiming == VirtualHardwareNesHeaderTiming.Pal)
                return Pal(NesRegionSelectionSource.Nes20Header, "NES 2.0 timing field declares PAL.");

            if (image.HeaderTiming == VirtualHardwareNesHeaderTiming.Ntsc)
            {
                // NES 2.0 distinguishes timing families but not NTSC-U from
                // NTSC-J, so a Japan filename marker may refine NTSC safely.
                if (LooksJapanese(fileName))
                    return Japan(NesRegionSelectionSource.FileName, "NES 2.0 declares NTSC and the filename identifies Japan.");
                return NorthAmerica(NesRegionSelectionSource.Nes20Header, "NES 2.0 timing field declares NTSC.");
            }
        }

        if (image.HeaderFormat == VirtualHardwareNesHeaderFormat.INes &&
            image.HeaderTiming == VirtualHardwareNesHeaderTiming.Pal)
        {
            return Pal(NesRegionSelectionSource.INesHeader, "Legacy iNES PAL hint is set.");
        }

        if (LooksPal(fileName))
            return Pal(NesRegionSelectionSource.FileName, "Filename contains a PAL-region marker.");
        if (LooksJapanese(fileName))
            return Japan(NesRegionSelectionSource.FileName, "Filename contains a Japan-region marker.");
        if (LooksNorthAmerican(fileName))
            return NorthAmerica(NesRegionSelectionSource.FileName, "Filename contains a North American NTSC marker.");

        // Multi-region and Dendy headers cannot be represented by the current
        // three physical profiles. Auto mode deliberately falls back to the
        // established NTSC-U default; a user can override it in the host.
        return NorthAmerica(NesRegionSelectionSource.Default, "No decisive region metadata; using NTSC-U default.");
    }

    private static NesHardwareRegion ToHardwareRegion(NesRegionSelection selection) => selection switch
    {
        NesRegionSelection.NtscNorthAmerica => NesHardwareRegion.NtscNorthAmerica,
        NesRegionSelection.NtscJapan => NesHardwareRegion.NtscJapan,
        NesRegionSelection.Pal => NesHardwareRegion.Pal,
        _ => throw new ArgumentOutOfRangeException(nameof(selection), selection, "Auto is not a physical motherboard region.")
    };

    private static NesResolvedRegion NorthAmerica(NesRegionSelectionSource source, string reason) =>
        new(NesHardwareRegion.NtscNorthAmerica, source, reason);

    private static NesResolvedRegion Japan(NesRegionSelectionSource source, string reason) =>
        new(NesHardwareRegion.NtscJapan, source, reason);

    private static NesResolvedRegion Pal(NesRegionSelectionSource source, string reason) =>
        new(NesHardwareRegion.Pal, source, reason);

    private static bool LooksPal(string? fileName) => ContainsAny(fileName,
        "(Europe)", "(Australia)", "(PAL)", "[PAL]", "(E)", "(AUS)");

    private static bool LooksJapanese(string? fileName) => ContainsAny(fileName,
        "(Japan)", "(Japan, USA)", "(J)", "[J]", "(Famicom)");

    private static bool LooksNorthAmerican(string? fileName) => ContainsAny(fileName,
        "(USA)", "(North America)", "(U)", "[U]", "(NTSC-U)");

    private static bool ContainsAny(string? fileName, params string[] markers)
    {
        var name = Path.GetFileNameWithoutExtension(fileName ?? string.Empty);
        return markers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
