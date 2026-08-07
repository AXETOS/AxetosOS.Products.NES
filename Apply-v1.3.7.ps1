$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$fileList = Join-Path $root 'PATCH_FILE_LIST_v1.3.7.txt'
$removedList = Join-Path $root 'REMOVED_FILES_v1.3.7.txt'

foreach ($manifest in @($fileList, $removedList)) {
    if (-not (Test-Path -LiteralPath $manifest)) {
        throw "v1.3.7 patch manifest is missing after extraction: $manifest"
    }
}

$required = Get-Content -LiteralPath $fileList | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
foreach ($relative in $required) {
    $nativeRelative = $relative -replace '/', [IO.Path]::DirectorySeparatorChar
    $file = Join-Path $root $nativeRelative
    if (-not (Test-Path -LiteralPath $file)) {
        throw "v1.3.7 patch file is missing after extraction: $file"
    }
}

$versionText = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
if ($versionText -notmatch '<Version>1\.3\.7</Version>') {
    throw 'Directory.Build.props does not contain version 1.3.7 after extraction.'
}

# Remove files belonging to the retired synthetic/helper execution paths.
$removed = Get-Content -LiteralPath $removedList | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
foreach ($relative in $removed) {
    $nativeRelative = $relative -replace '/', [IO.Path]::DirectorySeparatorChar
    $file = Join-Path $root $nativeRelative
    if (Test-Path -LiteralPath $file -PathType Leaf) {
        Remove-Item -LiteralPath $file -Force
    }
}

# Remove the two retired project directories completely, including stale local
# build artifacts that are not part of source-control patch manifests.
foreach ($relativeDir in @(
    'src\Products\NES\AxetosOS.Products.NES.Hardware',
    'src\Products\NES\AxetosOS.Products.NES.HeadlessHost',
    'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\Nes\Rp2C02'
)) {
    $dir = Join-Path $root $relativeDir
    if (Test-Path -LiteralPath $dir) {
        Remove-Item -LiteralPath $dir -Recurse -Force
    }
}

$componentFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\VirtualHardwareComponent.cs'
$pinFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Electrical\DigitalPin.cs'
$netFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Electrical\DigitalNet.cs'
$factoryFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Loading\VirtualHardwareNesMachineFactory.cs'
$desktopProject = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.DesktopHost\AxetosOS.Products.NES.DesktopHost.csproj'
$solutionFile = Join-Path $root 'AxetosOS.Products.NES.sln'
$testProject = Join-Path $root 'tests\AxetosOS.Products.NES.Tests\AxetosOS.Products.NES.Tests.csproj'
$boundaryTest = Join-Path $root 'tests\AxetosOS.Products.NES.Tests\VirtualHardwarePhysicalBoundaryTests.cs'

$componentText = Get-Content -LiteralPath $componentFile -Raw
$pinText = Get-Content -LiteralPath $pinFile -Raw
$netText = Get-Content -LiteralPath $netFile -Raw
$factoryText = Get-Content -LiteralPath $factoryFile -Raw
$desktopText = Get-Content -LiteralPath $desktopProject -Raw
$solutionText = Get-Content -LiteralPath $solutionFile -Raw
$testProjectText = Get-Content -LiteralPath $testProject -Raw
$boundaryText = Get-Content -LiteralPath $boundaryTest -Raw

if ($componentText -match 'DigitalNet' -or
    $componentText -notmatch '_changedOutputPins' -or
    $componentText -notmatch 'TryStageOutputChange\(DigitalPin pin\)' -or
    $pinText -notmatch 'PublishStagedDriveChanges' -or
    $netText -match 'InputActivation|RisingEdge|FallingEdge|ActivationPeriod|compiledAllReceiversRisingEdge' -or
    $factoryText -notmatch 'RegionalNesVirtualMachine' -or
    $factoryText -match 'NesCpuMotherboard' -or
    $desktopText -match 'AxetosOS\.Products\.NES\.Hardware|AxetosOS\.Products\.NES\.Cartridges' -or
    $solutionText -match 'AxetosOS\.Products\.NES\.Hardware|AxetosOS\.Products\.NES\.HeadlessHost' -or
    $testProjectText -match 'AxetosOS\.Products\.NES\.Hardware' -or
    $boundaryText -notmatch 'Component_base_retains_only_owned_pins_not_motherboard_nets' -or
    $boundaryText -notmatch 'Digital_net_transport_does_not_cache_receiver_activation_semantics') {
    throw 'v1.3.7 physical IC-boundary implementation is incomplete after extraction.'
}

$srcRoot = Join-Path $root 'src'
$legacyPatterns = @(
    'NesCpuMotherboard',
    'NesPpuTimingCore',
    'NesPpuRegisterPackage',
    'NesPpuMemoryDevice',
    'NesOamDmaController',
    'NesControllerIoPackage',
    'Rp2C02BusSequencer',
    'Rp2C02DataBufferRegister',
    'Rp2C02VramAddressRegisters',
    '_netQueue',
    '_inputComponentQueue',
    'NotifyNetDirty',
    'DeliverInputTransition',
    'ResolveQueued',
    '\.Settle\('
)
$legacyHits = Get-ChildItem -LiteralPath $srcRoot -Recurse -Filter '*.cs' |
    Select-String -Pattern $legacyPatterns
if ($legacyHits) {
    throw 'Retired synthetic-package/queue/settle code is present after applying v1.3.7.'
}

foreach ($relative in $removed) {
    $nativeRelative = $relative -replace '/', [IO.Path]::DirectorySeparatorChar
    $file = Join-Path $root $nativeRelative
    if (Test-Path -LiteralPath $file -PathType Leaf) {
        throw "Retired v1.3.7 file still exists after cleanup: $file"
    }
}

Write-Host 'AxetosOS Products / NES v1.3.7 applied.'
Write-Host 'Kernel: physical IC-boundary direct propagation; internal chip work stays inside the chip.'
Write-Host 'Note: obsolete reference/synthetic architecture tests were removed with those retired implementations, so the test count is expected to be lower than v1.3.6.'
Write-Host 'Run: dotnet test'
