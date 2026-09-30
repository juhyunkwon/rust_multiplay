$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

$Config = Join-Path $Root "config\appsettings.json"
if (-not (Test-Path $Config) -and -not $env:X0_DB_CONNECTION) {
    Copy-Item (Join-Path $Root "config\appsettings.example.json") $Config
    Write-Warning "config\appsettings.json 을 예제에서 만들었습니다. ConnectionStrings.Default 에 DB 계정을 넣고 다시 실행하세요."
    exit 1
}

$Bin = Join-Path $Root "_bin"
$Exe = Join-Path $Bin "server-host.exe"
if (-not (Test-Path $Exe)) {
    Write-Host "server-host.exe 없음 — 빌드합니다."
    & (Join-Path $PSScriptRoot "build.ps1")
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if (-not (Test-Path $Exe)) {
    throw "빌드 후에도 $Exe 가 없습니다."
}

$alive = Get-Process -Name "server-host" -ErrorAction SilentlyContinue
if ($alive) {
    Write-Host "이미 실행 중: server-host.exe PID=$(( $alive | ForEach-Object Id ) -join ',')"
    Write-Host "작업 표시줄에 콘솔이 없으면 숨김 기동입니다. 종료 후 이 스크립트를 다시 실행하세요."
    exit 0
}

Write-Host "server-host 기동: $Exe"
Write-Host "시그널링 19050/19051  Dedicated 19060  (config/appsettings.json)"
Start-Process -FilePath $Exe -WorkingDirectory $Root
Write-Host "작업 표시줄에 'X0 ServerHost' 콘솔이 나타납니다."
