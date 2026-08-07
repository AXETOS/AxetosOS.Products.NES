namespace AxetosOS.Products.NES.VirtualHardware.Components.Chips.Ricoh;

/// <summary>
/// Shared RP2A0x nonlinear DAC lookup. Pulse and triangle/noise/DMC transfer
/// functions are independent until their final analog sum, so keep one tiny
/// pulse table and one 32K TND table instead of constructing every million-plus
/// pulse/TND combination at startup.
/// </summary>
internal static class RicohApuMixer
{
    private const int NoiseCount = 16;
    private const int DmcCount = 128;
    private static readonly double[] PulseTable = BuildPulseTable();
    private static readonly double[] TndTable = BuildTndTable();

    public static byte Mix(byte pulse1, byte pulse2, byte triangle, byte noise, byte dmc)
    {
        var pulseOut = PulseTable[pulse1 + pulse2];
        var tndIndex = ((triangle * NoiseCount) + noise) * DmcCount + dmc;
        var mixed = (int)Math.Round((pulseOut + TndTable[tndIndex]) * 255.0);
        return (byte)Math.Clamp(mixed, 0, 255);
    }

    private static double[] BuildPulseTable()
    {
        var table = new double[31];
        for (var pulseSum = 1; pulseSum < table.Length; pulseSum++)
        {
            table[pulseSum] = 95.88 / ((8128.0 / pulseSum) + 100.0);
        }
        return table;
    }

    private static double[] BuildTndTable()
    {
        var table = new double[16 * NoiseCount * DmcCount];
        for (var triangle = 0; triangle < 16; triangle++)
        {
            for (var noise = 0; noise < NoiseCount; noise++)
            {
                for (var dmc = 0; dmc < DmcCount; dmc++)
                {
                    var tndInput = triangle / 8227.0 + noise / 12241.0 + dmc / 22638.0;
                    var index = ((triangle * NoiseCount) + noise) * DmcCount + dmc;
                    table[index] = tndInput == 0.0
                        ? 0.0
                        : 159.79 / ((1.0 / tndInput) + 100.0);
                }
            }
        }
        return table;
    }
}
