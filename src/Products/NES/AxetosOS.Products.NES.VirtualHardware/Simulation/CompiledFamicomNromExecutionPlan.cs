using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Boards.Nes;
using AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;

namespace AxetosOS.Products.NES.VirtualHardware.Simulation;

/// <summary>
/// Startup-compiled execution unit for the fixed Famicom + NROM circuit.
///
/// The ordinary board/chip/pin/net graph is assembled and validated first. Once
/// compiled, normal master-cycle execution no longer interprets that graph. The
/// fixed clock divider schedule, CPU address decoding, NROM mapping, CIRAM
/// mirroring, CPU-to-PPU register path, PPU VRAM path and NMI connection execute
/// directly through this one fabric. The Ricoh package classes retain the CPU,
/// PPU and APU silicon state, but their hot compiled paths do not drive/sample
/// motherboard package pins.
/// </summary>
internal sealed class CompiledFamicomNromExecutionPlan : IDisposable
{
    private readonly FamicomMotherboard _board;
    private readonly Rp2A03 _cpu;
    private readonly Rp2C02 _ppu;
    private readonly NromCartridge _cartridge;
    private readonly bool _horizontalMirroring;

    private ulong _masterClockRisingEdges;
    private readonly CompiledSerialPeripheralDescriptor _controller1;
    private readonly CompiledSerialPeripheralDescriptor _controller2;
    private bool _cpuReadLatchValid;
    private ushort _cpuReadLatchAddress;
    private byte _cpuReadLatch;
    private bool _resetAsserted;
    private readonly ICompiledBusFabric _cpuFabric;
    private readonly ICompiledBusFabric _ppuFabric;

    public CompiledFamicomNromExecutionPlan(FamicomMotherboard board)
    {
        _board = board ?? throw new ArgumentNullException(nameof(board));
        _cpu = board.Cpu;
        _ppu = board.Ppu;
        _cartridge = board.Board.Components.OfType<NromCartridge>().SingleOrDefault()
            ?? throw new InvalidOperationException("A physical NROM cartridge must be attached before compiling the Famicom machine.");
        if (!_cartridge.IsInserted)
            throw new InvalidOperationException("The NROM cartridge must contain an inserted image before compilation.");

        _horizontalMirroring = _cartridge.CompiledMirroring == VirtualHardwareNesMirroring.Horizontal;
        _masterClockRisingEdges = (board.MasterClock.HalfCycleCount + (board.MasterClock.Output.DriveLevel == DigitalLevel.High ? 1UL : 0UL)) / 2;
        _resetAsserted = board.ResetSource.Output.DriveLevel != DigitalLevel.High;
        _controller1 = GetControllerDescriptor(board.Controller1);
        _controller2 = GetControllerDescriptor(board.Controller2);
        ValidateControllerBinding(_controller1, _cpu.ControllerData1, _cpu.ControllerRead1Bar, _cpu.ControllerOut0);
        ValidateControllerBinding(_controller2, _cpu.ControllerData2, _cpu.ControllerRead2Bar, _cpu.ControllerOut0);
        _cpuFabric = new CpuBusFabric(this);
        _ppuFabric = new PpuBusFabric(this);
        _cpu.AttachCompiledBusFabric(_cpuFabric);
        _ppu.AttachCompiledBusFabric(_ppuFabric);
        _cpu.SetCompiledResetAsserted(_resetAsserted);
        _ppu.SetCompiledResetAsserted(_resetAsserted);
    }

    public ulong MasterClockRisingEdges => _masterClockRisingEdges;
    public bool CpuIrqLow => false; // NROM has no IRQ source.
    public int RuntimeUnits => 1;
    public int FoldedPhysicalTraces => 47;

    public void SynchronizePowerOn()
    {
        _masterClockRisingEdges = 0;
        _cpuReadLatchValid = false;
        SetResetAsserted(true);
    }

    public void SetResetAsserted(bool asserted)
    {
        _resetAsserted = asserted;
        _cpu.SetCompiledResetAsserted(asserted);
        _ppu.SetCompiledResetAsserted(asserted);
    }

