<#
.SYNOPSIS
  Builds OmenMon (Release) and packs installer\OmenMon.iss into installer\Output\OmenMon-Setup-<version>.exe.
.PARAMETER SkipBuild
  Use the existing Bin\OmenMon.exe instead of building.
#>
param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# Find ISCC first, so a missing Inno Setup is reported before the build
$candidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")
foreach ($key in 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
                 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
                 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1') {
    $dir = (Get-ItemProperty $key -ErrorAction SilentlyContinue).InstallLocation
    if ($dir) { $candidates += (Join-Path $dir 'ISCC.exe') }
}
$onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if ($onPath) { $candidates += $onPath.Source }
$iscc = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) {
    Write-Host 'Inno Setup 6 (ISCC.exe) was not found. Install it (winget install JRSoftware.InnoSetup) and run this again.' -ForegroundColor Red
    exit 2
}

if (-not $SkipBuild) {
    Push-Location $root
    try {
        dotnet build OmenMon.sln -c Release --no-incremental
        if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }
    } finally { Pop-Location }
}
if (-not (Test-Path "$root\Bin\OmenMon.exe")) { throw 'Bin\OmenMon.exe is missing: build first.' }
if (-not (Test-Path "$root\Bin\OmenMon.Core.dll")) { throw 'Bin\OmenMon.Core.dll is missing: build first.' }

& $iscc "$PSScriptRoot\OmenMon.iss"
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed.' }
Get-ChildItem "$PSScriptRoot\Output\OmenMon-Setup-*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1 |
    ForEach-Object { Write-Host "Setup: $($_.FullName)" -ForegroundColor Green }
