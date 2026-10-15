param([switch]$NoBuild, [string]$FfmpegPath, [string]$SettingsDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $NoBuild) { & (Join-Path $PSScriptRoot 'build.ps1') }
$exe = Join-Path $projectRoot 'src/CapchunScreen/bin/Debug/net10.0-windows10.0.19041.0/CapchunWindow.exe'
$startInfo = [System.Diagnostics.ProcessStartInfo]::new($exe)
$startInfo.WorkingDirectory = $projectRoot
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
if ($FfmpegPath) { $startInfo.ArgumentList.Add('--ffmpeg'); $startInfo.ArgumentList.Add($FfmpegPath) }
if ($SettingsDirectory) { $startInfo.ArgumentList.Add('--settings-dir'); $startInfo.ArgumentList.Add($SettingsDirectory) }
[System.Diagnostics.Process]::Start($startInfo) | Select-Object Id,ProcessName
