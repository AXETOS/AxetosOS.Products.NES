# AxetosOS Products / NES v1.4.0

Profiler-guided chip-owned package activation sweep.

- Motherboard still delivers every resolved physical level and contains no chip activation semantics.
- Physical package pins always retain their electrical level before deciding whether the owner needs to wake.
- Adds a chip-owned wake-enable latch for ordinary package inputs.
- RP2C02/RP2C07 external VRAM data changes are retained at the pins and sampled on the chip's own PPU clock transaction phase instead of recursively waking the PPU.
- RP2C02/RP2C07 CPU RS/RW/D inputs wake only while the chip's own selected transaction stage can consume them; `/CS` itself always remains active.
- RP2A03/RP2A07 D, IRQ and RESET levels remain current and are sampled at their CPU clock boundary; /NMI remains edge-active.
- HM6116, SN74LS373, SN74LS139A and SN74LS368A gate ordinary address/data inputs according to their own package power/select/enable state.
- NROM ignores CPU D-bus wakeups and only wakes from PPU AD data while its own latch/write circuitry can consume those levels.
- Adds a topology-only normal-run single-driver trace resolver fast path.
- Adds regression coverage proving a disabled chip input stage still receives/retains the electrical level without executing package logic.

Run `dotnet test`, then compare normal Release Mario and Donkey Kong FPS with v1.3.9.
