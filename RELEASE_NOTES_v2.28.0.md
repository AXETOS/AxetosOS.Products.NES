# AxetosOS Products / NES v2.28.0

## Controller hardware / host-input milestone

v2.28.0 connects native host key state to the already-modelled standard NES/Famicom controller hardware without introducing CPU, motherboard or compiler shortcuts.

### Physical input path

The desktop host now changes generic external digital sources connected to the controller button traces. Runtime input therefore follows:

```text
native keyboard event
  -> host controller mapping
  -> external digital source pin
  -> physical controller button trace
  -> standard controller package
  -> STROBE / serial shift hardware
  -> controller DATA trace
  -> RP2A03 controller input
  -> ordinary $4016/$4017 CPU read
```

The host does not write controller registers, controller shift state, CPU state, RAM or game state.

### Controller coverage

- Controller 1 and Controller 2 have independent eight-button external input sources.
- Standard serial order remains A, B, Select, Start, Up, Down, Left, Right.
- Desktop Controller 1 defaults:
  - Arrow keys: D-pad
  - Z: A
  - X: B
  - Enter: Start
  - Right Shift: Select
  - Escape: close host
- Controller 2 is physically/API-ready for a later second-player host binding.

### Compiled execution correctness

The specialized Famicom/NROM execution plan no longer owns duplicate zero-filled controller latch/shift registers. It resolves the real controller packages through their generic compiled serial-peripheral facets and validates those facets against the assembled physical DATA, /CLOCK and STROBE traces.

The controller package also retains compiled-delivered STROBE pin state so a host button transition arriving between compiled CPU bus operations observes the same package input level as the raw electrical path.

### Generic infrastructure

`DigitalExternalInputBank` is a product-neutral physical stimulus component: it owns output pins and knows nothing about NES controllers, keyboards or CPU registers. Future virtual hardware can reuse the same host/external input mechanism.

### Tests

New regression coverage exercises host controller input through:

- specialized compiled NROM execution;
- generic whole-circuit compiled execution;
- raw physical propagation;
- independent Controller 1 / Controller 2 sources;
- live button changes while compiled STROBE is High.

The expected Release-suite count is 286 tests. Local validation is required before this release is considered validated.
