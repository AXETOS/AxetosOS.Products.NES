# AxetosOS Products / NES v2.9.1

## Compiled package fan-out ordering correction

- Baseline: v2.9.0 clean compiled package-fanout experiment.
- Corrects a physical ordering regression found by the compiled/reference equivalence tests.
- Receiving packages now stage accepted physical input changes immediately during net presentation, exactly like the reference propagation-frame path.
- Destination execution remains deferred until every changed output net from the source package has been resolved and presented, preserving atomic package output semantics.
- Removes the delayed per-component receiver-mask array from the experimental transport; pending masks remain package-owned as in the reference engine.
- No NES/Famicom/NROM/component-type knowledge is introduced.
- The old propagation-frame implementation remains replaced for multi-output package fan-out, preserving the clean performance experiment.

Expected tests: 228 total. Run `dotnet test` locally before benchmarking.
