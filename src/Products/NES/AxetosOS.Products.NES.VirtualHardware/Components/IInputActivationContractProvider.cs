using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components;

/// <summary>
/// Allows a reusable chip package to publish its external activation rules to
/// whichever motherboard it is installed on. The motherboard compiles these
/// rules with the actual wiring at power-on/topology build time; the chip still
/// owns all internal state and output behavior.
/// </summary>
public interface IInputActivationContractProvider : IInputDrivenVirtualHardwareComponent
{
    PinActivationContract CompileInputActivation(DigitalPin pin);
}
