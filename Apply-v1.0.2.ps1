$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$expected = Join-Path $root 'tests\AxetosOS.Products.NES.Tests\VirtualHardwareRp2C02ChipTests.cs'

if (-not (Test-Path $expected)) {
    throw "v1.0.2 patch files were not extracted into the AxetosOS.Products.NES repository root."
}

Write-Host 'AxetosOS Products / NES v1.0.2 applied. Stale settlePasses test call removed.'
