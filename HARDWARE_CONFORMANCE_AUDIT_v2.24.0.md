# v2.24.0 chip-by-chip hardware audit

The criterion for this audit is behavioral equivalence at observable package/bus boundaries. Internal code may be simplified whenever that simplification preserves those results.

| Device | Audit result | v2.24.0 action |
|---|---|---|
| SN74LS139A | Compact active-low dual 2-to-4 decode is suitable | No chip rewrite; motherboard wiring corrected |
| SN74LS368A | Compact inverting tri-state buffer behavior suitable | No change |
| SN74LS373 | Runtime transparent-latch/storage behavior retained | No runtime rewrite |
| HM6116 | Addressed SRAM read/write/tri-state storage model suitable | No change |
| RP2A03 | Bus-cycle ordering, interrupt polling and open-bus gaps found | Corrected |
| RP2A07 | Same CPU-class bus-cycle gaps in PAL package | Corrected in PAL implementation |
| RP2C02 | Sprite/OAM evaluation and sprite-fetch bus cadence were over-simplified | Corrected |
| RP2C07 | Same PPU-class issues in PAL package | Corrected |
| Ricoh APU blocks | Triangle DAC hold and frame/noise timing gaps found | Corrected where evidence is deterministic |
| RicohApuMixer | Compact transfer-function model is acceptable for current digital boundary | No change |
| CIC3193 | Existing algorithm is synthetic, not a verified Nintendo lock/key implementation | Explicitly unresolved |
| CIC3195 | Existing algorithm is synthetic, not a verified Nintendo lock/key implementation | Explicitly unresolved |
| CIC3197 | Existing algorithm is synthetic, not a verified Nintendo lock/key implementation | Explicitly unresolved |
| NROM cartridge | CPU connector exposed impossible A15 and lacked physical /ROMSEL | Corrected |
| MMC1 cartridge | Same connector fault; PRG-RAM/ROM select needed physical M2-/ROMSEL qualification | Corrected |
| Famicom/NTSC/PAL board decode | LS139 wiring skipped first-stage M2 qualification and physical /ROMSEL | Corrected |

The audit deliberately does not turn a known-unknown into guessed behavior. CIC and silicon-unstable/analog details stay visible as unresolved work rather than being hidden by self-consistent unit tests.
