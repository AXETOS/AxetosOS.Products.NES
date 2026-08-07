using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Chips.Logic;

/// <summary>
/// Standalone SN74LS368A hex inverting buffer/driver with two active-low
/// output-enable groups.  The package decides internally whether an A-pin
/// transition can reach its output stage; the board always delivers the pin.
/// </summary>
public sealed class Sn74Ls368A : VirtualHardwareComponent
{
    private readonly ulong _powerInputMask;
    private readonly ulong _enable1InputMask;
    private readonly ulong _enable2InputMask;
    private readonly ulong _group1DataMask;
    private readonly ulong _group2DataMask;
    private bool _packagePowered;

    public Sn74Ls368A(string componentId) : base(componentId)
    {
        Vcc = AddPin("VCC", PinDirection.Input);
        Gnd = AddPin("GND", PinDirection.Input);
        Enable1Bar = AddPin("1G_BAR", PinDirection.Input);
        Enable2Bar = AddPin("2G_BAR", PinDirection.Input);
        A = Enumerable.Range(1, 6)
            .Select(channel => AddPin($"{channel}A", PinDirection.Input))
            .ToArray();
        YBar = Enumerable.Range(1, 6)
            .Select(channel => AddPin($"{channel}Y_BAR", PinDirection.Output))
            .ToArray();

        _powerInputMask = Vcc.InputChangeMask | Gnd.InputChangeMask;
        _enable1InputMask = Enable1Bar.InputChangeMask;
        _enable2InputMask = Enable2Bar.InputChangeMask;
        _group1DataMask = A[0].InputChangeMask | A[1].InputChangeMask |
            A[2].InputChangeMask | A[3].InputChangeMask;
        _group2DataMask = A[4].InputChangeMask | A[5].InputChangeMask;
    }

    public DigitalPin Vcc { get; }
    public DigitalPin Gnd { get; }
    public DigitalPin Enable1Bar { get; }
    public DigitalPin Enable2Bar { get; }
    public IReadOnlyList<DigitalPin> A { get; }
    public IReadOnlyList<DigitalPin> YBar { get; }

    protected override void OnInputChanges(ulong changedInputMask)
    {
        var powerChanged = (changedInputMask & _powerInputMask) != 0;
        if (!_packagePowered && !powerChanged) return;

        if (!IsPowered())
        {
            if (_packagePowered) ReleaseAll();
            _packagePowered = false;
            return;
        }

        if (!_packagePowered)
        {
            _packagePowered = true;
            powerChanged = true;
        }

        ProcessGroup(changedInputMask, powerChanged, Enable1Bar, _enable1InputMask, _group1DataMask, 0, 4);
        ProcessGroup(changedInputMask, powerChanged, Enable2Bar, _enable2InputMask, _group2DataMask, 4, 2);
    }

    private void ProcessGroup(
        ulong changedInputMask,
        bool powerChanged,
        DigitalPin enableBar,
        ulong enableMask,
        ulong dataMask,
        int start,
        int count)
    {
        var enableChanged = (changedInputMask & enableMask) != 0;
        var changedData = changedInputMask & dataMask;
        if (!powerChanged && !enableChanged && changedData == 0) return;

        if (enableBar.SampledLevel == DigitalLevel.High)
        {
            // Data can toggle forever while this output group is disconnected.
            // Only a transition of /G to High needs to release the old drive.
            if (powerChanged || enableChanged) ReleaseGroup(start, count);
            return;
        }

        if (enableBar.SampledLevel != DigitalLevel.Low)
        {
            if (powerChanged || enableChanged || changedData != 0)
                DriveGroupUnknown(start, count);
            return;
        }

        if (powerChanged || enableChanged)
        {
            for (var index = start; index < start + count; index++) EvaluateChannel(index);
            return;
        }

        // /G is already active: evaluate only the physical channels whose A
        // pins changed rather than rescanning all six package inputs.
        for (var index = start; index < start + count; index++)
        {
            if ((changedInputMask & A[index].InputChangeMask) != 0) EvaluateChannel(index);
        }
    }

    private void EvaluateChannel(int index)
    {
        YBar[index].Drive(A[index].SampledLevel switch
        {
            DigitalLevel.Low => DigitalLevel.High,
            DigitalLevel.High => DigitalLevel.Low,
            _ => DigitalLevel.Unknown
        });
    }

    private void ReleaseGroup(int start, int count)
    {
        for (var index = start; index < start + count; index++) YBar[index].Release();
    }

    private void DriveGroupUnknown(int start, int count)
    {
        for (var index = start; index < start + count; index++) YBar[index].Drive(DigitalLevel.Unknown);
    }

    private void ReleaseAll()
    {
        for (var index = 0; index < YBar.Count; index++) YBar[index].Release();
    }

    private bool IsPowered() =>
        Vcc.SampledLevel == DigitalLevel.High && Gnd.SampledLevel == DigitalLevel.Low;
}
