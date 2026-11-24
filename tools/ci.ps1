<#
.SYNOPSIS
  Run the same checks as .github/workflows/ci.yml on this machine, before pushing.

.DESCRIPTION
  Restores, builds in Release and runs the tests, stopping at the first failure.
  The Identity tests need Docker Desktop, and the AppHost must be off (12 GB RAM rule).
  Not identical to CI: this is Windows with your existing package cache, CI is a clean Linux machine.

.EXAMPLE
  .\tools\ci.ps1
  .\tools\ci.ps1 -SkipContainers    # only the tests that need no Docker
#>
param([switch]$SkipContainers)

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

# Native programs don't throw in PowerShell 5.1: a non-zero exit code has to be checked by hand.
function Invoke-Step([string]$Name, [string[]]$Arguments) {
    Write-Host "`n== $Name" -ForegroundColor Cyan
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { Write-Host "FAILED: $Name" -ForegroundColor Red; exit $LASTEXITCODE }
}

$projects = @('tests/FoodDelivery.ArchitectureTests', 'tests/FoodDelivery.Catalog.Tests')
if (-not $SkipContainers) {
    if (Get-Process FoodDelivery.AppHost -ErrorAction SilentlyContinue) {
        Write-Host 'Stop the AppHost first: it and the test containers do not fit in RAM together.' -ForegroundColor Red
        exit 1
    }
    $projects += 'tests/FoodDelivery.Identity.Tests'
}

Invoke-Step 'Restore' @('restore')
Invoke-Step 'Build' @('build', '--configuration', 'Release', '--no-restore', '-nodeReuse:false')
foreach ($project in $projects) {
    Invoke-Step "Test $project" @('test', '--project', $project, '--configuration', 'Release', '--no-build')
}

Write-Host "`nAll checks passed." -ForegroundColor Green
