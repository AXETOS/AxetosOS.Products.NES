# AxetosOS Products / NES v2.35.0

## Native ROM loading screen

This release improves desktop startup feedback without changing any emulated hardware, mapper, motherboard or compiler semantics.

- The Win32 framebuffer window is now created immediately after the user selects a ROM.
- An animated `AXETOSOS / LOADING ROM` framebuffer is shown while the cartridge image is parsed, the physical machine is assembled and the startup circuit is compiled.
- ROM loading runs on a ThreadPool worker while the native window continues pumping messages, so the loading window remains responsive instead of appearing frozen during multi-second startup compilation.
- Escape or closing the window remains responsive during loading.
- The same native presenter and framebuffer surface are reused once gameplay starts; there is no second game window or external UI dependency.
- Desktop diagnostics now report the measured ROM parse + physical assembly + startup compilation time.
- Normal hardware-real-time and uncapped benchmark behavior are unchanged after startup.

## Architecture

The loading screen is strictly desktop-host policy. It does not bypass or alter cartridge insertion, motherboard wiring, chip behavior, package pins, physical traces, or whole-circuit compilation. The hardware model is still assembled and compiled exactly as before; the work simply occurs while the host keeps the native presentation thread responsive.

## Validation

The validated v2.34.1 baseline is **359 / 359 tests passing**, including Mapper 11 / Color Dreams. v2.35.0 does not add or remove hardware tests, so the expected suite remains **359 tests** pending local Release validation.
