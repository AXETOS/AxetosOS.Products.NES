$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$versionFile = Join-Path $root 'Directory.Build.props'
$pinFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Electrical\DigitalPin.cs'
$analyzerFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\Instrumentation\Mos6502BusAnalyzer.cs'
$ppuFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\Nes\NesPpuRegisterPackage.cs'
$boundaryTest = Join-Path $root 'tests\AxetosOS.Products.NES.Tests\VirtualHardwareChipBoundaryTests.cs'

foreach ($file in @($versionFile, $pinFile, $analyzerFile, $ppuFile, $boundaryTest)) {
    if (-not (Test-Path -LiteralPath $file)) {
        throw "v1.1.4 patch file is missing after extraction: $file"
    }
}

$versionText = Get-Content -LiteralPath $versionFile -Raw
if ($versionText -notmatch '<Version>1\.1\.4</Version>') {
    throw 'Directory.Build.props does not contain version 1.1.4 after extraction.'
}

$pinText = Get-Content -LiteralPath $pinFile -Raw
if ($pinText -notmatch 'Direction == PinDirection\.Bidirectional && _driveLevel != DigitalLevel\.HighImpedance') {
    throw 'The v1.1.4 bidirectional input-state rule is missing.'
}

$analyzerText = Get-Content -LiteralPath $analyzerFile -Raw
if ($analyzerText -notmatch '_pendingDataSettled = !isRead && hasData') {
    throw 'The v1.1.4 read-cycle analyzer settling rule is missing.'
}

$ppuText = Get-Content -LiteralPath $ppuFile -Raw
if ($ppuText -notmatch 'PpuBusOwner' -or $ppuText -notmatch 'TakeCpuPpuBus') {
    throw 'The v1.1.4 RP2C02 external-bus ownership logic is missing.'
}

Write-Host 'AxetosOS Products / NES v1.1.4 applied.'
Write-Host 'Run: dotnet test'
