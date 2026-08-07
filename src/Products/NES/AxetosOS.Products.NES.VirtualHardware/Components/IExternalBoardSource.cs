namespace AxetosOS.Products.NES.VirtualHardware.Components;

/// <summary>
/// Host-controlled electrical stimulus mounted on a board, such as a rail,
/// oscillator, switch or reset button. Applying power may only establish that
/// source's own output-pin drive; it may not invoke package behavior.
/// </summary>
public interface IExternalBoardSource
{
    void ApplyPowerOnDrive();
}
