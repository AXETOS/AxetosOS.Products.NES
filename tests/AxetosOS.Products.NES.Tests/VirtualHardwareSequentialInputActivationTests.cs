using AxetosOS.Products.NES.VirtualHardware.Components;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class VirtualHardwareSequentialInputActivationTests
{
    [Fact]
    public void Motherboard_activation_contract_type_has_been_removed()
    {
        var assembly = typeof(VirtualHardwareComponent).Assembly;
        Assert.Null(assembly.GetType(
            "AxetosOS.Products.NES.VirtualHardware.Components.IInputActivationContractProvider"));
        Assert.Null(assembly.GetType(
            "AxetosOS.Products.NES.VirtualHardware.Components.PinActivationContract"));
    }

    [Fact]
    public void Motherboard_clock_edge_dispatch_contract_has_been_removed()
    {
        var assembly = typeof(VirtualHardwareComponent).Assembly;
        Assert.Null(assembly.GetType(
            "AxetosOS.Products.NES.VirtualHardware.Components.IClockEdgeDrivenVirtualHardwareComponent"));
    }
}
