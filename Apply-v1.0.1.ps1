$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$versionFile = Join-Path $root 'Directory.Build.props'
if (-not (Test-Path -LiteralPath $versionFile)) {
    throw "Run this script from the extracted AxetosOS.Products.NES v1.0.1 patch folder."
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

Write-Host "AxetosOS Products / NES v1.0.1 applied. Removed $deleted obsolete source file(s)."
