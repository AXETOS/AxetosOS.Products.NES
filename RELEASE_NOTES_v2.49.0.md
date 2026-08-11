# AxetosOS Products / NES v2.49.0

## Namcot 163 / Mapper 19 physical cartridge hardware

This release adds Mapper 19 as a replaceable Namcot 163 cartridge package while preserving the motherboard/compiler architecture boundary.

Implemented circuitry includes:

- three switchable 8 KiB PRG windows plus the fixed final 8 KiB bank;
- twelve independently programmed 1 KiB PPU windows;
- per-window CHR-ROM versus CIRAM page 0/1 routing, including CIRAM in pattern-table space;
- E800 low/high CHR CIRAM-disable controls;
- optional 8 KiB cartridge RAM/NVRAM split into four independently protected 2 KiB blocks with the Namco global write-enable gate;
- readable/writable 15-bit CPU-cycle IRQ counter and open-drain IRQ output;
- 128 bytes of chip-local sound/wave RAM with F800 address/autoincrement and 4800 data ports;
- physical Namco 163 time-multiplexed wavetable generator: 1–8 voices, one channel update every 15 CPU cycles, 18-bit frequency, 24-bit phase, programmable 4–256-sample waveform length/address, 4-bit sample and volume multiplication, and a single retained serial DAC node;
- raw and startup-compiled execution paths using generic bus target and live bus-address combinational facets only;
- Mapper-19 shutdown diagnostics, board/catalog metadata, synthetic IRQ/audio smoke ROM and broad conformance coverage.

Expansion audio remains inside the cartridge package and is intentionally not mixed into host PCM until the virtual-hardware connector exposes a reusable analog package/net path.

The .NET suite must be validated by the user with `dotnet test -c Release`; no local pass is claimed by this patch.
