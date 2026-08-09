# AxetosOS Products / NES v2.13.3

## MMC1 physical-bus regression fixture hotfix

v2.13.3 is a test-fixture hotfix over v2.13.2. It does not change MMC1, motherboard, electrical transport or compiler runtime behavior.

The v2.13.2 standalone physical PPU bus regression constructed a `VirtualHardwareBoard` and attached synthetic signal sources, but omitted the normal topology-compilation step. In this hardware model, `DigitalSignalSource.Set` changes the source package drive immediately, while propagation through a trace begins only after that trace has been compiled by `VirtualHardwareSimulator`. The test therefore reached its first CHR-data assertion with unresolved fixture nets and failed before testing the intended MMC1 ALE bus-release behavior.

The fixture now constructs `VirtualHardwareSimulator` after all synthetic connections are present and before any source transitions are driven. This matches every normal physical-machine execution path and the established standalone electrical-test pattern.

No production source file is changed by this hotfix. The v2.13.2 MMC1 PPU AD-bus release and consecutive-write implementation remains intact for real-ROM validation.

Expected total: **250 tests**. Local `dotnet test` remains the acceptance gate.
