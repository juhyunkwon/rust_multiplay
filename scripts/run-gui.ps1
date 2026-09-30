param(
    [switch]$Two
)
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

$Exe = Join-Path $Root "_bin\X0.MultiPlay.Gui\X0.MultiPlay.Gui.exe"
$proj = Join-Path $Root "csharp\X0.MultiPlay.Gui\X0.MultiPlay.Gui.csproj"
if (-not (Test-Path $Exe)) {
    Write-Host "C# GUI 없음 — 게시합니다."
    $out = Join-Path $Root "_bin\X0.MultiPlay.Gui"
    dotnet publish $proj -c Release -o $out --verbosity minimal
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if (-not (Test-Path $Exe)) {
    throw "빌드 후에도 $Exe 가 없습니다."
}

$wd = Split-Path $Exe
Write-Host "C# 클라 GUI: $Exe"
Write-Host "홀펀치: 창 2개, 계정 A / B, 이름만 다르게 같은 룸# → 선택 연결+홀펀치. 로그 [pkt tx/rx udp] HOLEPUNCH"
Start-Process -FilePath $Exe -WorkingDirectory $wd
if ($Two) {
    Start-Sleep -Seconds 1
    Start-Process -FilePath $Exe -WorkingDirectory $wd
}
