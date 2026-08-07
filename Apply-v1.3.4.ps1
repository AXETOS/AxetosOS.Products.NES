$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$required = @(
    'Directory.Build.props',
    'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\Clock\DigitalOscillator.cs',
    'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Electrical\DigitalPin.cs',
    'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Electrical\DigitalNet.cs',
    'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Simulation\CompiledClockExecutionPlan.cs',
    'src\Products\NES\AxetosOS.Products.NES.DesktopHost\Program.cs',
    'tests\AxetosOS.Products.NES.Tests\VirtualHardwareClockEdgeDispatchTests.cs'
)
foreach ($relative in $required) {
    $file = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $file)) { throw "v1.3.4 patch file is missing after extraction: $file" }
}
$versionText = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
if ($versionText -notmatch '<Version>1\.3\.4</Version>') { throw 'Directory.Build.props does not contain version 1.3.4 after extraction.' }
$planText = Get-Content -LiteralPath (Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Simulation\CompiledClockExecutionPlan.cs') -Raw
$pinText = Get-Content -LiteralPath (Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Electrical\DigitalPin.cs') -Raw
if ($planText -notmatch 'AdvanceLowToLowCyclesFast' -or $pinText -notmatch 'SkipInactiveCompiledRisingEdges') {
    throw 'v1.3.4 harmonic clock skip path is missing after extraction.'
}
$simulatorFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Simulation\VirtualHardwareSimulator.cs'
if (Test-Path -LiteralPath $simulatorFile) {
    $simulatorText = Get-Content -LiteralPath $simulatorFile -Raw
    if ($simulatorText -match '_netQueue|_inputComponentQueue|NotifyNetDirty|DeliverInputTransition') {
        throw 'Legacy central propagation queue code is present after applying v1.3.4.'
    }
}
Write-Host 'AxetosOS Products / NES v1.3.4 applied.'
Write-Host 'Clock: inactive master cycles jump directly between real chip activation boundaries.'
Write-Host 'Run: dotnet test'
