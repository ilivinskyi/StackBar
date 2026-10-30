$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "1/2 Compiling StackBar (.NET Framework 4.8)..." -ForegroundColor Cyan
$buildOutDir = Join-Path $root "src\TaskbarGrouper\bin\Release\net48"
dotnet build "$root\src\TaskbarGrouper\TaskbarGrouper.csproj" -c Release --no-restore

Write-Host "2/2 Staging standalone artifacts..." -ForegroundColor Cyan
$artifactsDir = Join-Path $root "artifacts"
if (Test-Path $artifactsDir) { Remove-Item $artifactsDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null

$appExe = Join-Path $buildOutDir "StackBar.exe"
Copy-Item $appExe (Join-Path $artifactsDir "StackBar.exe") -Force
Copy-Item $appExe (Join-Path $artifactsDir "StackBar-Setup.exe") -Force

Write-Host "Success! Created artifacts in: $artifactsDir" -ForegroundColor Green
Write-Host "  - StackBar.exe (Portable / App)" -ForegroundColor Green
Write-Host "  - StackBar-Setup.exe (Self-Installer)" -ForegroundColor Green
