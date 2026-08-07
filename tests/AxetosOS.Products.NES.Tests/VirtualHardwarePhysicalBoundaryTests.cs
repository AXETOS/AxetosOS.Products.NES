using System.Reflection;
using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;
using AxetosOS.Products.NES.VirtualHardware.Components.Nes;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

/// <summary>
/// Architectural guardrails for the physical-hardware model. A real IC is one
/// motherboard-visible package no matter how many functional blocks exist in
/// its silicon. Internal CPU/APU/DMA/PPU work therefore must remain ordinary
/// package-local state/classes and must never be wired together through a
/// private VirtualHardwareBoard/DigitalNet graph.
/// </summary>
public sealed class VirtualHardwarePhysicalBoundaryTests
{
    [Fact]
    public void Concrete_components_do_not_embed_peer_packages_or_private_board_nets()
    {
        var componentBase = typeof(VirtualHardwareComponent);
        var concreteTypes = componentBase.Assembly.GetTypes()
            .Where(type => !type.IsAbstract && componentBase.IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(concreteTypes);

        foreach (var type in concreteTypes)
        {
            var fields = type.GetFields(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly);

            foreach (var field in fields)
            {
                Assert.False(
                    ContainsForbiddenPhysicalBoundaryType(field.FieldType),
                    $"Physical component '{type.FullName}' embeds motherboard/package topology through field '{field.Name}' ({field.FieldType.FullName}). Internal hardware blocks must remain plain package-local state/classes and must not contain peer packages, private boards, or private nets.");
            }
        }
    }

    [Fact]
    public void Component_base_retains_only_owned_pins_not_motherboard_nets()
    {
        var fields = typeof(VirtualHardwareComponent).GetFields(
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly);

        Assert.DoesNotContain(fields, field => ContainsForbiddenPhysicalBoundaryType(field.FieldType));
        Assert.Contains(fields, field => field.FieldType == typeof(DigitalPin[]));
    }

    [Theory]
    [InlineData(typeof(Rp2A03))]
    [InlineData(typeof(Rp2A07))]
    [InlineData(typeof(Rp2C02))]
    [InlineData(typeof(Rp2C07))]
    public void Large_ricoh_ics_keep_all_functional_blocks_inside_one_package(Type chipType)
    {
        var fields = chipType.GetFields(
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly);

        Assert.DoesNotContain(fields, field => ContainsForbiddenPhysicalBoundaryType(field.FieldType));
    }

    [Fact]
    public void Famicom_board_exposes_only_real_package_level_cpu_ppu_boundaries()
    {
        var board = new FamicomMotherboard();

        Assert.Single(board.Board.Components.OfType<Rp2A03>());
        Assert.Single(board.Board.Components.OfType<Rp2C02>());

        var forbiddenLegacyNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "NesPpuTimingCore",
            "NesPpuRegisterPackage",
            "NesPpuMemoryDevice",
            "NesOamDmaController",
            "NesControllerIoPackage"
        };

        Assert.DoesNotContain(
            board.Board.Components,
            component => forbiddenLegacyNames.Contains(component.GetType().Name));
    }

    [Fact]
    public void Ntsc_board_exposes_only_real_package_level_cpu_ppu_boundaries()
    {
        var board = new NtscNesMotherboard();

        Assert.Single(board.Board.Components.OfType<Rp2A03>());
        Assert.Single(board.Board.Components.OfType<Rp2C02>());
        Assert.Single(board.Board.Components.OfType<Cic3193>());
    }

    [Fact]
    public void Pal_board_exposes_only_real_package_level_cpu_ppu_boundaries()
    {
        var board = new PalNesMotherboard(PalCicVariant.PalA3195);

        Assert.Single(board.Board.Components.OfType<Rp2A07>());
        Assert.Single(board.Board.Components.OfType<Rp2C07>());
    }

    [Fact]
    public void Internal_ricoh_blocks_are_not_motherboard_components()
    {
        var board = new FamicomMotherboard();
        var componentTypes = board.Board.Components.Select(component => component.GetType()).ToArray();

        Assert.DoesNotContain(componentTypes, type => type.Name.Contains("PulseChannel", StringComparison.Ordinal));
        Assert.DoesNotContain(componentTypes, type => type.Name.Contains("TriangleChannel", StringComparison.Ordinal));
        Assert.DoesNotContain(componentTypes, type => type.Name.Contains("NoiseChannel", StringComparison.Ordinal));
        Assert.DoesNotContain(componentTypes, type => type.Name.Contains("DmcChannel", StringComparison.Ordinal));
        Assert.DoesNotContain(componentTypes, type => type.Name.Contains("TimingCore", StringComparison.Ordinal));
        Assert.DoesNotContain(componentTypes, type => type.Name.Contains("Sprite", StringComparison.Ordinal) && type != typeof(NesStandardController));
    }
    [Fact]
    public void Digital_net_transport_does_not_cache_receiver_activation_semantics()
    {
        var fields = typeof(DigitalNet).GetFields(
            BindingFlags.Instance |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly);

        Assert.DoesNotContain(fields, field => field.Name.Contains("Activation", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fields, field => field.Name.Contains("Rising", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fields, field => field.Name.Contains("Falling", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsForbiddenPhysicalBoundaryType(Type type)
    {
        if (typeof(IVirtualHardwareComponent).IsAssignableFrom(type) ||
            typeof(VirtualHardwareBoard).IsAssignableFrom(type) ||
            typeof(DigitalNet).IsAssignableFrom(type))
        {
            return true;
        }

        if (type.IsArray)
            return ContainsForbiddenPhysicalBoundaryType(type.GetElementType()!);

        if (!type.IsGenericType) return false;
        return type.GetGenericArguments().Any(ContainsForbiddenPhysicalBoundaryType);
    }

}
