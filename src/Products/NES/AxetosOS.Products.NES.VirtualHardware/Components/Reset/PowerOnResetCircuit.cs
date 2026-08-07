using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Reset;

/// <summary>
/// Active-low reset source. Power arrives only through VCC. The external press
/// and release operations change the circuit's own mechanical state and drive
/// its output immediately; no board polling is required.
/// </summary>
public sealed class PowerOnResetCircuit : VirtualHardwareComponent, IExternalBoardSource
{
    private bool _released;

    public PowerOnResetCircuit(string componentId)
        : base(componentId)
    {
        Vcc = AddPin("VCC", PinDirection.Input);
        ResetBar = AddPin("/RESET", PinDirection.Output);
        ResetBar.Drive(DigitalLevel.Unknown);
    }

    public DigitalPin Vcc { get; }
    public DigitalPin ResetBar { get; }
    public bool IsReleased => _released;

    public void Press()
    {
        _released = false;
        DriveOutput();
    }

    public void Release()
    {
        _released = true;
        DriveOutput();
    }

    public void ApplyPowerOnDrive() => Press();

    protected override void OnInputChanges(ulong changedInputMask) => DriveOutput();

    private void DriveOutput()
    {
        if (Vcc.SampledLevel != DigitalLevel.High)
        {
            ResetBar.Drive(DigitalLevel.Unknown);
            return;
        }

        ResetBar.Drive(_released ? DigitalLevel.High : DigitalLevel.Low);
    }
}
