using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;

/// <summary>
/// Standalone mapper-0 cartridge board. PRG and CHR devices react only to the
/// normalized cartridge connector pins; no CPU, PPU, or motherboard calls are used.
/// </summary>
public sealed class NromCartridge : VirtualHardwareComponent, ISelectiveInputDrivenVirtualHardwareComponent, IInputActivationContractProvider
{
    private byte[] _prg = [];
    private byte[] _chr = [];
    private bool _chrRam;
    private VirtualHardwareNesMirroring _mirroring;
    private byte _ppuLowAddressLatch;
    private bool _cpuReadActive;
    private bool _ppuReadActive;
    private bool _ppuWriteActive;

    public NromCartridge(string componentId) : base(componentId)
    {
        Vcc = AddPin("VCC", PinDirection.Input);
        Gnd = AddPin("GND", PinDirection.Input);
        CpuAddress = new DigitalBus($"{componentId}.CPU.A", Enumerable.Range(0, 16).Select(i => AddPin($"CPU.A{i}", PinDirection.Input)).ToArray());
        CpuData = new DigitalBus($"{componentId}.CPU.D", Enumerable.Range(0, 8).Select(i => AddPin($"CPU.D{i}", PinDirection.Bidirectional)).ToArray());
        CpuReadWrite = AddPin("CPU.RW", PinDirection.Input);
        CpuM2 = AddPin("CPU.M2", PinDirection.Input);
        PpuAddressData = new DigitalBus($"{componentId}.PPU.AD", Enumerable.Range(0, 8).Select(i => AddPin($"PPU.AD{i}", PinDirection.Bidirectional)).ToArray());
        PpuHighAddress = new DigitalBus($"{componentId}.PPU.AH", Enumerable.Range(8, 6).Select(i => AddPin($"PPU.A{i}", PinDirection.Input)).ToArray());
        PpuAle = AddPin("PPU.ALE", PinDirection.Input);
        PpuReadBar = AddPin("PPU.RD_BAR", PinDirection.Input);
        PpuWriteBar = AddPin("PPU.WR_BAR", PinDirection.Input);
        CiramChipEnableBar = AddPin("CIRAM.CE_BAR", PinDirection.Output);
        CiramA10 = AddPin("CIRAM.A10", PinDirection.Output);
        IrqBar = AddPin("IRQ_BAR", PinDirection.Output);
    }

