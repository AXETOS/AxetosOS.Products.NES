# AxetosOS Products / NES v2.13.2

## MMC1 PPU bus ownership + consecutive-write conformance

v2.13.2 is a correctness release built from the locally validated v2.13.1 checkpoint (247/247 tests). It keeps the fixed motherboard / replaceable cartridge architecture unchanged and corrects two MMC1 package behaviors exposed by a real Alien Syndrome (Japan) run.

### Physical PPU multiplexed-bus ownership

- MMC1 now releases cartridge AD0-AD7 drivers immediately when PPU ALE is high.
- The RP2C0x drives the low PPU address byte onto the same multiplexed AD pins before raising ALE. A cartridge can therefore momentarily contend with that new address if it is still driving the previous CHR read byte.
- NROM already released those pins during ALE; MMC1 now obeys the same physical connector ownership rule.
- The regression test deliberately creates the real transition: previous CHR data remains driven, the PPU-side source begins driving a new low address and creates contention, then ALE must make the MMC1 release the bus and latch the new address cleanly.

### MMC1 consecutive CPU write cycles

- MMC1 now remembers whether the immediately preceding CPU bus cycle was a write.
- A D0 serial-port write on a consecutive write cycle is ignored, matching MMC1 hardware and preventing a 6502 read-modify-write instruction from shifting the serial register twice.
- Bit 7 reset is never suppressed, including on a consecutive write cycle.
- Physical execution derives this state from the cartridge's M2/RW connector pins.
- Compiled execution uses a new generic target-provided bus-cycle observer. The hardware compiler attaches that observer only after physical data-bus topology proves that the target belongs to a particular bus master. It has no MMC1, mapper, NES or address-space semantics.
- Bus-cycle observers are flattened when external units are bound, so targets that do not use the facility retain only an empty-array hot-path check.

### Diagnostics

The desktop host prints final MMC1 control/CHR/PRG registers, total mapper writes, ignored consecutive serial writes and PPU read count. This is intended to make real-ROM mapper diagnosis reproducible without adding semantic behavior to the simulator.

### Validation

Three tests are added over the v2.13.1 total:

1. physical MMC1 AD-bus release/latch behavior across ALE;
2. direct consecutive-write suppression with bit-7 reset exception;
3. compiled-vs-physical RMW integration using an actual 6502 `INC` double-write sequence.

Expected total: **250 tests**. This environment does not contain the .NET SDK, so local `dotnet test` remains the acceptance gate.
