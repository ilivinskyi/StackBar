$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "1/3 Building StackBar Release payload..." -ForegroundColor Cyan
$publishAppDir = Join-Path $root "out\app"
if (Test-Path $publishAppDir) { Remove-Item $publishAppDir -Recurse -Force }
dotnet publish "$root\src\TaskbarGrouper\TaskbarGrouper.csproj" -c Release -o $publishAppDir

Write-Host "2/3 Packaging payload.zip for Installer..." -ForegroundColor Cyan
$payloadDir = Join-Path $root "src\StackBar.Installer\Payload"
New-Item -ItemType Directory -Force -Path $payloadDir | Out-Null
$payloadZip = Join-Path $payloadDir "payload.zip"
if (Test-Path $payloadZip) { Remove-Item $payloadZip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($publishAppDir, $payloadZip)

Write-Host "3/3 Building StackBar-Setup.exe Installer..." -ForegroundColor Cyan
$installerOutDir = Join-Path $root "installer"
if (Test-Path $installerOutDir) { Remove-Item $installerOutDir -Recurse -Force }
dotnet build "$root\src\StackBar.Installer\StackBar.Installer.csproj" -c Release -o $installerOutDir

Remove-Item $payloadZip -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $root "out") -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Done! Installer generated at: $installerOutDir\StackBar-Setup.exe" -ForegroundColor Green
