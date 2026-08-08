$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$fileList = Join-Path $root 'PATCH_FILE_LIST_v1.3.8.txt'

if (-not (Test-Path -LiteralPath $fileList)) {
    throw "v1.3.8 patch manifest is missing after extraction: $fileList"
}

$required = Get-Content -LiteralPath $fileList | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
foreach ($relative in $required) {
    $nativeRelative = $relative -replace '/', [IO.Path]::DirectorySeparatorChar
    $file = Join-Path $root $nativeRelative
    if (-not (Test-Path -LiteralPath $file)) {
        throw "v1.3.8 patch file is missing after extraction: $file"
    }
}

$versionText = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
if ($versionText -notmatch '<Version>1\.3\.8</Version>') {
    throw 'Directory.Build.props does not contain version 1.3.8 after extraction.'
}

$simulatorFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Simulation\VirtualHardwareSimulator.cs'
$profileSampleFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Simulation\VirtualHardwareProfileSample.cs'
$clockPlanFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Simulation\CompiledClockExecutionPlan.cs'
$netFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Electrical\DigitalNet.cs'
$componentFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\VirtualHardwareComponent.cs'
$cpuFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\Chips\Ricoh\Rp2A03.cs'
$ppuFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\Chips\Ricoh\Rp2C02.cs'
$desktopFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.DesktopHost\Program.cs'
$testFile = Join-Path $root 'tests\AxetosOS.Products.NES.Tests\VirtualHardwareProfilerTests.cs'

$simulatorText = Get-Content -LiteralPath $simulatorFile -Raw
$profileSampleText = Get-Content -LiteralPath $profileSampleFile -Raw
$clockPlanText = Get-Content -LiteralPath $clockPlanFile -Raw
$netText = Get-Content -LiteralPath $netFile -Raw
$componentText = Get-Content -LiteralPath $componentFile -Raw
$cpuText = Get-Content -LiteralPath $cpuFile -Raw
$ppuText = Get-Content -LiteralPath $ppuFile -Raw
$desktopText = Get-Content -LiteralPath $desktopFile -Raw
$testText = Get-Content -LiteralPath $testFile -Raw

if ($simulatorText -notmatch 'TimedNetResolutionSamples' -or
    $simulatorText -notmatch 'RecordProfileSection' -or
    $profileSampleText -notmatch 'VirtualHardwareProfileSection' -or
    $clockPlanText -notmatch 'PropagateCompiledSingleDriverProfiled' -or
    $clockPlanText -match 'Profiling intentionally uses the generic immediate path' -or
    $netText -notmatch 'BeginNetResolutionTimingSample' -or
    $componentText -notmatch 'ReceiveInputChangesProfiled' -or
    $cpuText -notmatch 'Rp2A03CpuCore' -or
    $cpuText -notmatch 'Rp2A03Dma' -or
    $ppuText -notmatch 'Rp2C02Background' -or
    $ppuText -notmatch 'Rp2C02Sprite' -or
    $desktopText -notmatch 'PROFILE HOST SECTION' -or
    $desktopText -notmatch 'PROFILE IC SECTION' -or
    $testText -notmatch 'Profiler_is_opt_in_and_samples_direct_package_and_net_work') {
    throw 'v1.3.8 profiler implementation is incomplete after extraction.'
}

$srcRoot = Join-Path $root 'src'
$legacyHits = Get-ChildItem -LiteralPath $srcRoot -Recurse -Filter '*.cs' |
    Select-String -Pattern '_netQueue|_inputComponentQueue|NotifyNetDirty|DeliverInputTransition|SkipInactiveCompiledClockCycles|\.Settle\('
if ($legacyHits) {
    throw 'Legacy queue/settle/harmonic-skip code is present after applying v1.3.8.'
}

Write-Host 'AxetosOS Products / NES v1.3.8 applied.'
Write-Host 'Profiler: opt-in sampled physical-package, electrical-net, internal Ricoh IC and host timing.'
Write-Host 'Normal kernel: physical IC-boundary direct propagation; no signal queue.'
Write-Host 'Run: dotnet test'
Write-Host 'Then profile: add --profile to the same Release ROM command and paste the PROFILE output.'
