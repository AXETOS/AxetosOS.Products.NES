$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$testFile = Join-Path $root 'tests\AxetosOS.Products.NES.Tests\VirtualHardwareOutputRouteDeliveryTests.cs'

if (-not (Test-Path -LiteralPath $testFile)) {
    throw "v1.4.0 test-semantics hotfix cannot find: $testFile"
}

$lines = [System.Collections.Generic.List[string]]::new()
Get-Content -LiteralPath $testFile | ForEach-Object { [void]$lines.Add($_) }

function Patch-ActivationExpectation {
    param(
        [Parameter(Mandatory=$true)][string]$OldName,
        [Parameter(Mandatory=$true)][string]$NewName
    )

    $methodIndex = -1
    $alreadyRenamed = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match [regex]::Escape("public void $OldName")) {
            $methodIndex = $i
            break
        }
        if ($lines[$i] -match [regex]::Escape("public void $NewName")) {
            $methodIndex = $i
            $alreadyRenamed = $true
            break
        }
    }

    if ($methodIndex -lt 0) {
        throw "Could not find expected regression test '$OldName' in $testFile"
    }

    $endIndex = $lines.Count
    for ($i = $methodIndex + 1; $i -lt $lines.Count; $i++) {
        if ($lines[$i].Trim() -eq '[Fact]') {
            $endIndex = $i
            break
        }
    }

    if (-not $alreadyRenamed) {
        $lines[$methodIndex] = $lines[$methodIndex].Replace($OldName, $NewName)
    }

    $expectationIndex = -1
    $alreadyZero = $false
    for ($i = $methodIndex + 1; $i -lt $endIndex; $i++) {
        if ($lines[$i] -match 'Assert\.Equal\(1\s*,') {
            if ($expectationIndex -ge 0) {
                throw "Found more than one Assert.Equal(1, ...) in '$OldName'; refusing an ambiguous edit."
            }
            $expectationIndex = $i
        }
        elseif ($lines[$i] -match 'Assert\.Equal\(0\s*,') {
            $alreadyZero = $true
        }
    }

    if ($expectationIndex -ge 0) {
        $lines[$expectationIndex] = $lines[$expectationIndex] -replace 'Assert\.Equal\(1\s*,', 'Assert.Equal(0,'
    }
    elseif (-not $alreadyZero) {
        throw "Could not find the expected activation-count assertion in '$OldName'."
    }
}

Patch-ActivationExpectation -OldName 'Latched_data_delivery_is_processed_by_the_chip_while_output_remains_unchanged' -NewName 'Latched_data_delivery_reaches_pin_without_waking_inactive_chip_or_changing_output'

Patch-ActivationExpectation -OldName 'Sram_address_delivery_is_processed_by_the_chip_while_deselected_output_remains_unchanged' -NewName 'Sram_address_delivery_reaches_pin_without_waking_deselected_chip_or_changing_output'

Set-Content -LiteralPath $testFile -Value $lines -Encoding UTF8

Write-Host 'AxetosOS Products / NES v1.4.0 test-semantics hotfix applied.'
Write-Host 'Updated stale output-route tests to the v1.4.0 chip-owned activation contract.'
Write-Host 'Runtime code is unchanged.'
Write-Host 'Run: dotnet test'
