$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$versionFile = Join-Path $root 'Directory.Build.props'
$boundaryTest = Join-Path $root 'tests\AxetosOS.Products.NES.Tests\VirtualHardwareChipBoundaryTests.cs'

foreach ($file in @($versionFile, $boundaryTest)) {
    if (-not (Test-Path -LiteralPath $file)) {
        throw "v1.1.6 patch file is missing after extraction: $file"
    }
}

$versionText = Get-Content -LiteralPath $versionFile -Raw
if ($versionText -notmatch '<Version>1\.1\.6</Version>') {
    throw 'Directory.Build.props does not contain version 1.1.6 after extraction.'
}

$testText = Get-Content -LiteralPath $boundaryTest -Raw
if ($testText -notmatch 'DigitalSignalSource\("external", DigitalLevel\.Low\)' -or
    $testText -notmatch 'stayed Low throughout' -or
    $testText -notmatch 'Assert\.Equal\(DigitalLevel\.Low, package\.Bus\.SampledLevel\)') {
    throw 'The corrected unchanged-bus boundary regression test is missing.'
}

Write-Host 'AxetosOS Products / NES v1.1.6 applied.'
Write-Host 'Runtime code unchanged; this release corrects the invalid contention expectation in the boundary regression test.'
Write-Host 'Run: dotnet test'
