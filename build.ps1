$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "1/3 Compiling StackBar (.NET Framework 4.8)..." -ForegroundColor Cyan
$buildOutDir = Join-Path $root "src\TaskbarGrouper\bin\Release\net48"
dotnet build "$root\src\TaskbarGrouper\TaskbarGrouper.csproj" -c Release --no-restore

Write-Host "2/3 Staging standalone artifacts..." -ForegroundColor Cyan
$artifactsDir = Join-Path $root "artifacts"
if (Test-Path $artifactsDir) {
    Get-ChildItem -Path $artifactsDir -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null

$appExe = Join-Path $buildOutDir "StackBar.exe"
# Standard App
Copy-Item $appExe (Join-Path $artifactsDir "StackBar.exe") -Force
# Setup / Installer
Copy-Item $appExe (Join-Path $artifactsDir "StackBar-Setup.exe") -Force

Write-Host "3/3 Building StackBar Portable package..." -ForegroundColor Cyan
$portableDir = Join-Path $artifactsDir "StackBar-Portable"
New-Item -ItemType Directory -Force -Path $portableDir | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $portableDir "data") | Out-Null

# Copy exe and flag file
Copy-Item $appExe (Join-Path $portableDir "StackBar-Portable.exe") -Force
Set-Content -Path (Join-Path $portableDir ".portable") -Value "StackBar Portable Mode Flag"

# Also package as zip for clean portability
$zipPath = Join-Path $artifactsDir "StackBar-Portable.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($portableDir, $zipPath)

Write-Host "Success! Created artifacts in: $artifactsDir" -ForegroundColor Green
Write-Host "  - StackBar.exe (Standard App)" -ForegroundColor Green
Write-Host "  - StackBar-Setup.exe (Self-Installer / Update / Uninstall)" -ForegroundColor Green
Write-Host "  - StackBar-Portable/ (Folder: runs isolated with ./data/ directory)" -ForegroundColor Green
Write-Host "  - StackBar-Portable.zip (Zipped Portable distribution)" -ForegroundColor Green
