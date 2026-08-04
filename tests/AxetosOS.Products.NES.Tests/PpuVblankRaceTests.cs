using AxetosOS.Products.NES.Abstractions;
using AxetosOS.Products.NES.Cartridges;
using AxetosOS.Products.NES.Hardware;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class PpuVblankRaceTests
{
    [Fact]
    public void StatusReadOneDotBeforeVblankSuppressesFlagAndNmiForThatFrame()
    {
        var (ppu, nmi) = CreatePpu();
        ppu.CpuWrite(0x2000, 0x80);
        ClockTo(ppu, 241, 0);

        Assert.Equal(0, ppu.CpuRead(0x2002) & 0x80);
        ppu.Clock(); // dot 0 -> dot 1
        ppu.Clock(); // process dot 1 vblank boundary

        Assert.False(ppu.InVBlank);
        Assert.False(nmi.IsAsserted);
        Assert.Equal((ulong)1, ppu.VBlankStarts);
        Assert.Equal((ulong)0, ppu.NmiEdges);
    }

    [Fact]
    public void StatusReadAtVblankSetBoundarySuppressesFlagAndNmiForThatFrame()
    {
        var (ppu, nmi) = CreatePpu();
        ppu.CpuWrite(0x2000, 0x80);
        ClockTo(ppu, 241, 1);

        Assert.Equal(0, ppu.CpuRead(0x2002) & 0x80);
        ppu.Clock(); // process dot 1 vblank boundary

        Assert.False(ppu.InVBlank);
        Assert.False(nmi.IsAsserted);
        Assert.Equal((ulong)1, ppu.VBlankStarts);
        Assert.Equal((ulong)0, ppu.NmiEdges);
    }

    [Fact]
    public void StatusReadImmediatelyAfterVblankReturnsSetThenReleasesNmi()
    {
        var (ppu, nmi) = CreatePpu();
        ppu.CpuWrite(0x2000, 0x80);
        ClockTo(ppu, 241, 2);

        Assert.True(ppu.InVBlank);
        Assert.True(nmi.IsAsserted);
        Assert.NotEqual(0, ppu.CpuRead(0x2002) & 0x80);
        Assert.False(ppu.InVBlank);
        Assert.False(nmi.IsAsserted);
    }

    [Fact]
    public void EnablingNmiDuringUnclearedVblankCreatesImmediateEdgeButNotDuplicates()
    {
        var (ppu, nmi) = CreatePpu();
        var edges = 0;
        nmi.Asserted += () => edges++;
        ClockTo(ppu, 241, 2);

        ppu.CpuWrite(0x2000, 0x80);
        ppu.CpuWrite(0x2000, 0x80);

        Assert.Equal(1, edges);
        Assert.True(nmi.IsAsserted);
    }

    [Fact]
    public void DisablingThenReenablingNmiDuringVblankCreatesASecondHardwareEdge()
    {
        var (ppu, nmi) = CreatePpu();
        var edges = 0;
        nmi.Asserted += () => edges++;
        ppu.CpuWrite(0x2000, 0x80);
        ClockTo(ppu, 241, 2);

        ppu.CpuWrite(0x2000, 0x00);
        ppu.CpuWrite(0x2000, 0x80);

        Assert.Equal(2, edges);
        Assert.Equal((ulong)2, ppu.NmiEdges);
    }

    private static (Rp2C02Ppu Ppu, SignalLine Nmi) CreatePpu()
    {
        var bus = new PpuBus();
        bus.Attach(new TestPpuMemory());
        var nmi = new SignalLine();
        var ppu = new Rp2C02Ppu(bus, nmi, NesTimingProfile.For(NesTimingMode.Ntsc));
        ppu.PowerOn();
        return (ppu, nmi);
    }

    private static void ClockTo(Rp2C02Ppu ppu, int scanline, int dot)
    {
        while (ppu.Scanline != scanline || ppu.Dot != dot)
        {
            ppu.Clock();
        }
    }

    private sealed class TestPpuMemory : IPpuBusDevice
    {
        private readonly byte[] _memory = new byte[0x4000];
        public bool HandlesPpuAddress(ushort address) => true;
        public byte PpuRead(ushort address) => _memory[address & 0x3FFF];
        public void PpuWrite(ushort address, byte value) => _memory[address & 0x3FFF] = value;
    }
}
