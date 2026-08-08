# AxetosOS Products / NES v1.5.2

RP2C02/RP2C07 hardwired rendering VRAM fetch circuit.

The v1.5.0 profile showed VRAM transaction handling as one of the two largest named internal PPU sections. v1.5.2 keeps every real external VRAM bus transition but separates the fixed rendering read circuit from the CPU `$2007` sequencer.

## Rendering fetch circuit

Background and sprite rendering reads now retain only a two-phase read state, physical VRAM address, and destination latch. Starting a fetch immediately presents A8-A13, AD0-AD7 and ALE. The next PPU dot releases AD and asserts `/RD`; the following dot samples AD and completes the selected internal latch. No generic read/write transaction kind or variable completion-phase policy is decoded for rendering fetches.

## CPU VRAM circuit

CPU `$2007` reads and writes remain a separate package-internal sequencer with the same three-phase behavior as v1.5.1. CPU and rendering sequencers share the same physical package bus and are mutually exclusive, preserving the existing physical arbitration behavior.

## Architecture

Unchanged. The motherboard still resolves and immediately delivers every physical package-pin transition. No VRAM bus pulse, address, ALE edge, `/RD`, `/WR`, or receiver delivery is skipped. This patch only makes the fixed-function PPU rendering circuit execute like fixed-function hardware instead of carrying CPU-oriented generic transaction bookkeeping.

Expected test count remains 221.
