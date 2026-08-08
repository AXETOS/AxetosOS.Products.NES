using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Boards;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Logic;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Memory;
using AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;
using AxetosOS.Products.NES.VirtualHardware.Components.Clock;
using AxetosOS.Products.NES.VirtualHardware.Components.Nes;
using AxetosOS.Products.NES.VirtualHardware.Components.Passives;
using AxetosOS.Products.NES.VirtualHardware.Components.Power;
using AxetosOS.Products.NES.VirtualHardware.Electrical;

namespace AxetosOS.Products.NES.VirtualHardware.Simulation;

/// <summary>
/// Whole-circuit laboratory compiler for one fixed motherboard assembly.
///
/// The compiler is intentionally ignorant of NES address maps and mapper rules.
/// It discovers RP2A03/RP2C02/RAM/controller instances by package type, traces
/// their actual package pins through the assembled netlist, evaluates supported
/// combinational packages, and precomputes the fastest equivalent dispatch and
/// address-projection tables it can prove for that exact physical circuit.
///
/// Replaceable cartridge/mapper hardware is a separate runtime unit connected at
/// the compilation boundary. Changing that device does not change motherboard
/// compiler rules.
/// </summary>
internal sealed class CompiledLabMotherboardExecutionPlan : ICompiledFamicomNromFabric, IDisposable
{
    private const ushort RouteNone = 0x0000;
    private const ushort RouteRam = 0x8000;
    private const ushort RoutePpu = 0x4000;
    private const ushort RouteKindMask = 0xC000;
    private const ushort RouteAddressMask = 0x3FFF;

    private readonly DigitalOscillator _masterClock;
    private readonly Rp2A03 _cpu;
    private readonly Rp2C02 _ppu;
    private readonly Hm6116 _cpuRam;
    private readonly Hm6116 _ciram;
    private readonly ICompiledExternalCartridgeUnit _external;
    private readonly ushort[] _cpuReadRoutes;
    private readonly ushort[] _cpuWriteRoutes;
    private readonly ushort[] _ciramAddressMap;
    private readonly CompiledControllerBinding[] _controllerBindings;
    private readonly int _cpuClockPeriod;
    private readonly int _ppuClockPeriod;
    private readonly bool _cpuBeforePpuAtSharedEdge;
    private readonly bool _hasFastTwelveEdgeKernel;

    private ulong _masterClockRisingEdges;
    private bool _cpuReadLatchValid;
    private ushort _cpuReadLatchAddress;
    private byte _cpuReadLatch;
    private bool _resetAsserted;

    public CompiledLabMotherboardExecutionPlan(
        VirtualHardwareBoard board,
        DigitalOscillator masterClock,
        ICompiledExternalCartridgeUnit external)
    {
        ArgumentNullException.ThrowIfNull(board);
        _masterClock = masterClock ?? throw new ArgumentNullException(nameof(masterClock));
        _external = external ?? throw new ArgumentNullException(nameof(external));

        _cpu = board.Components.OfType<Rp2A03>().Single();
        _ppu = board.Components.OfType<Rp2C02>().Single();
        (_cpuRam, _ciram) = DiscoverMemoryRoles(board, _cpu, _ppu);

        var compiler = new TopologyCompiler(board, _cpu, _ppu, external.PhysicalComponent);
        _cpuReadRoutes = compiler.CompileCpuRoutes(_cpuRam, _ppu, readCycle: true);
        _cpuWriteRoutes = compiler.CompileCpuRoutes(_cpuRam, _ppu, readCycle: false);
        _ciramAddressMap = compiler.CompileCiramAddressMap(_ciram);
        _controllerBindings = compiler.CompileControllerBindings();
        (_cpuClockPeriod, _ppuClockPeriod, _cpuBeforePpuAtSharedEdge) =
            compiler.CompileMasterClockSchedule(masterClock, _cpu, _ppu);
        _hasFastTwelveEdgeKernel = _cpuClockPeriod == 6 && _ppuClockPeriod == 4;

        if (!ReferenceEquals(_ppu.NmiBar.Net, _cpu.NmiBar.Net))
            throw new InvalidOperationException("The compiled circuit cannot fuse NMI because the two package pins are not on the same physical net.");

        _masterClockRisingEdges =
            (masterClock.HalfCycleCount + (masterClock.Output.DriveLevel == DigitalLevel.High ? 1UL : 0UL)) / 2;
        _resetAsserted = _cpu.ResetBar.SampledLevel != DigitalLevel.High;
        _cpu.AttachCompiledFabric(this);
        _ppu.AttachCompiledFabric(this);
        _cpu.SetCompiledResetAsserted(_resetAsserted);
        _ppu.SetCompiledResetAsserted(_resetAsserted);

        InternalComponentCount = board.Components.Count(component => !ReferenceEquals(component, external.PhysicalComponent));
        BoundaryTraceCount = board.Nets.Count(net => net.Pins.Any(pin => ReferenceEquals(pin.OwnerComponent, external.PhysicalComponent)));
        FoldedInternalTraceCount = board.Nets.Count - BoundaryTraceCount;
    }

