# AxetosOS Products / NES v2.12.1

## Generic signal-router type hotfix

- Fixes CS1503 in `CompiledLabMotherboardExecutionPlan.CompiledSignalRouter`.
- Keeps signal-route grouping strongly typed as `DigitalNet` rather than allowing LINQ key inference to widen to `object` through `ReferenceEqualityComparer.Instance`.
- `DigitalNet` uses reference identity, so removing the explicit object comparer preserves the intended physical-net identity semantics.
- No runtime optimization behavior or hardware/compiler architecture changes from v2.12.0.
- Existing fused runtime remains unchanged.
