$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$versionFile = Join-Path $root 'Directory.Build.props'
$boundaryFile = Join-Path $root 'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\IVirtualHardwareComponent.cs'

if (-not (Test-Path -LiteralPath $versionFile) -or -not (Test-Path -LiteralPath $boundaryFile)) {
    throw 'v1.1.0 patch files were not extracted into the AxetosOS.Products.NES repository root.'
}

$removedFiles = @(
    'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\IClockEdgeDrivenVirtualHardwareComponent.cs',
    'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\ICombinationalVirtualHardwareComponent.cs',
    'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\ICompiledInputDrivenVirtualHardwareComponent.cs',
    'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\IEventDrivenVirtualHardwareComponent.cs',
    'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\IInputActivationContractProvider.cs',
    'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\IInputDrivenVirtualHardwareComponent.cs',
    'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\ISelectiveInputDrivenVirtualHardwareComponent.cs',
    'src\Products\NES\AxetosOS.Products.NES.VirtualHardware\Components\PinActivationContract.cs'
)

$deleted = 0
foreach ($relativePath in $removedFiles) {
    $path = Join-Path $root $relativePath
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force
        $deleted++
    }
}

$versionText = Get-Content -LiteralPath $versionFile -Raw
if ($versionText -notmatch '<Version>1\.1\.0</Version>') {
    throw 'Directory.Build.props does not contain version 1.1.0 after extraction.'
}

Write-Host "AxetosOS Products / NES v1.1.0 applied. Removed $deleted obsolete contract file(s)."
Write-Host 'Run: dotnet test'
