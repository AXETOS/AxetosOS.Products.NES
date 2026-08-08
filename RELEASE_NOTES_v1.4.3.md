# AxetosOS Products / NES v1.4.3

Chip-local direct reaction sweep.

v1.4.3 continues the physical-chip model rather than introducing motherboard semantics: package pins still receive every resolved electrical level, while each chip takes the shortest internal path that its own power/select/enable/phase state permits.

## Changes

- HM6116 selected read-address changes now go directly from retained package-pin levels to the addressed storage/output cell; selected write address/data changes go directly to the storage path. Power, `/CS`, `/OE`, `/WE`, unknown-state and tri-state behavior remain chip-owned.
- SN74LS373 D-only activity while `LE=High` now directly updates the transparent latch and, when enabled, its Q drivers. D pins still receive/store levels while `LE=Low` without waking storage.
- RP2A03/RP2A07 sample controller inputs at CPU boundaries only while `/OE1` or `/OE2` actually enables that internal input stage. Physical controller pins remain continuously delivered and selected pin transitions still latch immediately.
- RP2C02/RP2C07 clock-only paths skip the VRAM output stage on dots that have no VRAM transaction before or after the internal dot, and revisit `/NMI` only when the vblank latch changes. Every physical clock edge still reaches the package and every genuine package-output transition still crosses the motherboard boundary immediately.
- No motherboard-side `/CS`, `/OE`, `/WE`, clock, receiver-interest or chip-state knowledge is introduced.

## Architecture

Unchanged: dumb motherboard, smart physical chips. The motherboard resolves traces and delivers levels only. Package pins always retain the delivered level. Chip-owned circuitry decides whether and how that level propagates internally. There is no queue, scheduler, settle pass, receiver-aware suppression or skipped physical clock.

Run `dotnet test` first. The expected test count remains 215. Then benchmark normal Release Mario and Donkey Kong against v1.4.2 (22.55 / 21.04 FPS).
