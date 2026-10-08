#Requires -Version 5.1
# Build the portable Chatterbox zip: self-contained win-x64 publish, zipped
# into releases\Chatterbox-<version>-win-x64.zip.
[CmdletBinding()]
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'src\Chatterbox\Chatterbox.csproj'
[xml]$x = Get-Content $proj
$ver = ($x.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if (-not $ver) { throw 'No <Version> in the csproj.' }
$out = Join-Path $PSScriptRoot 'publish'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
Write-Host "==> Publishing Chatterbox $ver ($Configuration, win-x64, self-contained)" -ForegroundColor Cyan
dotnet publish $proj -c $Configuration -r win-x64 --self-contained -o $out -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=embedded
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
$releases = Join-Path $PSScriptRoot 'releases'
New-Item -ItemType Directory -Force $releases | Out-Null
$zip = Join-Path $releases "Chatterbox-$ver-win-x64.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "==> Done: $zip ($mb MB)" -ForegroundColor Green
