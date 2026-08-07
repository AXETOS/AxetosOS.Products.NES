# AxetosOS Products / NES v1.3.7

## Physical IC boundary consolidation

- Physical chips are now the only NES execution boundary in `VirtualHardware`. Internal functional blocks remain inside their real IC and communicate directly.
- Removed the synthetic helper-motherboard CPU/PPU composition and its helper packages/tests.
- Removed the older high-level Hardware/Headless execution architecture so the desktop/ROM runtime has one execution model.
- `VirtualHardwareNesMachineFactory` now selects the same Famicom/NTSC/PAL physical machines as the desktop host.
- `VirtualHardwareComponent` stages only owned output pins, not motherboard nets.
- `DigitalNet` no longer knows or caches receiver edge/divider activation semantics; those are owned by `DigitalPin`/the receiving chip.
- Added structural physical-boundary tests and a full source audit document.
- Removed dead scheduler/activation-contract placeholders left from earlier architectures.

No claim is made here that tests pass or that FPS improves; this environment cannot run .NET. Validate locally with `dotnet test` and the same Mario/Donkey Kong Release runs used for v1.3.6.
