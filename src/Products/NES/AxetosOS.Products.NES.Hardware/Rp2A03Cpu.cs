using AxetosOS.Products.NES.Abstractions;

namespace AxetosOS.Products.NES.Hardware;

public sealed class Rp2A03Cpu : INesHardwareModule, IClockedHardwareModule
{
    private const byte CarryFlag = 1 << 0;
    private const byte ZeroFlag = 1 << 1;
    private const byte InterruptDisableFlag = 1 << 2;
    private const byte DecimalFlag = 1 << 3;
    private const byte BreakFlag = 1 << 4;
    private const byte UnusedFlag = 1 << 5;
    private const byte OverflowFlag = 1 << 6;
    private const byte NegativeFlag = 1 << 7;

    private readonly CpuBus _bus;
    private int _cyclesRemaining;

    public Rp2A03Cpu(CpuBus bus)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
    }

    public string ModuleId => "nes.chip.rp2a03.cpu";
    public byte Accumulator { get; private set; }
    public byte X { get; private set; }
    public byte Y { get; private set; }
    public byte StackPointer { get; private set; }
    public byte Status { get; private set; }
    public ushort ProgramCounter { get; private set; }
    public ulong TotalCycles { get; private set; }
    public byte LastOpcode { get; private set; }
    public bool IsInstructionBoundary => _cyclesRemaining == 0;

    public void PowerOn()
    {
        Accumulator = 0;
        X = 0;
        Y = 0;
        StackPointer = 0x00;
        Status = InterruptDisableFlag | UnusedFlag;
        ProgramCounter = 0;
        TotalCycles = 0;
        LastOpcode = 0;
        _cyclesRemaining = 0;
        Reset();
    }

    public void Reset()
    {
        StackPointer = unchecked((byte)(StackPointer - 3));
        Status = (byte)((Status | InterruptDisableFlag | UnusedFlag) & ~BreakFlag);
        ProgramCounter = ReadWord(0xFFFC);
        _cyclesRemaining = 7;
    }

    public void Clock()
    {
        TotalCycles++;

        if (_cyclesRemaining > 0)
        {
            _cyclesRemaining--;
            return;
        }

        LastOpcode = ReadByte(ProgramCounter++);
        _cyclesRemaining = Execute(LastOpcode) - 1;
    }

    private int Execute(byte opcode) => opcode switch
    {
        0xEA => 2, // NOP
        0xA9 => LoadAccumulatorImmediate(),
        0x8D => StoreAccumulatorAbsolute(),
        0x4C => JumpAbsolute(),
        _ => throw new UnsupportedCpuOpcodeException(opcode, unchecked((ushort)(ProgramCounter - 1)))
    };

    private int LoadAccumulatorImmediate()
    {
        Accumulator = ReadByte(ProgramCounter++);
        SetZeroAndNegativeFlags(Accumulator);
        return 2;
    }

    private int StoreAccumulatorAbsolute()
    {
        var address = ReadWord(ProgramCounter);
        ProgramCounter += 2;
        _bus.Write(address, Accumulator);
        return 4;
    }

    private int JumpAbsolute()
    {
        ProgramCounter = ReadWord(ProgramCounter);
        return 3;
    }

    private byte ReadByte(ushort address) => _bus.Read(address);

    private ushort ReadWord(ushort address)
    {
        var low = ReadByte(address);
        var high = ReadByte(unchecked((ushort)(address + 1)));
        return (ushort)(low | (high << 8));
    }

    private void SetZeroAndNegativeFlags(byte value)
    {
        Status = value == 0 ? (byte)(Status | ZeroFlag) : (byte)(Status & ~ZeroFlag);
        Status = (value & 0x80) != 0 ? (byte)(Status | NegativeFlag) : (byte)(Status & ~NegativeFlag);
        Status |= UnusedFlag;
        Status &= unchecked((byte)~DecimalFlag); // The RP2A03 has no decimal arithmetic mode.
    }

}

public sealed class UnsupportedCpuOpcodeException(byte opcode, ushort address)
    : NotSupportedException($"CPU opcode ${opcode:X2} at ${address:X4} is not implemented yet.")
{
    public byte Opcode { get; } = opcode;
    public ushort Address { get; } = address;
}
