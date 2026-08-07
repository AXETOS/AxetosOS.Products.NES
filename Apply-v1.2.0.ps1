$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$versionFile = Join-Path $root 'Directory.Build.props'
$simulatorFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Simulation\VirtualHardwareSimulator.cs'
$cpuFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\Chips\Ricoh\Rp2A03.cs'
$ppuFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\Chips\Ricoh\Rp2C02.cs'

foreach ($file in @($versionFile, $simulatorFile, $cpuFile, $ppuFile)) {
    if (-not (Test-Path -LiteralPath $file)) {
        throw "v1.2.0 patch file is missing after extraction: $file"
    }
}

$versionText = Get-Content -LiteralPath $versionFile -Raw
if ($versionText -notmatch '<Version>1\.2\.0</Version>') {
    throw 'Directory.Build.props does not contain version 1.2.0 after extraction.'
}

Write-Host 'AxetosOS Products / NES v1.2.0 applied.'
Write-Host 'Run: dotnet test'
