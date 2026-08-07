$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$required = @(
    'Directory.Build.props',
    'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Electrical\DigitalPin.cs',
    'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Electrical\DigitalNet.cs',
    'tests\AxetosOS.Products.NES.Tests\VirtualHardwareClockEdgeDispatchTests.cs'
)
foreach ($relative in $required) {
    $file = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $file)) { throw "v1.3.3 patch file is missing after extraction: $file" }
}
$versionText = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
if ($versionText -notmatch '<Version>1\.3\.3</Version>') { throw 'Directory.Build.props does not contain version 1.3.3 after extraction.' }
$netText = Get-Content -LiteralPath (Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Electrical\DigitalNet.cs') -Raw
$pinText = Get-Content -LiteralPath (Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Electrical\DigitalPin.cs') -Raw
if ($netText -notmatch 'AcceptCompiledRisingEdgeClockLevel' -or $pinText -notmatch 'TryAcceptCompiledRisingEdgeClockLevel') {
    throw 'v1.3.3 rising-edge clock fast path is missing after extraction.'
}
$simulatorFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Simulation\VirtualHardwareSimulator.cs'
if (Test-Path -LiteralPath $simulatorFile) {
    $simulatorText = Get-Content -LiteralPath $simulatorFile -Raw
    if ($simulatorText -match '_netQueue|_inputComponentQueue|NotifyNetDirty|DeliverInputTransition') {
        throw 'Legacy central propagation queue code is present after applying v1.3.3.'
    }
}
Write-Host 'AxetosOS Products / NES v1.3.3 applied.'
Write-Host 'Clock: falling edges update pin state only; rising-edge divider remains chip-owned.'
Write-Host 'Run: dotnet test'
