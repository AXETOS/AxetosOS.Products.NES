namespace AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;

/// <summary>
/// Reusable Konami VRC IRQ divider/counter circuitry shared by the VRC4 and
/// VRC6 families. One CPU cycle subtracts three PPU-clock units from the
/// prescaler in scanline mode; cycle mode bypasses the 341-dot divider.
/// </summary>
public sealed class KonamiVrcIrqCounter
{
    private byte _reloadValue;
    private byte _counter;
    private int _prescaler;
    private bool _enabled;
    private bool _enabledAfterAcknowledge;
    private bool _cycleMode;
    private bool _asserted;

    public byte ReloadValue => _reloadValue;
    public byte Counter => _counter;
    public int Prescaler => _prescaler;
    public bool Enabled => _enabled;
    public bool EnabledAfterAcknowledge => _enabledAfterAcknowledge;
    public bool CycleMode => _cycleMode;
    public bool Asserted => _asserted;
    public ulong CpuClockCount { get; private set; }
    public ulong CounterClockCount { get; private set; }
    public ulong AssertCount { get; private set; }

    public void Reset()
    {
        _reloadValue = 0;
        _counter = 0;
        _prescaler = 0;
        _enabled = false;
        _enabledAfterAcknowledge = false;
        _cycleMode = false;
        _asserted = false;
        CpuClockCount = 0;
        CounterClockCount = 0;
        AssertCount = 0;
    }

    public void ClockCpuCycle()
    {
        CpuClockCount++;
        if (!_enabled) return;

        if (_cycleMode)
        {
            ClockCounter();
            return;
        }

        _prescaler -= 3;
        if (_prescaler > 0) return;

        ClockCounter();
        _prescaler += 341;
    }

    public void SetReloadNibble(byte value, bool highNibble)
    {
        if (highNibble)
            _reloadValue = (byte)((_reloadValue & 0x0F) | ((value & 0x0F) << 4));
        else
            _reloadValue = (byte)((_reloadValue & 0xF0) | (value & 0x0F));
    }

    public void SetControl(byte value)
    {
        _enabledAfterAcknowledge = (value & 0x01) != 0;
        _enabled = (value & 0x02) != 0;
        _cycleMode = (value & 0x04) != 0;
        if (_enabled)
        {
            _counter = _reloadValue;
            _prescaler = 341;
        }

        _asserted = false;
    }

    public void Acknowledge()
    {
        _enabled = _enabledAfterAcknowledge;
        _asserted = false;
    }

    private void ClockCounter()
    {
        CounterClockCount++;
        if (_counter == 0xFF)
        {
            _counter = _reloadValue;
            if (!_asserted)
            {
                _asserted = true;
                AssertCount++;
            }
        }
        else
        {
            _counter++;
        }
    }
}
