using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Power;

public sealed class DigitalPowerRail : VirtualHardwareComponent, IExternalBoardSource
{
    private readonly DigitalLevel _level;

    public DigitalPowerRail(string componentId, DigitalLevel level)
        : base(componentId)
    {
        if (level is not (DigitalLevel.Low or DigitalLevel.High))
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        _level = level;
        Output = AddPin("OUT", level == DigitalLevel.High ? PinDirection.Power : PinDirection.Ground);
        Output.Drive(level);
    }

    public DigitalPin Output { get; }

    public void ApplyPowerOnDrive() => Output.Drive(_level);
}
