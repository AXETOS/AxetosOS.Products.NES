# AxetosOS Products / NES v2.37.0

## Mapper 71 / Camerica-Codemasters physical cartridge

v2.37.0 adds Mapper 71 as another replaceable cartridge circuit without adding mapper semantics to the motherboard or generic whole-circuit compiler.

The cartridge models the Camerica/Codemasters family used by boards such as BF9093 and BF9097:

- one switchable 16 KiB PRG-ROM window at CPU `$8000-$BFFF`;
- the final 16 KiB PRG-ROM bank fixed at CPU `$C000-$FFFF`;
- 128 KiB or 256 KiB standard PRG capacity;
- one 8 KiB CHR-RAM chip at PPU `$0000-$1FFF`;
- no PRG RAM, cartridge IRQ or expansion audio;
- explicit prevention of CPU/PRG-ROM bus conflicts;
- end-of-M2-qualified mapper latch timing through the same physical CPU connector path used by the earlier discrete cartridges;
- submapper 0 hardwired horizontal/vertical CIRAM routing;
- submapper 1 BF9097/Fire Hawk live one-screen nametable latch, with CPU data bit 4 driving CIRAM A10;
- BF9097 three-bit PRG bank wiring rather than silently exposing the four-bit BF9093 bank latch;
- the `$E000-$FFFF` CIC-stun decode retained as board-local latch state for diagnostics, while the current normalized cartridge connector still has no CIC-stun package pin.

The Fire Hawk CIRAM A10 output is intentionally exposed as mutable combinational state, not a static topology fact. This prevents the generic compiler from folding mapper-controlled nametable selection while still allowing hardwired Mapper-71 boards to expose their fixed H/V route as a static combinational fact.

## Validation coverage

The patch adds 18 Mapper-71 test cases covering PRG banking, fixed-last-bank decode, absence of bus conflicts, ignored standard-board `$8000-$BFFF` writes, CHR RAM, hardwired H/V mirroring, BF9097 live single-screen selection, three-bit Fire Hawk banking, CIC-stun address decode, physical falling-M2 latch timing, generic compiled write phase, raw-vs-generic-compiled parity, invalid hardware geometry, submapper rejection and factory composition.

The previous validated baseline is v2.36.0 at **372 / 372 tests**, with GxROM additionally real-game validated by Thunder & Lightning. v2.37.0 therefore expects **390 Release tests** pending local validation.

A deterministic `samples/axetos-camerica-bank-switch.nes` NES 2.0 Mapper-71 submapper-1 image is included. It writes `$10` to `$9000` to select Fire Hawk nametable page 1, writes `$01` to `$C000` to select PRG bank 1, then remains in the selected lower PRG bank for deterministic diagnostics.
