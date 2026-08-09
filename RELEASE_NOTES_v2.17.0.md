# AxetosOS.Products.NES v2.17.0

## MMC1 end-of-cycle write timing

The precise Alien Syndrome reference/compiled traces finally expose the timing mismatch behind the remaining compiled-only CHR switch divergence. Both paths receive the same mapper write stream and commit the same CHR1 values, but the old compiled bus invoked MMC1's fifth serial write at CPU bus-start. Real package execution samples that transaction on the falling M2 edge. Depending on the CPU/PPU phase relationship, compiled execution therefore changed CHR1 before one PPU pattern fetch that the physical reference correctly completed from the old bank.

### Hardware/compiler correction

- `CompiledBusTargetDescriptor` now advertises a generic `CompiledBusWritePhase` (`Begin` or `Complete`).
- `ICompiledBusFabric` exposes `CompleteCycle()` so a bus master can publish its actual hardware cycle-completion edge without the compiler assigning semantic meaning to it.
- the RP2A03 compiled core calls `CompleteCycle()` on its own falling-M2 half-cycle; this is the same physical edge used by the package model;
- the generic compiled bus retains pending write address/data and invokes completion-phase targets only at that edge;
- MMC1 CPU ROM/register and optional PRG-RAM targets request completion-phase writes;
- all other target descriptors remain begin-phase by default, preserving existing behavior;
- the old hand-fused NROM fallback implements the new bus hook as a no-op and remains available.

No NES/Famicom/mapper/address semantics were added to `CompiledLabMotherboardExecutionPlan`. The write phase is supplied by the component hardware facet and is resolved through the existing physical bus topology.

### Evidence

In the frame 1450-1460 Alien Syndrome traces, frames 1452, 1455, 1458 and 1460 had different compiled/reference framebuffer hashes. In each case the MMC1 register/value sequence was identical, but compiled `PpuReadCountAtCommit` was exactly one read earlier at one CHR1 transition. Frames without that one-read difference had identical mapping/framebuffer hashes. v2.17.0 moves the compiled MMC1 write to the physical falling-M2 boundary that produced the reference result.

### Tests

Two regressions are added:

- the compiled MMC1 CPU target must advertise completion-phase write latching;
- a synthetic rendering-time MMC1 CHR1 switching workload requires physical and compiled commits to report the same `PpuReadCountAtCommit` sequence.

Validation target: **264 tests**. User-local `dotnet test` remains the acceptance gate.
