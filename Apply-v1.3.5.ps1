$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$required = @(
    'Directory.Build.props',
    'README.md',
    'src\Products\NES\AxetosOS.Products.NES.DesktopHost\Program.cs',
    'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Electrical\DigitalPin.cs',
    'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Electrical\DigitalNet.cs',
    'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\VirtualHardwareComponent.cs',
    'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\Cartridges\NromCartridge.cs',
    'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\Chips\Memory\Hm6116.cs',
    'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\Chips\Logic\Sn74Ls373.cs',
    'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\Chips\Ricoh\RicohApuMixer.cs',
    'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\Chips\Ricoh\Rp2A03.cs',
    'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\Chips\Ricoh\Rp2A07.cs',
    'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\Chips\Ricoh\Rp2C02.cs',
    'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\Chips\Ricoh\Rp2C07.cs'
)
foreach ($relative in $required) {
    $file = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $file)) { throw "v1.3.5 patch file is missing after extraction: $file" }
}
$versionText = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
if ($versionText -notmatch '<Version>1\.3\.5</Version>') { throw 'Directory.Build.props does not contain version 1.3.5 after extraction.' }
$pinText = Get-Content -LiteralPath (Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Electrical\DigitalPin.cs') -Raw
$ppuText = Get-Content -LiteralPath (Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\Chips\Ricoh\Rp2C02.cs') -Raw
$nromText = Get-Content -LiteralPath (Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\Cartridges\NromCartridge.cs') -Raw
$apuMixerText = Get-Content -LiteralPath (Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\Chips\Ricoh\RicohApuMixer.cs') -Raw
if ($pinText -notmatch 'AcceptPassiveInputLevel' -or
    $ppuText -notmatch '_cpuPortInputMask = ChipSelectBar\.InputChangeMask' -or
    $nromText -notmatch 'DigitalInputActivation\.RisingEdge' -or
    $apuMixerText -notmatch 'BuildMixTable') {
    throw 'v1.3.5 gated hot-path implementation is incomplete after extraction.'
}
$srcRoot = Join-Path $root 'src'
$legacyHits = Get-ChildItem -LiteralPath $srcRoot -Recurse -Filter '*.cs' | Select-String -Pattern '_netQueue|_inputComponentQueue|NotifyNetDirty|DeliverInputTransition|\.Settle\('
if ($legacyHits) { throw 'Legacy central propagation/settle code is present after applying v1.3.5.' }
Write-Host 'AxetosOS Products / NES v1.3.5 applied.'
Write-Host 'Kernel: gated retained-state direct propagation; physical pins remain current without unnecessary package activation.'
Write-Host 'Run: dotnet test'
