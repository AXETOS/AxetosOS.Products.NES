# AxetosOS Products / NES v1.3.9

Measured electrical-transport hot-path optimization based on the v1.3.8 sampled profiler.

- Keeps motherboard behavior topology/electrical only.
- Fast-paths ordinary AnyChange package pins.
- Adds exact two-driver net resolution without the generic driver-array scan.
- Splits normal electrical propagation from sampled profiler instrumentation.
- Retains HM6116/SN74LS373 power/output state to avoid redundant work.
- Adds electrical transport regression coverage for ordinary transitions and two-driver contention/release.

Run `dotnet test`, then compare normal Release FPS before running another `--profile` sample.
