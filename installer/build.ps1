<#
.SYNOPSIS
  Publica o Sistema PDV (self-contained, win-x64) e gera o instalador com Inno Setup 6.
.EXAMPLE
  .\installer\build.ps1 -Version 0.7.0
#>
param(
    [string]$Version = "0.7.0",
    # Zip dos binários do PostgreSQL para Windows (postgresql-17.x-windows-x64-binaries.zip, da EDB): embute o banco.
    [string]$PostgresZip,
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

$isccDefines = @("/DAppVersion=$Version", "/DPublishDir=$publishDir")
if ($PostgresZip) {
    $extract = Join-Path $root "artifacts\pgsql-zip"
    if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
    Expand-Archive -Path $PostgresZip -DestinationPath $extract
    $pgsql = Join-Path $extract "pgsql"
    # Só o necessário para rodar o servidor e o backup (sem pgAdmin, documentação, símbolos e cabeçalhos).
    $target = Join-Path $publishDir "postgres"
    foreach ($dir in @("bin", "lib", "share")) { Copy-Item (Join-Path $pgsql $dir) (Join-Path $target $dir) -Recurse }
    Get-ChildItem (Join-Path $target "bin") -Filter "pgAdmin*" | Remove-Item -Recurse -Force
    Remove-Item $extract -Recurse -Force
    $isccDefines += "/DWithPostgres"
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

& $iscc @isccDefines (Join-Path $PSScriptRoot "SistemaPDV.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC falhou." }

Write-Host "Instalador gerado em $(Join-Path $root 'artifacts\installer')"
