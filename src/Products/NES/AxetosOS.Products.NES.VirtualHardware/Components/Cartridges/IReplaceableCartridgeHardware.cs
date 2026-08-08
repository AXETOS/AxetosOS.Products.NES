using AxetosOS.Products.NES.VirtualHardware.Components;
using AxetosOS.Products.NES.VirtualHardware.Electrical;
using AxetosOS.Products.NES.VirtualHardware.Loading;
using AxetosOS.Products.NES.VirtualHardware.Simulation;

namespace AxetosOS.Products.NES.VirtualHardware.Components.Cartridges;

/// <summary>
/// Physical replaceable cartridge hardware presented to a regional motherboard
/// connector. The motherboard sees only these package pins. ROM metadata may
/// select which concrete cartridge circuit is constructed, but no motherboard
/// or hardware-compiler code is allowed to interpret mapper numbers.
/// </summary>
public interface IReplaceableCartridgeHardware : IVirtualHardwareComponent, ICompiledExternalDevice
{
    int MapperNumber { get; }
    bool IsInserted { get; }

    DigitalPin Vcc { get; }
    DigitalPin Gnd { get; }
    DigitalBus CpuAddress { get; }
    DigitalBus CpuData { get; }
    DigitalPin CpuReadWrite { get; }
    DigitalPin CpuM2 { get; }
    DigitalBus PpuAddressData { get; }
    DigitalBus PpuHighAddress { get; }
    DigitalPin PpuAle { get; }
    DigitalPin PpuReadBar { get; }
    DigitalPin PpuWriteBar { get; }
    DigitalPin CiramChipEnableBar { get; }
    DigitalPin CiramA10 { get; }
    DigitalPin IrqBar { get; }

    ulong CpuReadCount { get; }
    ushort LastCpuReadAddress { get; }
    byte LastCpuReadData { get; }
    ulong PpuReadCount { get; }
    ulong PpuWriteCount { get; }

    void LoadImage(VirtualHardwareNesRomImage image);
    void Eject();
}
