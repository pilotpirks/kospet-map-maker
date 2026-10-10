<#
.SYNOPSIS
  Build Kospet Map Maker: tests -> self-contained single-file executables -> (optional) GitHub release.

.EXAMPLE
  .\build.ps1                     # tests + publish win-x64 and linux-x64
  .\build.ps1 -SkipTests          # publish only
  .\build.ps1 -Runtimes win-x64   # one platform
  .\build.ps1 -Release            # + tag v<Version>, push it and create a GitHub release (needs gh)
  .\build.ps1 -Version 1.1.0 -Release

  Output: publish\<runtime>\ and publish\release\KospetMapMaker-<version>-<runtime>[.exe]
#>
param(
    [string]$Version = "",
    [string[]]$Runtimes = @("win-x64", "linux-x64"),
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$Release
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Run {
    param([string]$Exe, [string[]]$Arguments)
    Write-Host "$Exe $($Arguments -join ' ')" -ForegroundColor DarkGray
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "'$Exe $($Arguments -join ' ')' failed with exit code $LASTEXITCODE" }
}

if (-not $Version) {
    $Version = ([xml](Get-Content "KospetMapMaker.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if (-not $Version) { throw "Version not found: pass -Version or set <Version> in KospetMapMaker.csproj" }
$Tag = "v$Version"
Write-Host "Kospet Map Maker $Version" -ForegroundColor Green

if (-not $SkipTests) {
    Step "Tests"
    Run dotnet @("test", "tests/KospetMapMaker.Tests", "-c", $Configuration)
}

$releaseDir = Join-Path "publish" "release"
New-Item -ItemType Directory -Force $releaseDir | Out-Null
$assets = @()

foreach ($rid in $Runtimes) {
    Step "Publish $rid"
    $out = Join-Path "publish" $rid
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    Run dotnet @("publish", "KospetMapMaker.csproj", "-c", $Configuration, "-r", $rid, "--self-contained",
                 "-p:Version=$Version", "-o", $out)
    $isWin = $rid.StartsWith("win")
    $src = Join-Path $out ($(if ($isWin) { "KospetMapMaker.exe" } else { "KospetMapMaker" }))
    $dst = Join-Path $releaseDir ("KospetMapMaker-$Version-$rid" + $(if ($isWin) { ".exe" } else { "" }))
    Copy-Item $src $dst -Force
    $assets += $dst
    Write-Host ("  {0}  {1:N1} MB" -f $dst, ((Get-Item $dst).Length / 1MB))
}

if ($Release) {
    Step "GitHub release $Tag"
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "GitHub CLI 'gh' not found: winget install GitHub.cli; gh auth login" }
    if (git status --porcelain) { throw "Working tree has uncommitted changes: commit them first" }
    $existing = git tag --list $Tag
    if (-not $existing) {
        Run git @("tag", "-a", $Tag, "-m", "Kospet Map Maker $Version")
    }
    Run git @("push", "origin", $Tag)
    $notes = "Windows: KospetMapMaker-$Version-win-x64.exe`nLinux: KospetMapMaker-$Version-linux-x64 (chmod +x)`n`nSingle file, nothing to install."
    Run gh (@("release", "create", $Tag) + $assets + @("--title", "Kospet Map Maker $Version", "--notes", $notes))
}

Step "Done"
$assets | ForEach-Object { Write-Host "  $_" }
