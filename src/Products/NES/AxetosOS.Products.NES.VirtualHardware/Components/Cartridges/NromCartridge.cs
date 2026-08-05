using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;

/// <summary>
/// Standalone mapper-0 cartridge board. PRG and CHR devices react only to the
/// normalized cartridge connector pins; no CPU, PPU, or motherboard calls are used.
/// </summary>
public sealed class NromCartridge : VirtualHardwareComponent
{
    private byte[] _prg = [];
    private byte[] _chr = [];
    private bool _chrRam;
    private VirtualHardwareNesMirroring _mirroring;
    private byte _ppuLowAddressLatch;

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
    public ulong PpuReadCount { get; private set; }
    public ulong PpuWriteCount { get; private set; }

    public override void PowerOn() => ReleaseOutputs();
    public override void Reset() { _ppuLowAddressLatch = 0; ReleaseOutputs(); }

    public override void Evaluate()
    {
        if (!IsInserted || Vcc.SampledLevel != DigitalLevel.High || Gnd.SampledLevel != DigitalLevel.Low)
        {
            ReleaseOutputs();
            return;
        }

        IrqBar.Release(); // NROM has no IRQ source.
        CpuData.Release();
        PpuAddressData.Release();

        if (PpuAle.SampledLevel == DigitalLevel.High && PpuAddressData.TrySample(out var low))
            _ppuLowAddressLatch = (byte)low;

        var ppuAddressKnown = PpuHighAddress.TrySample(out var high);
        var ppuAddress = (ushort)(((high & 0x3F) << 8) | _ppuLowAddressLatch);
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

            if (ppuAddress < 0x2000)
            {
                if (PpuReadBar.SampledLevel == DigitalLevel.Low)
                {
                    PpuAddressData.Drive(_chr[ppuAddress & 0x1FFF]);
                    PpuReadCount++;
                }
                else if (_chrRam && PpuWriteBar.SampledLevel == DigitalLevel.Low && PpuAddressData.TrySample(out var data))
                {
                    _chr[ppuAddress & 0x1FFF] = (byte)data;
                    PpuWriteCount++;
                }
            }
        }
        else
        {
            CiramChipEnableBar.Drive(DigitalLevel.High);
            CiramA10.Drive(DigitalLevel.Unknown);
        }

        if (CpuAddress.TrySample(out var cpuAddress) && cpuAddress >= 0x8000 &&
            CpuReadWrite.SampledLevel == DigitalLevel.High && CpuM2.SampledLevel == DigitalLevel.High)
        {
            var index = _prg.Length == 16 * 1024
                ? (int)(cpuAddress & 0x3FFF)
                : (int)(cpuAddress & 0x7FFF);
            CpuData.Drive(_prg[index]);
            CpuReadCount++;
        }
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
