$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$versionFile = Join-Path $root 'Directory.Build.props'
$pinFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Electrical\DigitalPin.cs'
$netFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Electrical\DigitalNet.cs'
$simulatorFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Simulation\VirtualHardwareSimulator.cs'
$clockPlanFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Simulation\CompiledClockExecutionPlan.cs'
$boundaryTest = Join-Path $root 'tests\AxetosOS.Products.NES.Tests\VirtualHardwareFoundationTests.cs'

foreach ($file in @($versionFile, $pinFile, $netFile, $simulatorFile, $clockPlanFile, $boundaryTest)) {
    if (-not (Test-Path -LiteralPath $file)) {
        throw "v1.3.0 patch file is missing after extraction: $file"
    }
}

$versionText = Get-Content -LiteralPath $versionFile -Raw
if ($versionText -notmatch '<Version>1\.3\.0</Version>') {
    throw 'Directory.Build.props does not contain version 1.3.0 after extraction.'
}

$simulatorText = Get-Content -LiteralPath $simulatorFile -Raw
if ($simulatorText -match '_netQueue|_inputComponentQueue|DeliverInputTransition|NotifyNetDirty') {
    throw 'Legacy propagation queue code is still present after applying v1.3.0.'
}

Write-Host 'AxetosOS Products / NES v1.3.0 applied.'
Write-Host 'Propagation: immediate output pin -> trace -> input pin; no runtime signal/component queue.'
Write-Host 'Run: dotnet test'
