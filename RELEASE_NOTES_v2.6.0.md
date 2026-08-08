# AxetosOS Products / NES v2.6.0

## RP2C0x packed internal execution sweep

This release is an aggressive performance experiment built on the validated v2.5.0 true-hardware baseline. It changes only the internal host representation/execution of the RP2C02 and RP2C07.

### Changes

- Predecodes all 341 horizontal PPU dots into compact internal execution words.
- Replaces three arrays of sprite structs with eight packed 64-bit retained sprite-state words for secondary, next and active sprite circuits.
- Preserves per-sprite tile, attributes, X counter, row, pattern-low, pattern-high and sprite-zero state.
- Replaces per-fetch sprite bit reversal with a precomputed 256-byte internal lookup table.
- Moves vblank/pre-render raster event decoding onto dot-1 only and keeps the NTSC odd-frame skipped-dot behavior unchanged.
- External AD0-AD7, A8-A13, ALE, /RD, /WR, /NMI, CPU register pins and clock behavior remain physical package-pin operations.
- No motherboard or peer-chip knowledge is introduced into either PPU.

### Validation

The predecoded 341-dot plan was mechanically compared against the v2.5.0 decoder for every dot with no mismatches. Packed sprite shift/counter/output behavior was mechanically compared against the old struct representation across randomized states.

The .NET toolchain is not available in the patch-generation environment. Expected local suite: 228 tests. Run `dotnet test` before benchmarking or committing.
