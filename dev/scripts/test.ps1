param([string]$FfmpegPath, [string]$FfprobePath, [string]$Version)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $Version) { $Version = 'v' + ([xml](Get-Content (Join-Path $projectRoot 'src/CapchunScreen/CapchunScreen.csproj') -Raw)).Project.PropertyGroup.Version }
if ($Version -notmatch '^v\d+\.\d+\.\d+$') { throw '対象版はvX.Y.Zで指定してください。' }
$runDir = Join-Path $projectRoot ('dev/history/' + $Version + '/logs/tests-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $runDir -Force | Out-Null
dotnet build (Join-Path $projectRoot 'tests/CapchunScreen.Tests/CapchunScreen.Tests.csproj') -c Debug | Tee-Object -FilePath (Join-Path $runDir 'build.log')
if ($LASTEXITCODE -ne 0) { throw 'テストのビルドに失敗しました。' }
$testArgs = @('--output', $runDir)
if ($FfmpegPath) { $testArgs += @('--ffmpeg', $FfmpegPath) }
if ($FfprobePath) { $testArgs += @('--ffprobe', $FfprobePath) }
& (Join-Path $projectRoot 'tests/CapchunScreen.Tests/bin/Debug/net10.0-windows10.0.19041.0/CapchunScreen.Tests.exe') @testArgs | Tee-Object -FilePath (Join-Path $runDir 'test.log')
if ($LASTEXITCODE -ne 0) { throw "テストに失敗しました: $runDir" }
Write-Output "検証結果: $runDir"
