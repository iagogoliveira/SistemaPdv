<#
.SYNOPSIS
  Publica o Sistema PDV (self-contained, win-x64) e gera o instalador com Inno Setup 6.
.EXAMPLE
  .\installer\build.ps1 -Version 0.5.0
#>
param(
    [string]$Version = "0.5.0",
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$publishDir = Join-Path $root "artifacts\publish"

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

foreach ($app in @(@{ Project = "src\Pdv.Desktop\Pdv.Desktop.csproj"; Out = "caixa" },
                   @{ Project = "src\Pdv.Server\Pdv.Server.csproj"; Out = "servidor" })) {
    dotnet publish (Join-Path $root $app.Project) `
        -c Release -r win-x64 --self-contained true `
        -p:Version=$Version -p:DebugType=none `
        -o (Join-Path $publishDir $app.Out)
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou ($($app.Project))." }
}

if ($SkipInstaller) {
    Write-Host "Publicação pronta em $publishDir"
    return
}

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    throw "Inno Setup 6 não encontrado. Instale em https://jrsoftware.org/isdl.php ou use -SkipInstaller."
}

& $iscc "/DAppVersion=$Version" "/DPublishDir=$publishDir" (Join-Path $PSScriptRoot "SistemaPDV.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC falhou." }

Write-Host "Instalador gerado em $(Join-Path $root 'artifacts\installer')"
