param([switch]$NoBuild, [string]$Version)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $Version) { $Version = 'v' + ([xml](Get-Content (Join-Path $projectRoot 'src/CapchunScreen/CapchunScreen.csproj') -Raw)).Project.PropertyGroup.Version }
if ($Version -notmatch '^v\d+\.\d+\.\d+$') { throw '対象版はvX.Y.Zで指定してください。' }
if (-not $NoBuild) { & (Join-Path $PSScriptRoot 'build.ps1') }
$runDirectory = Join-Path $projectRoot ('dev/history/' + $Version + '/logs/desktop-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
$report = Join-Path $runDirectory 'desktop-status.json'
$application = Join-Path $projectRoot 'src/CapchunScreen/bin/Debug/net10.0-windows10.0.19041.0/CapchunWindow.dll'
dotnet $application --desktop-status --settings-dir $runDirectory --diagnostic-output $report
$result = $LASTEXITCODE
if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report }
Write-Output "診断結果: $report"
if ($result -ne 0) { throw 'デスクトップへのアクセスを確認できません。UIテストを開始せず、通常の画面へ戻ってから再確認してください。' }
