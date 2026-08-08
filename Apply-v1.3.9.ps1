$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$fileList = Join-Path $root 'PATCH_FILE_LIST_v1.3.9.txt'

if (-not (Test-Path -LiteralPath $fileList)) {
    throw "v1.3.9 patch manifest is missing after extraction: $fileList"
}

$required = Get-Content -LiteralPath $fileList | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
foreach ($relative in $required) {
    $nativeRelative = $relative -replace '/', [IO.Path]::DirectorySeparatorChar
    $file = Join-Path $root $nativeRelative
    if (-not (Test-Path -LiteralPath $file)) {
        throw "v1.3.9 patch file is missing after extraction: $file"
    }
}

$versionText = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
if ($versionText -notmatch '<Version>1\.3\.9</Version>') {
    throw 'Directory.Build.props does not contain version 1.3.9 after extraction.'
}

$pinFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Electrical\DigitalPin.cs'
$netFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Electrical\DigitalNet.cs'
$sramFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\Chips\Memory\Hm6116.cs'
$latchFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\Chips\Logic\Sn74Ls373.cs'
$testFile = Join-Path $root 'tests\AxetosOS.Products.NES.Tests\VirtualHardwareElectricalTransportTests.cs'

$pinText = Get-Content -LiteralPath $pinFile -Raw
$netText = Get-Content -LiteralPath $netFile -Raw
$sramText = Get-Content -LiteralPath $sramFile -Raw
$latchText = Get-Content -LiteralPath $latchFile -Raw
$testText = Get-Content -LiteralPath $testFile -Raw

if ($pinText -notmatch 'activation == DigitalInputActivation\.AnyChange' -or
    $pinText -notmatch 'Edge counters/dividers belong only to edge-activated pins' -or
    $netText -notmatch 'ResolveAndPresentFast' -or
    $netText -notmatch 'ResolveAndPresentProfiled' -or
    $netText -notmatch 'ResolveTwoDrivers' -or
    $netText -notmatch 'NetResolverKind\.TwoDrivers' -or
    $sramText -notmatch '_dataReleased' -or
    $sramText -notmatch 'private void ReleaseData\(' -or
    $latchText -notmatch 'if \(powerChanged\)' -or
    $testText -notmatch 'Two_driver_fast_resolver_preserves_contention_and_release_semantics') {
    throw 'v1.3.9 electrical transport optimization is incomplete after extraction.'
}

$srcRoot = Join-Path $root 'src'
$legacyHits = Get-ChildItem -LiteralPath $srcRoot -Recurse -Filter '*.cs' |
    Select-String -Pattern '_netQueue|_inputComponentQueue|NotifyNetDirty|DeliverInputTransition|SkipInactiveCompiledClockCycles|\.Settle\('
if ($legacyHits) {
    throw 'Legacy queue/settle/harmonic-skip code is present after applying v1.3.9.'
}

Write-Host 'AxetosOS Products / NES v1.3.9 applied.'
Write-Host 'Kernel: zero-profiler electrical transport fast path; motherboard remains topology/electrical only.'
Write-Host 'Run: dotnet test'
Write-Host 'Then benchmark normal Release Mario/Donkey Kong before running --profile again.'
