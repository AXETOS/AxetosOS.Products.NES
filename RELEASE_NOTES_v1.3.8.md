# AxetosOS Products / NES v1.3.8

v1.3.8 adds an opt-in sampled profiler for the physical virtual-hardware runtime.

The profiler measures the same compiled clock/trace architecture used by normal execution and reports motherboard electrical transport, all physical package activations, selected internal RP2A03/RP2A07 and RP2C02/RP2C07 sections, and host video/audio/event/diagnostic costs.

Normal execution remains queue-free and unchanged. Profiling is enabled only with `--profile`.
