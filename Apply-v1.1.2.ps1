$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$versionFile = Join-Path $root 'Directory.Build.props'
$netFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Electrical\DigitalNet.cs'
$testFile = Join-Path $root 'tests\AxetosOS.Products.NES.Tests\VirtualHardwareChipBoundaryTests.cs'

if (-not (Test-Path -LiteralPath $versionFile) -or
    -not (Test-Path -LiteralPath $netFile) -or
    -not (Test-Path -LiteralPath $testFile)) {
    throw 'v1.1.2 patch files were not extracted into the AxetosOS.Products.NES repository root.'
}

$versionText = Get-Content -LiteralPath $versionFile -Raw
if ($versionText -notmatch '<Version>1\.1\.2</Version>') {
    throw 'Directory.Build.props does not contain version 1.1.2 after extraction.'
}

$netText = Get-Content -LiteralPath $netFile -Raw
if ($netText -match 'dirtySourcePin|dirtyFromMultipleSources|ReferenceEquals\(_pin, sourcePin\)') {
    throw 'The obsolete dirty-source suppression logic is still present.'
}

$testText = Get-Content -LiteralPath $testFile -Raw
if ($testText -notmatch 'Releasing_bidirectional_pin_delivers_external_bus_level_as_input') {
    throw 'The v1.1.2 bus hand-off regression test is missing.'
}

Write-Host 'AxetosOS Products / NES v1.1.2 applied.'
Write-Host 'Run: dotnet test'
