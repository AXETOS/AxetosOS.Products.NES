using AxetosOS.Products.NES.Abstractions;

namespace AxetosOS.Products.NES.Hardware;

public sealed class SignalLine : ISignalLine
{
    public SignalLine(bool initiallyAsserted = false) => IsAsserted = initiallyAsserted;

    public bool IsAsserted { get; private set; }
    public event Action? Asserted;
    public event Action? Released;

    public void Assert()
    {
        if (IsAsserted)
        {
            return;
        }

        IsAsserted = true;
        Asserted?.Invoke();
    }

    public void Release()
    {
        if (!IsAsserted)
        {
            return;
        }

        IsAsserted = false;
        Released?.Invoke();
    }
}
