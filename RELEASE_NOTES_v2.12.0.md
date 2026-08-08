# AxetosOS Products / NES v2.12.0

## Product-agnostic whole-circuit hardware compiler

v2.12.0 keeps the v2.11 whole-circuit performance architecture but removes the remaining product/chip-role knowledge from the compiler itself.

### Hard compiler boundary

The compiler consumes only:

- component-provided physical compilation facets;
- physical package pins;
- the assembled netlist;
- electrical levels/drive strengths;
- clock activation periods and physical receiver order;
- replaceable external-device boundaries.

The compiler source does **not** select or special-case RP2A03, RP2C02, HM6116, 74-series parts, controllers, NROM, Famicom, CPU RAM, CIRAM, or NES address ranges. Concrete chips know only their own hardware and expose generic facets that any board may use.

### Aggressive shortcuts remain allowed

This is intentionally not a conservative interpreter. The compiler may precompute complete routing tables, collapse combinational chains, pack physical bit permutations, fuse signal delivery, unroll repeating clock schedules, and remove intermediate pin/net/package runtime work whenever that result is derived from and equivalent to the assembled hardware.

Changing a wire or component changes the compiler input and therefore rebuilds the affected shortcuts. Product names and semantic net names are irrelevant.

### Public hardware-compiler facets

Custom lab components can now advertise generic capabilities through public contracts including:

- bus masters and bus targets;
- combinational-output evaluation;
- bit projection through a component;
- clock sources and clocked components;
- signal sinks;
- serial peripherals;
- replaceable external devices.

This means a new chip can participate in compilation without adding a type switch to the compiler.

### Non-NES proof

The unrelated `PinDrivenMicrocomputer` example now compiles through the same execution plan. Its Tiny8 processor, generic static RAM, program ROM, binary address decoder and inverter expose the same hardware facets used by the compiler. A new test runs a Tiny8 load/store/load program entirely through the whole-circuit compiler and verifies the CPU result and RAM write.

This is an explicit regression guard against turning the compiler into an NES compiler.

### Existing execution modes retained

- default: existing hand-fused Famicom/NROM runtime, retained as the proven ~60 FPS fallback;
- `--reference-runtime`: v2.10 physical pin/net/package oracle;
- `--compiled-lab`: generalized whole-circuit hardware compiler with replaceable cartridge/mapper/ROM boundary.

Expected test total: **233**.
