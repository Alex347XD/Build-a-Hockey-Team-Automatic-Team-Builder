# Publishes a single self-contained exe and copies it to ..\dist\HockeyRoller.exe
$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "HockeyRoller.csproj"
$outDir  = Join-Path $PSScriptRoot "..\dist"

Write-Host "Publishing single-file exe..." -ForegroundColor Cyan
dotnet publish $project -c Release -r win-x64 --self-contained true `
    /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$exe = Join-Path $PSScriptRoot "bin\Release\net9.0-windows10.0.19041.0\win-x64\publish\HockeyRoller.exe"
Copy-Item $exe (Join-Path $outDir "HockeyRoller.exe") -Force

Write-Host "Done: $(Join-Path $outDir 'HockeyRoller.exe')" -ForegroundColor Green
