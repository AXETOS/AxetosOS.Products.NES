using AxetosOS.Products.NES.Abstractions;
using AxetosOS.Products.NES.Hardware;
using Xunit;

namespace AxetosOS.Products.NES.Tests;

public sealed class CpuInterruptMicrosequenceTests
{
    [Fact]
    public void NmiUsesSeparatedDummyReadStackAndVectorBusCycles()
    {
        var memory = CreateMemory(reset: 0x8000, nmi: 0x9000, irq: 0xA000);
        memory.Bytes[0x8000] = 0xEA;
        var cpu = CreateCpu(memory);
        FinishReset(cpu);

        cpu.RequestNmi();
        cpu.Clock(); // cycle 1: discarded opcode read
        cpu.Clock(); // cycle 2: second dummy read

        Assert.Empty(memory.WritesTo(0x01FD));

        cpu.Clock(); // cycle 3: PCH
        Assert.Equal(new byte[] { 0x80 }, memory.WritesTo(0x01FD));
        cpu.Clock(); // cycle 4: PCL
        Assert.Equal(new byte[] { 0x00 }, memory.WritesTo(0x01FC));
        cpu.Clock(); // cycle 5: P
        Assert.Single(memory.WritesTo(0x01FB));
        Assert.Equal(0, memory.WritesTo(0x01FB)[0] & Rp2A03Cpu.BreakFlag);

        cpu.Clock(); // cycle 6: vector low
        Assert.Equal(0x8000, cpu.ProgramCounter);
        cpu.Clock(); // cycle 7: vector high and commit

        Assert.Equal(0x9000, cpu.ProgramCounter);
        Assert.Equal(2, memory.ReadsFrom(0x8000).Length);
        Assert.Single(memory.ReadsFrom(0xFFFA));
        Assert.Single(memory.ReadsFrom(0xFFFB));
    }

    [Fact]
    public void BrkPushesIncrementedReturnAddressAndBreakStatusOnItsOwnCycles()
    {
        var memory = CreateMemory(reset: 0x8000, nmi: 0x9000, irq: 0xA000);
        memory.Bytes[0x8000] = 0x00;
        memory.Bytes[0x8001] = 0xEA;
        var cpu = CreateCpu(memory);
        FinishReset(cpu);

        cpu.Clock(); // BRK opcode
        cpu.Clock(); // padding byte
        cpu.Clock(); // PCH
        cpu.Clock(); // PCL
        cpu.Clock(); // status
        cpu.Clock(); // vector low
        cpu.Clock(); // vector high

        Assert.Equal(new byte[] { 0x80 }, memory.WritesTo(0x01FD));
        Assert.Equal(new byte[] { 0x02 }, memory.WritesTo(0x01FC));
        Assert.NotEqual(0, memory.WritesTo(0x01FB)[0] & Rp2A03Cpu.BreakFlag);
        Assert.Equal(0xA000, cpu.ProgramCounter);
    }

    [Fact]
    public void NmiCanHijackBrkVectorAfterStackImageHasBeenWritten()
    {
        var memory = CreateMemory(reset: 0x8000, nmi: 0x9000, irq: 0xA000);
        memory.Bytes[0x8000] = 0x00;
        var signals = new Rp2A03SignalLines();
        var cpu = CreateCpu(memory, signals);
        FinishReset(cpu);

        cpu.Clock(); // BRK opcode
        cpu.Clock(); // padding
        cpu.Clock(); // PCH
        cpu.Clock(); // PCL
        cpu.Clock(); // status
        signals.Nmi.Assert();
        cpu.Clock(); // NMI sampled before vector-low fetch
        cpu.Clock(); // vector high

        Assert.Equal(0x9000, cpu.ProgramCounter);
        Assert.NotEqual(0, memory.WritesTo(0x01FB)[0] & Rp2A03Cpu.BreakFlag);
        Assert.Equal((ulong)1, cpu.NmiServiced);
    }

    private static RecordingMemory CreateMemory(ushort reset, ushort nmi, ushort irq)
    {
        var memory = new RecordingMemory();
        memory.Bytes[0xFFFA] = (byte)nmi;
        memory.Bytes[0xFFFB] = (byte)(nmi >> 8);
        memory.Bytes[0xFFFC] = (byte)reset;
        memory.Bytes[0xFFFD] = (byte)(reset >> 8);
        memory.Bytes[0xFFFE] = (byte)irq;
        memory.Bytes[0xFFFF] = (byte)(irq >> 8);
        return memory;
    }

    private static Rp2A03Cpu CreateCpu(RecordingMemory memory, Rp2A03SignalLines? signals = null)
    {
        var bus = new CpuBus();
        bus.Attach(memory);
        var cpu = new Rp2A03Cpu(bus, signals);
        cpu.PowerOn();
        return cpu;
    }

    private static void FinishReset(Rp2A03Cpu cpu)
    {
        for (var cycle = 0; cycle < 7; cycle++) cpu.Clock();
    }

    private sealed class RecordingMemory : ICpuBusDevice
    {
        private readonly List<(ushort Address, byte Value)> _writes = [];
        private readonly List<ushort> _reads = [];
        public byte[] Bytes { get; } = new byte[ushort.MaxValue + 1];
        public bool HandlesCpuAddress(ushort address) => true;
        public byte CpuRead(ushort address)
        {
            _reads.Add(address);
            return Bytes[address];
        }
        public void CpuWrite(ushort address, byte value)
        {
            _writes.Add((address, value));
            Bytes[address] = value;
        }
        public byte[] WritesTo(ushort address) => _writes.Where(x => x.Address == address).Select(x => x.Value).ToArray();
        public ushort[] ReadsFrom(ushort address) => _reads.Where(x => x == address).ToArray();
    }
}
