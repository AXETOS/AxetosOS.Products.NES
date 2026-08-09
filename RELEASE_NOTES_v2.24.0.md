# AxetosOS Products / NES v2.24.0 — hardware conformance sweep

v2.24.0 is a hardware-conformance release, not an Alien Syndrome-specific patch. The implementation remains free to use compact Boolean/state-machine models, but this sweep removes shortcuts that changed observable package/bus behavior.

## RP2A03 / RP2A07 CPU corrections

- Restores externally visible dummy reads for implied/accumulator instructions, zero-page/indexed addressing and branches.
- Restores stack-cycle cadence for PHP/PHA/PLA/PLP and the real bus ordering for JSR/RTS/RTI/BRK/interrupt entry.
- Polls interrupts at instruction-specific hardware boundaries instead of sampling only at the following opcode fetch.
- Adds internal/external CPU data-bus latches so open-bus reads retain the prior driven value; `$4015` remains internal-bus-only and controller reads preserve the implemented open-bus bits.
- Keeps NTSC and PAL CPU state machines structurally aligned while preserving their separate package implementations.

## RP2C02 / RP2C07 PPU corrections

- Replaces the old one-sprite-per-three-dots approximation with byte-phased primary/secondary OAM evaluation over dots 1-256.
- Models secondary-OAM clear, misaligned OAMADDR progression, eight-sprite fill and diagonal sprite-overflow walking.
- Restores all eight sprite fetch slots during dots 257-320, including the package-visible dummy nametable accesses.
- Forces OAMADDR/data-bus behavior during sprite fetch and fixes `$2004` attribute-bit readback.
- Applies greyscale masking to palette `$2007` reads while preserving stored palette data.

## APU corrections

- Triangle output now holds the last DAC code when its sequencer is gated instead of being forced to zero.
- Noise divider reload is represented in the half-rate timer domain without changing the register-visible period table.
- Separates the four-step frame IRQ status flag from the IRQ output so the two terminal status cycles can exist while IRQ inhibit suppresses the CPU interrupt.

## Motherboard / cartridge / mapper corrections

- Rebuilds the console LS139 decode as the real two-stage `M2 + A15` qualification: `/M07` enables the low-map decoder and `/ROMSEL` is generated as a physical cartridge signal.
- The replaceable cartridge CPU connector is now the real A0-A14 bus; A15 is no longer exposed to NROM/MMC1.
- NROM and MMC1 consume `/ROMSEL` rather than inferring cartridge ROM selection from an impossible A15 cartridge pin.
- MMC1 PRG-RAM selection is reconstructed from M2, inactive `/ROMSEL`, A14 and A13; the compiled path has the same qualification.
- Mapper serial-write suppression still observes every CPU write cycle, while bit-7 reset remains effective on consecutive writes.

## Chips audited without source changes

`SN74LS139A`, `SN74LS368A`, runtime `SN74LS373`, `HM6116` and the compact Ricoh mixer remain compact models because their current simplification does not require a gate/transistor expansion to preserve the behavior this product exposes.

## Explicit unresolved hardware gaps

This release does **not** claim transistor-perfect coverage of behavior for which the source does not yet contain a defensible model:

- `CIC3193/3195/3197` still use the pre-existing synthetic lock-side challenge model, while the replaceable cartridge has no matching key-CIC package. The CIC subsystem therefore remains incomplete and is not represented as hardware-conformant in v2.24.0.
- Eight silicon-unstable NMOS unofficial CPU opcodes remain intentionally unresolved rather than assigning one guessed die-dependent result.
- Revision/analog effects not represented by the digital package model remain outside this release.

## Validation

The assistant environment does not contain the .NET SDK, so local `dotnet test` remains the acceptance gate. The patch adds ten xUnit cases over the validated v2.23.0 suite, so the expected discovered total is **276** if compilation and discovery succeed.