    public void LoadImage(VirtualHardwareNesRomImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.MapperNumber != 0) throw new NotSupportedException($"Mapper {image.MapperNumber} is not NROM.");
        if (image.PrgRom.Length is not (16 * 1024) and not (32 * 1024))
            throw new ArgumentException("NROM PRG must be 16 KiB or 32 KiB.", nameof(image));
        _prg = image.PrgRom.ToArray();
        _chrRam = image.ChrRom.Length == 0;
        _chr = _chrRam ? new byte[8 * 1024] : image.ChrRom.ToArray();
        if (_chr.Length != 8 * 1024) throw new ArgumentException("NROM CHR must be 8 KiB or absent for CHR RAM.", nameof(image));
        _mirroring = image.Mirroring;
        IsInserted = true;
        Reset();
    }

    public void Eject()
    {
        IsInserted = false;
        _prg = [];
        _chr = [];
        _cpuReadActive = false;
        _ppuReadActive = false;
        _ppuWriteActive = false;
        ReleaseOutputs();
    }

    public DigitalPin Vcc { get; }
    public DigitalPin Gnd { get; }
    public DigitalBus CpuAddress { get; }
    public DigitalBus CpuData { get; }
    public DigitalPin CpuReadWrite { get; }
    public DigitalPin CpuM2 { get; }
    public DigitalBus PpuAddressData { get; }
    public DigitalBus PpuHighAddress { get; }
    public DigitalPin PpuAle { get; }
    public DigitalPin PpuReadBar { get; }
    public DigitalPin PpuWriteBar { get; }
    public DigitalPin CiramChipEnableBar { get; }
    public DigitalPin CiramA10 { get; }
    public DigitalPin IrqBar { get; }
    public bool IsChrRam => _chrRam;
    public bool IsInserted { get; private set; }
    public ulong CpuReadCount { get; private set; }
    public ushort LastCpuReadAddress { get; private set; }
    public byte LastCpuReadData { get; private set; }
    public ulong PpuReadCount { get; private set; }
    public ulong PpuWriteCount { get; private set; }

    public PinActivationContract CompileInputActivation(DigitalPin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        if (pin.Direction == PinDirection.Input) return PinActivationContract.Always;
        if (CpuData.Pins.Contains(pin)) return PinActivationContract.Never;
        if (PpuAddressData.Pins.Contains(pin))
        {
            return PinActivationContract.When(() =>
                PpuAle.SampledLevel == DigitalLevel.High ||
                (_chrRam &&
                 PpuAle.SampledLevel != DigitalLevel.High &&
                 PpuWriteBar.SampledLevel == DigitalLevel.Low));
        }

        return PinActivationContract.Never;
    }

    public bool ShouldWakeForSampledPin(DigitalPin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);

        if (pin.Direction == PinDirection.Input) return true;

        // PRG ROM never consumes CPU data. During a selected read this bus is
        // cartridge output; at every other time it is electrically irrelevant.
        if (CpuData.Pins.Contains(pin)) return false;

        if (PpuAddressData.Pins.Contains(pin))
        {
            // AD0-AD7 are inputs only while ALE exposes the low address byte,
            // or while CHR RAM is accepting write data. During CHR reads the
            // resolved level is merely the echo of the cartridge's own output.
            return PpuAle.SampledLevel == DigitalLevel.High ||
                (_chrRam &&
                 PpuAle.SampledLevel != DigitalLevel.High &&
                 PpuWriteBar.SampledLevel == DigitalLevel.Low);
        }

        return false;
    }

    public override void PowerOn() => Reset();
    public override void Reset()
    {
        _ppuLowAddressLatch = 0;
        _cpuReadActive = false;
        _ppuReadActive = false;
        _ppuWriteActive = false;
        ReleaseOutputs();
    }

    public override void Evaluate()
    {
        if (!IsInserted || Vcc.SampledLevel != DigitalLevel.High || Gnd.SampledLevel != DigitalLevel.Low)
        {
            _cpuReadActive = false;
            _ppuReadActive = false;
            _ppuWriteActive = false;
            ReleaseOutputs();
            return;
        }

        IrqBar.Release(); // NROM has no IRQ source.

        if (PpuAle.SampledLevel == DigitalLevel.High)
        {
            // During ALE the cartridge must not own AD0-AD7; the PPU is placing
            // the low address byte on the multiplexed bus.
            PpuAddressData.Release();
            _ppuReadActive = false;
            _ppuWriteActive = false;
            if (PpuAddressData.TrySample(out var low))
            {
                _ppuLowAddressLatch = (byte)low;
            }
        }

        var ppuAddressKnown = PpuHighAddress.TrySample(out var high);
        var ppuAddress = (ushort)(((high & 0x3F) << 8) | _ppuLowAddressLatch);
        var ppuReadSelected = false;
        var ppuWriteSelected = false;

        if (ppuAddressKnown)
        {
            var nametable = (ppuAddress & 0x2000) != 0;
            CiramChipEnableBar.Drive(nametable ? DigitalLevel.Low : DigitalLevel.High);
            var a10SourceBit = _mirroring switch
            {
                VirtualHardwareNesMirroring.Vertical => 10,
                VirtualHardwareNesMirroring.Horizontal => 11,
                _ => 10
            };
            CiramA10.Drive((ppuAddress & (1 << a10SourceBit)) == 0 ? DigitalLevel.Low : DigitalLevel.High);

            if (PpuAle.SampledLevel != DigitalLevel.High && ppuAddress < 0x2000)
            {
                ppuReadSelected = PpuReadBar.SampledLevel == DigitalLevel.Low;
                ppuWriteSelected = _chrRam && PpuWriteBar.SampledLevel == DigitalLevel.Low;

                if (ppuReadSelected)
                {
                    PpuAddressData.Drive(_chr[ppuAddress & 0x1FFF]);
                    if (!_ppuReadActive)
                    {
                        PpuReadCount++;
                    }
                }
                else
                {
                    PpuAddressData.Release();
                    if (ppuWriteSelected && PpuAddressData.TrySample(out var data) && !_ppuWriteActive)
                    {
                        _chr[ppuAddress & 0x1FFF] = (byte)data;
                        PpuWriteCount++;
                    }
                }
            }
            else if (PpuAle.SampledLevel != DigitalLevel.High)
            {
                PpuAddressData.Release();
            }
        }
        else
        {
            CiramChipEnableBar.Drive(DigitalLevel.High);
            CiramA10.Drive(DigitalLevel.Unknown);
            if (PpuAle.SampledLevel != DigitalLevel.High)
            {
                PpuAddressData.Release();
            }
        }

        _ppuReadActive = ppuReadSelected;
        _ppuWriteActive = ppuWriteSelected;

        // PRG output remains valid for the complete selected CPU read cycle.
        // M2 qualifies a new cartridge transaction, but it must not make the
        // ROM release D0-D7 between the address phase and the RP2A03 sample
        // phase. Holding the data while A15 and R/W remain selected also makes
        // component evaluation order irrelevant during electrical settling.
        var cpuAddressKnown = CpuAddress.TrySample(out var cpuAddress);
        var cpuReadAddressSelected = cpuAddressKnown && cpuAddress >= 0x8000 &&
            CpuReadWrite.SampledLevel == DigitalLevel.High;
        var cpuReadTransactionSelected = cpuReadAddressSelected &&
            CpuM2.SampledLevel == DigitalLevel.High;

        if (cpuReadAddressSelected)
        {
            var index = _prg.Length == 16 * 1024
                ? (int)(cpuAddress & 0x3FFF)
                : (int)(cpuAddress & 0x7FFF);
            CpuData.Drive(_prg[index]);
        }
        else
        {
            CpuData.Release();
        }

        if (cpuReadTransactionSelected && !_cpuReadActive)
        {
            CpuReadCount++;
            LastCpuReadAddress = (ushort)cpuAddress;
            var index = _prg.Length == 16 * 1024
                ? (int)(cpuAddress & 0x3FFF)
                : (int)(cpuAddress & 0x7FFF);
            LastCpuReadData = _prg[index];
        }

        _cpuReadActive = cpuReadTransactionSelected;
    }

    private void ReleaseOutputs()
    {
        CpuData.Release();
        PpuAddressData.Release();
        CiramChipEnableBar.Release();
        CiramA10.Release();
        IrqBar.Release();
    }
}
