$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$versionFile = Join-Path $root 'Directory.Build.props'
$testFile = Join-Path $root 'tests\AxetosOS.Products.NES.Tests\VirtualHardwareFoundationTests.cs'
if (-not (Test-Path -LiteralPath $versionFile) -or -not (Test-Path -LiteralPath $testFile)) {
    throw 'v1.1.1 patch files were not extracted into the AxetosOS.Products.NES repository root.'
}
$versionText = Get-Content -LiteralPath $versionFile -Raw
if ($versionText -notmatch '<Version>1\.1\.1</Version>') {
    throw 'Directory.Build.props does not contain version 1.1.1 after extraction.'
}
$testText = Get-Content -LiteralPath $testFile -Raw
if ($testText -match 'OnInputChanges\(ulong changedInputMask\)\s*=>\s*OnInputChanges\(ulong\.MaxValue\)') {
    throw 'The stale duplicate MaskProbe input handler is still present.'
}
Write-Host 'AxetosOS Products / NES v1.1.1 applied.'
Write-Host 'Run: dotnet test'
