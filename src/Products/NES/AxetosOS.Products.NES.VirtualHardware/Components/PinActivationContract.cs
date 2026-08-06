using System.Runtime.CompilerServices;

namespace AxetosOS.Products.NES.VirtualHardware.Components;

/// <summary>
/// A topology-time description of whether a sampled package-pin transition can
/// activate its owning chip. Contracts are compiled once for the assembled
/// motherboard and then reused for every electrical transition until topology
/// changes or power is cycled.
/// </summary>
public readonly struct PinActivationContract
{
    private readonly Func<bool>? _condition;

    private PinActivationContract(PinActivationMode mode, Func<bool>? condition)
    {
        Mode = mode;
        _condition = condition;
    }

    public PinActivationMode Mode { get; }

    public static PinActivationContract Never { get; } =
        new(PinActivationMode.Never, null);

    public static PinActivationContract Always { get; } =
        new(PinActivationMode.Always, null);

    public static PinActivationContract When(Func<bool> condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return new PinActivationContract(PinActivationMode.Conditional, condition);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsActive()
    {
        return Mode switch
        {
            PinActivationMode.Never => false,
            PinActivationMode.Always => true,
            PinActivationMode.Conditional => _condition!(),
            _ => false
        };
    }
}

public enum PinActivationMode : byte
{
    Never = 0,
    Always = 1,
    Conditional = 2
}
