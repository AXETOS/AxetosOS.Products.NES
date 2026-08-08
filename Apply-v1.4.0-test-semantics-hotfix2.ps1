$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$testFile = Join-Path $root 'tests\AxetosOS.Products.NES.Tests\VirtualHardwareOutputRouteDeliveryTests.cs'

if (-not (Test-Path -LiteralPath $testFile)) {
    throw "v1.4.0 test-semantics hotfix2 cannot find: $testFile"
}

$lines = [System.Collections.Generic.List[string]]::new()
Get-Content -LiteralPath $testFile | ForEach-Object { [void]$lines.Add($_) }

function Patch-ActivationExpectation {
    param(
        [Parameter(Mandatory=$true)][string]$OldName,
        [Parameter(Mandatory=$true)][string]$NewName
    )

    $methodIndex = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $trimmed = $lines[$i].Trim()
        if ($trimmed -eq "public void $OldName()" -or $trimmed -eq "public void $NewName()") {
            $methodIndex = $i
            break
        }
    }

    if ($methodIndex -lt 0) {
        throw "Could not find expected regression test '$OldName' (or already-renamed '$NewName') in $testFile"
    }

    $endIndex = $lines.Count
    for ($i = $methodIndex + 1; $i -lt $lines.Count; $i++) {
        if ($lines[$i].Trim() -eq '[Fact]') {
            $endIndex = $i
            break
        }
    }

    if ($lines[$methodIndex].Contains($OldName)) {
        $lines[$methodIndex] = $lines[$methodIndex].Replace($OldName, $NewName)
    }

    $counterAssertionIndex = -1
    for ($i = $methodIndex + 1; $i -lt $endIndex; $i++) {
        if ($lines[$i].Contains('counters.ComponentEvaluations')) {
            if ($counterAssertionIndex -ge 0) {
                throw "Found more than one ComponentEvaluations assertion in '$NewName'; refusing an ambiguous edit."
            }
            $counterAssertionIndex = $i
        }
    }

    if ($counterAssertionIndex -lt 0) {
        throw "Could not find counters.ComponentEvaluations assertion in '$NewName'."
    }

    $counterLine = $lines[$counterAssertionIndex]
    if ($counterLine -match 'Assert\.Equal\(0(?:UL|U|L)?\s*,\s*counters\.ComponentEvaluations\s*\);') {
        return
    }

    if ($counterLine -match 'Assert\.Equal\(1(?:UL|U|L)?\s*,\s*counters\.ComponentEvaluations\s*\);') {
        $indent = $counterLine.Substring(0, $counterLine.Length - $counterLine.TrimStart().Length)
        $lines[$counterAssertionIndex] = $indent + 'Assert.Equal(0UL, counters.ComponentEvaluations);'
        return
    }

    throw "Unexpected ComponentEvaluations assertion in '$NewName': $counterLine"
}

Patch-ActivationExpectation `
    -OldName 'Latched_data_delivery_is_processed_by_the_chip_while_output_remains_unchanged' `
    -NewName 'Latched_data_delivery_reaches_pin_without_waking_inactive_chip_or_changing_output'

Patch-ActivationExpectation `
    -OldName 'Sram_address_delivery_is_processed_by_the_chip_while_deselected_output_remains_unchanged' `
    -NewName 'Sram_address_delivery_reaches_pin_without_waking_deselected_chip_or_changing_output'

Set-Content -LiteralPath $testFile -Value $lines -Encoding UTF8

# Verify the exact semantic contract after writing.
$text = Get-Content -LiteralPath $testFile -Raw
if ($text -notmatch 'Latched_data_delivery_reaches_pin_without_waking_inactive_chip_or_changing_output') {
    throw 'Latch regression test was not renamed as expected.'
}
if ($text -notmatch 'Sram_address_delivery_reaches_pin_without_waking_deselected_chip_or_changing_output') {
    throw 'SRAM regression test was not renamed as expected.'
}
$zeroCount = ([regex]::Matches($text, 'Assert\.Equal\(0UL,\s*counters\.ComponentEvaluations\s*\);')).Count
if ($zeroCount -lt 2) {
    throw "Expected at least two zero ComponentEvaluations assertions after hotfix; found $zeroCount."
}

Write-Host 'AxetosOS Products / NES v1.4.0 test-semantics hotfix2 applied.'
Write-Host 'Both inactive-package tests now require physical pin delivery with zero internal chip evaluations.'
Write-Host 'Runtime/emulator source is unchanged.'
Write-Host 'Run: dotnet test'