    public void AdvanceHalfCycle()
    {
        // Half-cycle stepping exists for conformance/debug tests. The desktop
        // hot path always advances complete master cycles in bulk.
        var rising = _board.MasterClock.AdvanceHalfCycleCompiledWithoutPropagation();
        if (!rising) return;
        AdvanceOneRisingEdge();
    }

    public void AdvanceCycles(int cycles)
    {
        if (cycles < 0) throw new ArgumentOutOfRangeException(nameof(cycles));
        if (cycles == 0) return;

        // Preserve oscillator diagnostics without executing two empty package
        // pin deliveries per master cycle. A complete cycle always contains one
        // physical rising and one falling level, so the compiled circuit can
        // account for both edges arithmetically while executing only divider
        // activations that can reach silicon state.
        _board.MasterClock.AdvanceFullCyclesCompiledWithoutPropagation(cycles);

        // Align to the repeating 12-master-cycle CPU/PPU divider schedule.
        while (cycles > 0 && (_masterClockRisingEdges % 12) != 0)
        {
            AdvanceOneRisingEdge();
            cycles--;
        }

        while (cycles >= 12)
        {
            AdvanceTwelveMasterCycles();
            cycles -= 12;
        }

        while (cycles-- > 0)
            AdvanceOneRisingEdge();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AdvanceTwelveMasterCycles()
    {
        // RP2C02: one dot per 4 master rising edges.
        _masterClockRisingEdges += 4;
        _ppu.ExecuteCompiledPpuDot();

        // RP2A03 CLK divider: one M2 half-cycle per 6 master rising edges.
        _masterClockRisingEdges += 2;
        _cpu.ExecuteCompiledM2HalfCycle();

        _masterClockRisingEdges += 2;
        _ppu.ExecuteCompiledPpuDot();

        // At edge 12 the CPU divider toggles first on the shared physical clock
        // trace, followed by the PPU divider activation, matching board pin order.
        _masterClockRisingEdges += 4;
        _cpu.ExecuteCompiledM2HalfCycle();
        _ppu.ExecuteCompiledPpuDot();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AdvanceOneRisingEdge()
    {
        var edge = ++_masterClockRisingEdges;
        if (edge % 6 == 0) _cpu.ExecuteCompiledM2HalfCycle();
        if (edge % 4 == 0) _ppu.ExecuteCompiledPpuDot();
    }

    public void BeginCpuRead(ushort address)
    {
        _cpuReadLatchValid = false;
        _cpuReadLatchAddress = address;

        if (address is >= 0x2000 and <= 0x3FFF)
        {
            _cpuReadLatch = _ppu.CompiledCpuReadRegister(address & 7);
            _cpuReadLatchValid = true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool CompleteCpuRead(ushort address, out byte value)
    {
        if (_cpuReadLatchValid && address == _cpuReadLatchAddress)
        {
            value = _cpuReadLatch;
            _cpuReadLatchValid = false;
            return true;
        }

        if (address < 0x2000)
        {
            value = _board.CpuRam.ReadCompiled(address);
            return true;
        }

        if (address >= 0x8000)
        {
            value = _cartridge.ReadCpuCompiled(address);
            return true;
        }

        value = 0;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginCpuWrite(ushort address, byte value)
    {
        _cpuReadLatchValid = false;

        if (address < 0x2000)
        {
            _board.CpuRam.WriteCompiled(address, value);
            return;
        }

        if (address is >= 0x2000 and <= 0x3FFF)
            _ppu.CompiledCpuWriteRegister(address & 7, value);
    }

    public byte ReadControllerSerial(int port) => port switch
    {
        0 => _controller1.ReadSerial(),
        1 => _controller2.ReadSerial(),
        _ => throw new ArgumentOutOfRangeException(nameof(port))
    };

    public void WriteControllerLatch(byte value)
    {
        // The optimized NROM fabric still talks to the real controller package
        // through the package's generic compiled serial facet. Host input reaches
        // that package only through its physical button pins; there is no second
        // software-only controller state inside this fused execution plan.
        var high = (value & 0x01) != 0;
        _controller1.WriteLatch(high);
        _controller2.WriteLatch(high);
    }

    private static CompiledSerialPeripheralDescriptor GetControllerDescriptor(
        ICompiledSerialPeripheralProvider provider) =>
        provider.GetCompiledSerialPeripherals().Single();

    private static void ValidateControllerBinding(
        CompiledSerialPeripheralDescriptor peripheral,
        DigitalPin cpuData,
        DigitalPin cpuClockBar,
        DigitalPin cpuLatch)
    {
        if (!ReferenceEquals(peripheral.DataPin.Net, cpuData.Net)
            || !ReferenceEquals(peripheral.ClockPin.Net, cpuClockBar.Net)
            || !ReferenceEquals(peripheral.LatchPin.Net, cpuLatch.Net))
        {
            throw new InvalidOperationException(
                "The specialized compiled fabric requires the controller serial facet to match the assembled physical traces.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadPpuVram(ushort address)
    {
        address &= 0x3FFF;
        if (address < 0x2000)
            return _cartridge.ReadPpuCompiled(address);

        if (address < 0x3F00)
            return _board.Ciram.ReadCompiled(MapCiramAddress(address));

        // Palette accesses are package-internal and normally never reach this
        // external fabric. Return zero rather than inventing an external device.
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WritePpuVram(ushort address, byte value)
    {
        address &= 0x3FFF;
        if (address < 0x2000)
        {
            _cartridge.WritePpuCompiled(address, value);
            return;
        }

        if (address < 0x3F00)
            _board.Ciram.WriteCompiled(MapCiramAddress(address), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int MapCiramAddress(ushort address)
    {
        // $3000-$3EFF mirrors $2000-$2EFF before the cartridge's CIRAM A10
        // wiring is applied.
        var nametableAddress = (ushort)((address - 0x2000) & 0x0FFF);
        if (_horizontalMirroring)
            return (nametableAddress & 0x03FF) | ((nametableAddress >> 1) & 0x0400);
        return nametableAddress & 0x07FF;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PresentPpuNmi(bool assertedLow) => _cpu.PresentCompiledNmi(assertedLow);

    public void Dispose()
    {
        _cpu.DetachCompiledBusFabric();
        _ppu.DetachCompiledBusFabric();
    }

    private sealed class CpuBusFabric : ICompiledBusFabric
    {
        private readonly CompiledFamicomNromExecutionPlan _owner;
        public CpuBusFabric(CompiledFamicomNromExecutionPlan owner) => _owner = owner;
        public ulong ClockRisingEdges => _owner.MasterClockRisingEdges;
        public bool InterruptRequestLow => _owner.CpuIrqLow;
        public void BeginRead(ushort address) => _owner.BeginCpuRead(address);
        public bool CompleteRead(ushort address, out byte value) => _owner.CompleteCpuRead(address, out value);
        public void Write(ushort address, byte value) => _owner.BeginCpuWrite(address, value);
        public void CompleteCycle() { }
        public byte ReadSerialInput(int channel) => _owner.ReadControllerSerial(channel);
        public void WriteParallelOutputs(byte value) => _owner.WriteControllerLatch(value);
        public void PresentOutputSignal(DigitalPin sourcePin, DigitalLevel level) { }
    }

    private sealed class PpuBusFabric : ICompiledBusFabric
    {
        private readonly CompiledFamicomNromExecutionPlan _owner;
        public PpuBusFabric(CompiledFamicomNromExecutionPlan owner) => _owner = owner;
        public ulong ClockRisingEdges => _owner.MasterClockRisingEdges;
        public bool InterruptRequestLow => false;
        public void BeginRead(ushort address) { }
        public bool CompleteRead(ushort address, out byte value)
        {
            value = _owner.ReadPpuVram(address);
            return true;
        }
        public void Write(ushort address, byte value) => _owner.WritePpuVram(address, value);
        public void CompleteCycle() { }
        public byte ReadSerialInput(int channel) => 0;
        public void WriteParallelOutputs(byte value) { }
        public void PresentOutputSignal(DigitalPin sourcePin, DigitalLevel level) =>
            _owner.PresentPpuNmi(level == DigitalLevel.Low);
    }

}
