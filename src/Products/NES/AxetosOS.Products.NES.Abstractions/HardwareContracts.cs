namespace AxetosOS.Products.NES.Abstractions;

public interface INesHardwareModule
{
    string ModuleId { get; }
    void PowerOn();
    void Reset();
}

public interface IClockedHardwareModule
{
    void Clock();
}

public interface ICpuBusDevice
{
    bool HandlesCpuAddress(ushort address);
    byte CpuRead(ushort address);
    void CpuWrite(ushort address, byte value);
}

public interface IPpuBusDevice
{
    bool HandlesPpuAddress(ushort address);
    byte PpuRead(ushort address);
    void PpuWrite(ushort address, byte value);
}

public interface ISignalLine
{
    bool IsAsserted { get; }
    void Assert();
    void Release();
}
