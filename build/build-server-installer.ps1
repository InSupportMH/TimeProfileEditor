<#
.SYNOPSIS
    Builds the Event Server component and packages it as its own MSI.

.DESCRIPTION
    Produces dist\TimeProfileEditor-EventServer-<version>.msi, a per-machine x64 package that
    installs into

        C:\Program Files\Milestone\XProtect Event Server\MIPPlugins\TimeProfileEditor.Server

    and stops and starts the Event Server service around the copy.

    Kept separate from build-installer.ps1 on purpose. The two packages go to different machines -
    this one to a single server, the other to every workstation - and are updated on different
    occasions. One script producing both invites shipping the wrong one, and an administrative
    component landing on an operator's PC is exactly the mistake worth making hard.

    Requires the WiX 5 CLI:  dotnet tool install --global wix

.PARAMETER Configuration
    Build configuration. Release by default.

.EXAMPLE
    .\build-server-installer.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root         = Split-Path -Parent $PSScriptRoot
$project      = Join-Path $root "src\TimeProfileEditor.Server\TimeProfileEditor.Server.csproj"
$installerDir = Join-Path $root "installer"
$binDir       = Join-Path $root "src\TimeProfileEditor.Server\bin\$Configuration"
$distDir      = Join-Path $root "dist"

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

Write-Host ""
Write-Host "=== Serverkomponent ===" -ForegroundColor Cyan
Write-Host "Bygger $Configuration..." -ForegroundColor Cyan

& dotnet build $project -c $Configuration --nologo -v minimal --no-incremental
if ($LASTEXITCODE -ne 0) { throw "Bygget misslyckades." }

foreach ($required in @("TimeProfileEditor.Server.dll", "plugin.def")) {
    if (-not (Test-Path (Join-Path $binDir $required))) {
        throw "Hittar inte $required i $binDir."
    }
}

# The Event Server ships its own VideoOS assemblies. A second set in the plugin folder makes MIP
# load the platform twice and the plugin fails to bind - the same trap as in the Smart Client.
$strays = Get-ChildItem $binDir -Filter "VideoOS.*.dll" -ErrorAction SilentlyContinue
if ($strays) {
    throw "Byggutdata innehåller VideoOS-assemblies ($($strays.Name -join ', ')). " +
          "De ska levereras av Event Server, inte av komponenten."
}

# The manifest decides which host loads this, and getting it wrong is not a build error - it is a
# component that quietly runs with administrator rights inside a client, or one that never starts
# at all. Cheap to check, so it is checked.
$manifest = Get-Content (Join-Path $binDir "plugin.def") -Raw
if ($manifest -notmatch '<load\s+env="Service"\s*/>') {
    throw "plugin.def laddar inte env=""Service"". Komponenten skulle då hamna i fel värdmiljö."
}

# MSI versions are three-part; a four-part assembly version would be rejected.
$fileVersion = (Get-Item (Join-Path $binDir "TimeProfileEditor.Server.dll")).VersionInfo.FileVersion
$parts = $fileVersion.Split('.')
$version = "$($parts[0]).$($parts[1]).$($parts[2])"

$msi = Join-Path $distDir "TimeProfileEditor-EventServer-$version.msi"

Write-Host "Paketerar version $version..." -ForegroundColor Cyan
& $wixExe build `
    -arch x64 `
    -ext WixToolset.UI.wixext `
    -d "Version=$version" `
    -d "BinDir=$binDir" `
    -d "InstallerDir=$installerDir" `
    -o $msi `
    (Join-Path $installerDir "ServerPackage.wxs")

if ($LASTEXITCODE -ne 0) { throw "WiX misslyckades." }

$size = [math]::Round((Get-Item $msi).Length / 1KB, 1)
Write-Host ""
Write-Host "Klart: $msi ($size KB)" -ForegroundColor Green
Write-Host ""
Write-Host "Installeras BARA på maskinen som kör Event Server:" -ForegroundColor Yellow
Write-Host "  msiexec /i `"$msi`" /qn"
Write-Host ""
Write-Host "Tjänsten stoppas och startas automatiskt. Den kan ta flera minuter på att stoppa -"
Write-Host "stoppa den gärna i förväg om installationen klagar på filer som används."
Write-Host ""
Write-Host "Avinstallera:"
Write-Host "  msiexec /x `"$msi`" /qn"
