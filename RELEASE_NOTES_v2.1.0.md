# AxetosOS Products / NES v2.1.0

## Fused compiled Famicom/NROM circuit

v2.1.0 replaces the slower v2.0 compiled-route experiment with a deeper startup-compiled runtime for the fixed Famicom + mapper-0 machine.

### Runtime change

The physical board/chip/pin/net model is still built first and remains the hardware definition and standalone conformance path. After NROM insertion, the compiled Famicom runtime executes the fixed inter-chip circuit without ordinary motherboard pin/net/component dispatch in the hot loop.

The compiled unit directly owns the fixed execution relationships for:

- RP2A03/RP2C02 master-clock divider schedule;
- CPU RAM mirrored address selection;
- NROM PRG selection;
- CPU-to-PPU register decode;
- PPU CHR reads/writes;
- CIRAM nametable mirroring;
- PPU NMI delivery;
- controller shift state until the desktop input adapter is added.

RP2A03 and RP2C02 retain their existing internal CPU/PPU/APU state machines, but compiled execution enters those cores directly and suppresses their package-bus pin work. The reference runtime remains available with `--reference-runtime`.

### Important benchmark goal

v2.0 proved that merely replacing DigitalNet topology interpretation was not enough and actually slowed both benchmark games. v2.1 is intentionally more drastic: decoder, latch, RAM-package, cartridge-package, pin and net dispatch are removed from normal Famicom/NROM execution.

Expected test count: **224 tests**.

The .NET SDK is unavailable in the packaging environment, so run `dotnet test` locally before benchmarking.
