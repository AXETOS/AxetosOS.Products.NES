$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$versionFile = Join-Path $root 'Directory.Build.props'
$pinFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Electrical\DigitalPin.cs'
$netFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Electrical\DigitalNet.cs'
$componentFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\VirtualHardwareComponent.cs'
$simulatorFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Simulation\VirtualHardwareSimulator.cs'
$foundationTest = Join-Path $root 'tests\AxetosOS.Products.NES.Tests\VirtualHardwareFoundationTests.cs'

foreach ($file in @($versionFile, $pinFile, $netFile, $componentFile, $simulatorFile, $foundationTest)) {
    if (-not (Test-Path -LiteralPath $file)) {
        throw "v1.3.1 patch file is missing after extraction: $file"
    }
}

$versionText = Get-Content -LiteralPath $versionFile -Raw
if ($versionText -notmatch '<Version>1\.3\.1</Version>') {
    throw 'Directory.Build.props does not contain version 1.3.1 after extraction.'
}

$simulatorText = Get-Content -LiteralPath $simulatorFile -Raw
if ($simulatorText -match '_netQueue|_inputComponentQueue|DeliverInputTransition|NotifyNetDirty') {
    throw 'Legacy central propagation queue code is present after applying v1.3.1.'
}

Write-Host 'AxetosOS Products / NES v1.3.1 applied.'
Write-Host 'Propagation: immediate queue-free traces with atomic per-package output publication.'
Write-Host 'Run: dotnet test'
