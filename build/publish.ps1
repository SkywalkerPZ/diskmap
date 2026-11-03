# Builds the portable release zip: artifacts\DiskMap-<version>-win-x64.zip
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

if (-not $env:NUGET_PACKAGES)   { $env:NUGET_PACKAGES   = 'D:\nuget\packages' }
if (-not $env:DOTNET_CLI_HOME)  { $env:DOTNET_CLI_HOME  = 'D:\dotnet-home' }

# global.json needs .NET 10; prefer the D: install when present, else dotnet on PATH.
$dotnet = if (Test-Path 'D:\dotnet\dotnet.exe') { 'D:\dotnet\dotnet.exe' } else { 'dotnet' }

$out = Join-Path $repo 'artifacts\DiskMap'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

& $dotnet publish src/DiskMap.App -c Release -r win-x64 --self-contained -p:WindowsAppSDKSelfContained=true -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$props = [xml](Get-Content (Join-Path $repo 'Directory.Build.props'))
$version = $props.Project.PropertyGroup.Version
if (-not $version) { throw 'No <Version> in Directory.Build.props' }

$zip = Join-Path $repo "artifacts\DiskMap-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
Write-Host "Created $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
