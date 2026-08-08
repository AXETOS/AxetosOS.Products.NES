$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$fileList = Join-Path $root 'PATCH_FILE_LIST_v1.4.0.txt'

if (-not (Test-Path -LiteralPath $fileList)) {
    throw "v1.4.0 patch manifest is missing after extraction: $fileList"
}

$required = Get-Content -LiteralPath $fileList | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
foreach ($relative in $required) {
    $nativeRelative = $relative -replace '/', [IO.Path]::DirectorySeparatorChar
    $file = Join-Path $root $nativeRelative
    if (-not (Test-Path -LiteralPath $file)) {
        throw "v1.4.0 patch file is missing after extraction: $file"
    }
}

$versionText = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
if ($versionText -notmatch '<Version>1\.4\.0</Version>') {
    throw 'Directory.Build.props does not contain version 1.4.0 after extraction.'
}

$pinFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Electrical\DigitalPin.cs'
$netFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Electrical\DigitalNet.cs'
$ppuFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\Chips\Ricoh\Rp2C02.cs'
$apuFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\Chips\Ricoh\Rp2A03.cs'
$ramFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\Chips\Memory\Hm6116.cs'
$latchFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\Chips\Logic\Sn74Ls373.cs'
$nromFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\Cartridges\NromCartridge.cs'
$testFile = Join-Path $root 'tests\AxetosOS.Products.NES.Tests\VirtualHardwareChipOwnedActivationTests.cs'

$pinText = Get-Content -LiteralPath $pinFile -Raw
$netText = Get-Content -LiteralPath $netFile -Raw
$ppuText = Get-Content -LiteralPath $ppuFile -Raw
$apuText = Get-Content -LiteralPath $apuFile -Raw
$ramText = Get-Content -LiteralPath $ramFile -Raw
$latchText = Get-Content -LiteralPath $latchFile -Raw
$nromText = Get-Content -LiteralPath $nromFile -Raw
$testText = Get-Content -LiteralPath $testFile -Raw

if ($pinText -notmatch '_ownerWakeEnabled' -or
    $pinText -notmatch 'SetOwnerWakeEnabled' -or
    $pinText -notmatch 'AnyChange\) return OwnerWantsWake' -or
    $netText -notmatch 'PresentResolvedSingleDriver' -or
    $ppuText -notmatch 'RefreshCpuPortWakeState' -or
    $ppuText -notmatch 'MultiplexedAddressData\.SetOwnerWakeEnabled\(false\)' -or
    $apuText -notmatch 'Data\.SetOwnerWakeEnabled\(false\)' -or
    $ramText -notmatch 'RefreshInputWakeState' -or
    $latchText -notmatch 'RefreshDataWakeState' -or
    $nromText -notmatch 'RefreshPpuDataWakeState' -or
    $testText -notmatch 'Disabled_chip_input_stage_records_levels_without_waking_package_logic') {
    throw 'v1.4.0 chip-owned pin activation sweep is incomplete after extraction.'
}

# Activation semantics must stay out of the motherboard/board composition.
$boardRoot = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Boards'
$boardActivationHits = Get-ChildItem -LiteralPath $boardRoot -Recurse -Filter '*.cs' |
    Select-String -Pattern 'SetOwnerWakeEnabled|OwnerWantsWake'
if ($boardActivationHits) {
    throw 'Chip-owned activation semantics leaked into motherboard/board source in v1.4.0.'
}

$srcRoot = Join-Path $root 'src'
$legacyHits = Get-ChildItem -LiteralPath $srcRoot -Recurse -Filter '*.cs' |
    Select-String -Pattern '_netQueue|_inputComponentQueue|NotifyNetDirty|DeliverInputTransition|SkipInactiveCompiledClockCycles|\.Settle\('
if ($legacyHits) {
    throw 'Legacy queue/settle/harmonic-skip code is present after applying v1.4.0.'
}

Write-Host 'AxetosOS Products / NES v1.4.0 applied.'
Write-Host 'Kernel: chip-owned pin-gated direct propagation; motherboard still delivers every physical level.'
Write-Host 'Run: dotnet test'
Write-Host 'Then benchmark normal Release Mario and Donkey Kong before using --profile.'
