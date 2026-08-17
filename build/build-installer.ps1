<#
.SYNOPSIS
    Builds the plugin and packages it as an MSI.

.DESCRIPTION
    Produces dist\TimeProfileEditor-<version>.msi, a per-machine x64 package that installs
    into C:\Program Files\Milestone\MIPPlugins\TimeProfileEditor.

    The MSI version is read from the built assembly, so bumping <Version> in the csproj is
    the only place a release number has to change. The UpgradeCode in installer\Package.wxs
    stays fixed, which is what lets a new build replace an installed one instead of
    installing alongside it.

    Requires the WiX 5 CLI:  dotnet tool install --global wix

.PARAMETER Edition
    What to build.

      Normal      - the product. One binary, correct on every XProtect tier: permissions come from
                    the plugin's own security namespace, which exists on Express+, Professional+,
                    Expert and Corporate alike, and a write the server refuses is routed through
                    the Event Server component instead.

      Measurement - no permission check inside the plugin at all, so that a refusal can only have
                    come from the Management Server. A measuring instrument, not a product: never
                    built by default, named so it cannot be confused with a release, and it
                    announces itself in the Smart Client. See EditionMode.Measurement.

    There used to be Corporate and Standard packages here, on the belief that the plugin's own
    permissions could not be granted on Expert and Professional+. They can - what those tiers lack
    is delegating *configuration* rights to a role, which is a different thing - so the two
    packages were a distinction without a difference, and one of them could be installed on the
    wrong system.

.PARAMETER Configuration
    Build configuration. Release by default.

.PARAMETER SkipDiagnostics
    Skip building dist\Diagnostik, the xcopy-deployable tool that reports what a given server
    thinks of the plugin's permissions.

.EXAMPLE
    .\build-installer.ps1 -Edition All
