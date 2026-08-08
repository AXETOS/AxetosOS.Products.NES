using System.Runtime.CompilerServices;
using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;
using AxetosOS.Products.NES.VirtualHardware.Loading;

namespace AxetosOS.Products.NES.VirtualHardware.Simulation;

/// <summary>
/// Replaceable hardware boundary presented to a whole-board compiled circuit.
/// The motherboard compiler never inspects mapper rules or ROM contents. A
/// cartridge/mapper unit owns those rules and exposes only the electrical
/// consequences that reach the cartridge connector.
/// </summary>
internal interface ICompiledExternalCartridgeUnit
{
    VirtualHardwareComponent PhysicalComponent { get; }
    bool CpuIrqLow { get; }
    bool TryCpuRead(ushort address, out byte value);
    void CpuWrite(ushort address, byte value);
    CompiledExternalPpuState PpuRead(ushort address);
    CompiledExternalPpuState PpuWrite(ushort address, byte value);
}

internal readonly record struct CompiledExternalPpuState(
    bool DrivesData,
    byte Data,
    bool CiramEnabled,
    bool CiramA10);

/// <summary>
/// Mapper-0 implementation of the replaceable cartridge unit. All mapper and
/// ROM-specific shortcuts live here, outside the compiled motherboard.
/// </summary>
internal sealed class CompiledNromCartridgeUnit : ICompiledExternalCartridgeUnit
{
    private readonly NromCartridge _cartridge;
    private readonly bool _horizontalMirroring;

    public CompiledNromCartridgeUnit(NromCartridge cartridge)
    {
        _cartridge = cartridge ?? throw new ArgumentNullException(nameof(cartridge));
        if (!cartridge.IsInserted)
            throw new InvalidOperationException("The cartridge must contain an inserted image before compilation.");
        _horizontalMirroring = cartridge.CompiledMirroring == VirtualHardwareNesMirroring.Horizontal;
    }

    public VirtualHardwareComponent PhysicalComponent => _cartridge;
    public bool CpuIrqLow => false;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryCpuRead(ushort address, out byte value)
    {
        if ((address & 0x8000) == 0)
        {
            value = 0;
            return false;
        }

        value = _cartridge.ReadCpuCompiled(address);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CpuWrite(ushort address, byte value)
    {
        // Mapper 0 contains no CPU-write register. The connector transaction is
        // intentionally ignored by this replaceable hardware unit.
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public CompiledExternalPpuState PpuRead(ushort address)
    {
        address &= 0x3FFF;
        var ciramEnabled = (address & 0x2000) != 0;
        var ciramA10 = (address & (1 << (_horizontalMirroring ? 11 : 10))) != 0;
        if ((address & 0x2000) == 0)
        {
            return new CompiledExternalPpuState(
                true,
                _cartridge.ReadPpuCompiled(address),
                false,
                ciramA10);
        }

        return new CompiledExternalPpuState(false, 0, ciramEnabled, ciramA10);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public CompiledExternalPpuState PpuWrite(ushort address, byte value)
    {
        address &= 0x3FFF;
        var ciramEnabled = (address & 0x2000) != 0;
        var ciramA10 = (address & (1 << (_horizontalMirroring ? 11 : 10))) != 0;
        if ((address & 0x2000) == 0)
            _cartridge.WritePpuCompiled(address, value);
        return new CompiledExternalPpuState(false, 0, ciramEnabled, ciramA10);
    }
}

internal static class CompiledExternalCartridgeFactory
{
    public static ICompiledExternalCartridgeUnit Create(VirtualHardwareComponent cartridge) => cartridge switch
    {
        NromCartridge nrom => new CompiledNromCartridgeUnit(nrom),
        _ => throw new NotSupportedException(
            $"No compiled replaceable-device backend exists for cartridge package '{cartridge.GetType().Name}'.")
    };
}
