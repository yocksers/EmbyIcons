# Deploy EmbyIcons plugin DLL to local Emby Server and restart service
# Usage: run from an elevated PowerShell prompt (Run as Administrator)

$builtDll = "c:\Users\yock1\Desktop\Code\bin\Release\net8.0\EmbyIcons.dll"
$destDir = Join-Path $env:APPDATA "Emby-Server\programdata\plugins"
$destDll = Join-Path $destDir "EmbyIcons.dll"

Write-Host "Stopping Emby service..."
Stop-Service -Name EmbyServer -ErrorAction Stop

Write-Host "Copying DLL to plugins folder..."
if (-not (Test-Path -Path $builtDll)) {
    Write-Error "Built DLL not found: $builtDll"
    exit 1
}

if (-not (Test-Path -Path $destDir)) {
    Write-Host "Plugins directory does not exist; creating: $destDir"
    New-Item -ItemType Directory -Path $destDir -Force | Out-Null
}

Copy-Item -Path $builtDll -Destination $destDll -Force
Write-Host "Copied: $builtDll -> $destDll"

Write-Host "Starting Emby service..."
Start-Service -Name EmbyServer -ErrorAction Stop

Write-Host "Done. Tail the log file to watch for errors:"
Write-Host "Get-Content -Path \"$env:APPDATA\Emby-Server\programdata\logs\embyserver.txt\" -Wait -Tail 200"

Write-Host "Then open the Emby web UI and load the plugin settings (clear browser cache if necessary)."