#>
[CmdletBinding()]
param(
    [ValidateSet("Normal", "Measurement")]
    [string]$Edition = "Normal",
    [string]$Configuration = "Release",
    [switch]$SkipDiagnostics
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\TimeProfileEditor\TimeProfileEditor.csproj"
$installerDir = Join-Path $root "installer"
$binDir = Join-Path $root "src\TimeProfileEditor\bin\$Configuration"
$distDir = Join-Path $root "dist"

# The WiX CLI installs as a dotnet global tool, which is not always on PATH in a fresh shell.
$wix = Get-Command wix -ErrorAction SilentlyContinue
if ($wix) {
    $wixExe = $wix.Source
} else {
    $wixExe = Join-Path $env:USERPROFILE ".dotnet\tools\wix.exe"
    if (-not (Test-Path $wixExe)) {
        throw "Hittar inte WiX. Installera med: dotnet tool install --global wix"
    }
}

if (-not (Test-Path $distDir)) { New-Item -ItemType Directory -Path $distDir -Force | Out-Null }

$editions = @($Edition)
$built = @()

# Friendly names, kept out of the .wxs so the same package definition serves both builds.
$editionNames = @{
    Normal      = "alla XProtect-nivaer"
    Measurement = "MATLAGE - ingen behorighetskontroll"
}

# The measurement build shares the UpgradeCode with the real ones, so installing it replaces the
# installed plugin rather than sitting beside it. That is deliberate: two copies under MIPPlugins
# means MIP loads whichever folder it reaches first and ignores the other, and a machine quietly
# running the unchecked build while the normal one appears installed is a far worse outcome than
# having to reinstall afterwards. The file name carries the warning instead.
$fileNames = @{
    Measurement = "MATLAGE-EJ-FOR-DRIFT"
}

foreach ($current in $editions) {
    Write-Host ""
    Write-Host "=== $current ===" -ForegroundColor Cyan
    Write-Host "Bygger $Configuration..." -ForegroundColor Cyan

    # A clean build per edition. The edition is baked into the assembly as metadata, and an
    # incremental build would happily reuse the previous one's output.
    & dotnet build $project -c $Configuration --nologo -v minimal -p:Edition=$current --no-incremental
    if ($LASTEXITCODE -ne 0) { throw "Bygget misslyckades." }

    foreach ($required in @("TimeProfileEditor.dll", "plugin.def")) {
        if (-not (Test-Path (Join-Path $binDir $required))) {
            throw "Hittar inte $required i $binDir."
        }
    }

    # Smart Client ships its own VideoOS assemblies. A copy in the plugin folder makes MIP load
    # the platform twice and the plugin fails to bind, so refuse to package one.
    $strays = Get-ChildItem $binDir -Filter "VideoOS.*.dll" -ErrorAction SilentlyContinue
    if ($strays) {
        throw "Byggutdata innehåller VideoOS-assemblies ($($strays.Name -join ', ')). " +
              "De ska levereras av Smart Client, inte av pluginet."
    }

    # MSI versions are three-part; a four-part assembly version would be rejected.
    $fileVersion = (Get-Item (Join-Path $binDir "TimeProfileEditor.dll")).VersionInfo.FileVersion
    $parts = $fileVersion.Split('.')
    $version = "$($parts[0]).$($parts[1]).$($parts[2])"

    $label = if ($fileNames.ContainsKey($current)) { "-" + $fileNames[$current] } else { "" }
    $msi = Join-Path $distDir "TimeProfileEditor$label-$version.msi"

    Write-Host "Paketerar version $version..." -ForegroundColor Cyan
    & $wixExe build `
        -arch x64 `
        -ext WixToolset.UI.wixext `
        -d "Version=$version" `
        -d "BinDir=$binDir" `
        -d "InstallerDir=$installerDir" `
        -d "EditionName=$($editionNames[$current])" `
        -o $msi `
        (Join-Path $installerDir "Package.wxs")

    if ($LASTEXITCODE -ne 0) { throw "WiX misslyckades." }
    $built += $msi
}

# The diagnostics tool travels to the machine that has the problem, which is rarely a machine
# with a build environment on it. So it ships as a plain folder: .NET Framework 4.8 is already
# on every Windows that runs Smart Client, and everything else it needs is copied next to it.
if (-not $SkipDiagnostics) {
    Write-Host ""
    Write-Host "=== Diagnostikverktyg ===" -ForegroundColor Cyan

    $harness = Join-Path $root "tests\TimeProfileEditor.Harness\TimeProfileEditor.Harness.csproj"
    & dotnet build $harness -c $Configuration --nologo -v minimal --no-incremental
    if ($LASTEXITCODE -ne 0) { throw "Diagnostikbygget misslyckades." }

    $harnessBin = Join-Path $root "tests\TimeProfileEditor.Harness\bin\$Configuration\net48"
    $diagDir = Join-Path $distDir "Diagnostik"

    if (Test-Path $diagDir) { Remove-Item $diagDir -Recurse -Force }
    New-Item -ItemType Directory -Path $diagDir -Force | Out-Null
    Copy-Item (Join-Path $harnessBin "*") $diagDir -Recurse -Force

    # The harness carries TimeProfileEditor.dll, and the build drops a plugin.def next to it. That
    # pair is all MIP looks for: copied under MIPPlugins - which is where a folder full of tools
    # naturally ends up - the Smart Client loads this folder as the plugin. It then wins over the
    # installed copy, because PluginManager recurses the subfolders and ignores a second plugin
    # with the same definition id, so the MSI silently stops taking effect. The harness runs from
    # its own exe and never needs the manifest, so it is removed rather than shipped.
    Remove-Item (Join-Path $diagDir "plugin.def") -Force -ErrorAction SilentlyContinue

    # Only the read-only modes get a launcher. The harness also has a --write mode that creates and
    # deletes a test profile, and that must never be a double-click away on a customer's server.
    $launchers = @{
        "Kor diagnostik.cmd" = @{
            Flag = "--diag"
            What = "Laser av vad servern anser om pluginets behorigheter."
            File = "diagnostik.txt"
        }
        "Kor tokentest.cmd"  = @{
            Flag = "--tokenprobe"
            What = "Kontrollerar att servern godtar en akta token och avvisar en forfalskad."
            File = "tokentest.txt"
        }
    }

    foreach ($name in $launchers.Keys) {
        $l = $launchers[$name]
        @"
@echo off
rem $($l.What) Andrar ingenting.
rem Ligger Management Server pa en annan maskin: lagg till --server http://servernamn
"%~dp0TimeProfileEditor.Harness.exe" $($l.Flag) %* > "%~dp0$($l.File)" 2>&1
type "%~dp0$($l.File)"
echo.
echo Rapporten sparades som %~dp0$($l.File)
pause
"@ | Set-Content -Path (Join-Path $diagDir $name) -Encoding OEM
    }

    # The one thing that is easy to get wrong about this folder, written where it will be seen.
    @'
Tidsprofiler - diagnostikverktyg
================================

VIKTIGT: verktyget loggar in som den WINDOWS-ANVANDARE som kor det.

Ska du felsoka en basic-anvandare, eller nagon annan an dig sjalv, sager det har
verktyget alltsa fel sak. Anvand da i stallet knappen "Kopiera diagnostik" i
pluginet inne i Smart Client - den kors som den faktiskt inloggade anvandaren och
tacker samma sak plus tokenkontrollen.

Kor diagnostik.cmd   Vad servern anser om behorigheterna, och vad Configuration
                     API lamnar ut. Andrar ingenting.

Kor tokentest.cmd    Att servern godtar en akta token och avvisar en forfalskad.
                     Sjalva token skrivs aldrig ut.

Ligger Management Server pa en annan maskin an den du kor fran, lagg till
servernamnet:

    "Kor diagnostik.cmd" --server http://servernamn

Krav: .NET Framework 4.8, som redan finns pa varje Windows som kor Smart Client.
Ingenting behover installeras - mappen kors dar den star.
'@ | Set-Content -Path (Join-Path $diagDir "LAS MIG.txt") -Encoding UTF8

    Write-Host "Klart: $diagDir" -ForegroundColor Green
}

Write-Host ""
foreach ($msi in $built) {
    $size = [math]::Round((Get-Item $msi).Length / 1KB, 1)
    Write-Host "Klart: $msi ($size KB)" -ForegroundColor Green
}

if ($editions -contains "Measurement") {
    Write-Host ""
    Write-Host "OBS: mätbygget kontrollerar ingen behörighet i pluginet." -ForegroundColor Red
    Write-Host "     Servern prövar fortfarande varje sparning, men banderollen som förklarar" -ForegroundColor Red
    Write-Host "     ett nej är borta. Avinstallera det när mätningen är gjord." -ForegroundColor Red
}

Write-Host ""
Write-Host "Installera:" -ForegroundColor Yellow
Write-Host "  msiexec /i `"$($built[0])`" /qn"
Write-Host ""
Write-Host "Avinstallera:"
Write-Host "  msiexec /x `"$($built[0])`" /qn"
