param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
dotnet build (Join-Path $projectRoot 'src/CapchunScreen/CapchunScreen.csproj') -c $Configuration
if ($LASTEXITCODE -ne 0) { throw 'ビルドに失敗しました。' }
