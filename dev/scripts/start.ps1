param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $NoBuild) { & (Join-Path $PSScriptRoot 'build.ps1') }
$exe = Join-Path $projectRoot 'src/CapchunScreen/bin/Debug/net10.0-windows10.0.19041.0/CapchunScreen.exe'
Start-Process -FilePath $exe -WorkingDirectory $projectRoot
