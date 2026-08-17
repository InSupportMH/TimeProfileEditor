<#
.SYNOPSIS
    Builds the plugin and installs it into the local MIP plugin folder.

.DESCRIPTION
    MIP discovers plugins by scanning C:\Program Files\Milestone\MIPPlugins for
    subfolders containing a plugin.def. Every MIP host reads the same folder, so a
    single copy serves both the Smart Client (where the plugin is used) and the
    Management Client (which registers the role permissions).

    Run this on each machine that needs the plugin - or point -Destination at a
    staging folder and let your software distribution tool copy it out. See
    README.md, "Installation och distribution".

.PARAMETER Destination
    Plugin root to install into. Defaults to the local MIP plugin folder.

.PARAMETER Configuration
    Build configuration. Release by default.

.EXAMPLE
    .\deploy.ps1
    .\deploy.ps1 -Destination \\fileserver\xprotect$\MIPPlugins
#>
[CmdletBinding()]
param(
    [string]$Destination = "$env:ProgramFiles\Milestone\MIPPlugins",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\TimeProfileEditor\TimeProfileEditor.csproj"
$pluginFolder = Join-Path $Destination "TimeProfileEditor"

Write-Host "Bygger $Configuration..." -ForegroundColor Cyan
& dotnet build $project -c $Configuration --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "Bygget misslyckades." }

$output = Join-Path $root "src\TimeProfileEditor\bin\$Configuration"
foreach ($required in @("TimeProfileEditor.dll", "plugin.def")) {
    if (-not (Test-Path (Join-Path $output $required))) {
        throw "Hittar inte $required i $output."
    }
}

# A stale VideoOS assembly next to the plugin makes MIP load a second copy of the
# platform and the plugin fails to bind - refuse rather than ship that.
$strays = Get-ChildItem $output -Filter "VideoOS.*.dll" -ErrorAction SilentlyContinue
if ($strays) {
    throw "Byggutdata innehåller VideoOS-assemblies ($($strays.Name -join ', ')). " +
          "De ska levereras av Smart Client, inte av pluginet."
}

Write-Host "Installerar till $pluginFolder..." -ForegroundColor Cyan
if (-not (Test-Path $pluginFolder)) {
    New-Item -ItemType Directory -Path $pluginFolder -Force | Out-Null
}

Copy-Item (Join-Path $output "TimeProfileEditor.dll") $pluginFolder -Force
Copy-Item (Join-Path $output "plugin.def") $pluginFolder -Force

$version = (Get-Item (Join-Path $pluginFolder "TimeProfileEditor.dll")).VersionInfo.FileVersion
Write-Host ""
Write-Host "Klart. TimeProfileEditor $version installerat i $pluginFolder" -ForegroundColor Green
Write-Host ""
Write-Host "Nästa steg:" -ForegroundColor Yellow
Write-Host "  1. Starta Management Client en gång så att behörigheterna registreras."
Write-Host "  2. Roller -> <din roll> -> fliken Tidsprofiler -> kryssa i behörigheterna."
Write-Host "  3. Rollen behöver också läs-/skrivrätt mot Management Server för att kunna spara."
Write-Host "  4. Starta om Smart Client - fliken Tidsprofiler dyker upp."
