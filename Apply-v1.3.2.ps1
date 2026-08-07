$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$required = @(
    'Directory.Build.props',
    'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Electrical\DigitalInputActivation.cs',
    'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Electrical\DigitalPin.cs',
    'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Electrical\DigitalNet.cs',
    'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\VirtualHardwareComponent.cs',
    'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Simulation\CompiledClockExecutionPlan.cs'
)
foreach ($relative in $required) {
    $file = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $file)) { throw "v1.3.2 patch file is missing after extraction: $file" }
}
$versionText = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
if ($versionText -notmatch '<Version>1\.3\.2</Version>') { throw 'Directory.Build.props does not contain version 1.3.2 after extraction.' }
$simulatorText = Get-Content -LiteralPath (Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Simulation\VirtualHardwareSimulator.cs') -Raw
if ($simulatorText -match '_netQueue|_inputComponentQueue|NotifyNetDirty|DeliverInputTransition') {
    throw 'Legacy central propagation queue code is present after applying v1.3.2.'
}
Write-Host 'AxetosOS Products / NES v1.3.2 applied.'
Write-Host 'Propagation: compiled clock + coalesced immediate destination fan-out; no signal queue.'
Write-Host 'Run: dotnet test'
