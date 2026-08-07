$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$fileList = Join-Path $root 'PATCH_FILE_LIST_v1.3.6.txt'
if (-not (Test-Path -LiteralPath $fileList)) {
    throw "v1.3.6 patch manifest is missing after extraction: $fileList"
}

$required = Get-Content -LiteralPath $fileList | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
foreach ($relative in $required) {
    $nativeRelative = $relative -replace '/', [IO.Path]::DirectorySeparatorChar
    $file = Join-Path $root $nativeRelative
    if (-not (Test-Path -LiteralPath $file)) {
        throw "v1.3.6 patch file is missing after extraction: $file"
    }
}

$versionText = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
if ($versionText -notmatch '<Version>1\.3\.6</Version>') {
    throw 'Directory.Build.props does not contain version 1.3.6 after extraction.'
}

$pinFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Electrical\DigitalPin.cs'
$clockPlanFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Simulation\CompiledClockExecutionPlan.cs'
$ppuFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\Chips\Ricoh\Rp2C02.cs'
$nromFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\Cartridges\NromCartridge.cs'
$mixerFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\Chips\Ricoh\RicohApuMixer.cs'

$pinText = Get-Content -LiteralPath $pinFile -Raw
$clockPlanText = Get-Content -LiteralPath $clockPlanFile -Raw
$ppuText = Get-Content -LiteralPath $ppuFile -Raw
$nromText = Get-Content -LiteralPath $nromFile -Raw
$mixerText = Get-Content -LiteralPath $mixerFile -Raw

if ($pinText -notmatch 'InputChangeMask \{ get; init; \}' -or
    $pinText -match 'AcceptPassiveInputLevel' -or
    $clockPlanText -match 'AdvanceLowToLowCyclesFast|SkipInactiveCompiledClockCycles' -or
    $ppuText -notmatch 'if \(ChipSelectBar\.SampledLevel == DigitalLevel\.High\) return;' -or
    $ppuText -notmatch 'if \(!CpuData\.TrySample\(out var rawValue\)\) return;' -or
    $nromText -notmatch 'PpuAddressData\.InputChangeMask' -or
    $mixerText -notmatch 'BuildPulseTable' -or
    $mixerText -notmatch 'BuildTndTable') {
    throw 'v1.3.6 chip-owned activation implementation is incomplete after extraction.'
}

$srcRoot = Join-Path $root 'src'
$legacyHits = Get-ChildItem -LiteralPath $srcRoot -Recurse -Filter '*.cs' |
    Select-String -Pattern '_netQueue|_inputComponentQueue|NotifyNetDirty|DeliverInputTransition|AcceptPassiveInputLevel|SkipInactiveCompiledClockCycles|\.Settle\('
if ($legacyHits) {
    throw 'Legacy queue/settle/passive-route/harmonic-skip code is present after applying v1.3.6.'
}

Write-Host 'AxetosOS Products / NES v1.3.6 applied.'
Write-Host 'Kernel: chip-owned activation direct propagation; motherboard always delivers physical pin levels.'
Write-Host 'Run: dotnet test'
