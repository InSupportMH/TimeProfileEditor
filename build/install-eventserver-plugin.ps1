<#
.SYNOPSIS
    Installs the Event Server component on this machine and reports whether it loaded.

.DESCRIPTION
    Copies src\TimeProfileEditor.Server\bin\<Configuration> into the Event Server's own plugin
    folder and restarts the service, then reads back the log lines that say whether MIP found the
    plugin and whether it started.

    The folder is the one Milestone's own service plugins use on this machine:

        C:\Program Files\Milestone\XProtect Event Server\MIPPlugins\<name>\

    A developer tool, not part of the product. It restarts a Windows service, so it is a .ps1 that
    demands elevation rather than anything that can be run by accident - the same reason the
    diagnostics folder ships launchers for its read-only modes and none for --write.

.PARAMETER Uninstall
    Remove the plugin folder and restart, instead of installing. This is the way back if the
    Event Server will not start with the component in place.

.PARAMETER NoRestart
    Copy the files but leave the service alone. The Event Server only reads its plugin folders at
    startup, so nothing takes effect until it is restarted by some other means.

.EXAMPLE
    .\install-eventserver-plugin.ps1

.EXAMPLE
    .\install-eventserver-plugin.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [switch]$Uninstall,
    [switch]$NoRestart
)

$ErrorActionPreference = "Stop"

$root       = Split-Path -Parent $PSScriptRoot
$source     = Join-Path $root "src\TimeProfileEditor.Server\bin\$Configuration"
$pluginName = "TimeProfileEditor.Server"
$eventServer= "C:\Program Files\Milestone\XProtect Event Server"
$target     = Join-Path $eventServer "MIPPlugins\$pluginName"
$logDir     = "C:\ProgramData\Milestone\XProtect Event Server\logs"
$service    = "MilestoneEventServerService"

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Kör det här fönstret som administratör - skriva under Program Files och starta om " +
          "$service kräver förhöjda rättigheter."
}

if (-not (Test-Path $eventServer)) { throw "Ingen Event Server installerad på den här maskinen." }

# Where the log stood before the restart, so only the new lines are read back afterwards. A restart
# writes a few hundred lines and the interesting ones are three of them.
$baseline = @{}
foreach ($name in @("MIP.log", "Application.log")) {
    $file = Join-Path $logDir $name
    $baseline[$name] = if (Test-Path $file) { (Get-Item $file).Length } else { 0 }
}

function Restart-EventServer {
    Write-Host "Startar om $service..." -ForegroundColor Cyan
    Restart-Service -Name $service -Force
    $svc = Get-Service -Name $service
    $svc.WaitForStatus("Running", [TimeSpan]::FromMinutes(2))
    Write-Host "Tjänsten kör." -ForegroundColor Green
}

if ($Uninstall) {
    if (Test-Path $target) {
        Remove-Item $target -Recurse -Force
        Write-Host "Borttaget: $target" -ForegroundColor Green
    } else {
        Write-Host "Fanns inte: $target" -ForegroundColor Yellow
    }

    if (-not $NoRestart) { Restart-EventServer }
    return
}

foreach ($required in @("$pluginName.dll", "plugin.def")) {
    if (-not (Test-Path (Join-Path $source $required))) {
        throw "Hittar inte $required i $source. Bygg först: dotnet build -c $Configuration"
    }
}

# The Event Server ships its own VideoOS assemblies. A copy in the plugin folder makes MIP load the
# platform twice and the plugin fails to bind, exactly as in the Smart Client.
$strays = Get-ChildItem $source -Filter "VideoOS.*.dll" -ErrorAction SilentlyContinue
if ($strays) { throw "Byggutdata innehåller VideoOS-assemblies ($($strays.Name -join ', '))." }

# Stopped before copying: the service holds the DLL open while it runs, and a copy onto a locked
# file fails in a way that looks like it worked - the old build stays and the log then describes
# something other than what was just built.
#
# Ten minutes, and everything after the stop is inside a finally. A stopped Event Server is not a
# failed deployment, it is an outage: alarms, events and rules are all down until it comes back.
# Measured here, a stop can take well over two minutes, and the first version of this script threw
# on a two-minute timeout and left the service down - which is a far worse way to fail than not
# deploying at all.
Write-Host "Stoppar $service (kan ta flera minuter)..." -ForegroundColor Cyan
Stop-Service -Name $service -Force
(Get-Service -Name $service).WaitForStatus("Stopped", [TimeSpan]::FromMinutes(10))

try {
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item (Join-Path $source "*") $target -Force
    Write-Host "Kopierat till $target" -ForegroundColor Green
    Get-ChildItem $target -File | Select-Object Name, Length | Format-Table -AutoSize
}
finally {
    if ($NoRestart) {
        Write-Host "Startar inte om - komponenten laddas först vid nästa start." -ForegroundColor Yellow
    } else {
        Write-Host "Startar $service..." -ForegroundColor Cyan
        Start-Service -Name $service
        (Get-Service -Name $service).WaitForStatus("Running", [TimeSpan]::FromMinutes(10))
        Write-Host "Tjänsten kör." -ForegroundColor Green
    }
}

if ($NoRestart) { return }

# Startup is asynchronous - the service reports Running well before the plugins have loaded.
Start-Sleep -Seconds 25

Write-Host ""
Write-Host "=== Vad loggen säger ===" -ForegroundColor Cyan

$found = $false
foreach ($name in @("MIP.log", "Application.log")) {
    $file = Join-Path $logDir $name
    if (-not (Test-Path $file)) { continue }

    $stream = [IO.File]::Open($file, "Open", "Read", "ReadWrite")
    try {
        $from = [Math]::Min($baseline[$name], $stream.Length)
        $stream.Seek($from, "Begin") | Out-Null
        $reader = New-Object IO.StreamReader($stream)
        $fresh  = $reader.ReadToEnd() -split "`r?`n"
    } finally {
        $stream.Dispose()
    }

    $hits = $fresh | Where-Object {
        $_ -match "Tidsprofiler" -or $_ -match "TimeProfileEditor" -or
        $_ -match "PluginLoader" -or $_ -match "ERROR" -or $_ -match "FATAL"
    }

    if ($hits) {
        $found = $true
        Write-Host ""
        Write-Host "--- $name ---" -ForegroundColor Cyan
        $hits | ForEach-Object { Write-Host $_ }
    }
}

if (-not $found) {
    Write-Host "Inga rader om pluginet och inga fel. Läs hela loggen i $logDir." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Vägen tillbaka om Event Servern inte startar:" -ForegroundColor Yellow
Write-Host "  .\install-eventserver-plugin.ps1 -Uninstall"
