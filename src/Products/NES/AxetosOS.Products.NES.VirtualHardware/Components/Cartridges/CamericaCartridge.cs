using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;

/// <summary>
/// Mapper-71/Camerica/Codemasters replaceable cartridge hardware. The cartridge
/// owns its 16 KiB PRG bank latch, fixed-last-bank decode, 8 KiB CHR RAM, and
/// either hardwired H/V mirroring or the BF9097/Fire Hawk live one-screen latch.
/// Camerica boards prevent CPU/ROM bus conflicts; the motherboard and generic
/// hardware compiler see only connector pins and generic hardware facets.
/// </summary>
public sealed class CamericaCartridge : VirtualHardwareComponent, IReplaceableCartridgeHardware,
    ICompiledBusTargetProvider, ICompiledCombinationalComponent, ICompiledStaticCombinationalComponent
{
    private const int PrgBankSize = 16 * 1024;
    private const int StandardChrRamSize = 8 * 1024;

    private byte[] _prg = [];
    private byte[] _chrRam = [];
    private VirtualHardwareNesMirroring _mirroring;
    private byte _bankRegister;
    private byte _bankSelectMask;
    private int _switchableBankBase;
    private int _fixedBankBase;
    private bool _mapperControlledSingleScreen;
    private byte _selectedNametablePage;
    private bool _cicStunLatch;

    private bool _cpuReadAddressSelected;
    private ushort _cpuSelectedAddress;
    private byte _cpuSelectedData;
    private bool _cpuWriteCycleSelected;
    private ushort _cpuWriteAddress;
    private bool _ppuReadActive;
    private bool _ppuWriteActive;

    private readonly ulong _powerInputMask;
    private readonly ulong _cpuAddressControlInputMask;
    private readonly ulong _cpuM2InputMask;
    private readonly ulong _cpuRomSelectInputMask;
    private readonly ulong _ppuAddressControlInputMask;
    private readonly ulong _ppuDataInputMask;

    public CamericaCartridge(string componentId) : base(componentId)
    {
        Vcc = AddPin("VCC", PinDirection.Input);
        Gnd = AddPin("GND", PinDirection.Input);
        CpuAddress = new DigitalBus($"{componentId}.CPU.A",
            Enumerable.Range(0, 15).Select(i => AddPin($"CPU.A{i}", PinDirection.Input)).ToArray());
        CpuData = new DigitalBus($"{componentId}.CPU.D",
            Enumerable.Range(0, 8).Select(i => AddPin($"CPU.D{i}", PinDirection.Bidirectional)).ToArray());
        CpuReadWrite = AddPin("CPU.RW", PinDirection.Input);
        CpuM2 = AddPin("CPU.M2", PinDirection.Input);
        CpuRomSelectBar = AddPin("CPU.ROMSEL_BAR", PinDirection.Input);
        PpuAddress = new DigitalBus($"{componentId}.PPU.A",
            Enumerable.Range(0, 14).Select(i => AddPin($"PPU.A{i}", PinDirection.Input)).ToArray());
        PpuData = new DigitalBus($"{componentId}.PPU.D",
            Enumerable.Range(0, 8).Select(i => AddPin($"PPU.D{i}", PinDirection.Bidirectional)).ToArray());
        PpuReadBar = AddPin("PPU.RD_BAR", PinDirection.Input);
        PpuWriteBar = AddPin("PPU.WR_BAR", PinDirection.Input);
        CiramChipEnableBar = AddPin("CIRAM.CE_BAR", PinDirection.Output);
        CiramA10 = AddPin("CIRAM.A10", PinDirection.Output);
        IrqBar = AddPin("IRQ_BAR", PinDirection.Output);

        _powerInputMask = Vcc.InputChangeMask | Gnd.InputChangeMask;
        _cpuAddressControlInputMask = CpuAddress.InputChangeMask | CpuReadWrite.InputChangeMask;
        _cpuM2InputMask = CpuM2.InputChangeMask;
        _cpuRomSelectInputMask = CpuRomSelectBar.InputChangeMask;
        _ppuAddressControlInputMask = PpuAddress.InputChangeMask |
            PpuReadBar.InputChangeMask | PpuWriteBar.InputChangeMask;
        _ppuDataInputMask = PpuData.InputChangeMask;

        // CPU data is sampled by the bank latch only at the end of an
        // M2-qualified write. PPU data is consumed only by active CHR-RAM writes.
        // The connector pins still retain every delivered level while owner wake
        // remains disabled for transitions that cannot clock internal hardware.
        CpuData.SetOwnerWakeEnabled(false);
        PpuData.SetOwnerWakeEnabled(false);
        ApplyResetState();
    }

    public int MapperNumber => 71;
    public DigitalPin Vcc { get; }
    public DigitalPin Gnd { get; }
    public DigitalBus CpuAddress { get; }
    public DigitalBus CpuData { get; }
    public DigitalPin CpuReadWrite { get; }
    public DigitalPin CpuM2 { get; }
    public DigitalPin CpuRomSelectBar { get; }
    public DigitalBus PpuAddress { get; }
    public DigitalBus PpuData { get; }
    public DigitalPin PpuReadBar { get; }
    public DigitalPin PpuWriteBar { get; }
    public DigitalPin CiramChipEnableBar { get; }
    public DigitalPin CiramA10 { get; }
    public DigitalPin IrqBar { get; }

    public bool IsInserted { get; private set; }
    public bool IsChrRam => true;
    public int ChrRamSizeBytes => _chrRam.Length;
    public byte BankRegister => _bankRegister;
    public int SelectedPrgBank => _prg.Length == 0 ? 0 : _switchableBankBase / PrgBankSize;
    public int FixedPrgBank => _prg.Length == 0 ? 0 : _fixedBankBase / PrgBankSize;
    public int PrgBankCount => _prg.Length / PrgBankSize;
    public bool MapperControlledSingleScreen => _mapperControlledSingleScreen;
    public int SelectedNametablePage => _selectedNametablePage;
    public bool CicStunLatch => _cicStunLatch;
    public ulong MapperWriteCount { get; private set; }
    public ulong PrgBankWriteCount { get; private set; }
    public ulong MirroringWriteCount { get; private set; }
    public ulong CicStunWriteCount { get; private set; }
    public ushort LastMapperWriteAddress { get; private set; }
    public byte LastMapperWriteData { get; private set; }

    public ulong CpuReadCount { get; private set; }
    public ushort LastCpuReadAddress { get; private set; }
    public byte LastCpuReadData { get; private set; }
    public ulong PpuReadCount { get; private set; }
    public ulong PpuWriteCount { get; private set; }

    public void LoadImage(VirtualHardwareNesRomImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.MapperNumber != 71)
            throw new NotSupportedException($"Mapper {image.MapperNumber} is not Camerica/Codemasters hardware.");
        if (image.PrgRom.Length != 128 * 1024 && image.PrgRom.Length != 256 * 1024)
            throw new NotSupportedException(
                $"Mapper 71 Camerica boards require 128 KiB or 256 KiB PRG ROM; the image contains {image.PrgRom.Length:N0} bytes.");
        if (image.ChrRom.Length != 0)
            throw new NotSupportedException("Mapper 71 Camerica boards use 8 KiB CHR RAM rather than CHR ROM.");
        if (image.Mirroring == VirtualHardwareNesMirroring.FourScreen)
            throw new NotSupportedException("Mapper 71 Camerica boards do not provide four-screen cartridge nametable RAM.");
        if (image.HasBatteryBackedMemory)
            throw new NotSupportedException("Mapper 71 Camerica hardware has no battery-backed cartridge memory.");
        if (image.HasExplicitRamSizes && image.TotalPrgRamSizeBytes != 0)
            throw new NotSupportedException("Mapper 71 Camerica hardware has no PRG RAM/NVRAM chip.");
        if (image.HasExplicitRamSizes && image.ChrNvRamSizeBytes > 0)
            throw new NotSupportedException("Mapper 71 Camerica boards use volatile CHR RAM rather than CHR NVRAM.");

        var chrRamSize = image.HasExplicitRamSizes ? image.TotalChrRamSizeBytes : StandardChrRamSize;
        if (chrRamSize != StandardChrRamSize)
            throw new NotSupportedException(
                $"Mapper 71 Camerica requires one {StandardChrRamSize:N0}-byte CHR RAM chip; the image declares {chrRamSize:N0} bytes.");

        _mapperControlledSingleScreen = image.SubmapperNumber switch
        {
            null => false,
            0 => false,
            1 => true,
            _ => throw new NotSupportedException(
                $"Mapper 71 submapper {image.SubmapperNumber} is not defined by the current Camerica cartridge hardware.")
        };

        var prgBankCount = image.PrgRom.Length / PrgBankSize;
        if (_mapperControlledSingleScreen && prgBankCount > 8)
            throw new NotSupportedException(
                "Mapper 71 submapper 1 models the BF9097/Fire Hawk board, whose PRG bank latch exposes only three bank bits (128 KiB PRG).");

        // BF9093 exposes four PRG bank bits; BF9097 exposes three. Masking is
        // therefore a physical consequence of the selected board and fitted ROM.
        _bankSelectMask = (byte)((_mapperControlledSingleScreen ? 0x07 : 0x0F) & (prgBankCount - 1));
        _prg = image.PrgRom.ToArray();
        _chrRam = new byte[StandardChrRamSize];
        _mirroring = image.Mirroring;
        IsInserted = true;
        ApplyResetState();
        RefreshPpuDataWakeState();
    }

    public void Eject()
    {
        IsInserted = false;
        _prg = [];
        _chrRam = [];
        _cpuReadAddressSelected = false;
        _cpuSelectedAddress = 0;
        _cpuSelectedData = 0;
        _cpuWriteCycleSelected = false;
        _cpuWriteAddress = 0;
        _ppuReadActive = false;
        _ppuWriteActive = false;
        RefreshPpuDataWakeState();
        ReleaseOutputs();
    }

    private bool IsPowered() =>
        IsInserted && Vcc.SampledLevel == DigitalLevel.High && Gnd.SampledLevel == DigitalLevel.Low;

    private void ApplyResetState()
    {
        _bankRegister = 0;
        RefreshDecodedBankBases();
        _cpuReadAddressSelected = false;
        _cpuSelectedAddress = 0;
        _cpuSelectedData = 0;
        _cpuWriteCycleSelected = false;
        _cpuWriteAddress = 0;
        _ppuReadActive = false;
        _ppuWriteActive = false;
        _selectedNametablePage = 0;
        _cicStunLatch = false;
        MapperWriteCount = 0;
        PrgBankWriteCount = 0;
        MirroringWriteCount = 0;
        CicStunWriteCount = 0;
        LastMapperWriteAddress = 0;
        LastMapperWriteData = 0;
        RefreshPpuDataWakeState();
        ReleaseOutputs();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RefreshDecodedBankBases()
    {
        if (_prg.Length == 0)
        {
            _switchableBankBase = 0;
            _fixedBankBase = 0;
            return;
        }

        var bankCount = _prg.Length / PrgBankSize;
        _switchableBankBase = (_bankRegister & _bankSelectMask) * PrgBankSize;
        _fixedBankBase = (bankCount - 1) * PrgBankSize;
    }

    private void RefreshPpuDataWakeState()
    {
        var enabled = IsPowered() && PpuWriteBar.SampledLevel == DigitalLevel.Low;
        PpuData.SetOwnerWakeEnabled(enabled);
    }

    protected override void OnInputChanges(ulong changedInputMask)
    {
        if (changedInputMask == 0) return;

        var powerChanged = (changedInputMask & _powerInputMask) != 0;
        var cpuAddressOrControlChanged = (changedInputMask & _cpuAddressControlInputMask) != 0;
        var cpuM2Changed = (changedInputMask & _cpuM2InputMask) != 0;
        var cpuRomSelectChanged = (changedInputMask & _cpuRomSelectInputMask) != 0;
        var ppuAddressOrControlChanged = (changedInputMask & _ppuAddressControlInputMask) != 0;
        var ppuDataChanged = (changedInputMask & _ppuDataInputMask) != 0;

        if (!powerChanged && !cpuAddressOrControlChanged && !cpuM2Changed &&
            !cpuRomSelectChanged && !ppuAddressOrControlChanged && !ppuDataChanged)
            return;

        if (!IsPowered())
        {
            if (!powerChanged && !IsInserted) return;
            _cpuReadAddressSelected = false;
            _cpuWriteCycleSelected = false;
            _ppuReadActive = false;
            _ppuWriteActive = false;
            ReleaseOutputs();
            RefreshPpuDataWakeState();
            return;
        }

        if (powerChanged || ppuAddressOrControlChanged) RefreshPpuDataWakeState();
        var ppuDataCanMatter = ppuDataChanged && PpuWriteBar.SampledLevel == DigitalLevel.Low;
        if (powerChanged || ppuAddressOrControlChanged || ppuDataCanMatter)
            ProcessPpuPort();

        // The falling M2 connector edge completes the preceding CPU bus window
        // while address, R/W and CPU data still represent that transaction.
        if (!powerChanged && cpuM2Changed && CpuM2.SampledLevel == DigitalLevel.Low)
            CompleteCpuTransaction();

        if (powerChanged || cpuAddressOrControlChanged || cpuRomSelectChanged || cpuM2Changed)
            UpdateCpuPort();
    }

    private void UpdateCpuPort()
    {
        if (!CpuAddress.TrySample(out var rawAddress) || CpuRomSelectBar.SampledLevel != DigitalLevel.Low)
        {
            _cpuReadAddressSelected = false;
            if (CpuM2.SampledLevel == DigitalLevel.High) _cpuWriteCycleSelected = false;
            CpuData.Release();
            return;
        }

        var logicalAddress = (ushort)(0x8000 | rawAddress);
        if (CpuReadWrite.SampledLevel == DigitalLevel.High)
        {
            _cpuWriteCycleSelected = false;
            _cpuReadAddressSelected = true;
            _cpuSelectedAddress = logicalAddress;
            _cpuSelectedData = ReadPrg(logicalAddress);
            CpuData.Drive(_cpuSelectedData);
            return;
        }

        _cpuReadAddressSelected = false;
        CpuData.Release();
        if (CpuM2.SampledLevel == DigitalLevel.High)
        {
            _cpuWriteCycleSelected = true;
            _cpuWriteAddress = logicalAddress;
        }
    }

    private void CompleteCpuTransaction()
    {
        if (_cpuReadAddressSelected)
        {
            CpuReadCount++;
            LastCpuReadAddress = _cpuSelectedAddress;
            LastCpuReadData = _cpuSelectedData;
        }

        if (_cpuWriteCycleSelected && CpuData.TrySample(out var rawData))
            WriteMapperRegister(_cpuWriteAddress, (byte)rawData);

        _cpuWriteCycleSelected = false;
    }

    private void ProcessPpuPort()
    {
        var addressKnown = PpuAddress.TrySample(out var rawAddress);
        var readSelected = false;
        var writeSelected = false;

        if (_mapperControlledSingleScreen) DriveCiramA10();
        if (!addressKnown)
        {
            CiramChipEnableBar.Drive(DigitalLevel.High);
            if (!_mapperControlledSingleScreen) CiramA10.Drive(DigitalLevel.Unknown);
            PpuData.Release();
            _ppuReadActive = false;
            _ppuWriteActive = false;
            return;
        }

        var address = (ushort)(rawAddress & 0x3FFF);
        DriveCiramOutputs(address);

        if (address < 0x2000)
        {
            readSelected = PpuReadBar.SampledLevel == DigitalLevel.Low;
            writeSelected = PpuWriteBar.SampledLevel == DigitalLevel.Low;
            if (readSelected)
            {
                PpuData.Drive(_chrRam[address & 0x1FFF]);
                if (!_ppuReadActive) PpuReadCount++;
            }
            else
            {
                PpuData.Release();
                if (writeSelected && PpuData.TrySample(out var data) && !_ppuWriteActive)
                {
                    _chrRam[address & 0x1FFF] = (byte)data;
                    PpuWriteCount++;
                }
            }
        }
        else
        {
            PpuData.Release();
        }

        _ppuReadActive = readSelected;
        _ppuWriteActive = writeSelected;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DriveCiramOutputs(ushort ppuAddress)
    {
        CiramChipEnableBar.Drive((ppuAddress & 0x2000) != 0 ? DigitalLevel.Low : DigitalLevel.High);
        if (_mapperControlledSingleScreen)
        {
            DriveCiramA10();
            return;
        }

        var sourceBit = _mirroring == VirtualHardwareNesMirroring.Horizontal ? 11 : 10;
        CiramA10.Drive((ppuAddress & (1 << sourceBit)) != 0 ? DigitalLevel.High : DigitalLevel.Low);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DriveCiramA10() =>
        CiramA10.Drive(_selectedNametablePage == 0 ? DigitalLevel.Low : DigitalLevel.High);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadPrg(ushort address)
    {
        var baseAddress = address < 0xC000 ? _switchableBankBase : _fixedBankBase;
        return _prg[baseAddress + (address & 0x3FFF)];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteMapperRegister(ushort address, byte cpuData)
    {
        var handled = false;

        // BF9097 (Fire Hawk) decodes $8000-$9FFF into a one-screen
        // nametable latch. Bit 4 is wired directly to CIRAM A10. Other
        // Camerica boards do not populate this register.
        if (_mapperControlledSingleScreen && address is >= 0x8000 and <= 0x9FFF)
        {
            _selectedNametablePage = (byte)((cpuData >> 4) & 0x01);
            MirroringWriteCount++;
            if (IsPowered()) DriveCiramA10();
            handled = true;
        }

        // All mapper-71 boards decode $C000-$FFFF into the PRG bank latch.
        // The fitted IC determines whether three or four low data bits are
        // physically connected to PRG ROM address lines. Camerica boards
        // explicitly avoid CPU/ROM bus conflicts, so CPU data is latched as-is.
        if (address >= 0xC000)
        {
            _bankRegister = (byte)(cpuData & _bankSelectMask);
            RefreshDecodedBankBases();
            PrgBankWriteCount++;
            handled = true;
        }

        // The $E000-$FFFF decode also clocks the Camerica CIC-stun latch from
        // CPU A0. The current normalized cartridge connector has no CIC-stun
        // package pin, but retaining the board-local latch state preserves the
        // decoded hardware behavior for diagnostics and a future CIC connector.
        if (address >= 0xE000)
        {
            _cicStunLatch = (address & 0x0001) != 0;
            CicStunWriteCount++;
            handled = true;
        }

        if (!handled) return;

        MapperWriteCount++;
        LastMapperWriteAddress = address;
        LastMapperWriteData = cpuData;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte ReadCpuCompiled(ushort address)
    {
        var value = ReadPrg(address);
        CpuReadCount++;
        LastCpuReadAddress = address;
        LastCpuReadData = value;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteCpuCompiled(ushort address, byte value) => WriteMapperRegister(address, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte ReadPpuCompiled(ushort address)
    {
        PpuReadCount++;
        return _chrRam[address & 0x1FFF];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WritePpuCompiled(ushort address, byte value)
    {
        _chrRam[address & 0x1FFF] = value;
        PpuWriteCount++;
    }

    private void ReleaseOutputs()
    {
        CpuData.Release();
        PpuData.Release();
        CiramChipEnableBar.Release();
        CiramA10.Release();
        IrqBar.Release();
    }

    bool ICompiledExternalDevice.ReadyForCompiledExecution => IsInserted;

    IEnumerable<CompiledBusTargetDescriptor> ICompiledBusTargetProvider.GetCompiledBusTargets()
    {
        if (!IsInserted) yield break;

        yield return new CompiledBusTargetDescriptor(
            this,
            CpuAddress.Pins,
            CpuData.Pins,
            new[]
            {
                new CompiledPinCondition(CpuReadWrite, DigitalLevel.High),
                new CompiledPinCondition(CpuRomSelectBar, DigitalLevel.Low)
            },
            new[]
            {
                new CompiledPinCondition(CpuReadWrite, DigitalLevel.Low),
                new CompiledPinCondition(CpuRomSelectBar, DigitalLevel.Low)
            },
            CompiledBusReadPhase.Complete,
            address => ReadCpuCompiled((ushort)(0x8000 | address)),
            (address, value) => WriteCpuCompiled((ushort)(0x8000 | address), value),
            writePhase: CompiledBusWritePhase.Complete);

        yield return new CompiledBusTargetDescriptor(
            this,
            PpuAddress.Pins,
            PpuData.Pins,
            new[]
            {
                new CompiledPinCondition(PpuAddress.Pins[13], DigitalLevel.Low),
                new CompiledPinCondition(PpuReadBar, DigitalLevel.Low)
            },
            new[]
            {
                new CompiledPinCondition(PpuAddress.Pins[13], DigitalLevel.Low),
                new CompiledPinCondition(PpuWriteBar, DigitalLevel.Low)
            },
            CompiledBusReadPhase.Complete,
            address => ReadPpuCompiled((ushort)address),
            (address, value) => WritePpuCompiled((ushort)address, value));
    }

    bool ICompiledStaticCombinationalComponent.TryEvaluateCompiledStaticOutput(
        DigitalPin output,
        Func<DigitalPin, DigitalLevel> sampleInput,
        out CompiledDriveState drive)
    {
        if (ReferenceEquals(output, CiramChipEnableBar))
        {
            drive = new CompiledDriveState(sampleInput(PpuAddress.Pins[13]) switch
            {
                DigitalLevel.Low => DigitalLevel.High,
                DigitalLevel.High => DigitalLevel.Low,
                _ => DigitalLevel.Unknown
            });
            return true;
        }

        // Hardwired Camerica boards expose a state-independent PPU-address
        // route. Fire Hawk's CIRAM A10 is mutable mapper state and must remain
        // live, so the generic compiler is deliberately not allowed to fold it.
        if (ReferenceEquals(output, CiramA10) && !_mapperControlledSingleScreen)
        {
            var sourceBit = _mirroring == VirtualHardwareNesMirroring.Horizontal ? 11 : 10;
            drive = new CompiledDriveState(sampleInput(PpuAddress.Pins[sourceBit]));
            return true;
        }

        if (ReferenceEquals(output, IrqBar))
        {
            drive = new CompiledDriveState(DigitalLevel.HighImpedance);
            return true;
        }

        drive = default;
        return false;
    }

    bool ICompiledCombinationalComponent.TryEvaluateCompiledOutput(
        DigitalPin output,
        Func<DigitalPin, DigitalLevel> sampleInput,
        out CompiledDriveState drive)
    {
        if (ReferenceEquals(output, CiramA10) && _mapperControlledSingleScreen)
        {
            drive = new CompiledDriveState(_selectedNametablePage == 0 ? DigitalLevel.Low : DigitalLevel.High);
            return true;
        }

        return ((ICompiledStaticCombinationalComponent)this).TryEvaluateCompiledStaticOutput(
            output, sampleInput, out drive);
    }
}
