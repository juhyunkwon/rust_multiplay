$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

$Garbage = Join-Path $Root "_garbage"
$Bin = Join-Path $Root "_bin"
$Log = Join-Path $Root "_log"
New-Item -ItemType Directory -Force -Path $Garbage, $Bin, $Log | Out-Null

Write-Host "== rust workspace -> $Garbage"
$env:CARGO_TARGET_DIR = $Garbage
cargo build --workspace
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$names = @(
    "server-host",
    "sequence-tester",
    "log-viewer",
    "signaling-server",
    "dedicated-server"
)
$debug = Join-Path $Garbage "debug"
foreach ($name in $names) {
    $src = Join-Path $debug "$name.exe"
    if (-not (Test-Path $src)) {
        Write-Warning "missing $src"
        continue
    }
    $dst = Join-Path $Bin "$name.exe"
    try {
        Copy-Item $src $dst -Force
        Write-Host "  copied $name.exe"
    }
    catch {
        $alt = Join-Path $Bin "$name.new.exe"
        try {
            Copy-Item $src $alt -Force
            Write-Warning "locked $name.exe — copied $name.new.exe instead. Close the running process, then rename or rerun this script."
        }
        catch {
            Write-Warning "copy skip ${name}: file in use. Close it and rerun. $_"
        }
    }
}

$csSln = Join-Path $Root "csharp\X0.MultiPlay.sln"
if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    if (Test-Path $csSln) {
        Write-Host "== csharp GUI + CLI"
        $guiProj = Join-Path $Root "csharp\X0.MultiPlay.Gui\X0.MultiPlay.Gui.csproj"
        $cliProj = Join-Path $Root "csharp\X0.MultiPlay.Cli\X0.MultiPlay.Cli.csproj"
        $guiOut = Join-Path $Bin "X0.MultiPlay.Gui"
        $cliOut = Join-Path $Bin "X0.MultiPlay.Cli"
        dotnet publish $guiProj -c Release -o $guiOut --verbosity minimal
        if ($LASTEXITCODE -ne 0) {
            $guiAlt = Join-Path $Bin "X0.MultiPlay.Gui.new"
            Write-Warning "GUI publish locked — writing $guiAlt"
            dotnet publish $guiProj -c Release -o $guiAlt --verbosity minimal
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
            Write-Host "  gui : $guiAlt\X0.MultiPlay.Gui.exe  (close the old GUI then rename)"
        }
        dotnet publish $cliProj -c Release -o $cliOut --verbosity minimal
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        Write-Host "  gui : $guiOut\X0.MultiPlay.Gui.exe"
        Write-Host "  cli : $cliOut\X0.MultiPlay.Cli.exe"
    }
}
else {
    Write-Host "dotnet 없음 — csharp 빌드 생략"
}

Write-Host ""
Write-Host "ok"
Write-Host "  server : $Bin\server-host.exe"
Write-Host "  seq    : $Bin\sequence-tester.exe"
Write-Host "  gui    : $Bin\X0.MultiPlay.Gui\X0.MultiPlay.Gui.exe"
Write-Host "  log    : $Log"
Write-Host "실행:  .\scripts\run-server.ps1"
Write-Host "클라:  .\scripts\run-gui.ps1"
