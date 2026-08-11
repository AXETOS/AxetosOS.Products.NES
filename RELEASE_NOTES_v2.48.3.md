# AxetosOS Products / NES v2.48.3

## VRC6 compiled PPU boundary hot path

Akumajou Densetsu exposed a VRC6-specific compiled-bus performance regression. The VRC6 package previously exposed one dynamic PPU data target for both fixed pattern-table CHR reads and the optional `$B003` CHR-backed nametable mode. Because the target had a mutable chip-select gate, the generic compiler correctly treated every cartridge PPU read as dynamic, forcing ordinary pattern fetches through runtime target selection/address resolution.

v2.48.3 separates the physical package behavior into the two circuits the ASIC actually exposes:

- `$0000-$1FFF` CHR pattern reads are a fixed A13-low cartridge ROM path and now compile to a direct static bus route.
- CHR-backed nametable reads remain a separate dynamic A13-high cartridge driver controlled by `$B003`.
- The VRC6 package now exposes the immutable fact that CIRAM `/CE` is always inactive while PPU A13 is low. This lets the generic compiler prove CIRAM rejection for pattern fetches without learning VRC6/NES semantics or freezing mutable nametable state.

No PPU reads, package-pin behavior, CHR banking, CIRAM ownership, CHR-backed nametable functionality, IRQ clocks, VRC6 audio clocks, or diagnostics are skipped. The optimization only removes runtime re-evaluation of physical decode facts that are invariant for pattern-table addresses.

The existing VRC6 conformance suite is retained at 656 total tests; descriptor-oriented assertions now verify that the pattern path is static/direct while the CHR-nametable path remains state controlled.
