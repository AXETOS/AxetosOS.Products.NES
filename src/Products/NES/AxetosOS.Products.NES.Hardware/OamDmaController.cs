using AxetosOS.Products.NES.Abstractions;

namespace AxetosOS.Products.NES.Hardware;

public sealed class OamDmaController : INesHardwareModule, ICpuBusDevice
{
    private readonly CpuBus _bus;
    private readonly Rp2C02Ppu _ppu;
    private readonly Rp2A03Cpu _cpu;

    public OamDmaController(CpuBus bus, Rp2C02Ppu ppu, Rp2A03Cpu cpu)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _ppu = ppu ?? throw new ArgumentNullException(nameof(ppu));
        _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));
    }

    public string ModuleId => "nes.dma.oam";
    public byte LastPage { get; private set; }
    public ulong Transfers { get; private set; }

    public void PowerOn()
    {
        LastPage = 0;
        Transfers = 0;
    }

    public void Reset() { }

    public bool HandlesCpuAddress(ushort address) => address == 0x4014;

    public byte CpuRead(ushort address) => LastPage;

    public void CpuWrite(ushort address, byte value)
    {
        LastPage = value;
        var source = value << 8;
        for (var offset = 0; offset < 256; offset++)
        {
            _ppu.WriteOamDmaByte(_bus.Read((ushort)(source + offset)));
        }

        Transfers++;
        _cpu.RequestDmaStall(513 + (int)(_cpu.TotalCycles & 1));
    }
}