    public ulong MasterClockRisingEdges => _masterClockRisingEdges;
    public bool CpuIrqLow => _external.CpuIrqLow;
    public int RuntimeUnits => 2; // compiled motherboard + replaceable cartridge/mapper unit
    public int InternalComponentCount { get; }
    public int BoundaryTraceCount { get; }
    public int FoldedInternalTraceCount { get; }

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
        var rising = _masterClock.AdvanceHalfCycleCompiledWithoutPropagation();
        if (!rising) return;
        AdvanceOneRisingEdge();
    }

    public void AdvanceCycles(int cycles)
    {
        if (cycles < 0) throw new ArgumentOutOfRangeException(nameof(cycles));
        if (cycles == 0) return;

        _masterClock.AdvanceFullCyclesCompiledWithoutPropagation(cycles);

        if (_hasFastTwelveEdgeKernel)
        {
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
        }

        while (cycles-- > 0)
            AdvanceOneRisingEdge();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AdvanceTwelveMasterCycles()
    {
        // This unrolled kernel is selected only after the compiler derives a
        // 6-edge and 4-edge activation schedule from the actual connected clock
        // pins. It is a circuit-pattern shortcut, not a console-specific rule.
        _masterClockRisingEdges += 4;
        _ppu.ExecuteCompiledPpuDot();

        _masterClockRisingEdges += 2;
        _cpu.ExecuteCompiledM2HalfCycle();

        _masterClockRisingEdges += 2;
        _ppu.ExecuteCompiledPpuDot();

        _masterClockRisingEdges += 4;
        if (_cpuBeforePpuAtSharedEdge)
        {
            _cpu.ExecuteCompiledM2HalfCycle();
            _ppu.ExecuteCompiledPpuDot();
        }
        else
        {
            _ppu.ExecuteCompiledPpuDot();
            _cpu.ExecuteCompiledM2HalfCycle();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AdvanceOneRisingEdge()
    {
        var edge = ++_masterClockRisingEdges;
        var cpuActive = edge % (ulong)_cpuClockPeriod == 0;
        var ppuActive = edge % (ulong)_ppuClockPeriod == 0;
        if (cpuActive && ppuActive)
        {
            if (_cpuBeforePpuAtSharedEdge)
            {
                _cpu.ExecuteCompiledM2HalfCycle();
                _ppu.ExecuteCompiledPpuDot();
            }
            else
            {
                _ppu.ExecuteCompiledPpuDot();
                _cpu.ExecuteCompiledM2HalfCycle();
            }
            return;
        }
        if (cpuActive) _cpu.ExecuteCompiledM2HalfCycle();
        if (ppuActive) _ppu.ExecuteCompiledPpuDot();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginCpuRead(ushort address)
    {
        _cpuReadLatchValid = false;
        _cpuReadLatchAddress = address;
        var route = _cpuReadRoutes[address];
        if ((route & RouteKindMask) == RoutePpu)
        {
            _cpuReadLatch = _ppu.CompiledCpuReadRegister(route & RouteAddressMask);
            _cpuReadLatchValid = true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool CompleteCpuRead(ushort address, out byte value)
    {
        var externalDrives = _external.TryCpuRead(address, out var externalValue);

        if (_cpuReadLatchValid && address == _cpuReadLatchAddress)
        {
            value = _cpuReadLatch;
            _cpuReadLatchValid = false;
            if (externalDrives && externalValue != value) return false;
            return true;
        }

        var route = _cpuReadRoutes[address];
        if ((route & RouteKindMask) == RouteRam)
        {
            value = _cpuRam.ReadCompiled(route & RouteAddressMask);
            if (externalDrives && externalValue != value) return false;
            return true;
        }

        if (externalDrives)
        {
            value = externalValue;
            return true;
        }

        value = 0;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginCpuWrite(ushort address, byte value)
    {
        _cpuReadLatchValid = false;
        var route = _cpuWriteRoutes[address];
        switch (route & RouteKindMask)
        {
            case RouteRam:
                _cpuRam.WriteCompiled(route & RouteAddressMask, value);
                break;
            case RoutePpu:
                _ppu.CompiledCpuWriteRegister(route & RouteAddressMask, value);
                break;
        }
        _external.CpuWrite(address, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadControllerSerial(int port)
    {
        if ((uint)port >= (uint)_controllerBindings.Length) return 0;
        return _controllerBindings[port].ReadSerial();
    }

    public void WriteControllerLatch(byte value)
    {
        for (var index = 0; index < _controllerBindings.Length; index++)
            _controllerBindings[index].WriteOutputs(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadPpuVram(ushort address)
    {
        address &= 0x3FFF;
        var external = _external.PpuRead(address);
        if (external.DrivesData) return external.Data;
        if (!external.CiramEnabled) return 0;
        var mapIndex = address | (external.CiramA10 ? 0x4000 : 0);
        return _ciram.ReadCompiled(_ciramAddressMap[mapIndex]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WritePpuVram(ushort address, byte value)
    {
        address &= 0x3FFF;
        var external = _external.PpuWrite(address, value);
        if (!external.CiramEnabled) return;
        var mapIndex = address | (external.CiramA10 ? 0x4000 : 0);
        _ciram.WriteCompiled(_ciramAddressMap[mapIndex], value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PresentPpuNmi(bool assertedLow) => _cpu.PresentCompiledNmi(assertedLow);

    public void Dispose()
    {
        _cpu.DetachCompiledFabric();
        _ppu.DetachCompiledFabric();
    }

    private static (Hm6116 CpuRam, Hm6116 Ciram) DiscoverMemoryRoles(
        VirtualHardwareBoard board,
        Rp2A03 cpu,
        Rp2C02 ppu)
    {
        Hm6116? cpuRam = null;
        Hm6116? ciram = null;
        foreach (var memory in board.Components.OfType<Hm6116>())
        {
            var cpuMatches = CountSharedNets(memory.Data.Pins, cpu.Data.Pins);
            var ppuMatches = CountSharedNets(memory.Data.Pins, ppu.MultiplexedAddressData.Pins);
            if (cpuMatches > ppuMatches && cpuMatches >= 4) cpuRam = memory;
            if (ppuMatches > cpuMatches && ppuMatches >= 4) ciram = memory;
        }

        return (
            cpuRam ?? throw new InvalidOperationException("No SRAM package connected to the RP2A03 data pins was found."),
            ciram ?? throw new InvalidOperationException("No SRAM package connected to the RP2C02 multiplexed pins was found."));
    }

    private static int CountSharedNets(IReadOnlyList<DigitalPin> left, IReadOnlyList<DigitalPin> right)
    {
        var count = 0;
        foreach (var a in left)
            foreach (var b in right)
                if (a.Net is not null && ReferenceEquals(a.Net, b.Net)) count++;
        return count;
    }

    private sealed class CompiledControllerBinding
    {
        private byte _shift;
        private bool _strobeHigh;
        private readonly NesStandardController? _controller;
        private readonly int _outputBit;

        public CompiledControllerBinding(NesStandardController? controller, int outputBit)
        {
            _controller = controller;
            _outputBit = outputBit;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte ReadSerial()
        {
            var value = (byte)(_shift & 1);
            if (!_strobeHigh) _shift = (byte)((_shift >> 1) | 0x80);
            return value;
        }

        public void WriteOutputs(byte value)
        {
            var high = (value & (1 << _outputBit)) != 0;
            if (high || _strobeHigh)
                _shift = ReadButtons(_controller);
            _strobeHigh = high;
        }

        private static byte ReadButtons(NesStandardController? controller)
        {
            if (controller is null) return 0;
            byte value = 0;
            for (var bit = 0; bit < controller.Buttons.Width; bit++)
            {
                if (controller.Buttons.Pins[bit].SampledLevel == DigitalLevel.High)
                    value |= (byte)(1 << bit);
            }
            return value;
        }
    }

    private sealed class TopologyCompiler
    {
        private readonly VirtualHardwareBoard _board;
        private readonly Rp2A03 _cpu;
        private readonly Rp2C02 _ppu;
        private readonly VirtualHardwareComponent _external;

        public TopologyCompiler(
            VirtualHardwareBoard board,
            Rp2A03 cpu,
            Rp2C02 ppu,
            VirtualHardwareComponent external)
        {
            _board = board;
            _cpu = cpu;
            _ppu = ppu;
            _external = external;
        }

        public ushort[] CompileCpuRoutes(Hm6116 ram, Rp2C02 ppu, bool readCycle)
        {
            var ramBits = CompileProjection(ram.Address.Pins, _cpu.Address.Pins, allowLatch: false);
            var ppuBits = CompileProjection(ppu.RegisterSelect.Pins, _cpu.Address.Pins, allowLatch: false);
            var routes = new ushort[ushort.MaxValue + 1];
            for (var raw = 0; raw <= ushort.MaxValue; raw++)
            {
                var address = (ushort)raw;
                var ramChipSelected = EvaluateInput(ram.ChipSelectBar, address, readCycle) == DigitalLevel.Low;
                var ramSelected = readCycle
                    ? ramChipSelected
                        && EvaluateInput(ram.WriteEnableBar, address, readCycle) == DigitalLevel.High
                        && EvaluateInput(ram.OutputEnableBar, address, readCycle) == DigitalLevel.Low
                    : ramChipSelected
                        && EvaluateInput(ram.WriteEnableBar, address, readCycle) == DigitalLevel.Low;
                var ppuSelected = EvaluateInput(ppu.ChipSelectBar, address, readCycle) == DigitalLevel.Low
                    && EvaluateInput(ppu.CpuReadWrite, address, readCycle)
                        == (readCycle ? DigitalLevel.High : DigitalLevel.Low);
                if (ramSelected && ppuSelected)
                    throw new InvalidOperationException($"Compiled circuit selects both internal SRAM and RP2C02 at source pattern 0x{address:X4}.");
                if (ramSelected)
                    routes[raw] = (ushort)(RouteRam | ProjectAddress(address, ramBits));
                else if (ppuSelected)
                    routes[raw] = (ushort)(RoutePpu | ProjectAddress(address, ppuBits));
                else
                    routes[raw] = RouteNone;
            }
            return routes;
        }

        public ushort[] CompileCiramAddressMap(Hm6116 ciram)
        {
            ValidateSameNet(ciram.OutputEnableBar, _ppu.VramReadBar, "PPU-side SRAM output-enable");
            ValidateSameNet(ciram.WriteEnableBar, _ppu.VramWriteBar, "PPU-side SRAM write-enable");
            ValidateExternalDriver(ciram.ChipSelectBar, "PPU-side SRAM chip-select");

            var sources = new int[ciram.Address.Width];
            for (var bit = 0; bit < sources.Length; bit++)
            {
                sources[bit] = FindRootBit(ciram.Address.Pins[bit], _ppu.MultiplexedAddressData.Pins, allowLatch: true);
                if (sources[bit] < 0)
                {
                    var highBit = FindRootBit(ciram.Address.Pins[bit], _ppu.HighAddress.Pins, allowLatch: true);
                    sources[bit] = highBit >= 0 ? highBit + 8 : -1;
                }
            }

            var externalBit = Array.FindIndex(sources, bit => bit < 0);
            if (externalBit < 0)
                throw new InvalidOperationException("No external address contribution reaches the PPU-side SRAM package.");
            for (var bit = 0; bit < sources.Length; bit++)
            {
                if (bit != externalBit && sources[bit] < 0)
                    throw new InvalidOperationException("The compiler could not derive every PPU-side SRAM address pin from the physical topology.");
            }
            ValidateExternalDriver(ciram.Address.Pins[externalBit], "PPU-side SRAM external address contribution");

            var map = new ushort[1 << 15];
            for (var key = 0; key < map.Length; key++)
            {
                var address = key & 0x3FFF;
                var externalHigh = (key & 0x4000) != 0;
                var local = 0;
                for (var bit = 0; bit < sources.Length; bit++)
                {
                    var high = bit == externalBit
                        ? externalHigh
                        : (address & (1 << sources[bit])) != 0;
                    if (high) local |= 1 << bit;
                }
                map[key] = (ushort)local;
            }
            return map;
        }

        public CompiledControllerBinding[] CompileControllerBindings()
        {
            var cpuInputs = new[] { _cpu.ControllerData1, _cpu.ControllerData2 };
            var result = new CompiledControllerBinding[cpuInputs.Length];
            for (var port = 0; port < cpuInputs.Length; port++)
            {
                NesStandardController? controller = null;
                var dataNet = cpuInputs[port].Net;
                if (dataNet is not null)
                {
                    controller = _board.Components.OfType<NesStandardController>()
                        .FirstOrDefault(candidate => ReferenceEquals(candidate.Data.Net, dataNet));
                }

                var outputBit = 0;
                if (controller?.Strobe.Net is not null)
                {
                    if (ReferenceEquals(controller.Strobe.Net, _cpu.ControllerOut1.Net)) outputBit = 1;
                    else if (ReferenceEquals(controller.Strobe.Net, _cpu.ControllerOut2.Net)) outputBit = 2;
                }
                result[port] = new CompiledControllerBinding(controller, outputBit);
            }
            return result;
        }

        public (int CpuPeriod, int PpuPeriod, bool CpuBeforePpu) CompileMasterClockSchedule(
            DigitalOscillator oscillator,
            Rp2A03 cpu,
            Rp2C02 ppu)
        {
            var net = oscillator.Output.Net
                ?? throw new InvalidOperationException("The compiled oscillator has no attached physical net.");
            if (!ReferenceEquals(cpu.MasterClock.Net, net) || !ReferenceEquals(ppu.Clock.Net, net))
                throw new InvalidOperationException("The expected clocked packages are not attached to the compiled oscillator trace.");

            var cpuIndex = IndexOfReference(net.Pins, cpu.MasterClock);
            var ppuIndex = IndexOfReference(net.Pins, ppu.Clock);
            if (cpuIndex < 0 || ppuIndex < 0)
                throw new InvalidOperationException("Clock receiver order could not be derived from the physical trace.");
            return (cpu.MasterClock.InputActivationPeriod, ppu.Clock.InputActivationPeriod, cpuIndex < ppuIndex);
        }

        private int[] CompileProjection(
            IReadOnlyList<DigitalPin> targets,
            IReadOnlyList<DigitalPin> roots,
            bool allowLatch)
        {
            var result = new int[targets.Count];
            for (var bit = 0; bit < targets.Count; bit++)
            {
                result[bit] = FindRootBit(targets[bit], roots, allowLatch);
                if (result[bit] < 0)
                    throw new InvalidOperationException($"Could not derive package address pin '{targets[bit].Name}' from the source bus topology.");
            }
            return result;
        }

        private int FindRootBit(
            DigitalPin target,
            IReadOnlyList<DigitalPin> roots,
            bool allowLatch)
        {
            var net = target.Net;
            if (net is null) return -1;
            foreach (var driver in net.Pins)
            {
                if (!driver.IsOutputCapable || ReferenceEquals(driver, target)) continue;
                for (var root = 0; root < roots.Count; root++)
                {
                    if (ReferenceEquals(driver, roots[root])) return root;
                }

                if (allowLatch && driver.OwnerComponent is Sn74Ls373 latch)
                {
                    var qIndex = IndexOfReference(latch.Q.Pins, driver);
                    if (qIndex >= 0)
                    {
                        if (EvaluateStaticInput(latch.OutputEnableBar) != DigitalLevel.Low) return -1;
                        return FindRootBit(latch.D.Pins[qIndex], roots, allowLatch: false);
                    }
                }
            }
            return -1;
        }

        private static ushort ProjectAddress(ushort address, int[] bits)
        {
            var local = 0;
            for (var bit = 0; bit < bits.Length; bit++)
                if ((address & (1 << bits[bit])) != 0) local |= 1 << bit;
            return (ushort)local;
        }

        private DigitalLevel EvaluateStaticInput(DigitalPin input) => EvaluateInput(input, 0, readCycle: true, depth: 0);

        private DigitalLevel EvaluateInput(DigitalPin input, ushort address, bool readCycle, int depth = 0)
        {
            if (depth > 16) throw new InvalidOperationException("Combinational topology recursion exceeded the compiler limit.");
            var net = input.Net;
            return net is null ? DigitalLevel.Unknown : EvaluateNet(net, address, readCycle, depth);
        }

        private DigitalLevel EvaluateNet(DigitalNet net, ushort address, bool readCycle, int depth)
        {
            if (depth > 16) throw new InvalidOperationException("Combinational topology recursion exceeded the compiler limit.");
            var haveStrong = false;
            var strongLow = false;
            var strongHigh = false;
            var strongUnknown = false;
            var haveWeak = false;
            var weakLow = false;
            var weakHigh = false;
            var weakUnknown = false;

            foreach (var pin in net.Pins)
            {
                if (!pin.IsOutputCapable) continue;
                var (level, strength) = EvaluateDriver(pin, address, readCycle, depth + 1);
                if (level == DigitalLevel.HighImpedance) continue;
                if (strength == DigitalDriveStrength.Strong)
                {
                    haveStrong = true;
                    strongLow |= level == DigitalLevel.Low;
                    strongHigh |= level == DigitalLevel.High;
                    strongUnknown |= level is not (DigitalLevel.Low or DigitalLevel.High);
                }
                else
                {
                    haveWeak = true;
                    weakLow |= level == DigitalLevel.Low;
                    weakHigh |= level == DigitalLevel.High;
                    weakUnknown |= level is not (DigitalLevel.Low or DigitalLevel.High);
                }
            }

            if (haveStrong) return FinishResolution(strongLow, strongHigh, strongUnknown);
            if (haveWeak) return FinishResolution(weakLow, weakHigh, weakUnknown);
            return DigitalLevel.Unknown;
        }

        private (DigitalLevel Level, DigitalDriveStrength Strength) EvaluateDriver(
            DigitalPin driver,
            ushort address,
            bool readCycle,
            int depth)
        {
            for (var bit = 0; bit < _cpu.Address.Width; bit++)
            {
                if (ReferenceEquals(driver, _cpu.Address.Pins[bit]))
                    return (((address & (1 << bit)) != 0 ? DigitalLevel.High : DigitalLevel.Low), DigitalDriveStrength.Strong);
            }
            if (ReferenceEquals(driver, _cpu.ReadWrite))
                return (readCycle ? DigitalLevel.High : DigitalLevel.Low, DigitalDriveStrength.Strong);

            switch (driver.OwnerComponent)
            {
                case DigitalPowerRail _:
                    return (driver.DriveLevel, driver.DriveStrength);
                case PullResistor pull when ReferenceEquals(driver, pull.Node):
                    return (EvaluateInput(pull.Rail, address, readCycle, depth + 1), DigitalDriveStrength.Weak);
                case Sn74Ls139A decoder:
                    return (EvaluateDecoderOutput(decoder, driver, address, readCycle, depth), DigitalDriveStrength.Strong);
                case Sn74Ls368A inverter:
                    return (EvaluateInverterOutput(inverter, driver, address, readCycle, depth), DigitalDriveStrength.Strong);
            }

            if (ReferenceEquals(driver.OwnerComponent, _external))
                return (DigitalLevel.HighImpedance, DigitalDriveStrength.Strong);

            throw new NotSupportedException(
                $"Whole-circuit compiler has no combinational backend for driver pin '{driver.Name}' ({driver.OwnerComponent?.GetType().Name ?? "unowned"}).");
        }

        private DigitalLevel EvaluateDecoderOutput(
            Sn74Ls139A decoder,
            DigitalPin output,
            ushort address,
            bool readCycle,
            int depth)
        {
            DigitalPin enable;
            DigitalPin a;
            DigitalPin b;
            int selectedOutput;
            if (ReferenceEquals(output, decoder.Y10Bar)) selectedOutput = 0;
            else if (ReferenceEquals(output, decoder.Y11Bar)) selectedOutput = 1;
            else if (ReferenceEquals(output, decoder.Y12Bar)) selectedOutput = 2;
            else if (ReferenceEquals(output, decoder.Y13Bar)) selectedOutput = 3;
            else selectedOutput = -1;

            if (selectedOutput >= 0)
            {
                enable = decoder.Enable1Bar;
                a = decoder.A1;
                b = decoder.B1;
            }
            else
            {
                if (ReferenceEquals(output, decoder.Y20Bar)) selectedOutput = 0;
                else if (ReferenceEquals(output, decoder.Y21Bar)) selectedOutput = 1;
                else if (ReferenceEquals(output, decoder.Y22Bar)) selectedOutput = 2;
                else if (ReferenceEquals(output, decoder.Y23Bar)) selectedOutput = 3;
                else return DigitalLevel.Unknown;
                enable = decoder.Enable2Bar;
                a = decoder.A2;
                b = decoder.B2;
            }

            var enableLevel = EvaluateInput(enable, address, readCycle, depth + 1);
            if (enableLevel == DigitalLevel.High) return DigitalLevel.High;
            if (enableLevel != DigitalLevel.Low) return DigitalLevel.Unknown;
            var aLevel = EvaluateInput(a, address, readCycle, depth + 1);
            var bLevel = EvaluateInput(b, address, readCycle, depth + 1);
            if (aLevel is not (DigitalLevel.Low or DigitalLevel.High) || bLevel is not (DigitalLevel.Low or DigitalLevel.High))
                return DigitalLevel.Unknown;
            var selected = (aLevel == DigitalLevel.High ? 1 : 0) | (bLevel == DigitalLevel.High ? 2 : 0);
            return selected == selectedOutput ? DigitalLevel.Low : DigitalLevel.High;
        }

        private DigitalLevel EvaluateInverterOutput(
            Sn74Ls368A inverter,
            DigitalPin output,
            ushort address,
            bool readCycle,
            int depth)
        {
            var channel = IndexOfReference(inverter.YBar, output);
            if (channel < 0) return DigitalLevel.Unknown;
            var enable = channel < 4 ? inverter.Enable1Bar : inverter.Enable2Bar;
            var enabled = EvaluateInput(enable, address, readCycle, depth + 1);
            if (enabled == DigitalLevel.High) return DigitalLevel.HighImpedance;
            if (enabled != DigitalLevel.Low) return DigitalLevel.Unknown;
            return EvaluateInput(inverter.A[channel], address, readCycle, depth + 1) switch
            {
                DigitalLevel.Low => DigitalLevel.High,
                DigitalLevel.High => DigitalLevel.Low,
                _ => DigitalLevel.Unknown
            };
        }

        private void ValidateExternalDriver(DigitalPin target, string purpose)
        {
            var net = target.Net
                ?? throw new InvalidOperationException($"{purpose} is not attached to a physical trace.");
            if (!net.Pins.Any(pin => ReferenceEquals(pin.OwnerComponent, _external) && pin.IsOutputCapable))
                throw new InvalidOperationException($"{purpose} is not driven from the replaceable external-device boundary.");
        }

        private static void ValidateSameNet(DigitalPin left, DigitalPin right, string purpose)
        {
            if (left.Net is null || !ReferenceEquals(left.Net, right.Net))
                throw new InvalidOperationException($"{purpose} cannot be compiled because the required package pins do not share one physical trace.");
        }

        private static DigitalLevel FinishResolution(bool low, bool high, bool unknown)
        {
            if (low && high) return DigitalLevel.Contention;
            if (unknown) return DigitalLevel.Unknown;
            if (low) return DigitalLevel.Low;
            if (high) return DigitalLevel.High;
            return DigitalLevel.Unknown;
        }

        private static int IndexOfReference(IReadOnlyList<DigitalPin> pins, DigitalPin target)
        {
            for (var index = 0; index < pins.Count; index++)
                if (ReferenceEquals(pins[index], target)) return index;
            return -1;
        }
    }
}
