<#
.SYNOPSIS
  Prepara o PostgreSQL embutido do Sistema PDV no computador principal, sem intervenção do técnico.
.DESCRIPTION
  Executado pelo instalador (como administrador) quando o componente "PostgreSQL" é escolhido:
  1. cria o cluster (initdb, UTF-8, autenticação scram-sha-256, só localhost);
  2. registra e inicia o serviço do Windows (conta NetworkService) — ou só inicia com -NoService (testes);
  3. cria o usuário "pdv" e o banco "sistema_pdv" com senhas aleatórias;
  4. grava a conexão em server.json (o servidor do PDV cifra a senha com DPAPI ao iniciar) e guarda a senha do
     superusuário cifrada com DPAPI para o suporte.
  Se o cluster já existe (atualização), não altera nada.
#>
param(
    [Parameter(Mandatory)] [string]$PgBin,
    [Parameter(Mandatory)] [string]$DataDir,
    [Parameter(Mandatory)] [string]$ServerJson,
    [int]$Port = 5432,
    [string]$ServiceName = 'SistemaPDV-PostgreSQL',
    [switch]$NoService
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security

function New-Secret {
    $bytes = New-Object byte[] 24
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()   # compatível com o Windows PowerShell 5.1
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    [Convert]::ToBase64String($bytes).Replace('+', 'A').Replace('/', 'B').TrimEnd('=')
}

# Mesmo formato do Dpapi.cs (escopo da máquina, propósito do server.json).
function Protect([string]$value) {
    $data = [System.Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($value),
        [Text.Encoding]::UTF8.GetBytes('SistemaPDV.Server.v1'), [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
    'dpapi:' + [Convert]::ToBase64String($data)
}

function Psql([string]$sql, [string]$database = 'postgres') {
    & (Join-Path $PgBin 'psql.exe') -h localhost -p $Port -U postgres -d $database -v ON_ERROR_STOP=1 -q -c $sql
    if ($LASTEXITCODE -ne 0) { throw "psql falhou: $sql" }
}

if (Test-Path (Join-Path $DataDir 'PG_VERSION')) {
    Write-Output "Cluster já existe em $DataDir; nada a fazer."
    exit 0
}

New-Item -ItemType Directory -Force $DataDir | Out-Null
$superPassword = New-Secret
$appPassword = New-Secret
$pwFile = Join-Path ([IO.Path]::GetTempPath()) ("pdv-pg-" + [guid]::NewGuid().ToString('N') + '.txt')
try {
    Set-Content -Path $pwFile -Value $superPassword -NoNewline -Encoding ASCII
    & (Join-Path $PgBin 'initdb.exe') -D $DataDir -U postgres --pwfile=$pwFile -A scram-sha-256 -E UTF8 `
        --locale-provider=builtin --builtin-locale=C.UTF-8 --locale=C | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'initdb falhou.' }
}
finally {
    Remove-Item $pwFile -Force -ErrorAction SilentlyContinue
}

# Só conexões locais: o servidor do PDV é o único cliente.
Add-Content -Path (Join-Path $DataDir 'postgresql.conf') -Value "`nlisten_addresses = 'localhost'`nport = $Port`n"

$env:PGPASSWORD = $superPassword
if ($NoService) {
    # pg_ctl start deixa o postgres herdando a saída: sem redirecionar, e esperando só o pg_ctl.
    $ctl = Start-Process -FilePath (Join-Path $PgBin 'pg_ctl.exe') -ArgumentList @('-D', "`"$DataDir`"", '-l', "`"$(Join-Path $DataDir 'pg.log')`"", '-w', 'start') `
        -WindowStyle Hidden -PassThru
    $ctl.WaitForExit()
}
else {
    & icacls.exe $DataDir /grant '*S-1-5-20:(OI)(CI)F' /T /Q | Out-Null   # NetworkService
    & (Join-Path $PgBin 'pg_ctl.exe') register -N $ServiceName -D $DataDir -S auto -U 'NT AUTHORITY\NetworkService' -w | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Não foi possível registrar o serviço do PostgreSQL.' }
    Start-Service $ServiceName
}

$deadline = (Get-Date).AddSeconds(60)
do {
    & (Join-Path $PgBin 'pg_isready.exe') -h localhost -p $Port -q
    if ($LASTEXITCODE -eq 0) { break }
    Start-Sleep -Milliseconds 500
} while ((Get-Date) -lt $deadline)
if ($LASTEXITCODE -ne 0) { throw 'O PostgreSQL não respondeu.' }

Psql "CREATE ROLE pdv LOGIN CREATEDB PASSWORD '$appPassword';"
Psql 'CREATE DATABASE sistema_pdv OWNER pdv;'
Remove-Item Env:\PGPASSWORD

New-Item -ItemType Directory -Force (Split-Path $ServerJson) | Out-Null
$connection = "Host=localhost;Port=$Port;Database=sistema_pdv;Username=pdv;Password=$appPassword;Timeout=10"
$config = if (Test-Path $ServerJson) { Get-Content $ServerJson -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
$config | Add-Member -Force -NotePropertyName ConnectionStrings -NotePropertyValue ([pscustomobject]@{ Pdv = $connection })
$config | Add-Member -Force -NotePropertyName Postgres -NotePropertyValue ([pscustomobject]@{
    DataDirectory = $DataDir; Service = $(if ($NoService) { $null } else { $ServiceName }); AdminPassword = (Protect $superPassword) })
$config | ConvertTo-Json -Depth 5 | Set-Content -Path $ServerJson -Encoding UTF8
Write-Output "PostgreSQL pronto na porta $Port (dados em $DataDir)."
