# AxetosOS Products / NES v2.48.4

## Generic live combinational bus-address fast path

Akumajou Densetsu improved materially in v2.48.3 but still measured only 58.63 FPS uncapped. The remaining dominant VRC6 PPU path is nametable/CIRAM traffic: unlike the now-static pattern-ROM route, CIRAM `/CE` and A10 legitimately depend on live VRC6 latch state and therefore must remain dynamic.

v2.48.4 keeps that physical state live while removing redundant topology recursion:

- Adds the product-agnostic `ICompiledBusAddressCombinationalComponent` facet. A package may expose an output that it can evaluate directly from the current compiled bus address/direction plus its own live state.
- Dynamic target bindings now precompile direct runtime pin resolvers when a physical net has one such package output driver. The generic fallback remains the full topology evaluator for nets that cannot be reduced safely.
- VRC6 exposes only its CIRAM `/CE` and CIRAM A10 outputs through this facet. Both still consult the current `$B003`/CHR-derived nametable state on every access.
- The HM6116 CIRAM remains a motherboard RAM chip and still owns its memory cells. The compiler merely avoids recursively rediscovering the same cartridge address-pin projection for every nametable byte.
- Existing VRC6 tests are strengthened to compare the new direct combinational facet against the ordinary package combinational outputs across CIRAM mapping modes.

No PPU/CPU clocks, memory accesses, mapper writes, CIRAM ownership, CHR-backed nametable behavior, IRQ clocks, audio clocks, package pins or diagnostic counters are skipped.

The validated incoming suite remains 656 tests; this environment does not run the .NET toolchain, so local Release validation is required.
