namespace AxetosOS.Products.NES.Hardware;

/// <summary>Combines independent level-sensitive IRQ sources without allowing one source to clear another.</summary>
public sealed class IrqLineCombiner
{
    private readonly Action<bool> _output;
    private readonly List<bool> _states = [];
    private bool _asserted;

    public IrqLineCombiner(Action<bool> output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    public Action<bool> CreateSource()
    {
        var index = _states.Count;
        _states.Add(false);
        return value => SetSource(index, value);
    }

    private void SetSource(int index, bool value)
    {
        if (_states[index] == value) return;
        _states[index] = value;

        var combined = false;
        for (var i = 0; i < _states.Count; i++)
        {
            if (!_states[i]) continue;
            combined = true;
            break;
        }

        if (_asserted == combined) return;
        _asserted = combined;
        _output(combined);
    }
}
