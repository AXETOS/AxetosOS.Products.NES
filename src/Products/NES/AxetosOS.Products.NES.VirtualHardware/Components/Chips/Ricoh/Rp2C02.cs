using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;

/// <summary>
/// Standalone NTSC Ricoh RP2C02 package. The chip exposes only package pins and
/// advances from its external master clock. This foundation implements package
/// power/reset behaviour, raster timing, the CPU-visible control/status port,
/// and the external PPU memory-bus pins without references to a motherboard,
/// CPU, cartridge, renderer, or memory object.
/// </summary>
public sealed class Rp2C02 : VirtualHardwareComponent
{
    private const int DotsPerScanline = 341;
    private const int ScanlinesPerFrame = 262;
    private const int VblankStartScanline = 241;
    private const int PreRenderScanline = 261;

    private DigitalLevel _previousClock;
    private bool _cpuSelectedLast;
    private bool _vblank;
    private byte _control;
    private byte _openBus;

    public Rp2C02(string componentId) : base(componentId)
    {
        Vcc = AddPin("VCC", PinDirection.Input);
        Gnd = AddPin("GND", PinDirection.Input);
        Clock = AddPin("CLK", PinDirection.Input);
        ResetBar = AddPin("/RES", PinDirection.Input);
        NmiBar = AddPin("/NMI", PinDirection.Output);

        RegisterSelect = CreateBus("RS", 3, PinDirection.Input);
        CpuData = CreateBus("D", 8, PinDirection.Bidirectional);
        CpuReadWrite = AddPin("R/W", PinDirection.Input);
        ChipSelectBar = AddPin("/CS", PinDirection.Input);

        MultiplexedAddressData = CreateBus("AD", 8, PinDirection.Bidirectional);
        HighAddress = CreateBus("A", 6, PinDirection.Output, firstBitNumber: 8);
        AddressLatchEnable = AddPin("ALE", PinDirection.Output);
        VramReadBar = AddPin("/RD", PinDirection.Output);
        VramWriteBar = AddPin("/WR", PinDirection.Output);
        Extension = CreateBus("EXT", 4, PinDirection.Bidirectional);
    }

    public DigitalPin Vcc { get; }
    public DigitalPin Gnd { get; }
    public DigitalPin Clock { get; }
    public DigitalPin ResetBar { get; }
    public DigitalPin NmiBar { get; }
    public DigitalBus RegisterSelect { get; }
    public DigitalBus CpuData { get; }
    public DigitalPin CpuReadWrite { get; }
    public DigitalPin ChipSelectBar { get; }
    public DigitalBus MultiplexedAddressData { get; }
    public DigitalBus HighAddress { get; }
    public DigitalPin AddressLatchEnable { get; }
    public DigitalPin VramReadBar { get; }
    public DigitalPin VramWriteBar { get; }
    public DigitalBus Extension { get; }

    public int Dot { get; private set; }
    public int Scanline { get; private set; }
    public ulong Frame { get; private set; }
    public ulong MasterClockRisingEdgeCount { get; private set; }
    public bool Vblank => _vblank;
    public bool NmiEnabled => (_control & 0x80) != 0;
    public byte ControlRegister => _control;

    private bool Powered => Vcc.SampledLevel == DigitalLevel.High && Gnd.SampledLevel == DigitalLevel.Low;

    public override void PowerOn()
    {
        Dot = 0;
        Scanline = 0;
        Frame = 0;
        MasterClockRisingEdgeCount = 0;
        _previousClock = DigitalLevel.Low;
        _cpuSelectedLast = false;
        _vblank = false;
        _control = 0;
        _openBus = 0;
        ReleasePackageOutputs();
    }

    public override void Reset()
    {
        Dot = 0;
        Scanline = 0;
        _vblank = false;
        _control = 0;
        _cpuSelectedLast = false;
        ReleasePackageOutputs();
    }

    public override void Evaluate()
    {
        if (!Powered)
        {
            ReleasePackageOutputs();
            _previousClock = Clock.SampledLevel;
            _cpuSelectedLast = false;
            return;
        }

        if (ResetBar.SampledLevel == DigitalLevel.Low)
        {
            Reset();
            _previousClock = Clock.SampledLevel;
            return;
        }

        var clock = Clock.SampledLevel;
        if (clock == DigitalLevel.High && _previousClock != DigitalLevel.High)
        {
            MasterClockRisingEdgeCount++;
            AdvanceRaster();
        }
        _previousClock = clock;

        HandleCpuPort();
        DriveIdleVramBus();
        DriveNmi();
    }

    private void AdvanceRaster()
    {
        Dot++;
        if (Dot >= DotsPerScanline)
        {
            Dot = 0;
            Scanline++;
            if (Scanline >= ScanlinesPerFrame)
            {
                Scanline = 0;
                Frame++;
            }
        }

        if (Scanline == VblankStartScanline && Dot == 1) _vblank = true;
        else if (Scanline == PreRenderScanline && Dot == 1) _vblank = false;
    }

    private void HandleCpuPort()
    {
        var selected = ChipSelectBar.SampledLevel == DigitalLevel.Low;
        if (!selected)
        {
            CpuData.Release();
            _cpuSelectedLast = false;
            return;
        }

        if (!RegisterSelect.TrySample(out var register))
        {
            CpuData.Release();
            return;
        }

        var read = CpuReadWrite.SampledLevel == DigitalLevel.High;
        if (read)
        {
            if ((register & 7) == 2)
            {
                var status = (byte)((_vblank ? 0x80 : 0) | (_openBus & 0x1F));
                CpuData.Drive(status);
                if (!_cpuSelectedLast)
                {
                    _vblank = false;
                    _openBus = status;
                }
            }
            else
            {
                CpuData.Drive(_openBus);
            }
        }
        else
        {
            CpuData.Release();
            if (!_cpuSelectedLast && CpuData.TrySample(out var value))
            {
                _openBus = (byte)value;
                if ((register & 7) == 0) _control = (byte)value;
            }
        }

        _cpuSelectedLast = true;
    }

    private void DriveIdleVramBus()
    {
        MultiplexedAddressData.Release();
        HighAddress.Release();
        Extension.Release();
        AddressLatchEnable.Drive(DigitalLevel.Low);
        VramReadBar.Drive(DigitalLevel.High);
        VramWriteBar.Drive(DigitalLevel.High);
    }

    private void DriveNmi()
    {
        if (_vblank && NmiEnabled) NmiBar.Drive(DigitalLevel.Low);
        else NmiBar.Release();
    }

    private void ReleasePackageOutputs()
    {
        CpuData.Release();
        MultiplexedAddressData.Release();
        HighAddress.Release();
        Extension.Release();
        AddressLatchEnable.Release();
        VramReadBar.Release();
        VramWriteBar.Release();
        NmiBar.Release();
    }

    private DigitalBus CreateBus(string prefix, int width, PinDirection direction, int firstBitNumber = 0)
    {
        var pins = new DigitalPin[width];
        for (var bit = 0; bit < width; bit++) pins[bit] = AddPin($"{prefix}{bit + firstBitNumber}", direction);
        return new DigitalBus($"{ComponentId}.{prefix}", pins);
    }
}
