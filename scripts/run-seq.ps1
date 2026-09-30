$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

$suite = if ($args.Count -ge 1) { $args[0] } else { "p2p" }
$Bin = Join-Path $Root "_bin"
$Exe = Join-Path $Bin "sequence-tester.exe"
if (-not (Test-Path $Exe)) {
    & (Join-Path $PSScriptRoot "build.ps1")
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# 서버 꺼져 있으면 _bin exe 로 띄움. 테스트 끝나도 안 끔
$listening = $null
try {
    $listening = Get-NetTCPConnection -LocalPort 19050 -State Listen -ErrorAction SilentlyContinue
} catch { }

if (-not $listening) {
    $server = Join-Path $Bin "server-host.exe"
    if (Test-Path $server) {
        Start-Process -FilePath $server -WorkingDirectory $Root
        Start-Sleep -Seconds 2
    }
}

& $Exe --cli $suite
