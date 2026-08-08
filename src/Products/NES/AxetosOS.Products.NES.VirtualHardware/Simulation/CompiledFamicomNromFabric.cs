namespace AxetosOS.Products.NES.VirtualHardware.Simulation;

/// <summary>
/// Chip-local view of the startup-compiled Famicom/NROM fabric.  The physical
/// board is still assembled and validated first, but after compilation the two
/// Ricoh cores exchange CPU/PPU bus state through this fixed circuit contract
/// rather than through runtime DigitalPin/DigitalNet/component dispatch.
/// </summary>
internal interface ICompiledFamicomNromFabric
{
    ulong MasterClockRisingEdges { get; }
    bool CpuIrqLow { get; }

    void BeginCpuRead(ushort address);
    bool CompleteCpuRead(ushort address, out byte value);
    void BeginCpuWrite(ushort address, byte value);

    byte ReadControllerSerial(int port);
    void WriteControllerLatch(byte value);

    byte ReadPpuVram(ushort address);
    void WritePpuVram(ushort address, byte value);
    void PresentPpuNmi(bool assertedLow);
}
