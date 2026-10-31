# -Version X.Y.Z stamps the product version (the release workflow passes the tag); omit it to use the csproj default
param([string]$Version)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = "$root\src\TaskbarGrouper\TaskbarGrouper.csproj"
$versionArgs = @()
if ($Version) { $versionArgs += "-p:Version=$Version" }

function Build-StackBar([string]$label, [string]$outDir, [string[]]$extraArgs) {
    Write-Host $label -ForegroundColor Cyan
    # Out-Host keeps dotnet's log on screen instead of in the function's return value
    dotnet build $project -c Release --no-restore @versionArgs @extraArgs | Out-Host
    # $ErrorActionPreference does not cover native exit codes in Windows PowerShell 5.1:
    # without this check a failed build would package the previous exe and still print "Success!"
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit code $LASTEXITCODE); nothing was packaged." }
    $exe = Join-Path $root "$outDir\StackBar.exe"
    if (-not (Test-Path $exe)) { throw "Build output not found: $exe" }
    return $exe
}

$appExe = Build-StackBar "1/3 Compiling StackBar (.NET Framework 4.8)..." "src\TaskbarGrouper\bin\Release\net48" @()
# The portable build runs portable under any file name, so it needs no .portable marker or data folder next to it
$portableExe = Build-StackBar "2/3 Compiling StackBar Portable Edition..." "src\TaskbarGrouper\bin\Portable\Release\net48" @("-p:StackBarEdition=Portable")

Write-Host "3/3 Staging artifacts..." -ForegroundColor Cyan
$artifactsDir = Join-Path $root "artifacts"
if (Test-Path $artifactsDir) {
    Get-ChildItem -Path $artifactsDir -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null

Copy-Item $appExe (Join-Path $artifactsDir "StackBar.exe") -Force
Copy-Item $appExe (Join-Path $artifactsDir "StackBar-Setup.exe") -Force
Copy-Item $portableExe (Join-Path $artifactsDir "StackBar-Portable.exe") -Force

Write-Host "Success! Created artifacts in: $artifactsDir" -ForegroundColor Green
Write-Host "  - StackBar.exe (Standard App)" -ForegroundColor Green
Write-Host "  - StackBar-Setup.exe (Self-Installer / Update / Uninstall)" -ForegroundColor Green
Write-Host "  - StackBar-Portable.exe (Portable Edition: keeps everything in a data folder next to itself)" -ForegroundColor Green
