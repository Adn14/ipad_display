# Installs the Virtual Audio Driver that ships inside the Virtual Display Driver winget package.
# It adds a speaker to Windows; IPadScreenHost forwards whatever plays on it to the iPad.
# Run from an Administrator PowerShell:   powershell -ExecutionPolicy Bypass -File install-audio-driver.ps1
$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Please run this from an Administrator PowerShell (right-click PowerShell > Run as administrator)." -ForegroundColor Yellow
    exit 1
}

$pkg = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Directory -Filter 'VirtualDrivers.Virtual-Display-Driver*' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $pkg) {
    Write-Host "Virtual Display Driver package not found. Install it first:  winget install --id VirtualDrivers.Virtual-Display-Driver -e" -ForegroundColor Yellow
    exit 1
}
$devcon = Join-Path $pkg.FullName 'Dependencies\devcon.exe'
$inf = Get-ChildItem (Join-Path $pkg.FullName 'SignedDrivers\x86\VAD') -Recurse -Filter *.inf | Select-Object -First 1

if (Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.HardwareID -contains 'ROOT\VirtualAudioDriver' }) {
    Write-Host "Virtual Audio Driver is already installed."
} else {
    & $devcon install $inf.FullName 'ROOT\VirtualAudioDriver'
    if ($LASTEXITCODE -ne 0) { Write-Host "devcon failed ($LASTEXITCODE)" -ForegroundColor Red; exit $LASTEXITCODE }
}

Write-Host ""
Write-Host "Done. Now pick the new speaker for sound on the iPad:" -ForegroundColor Green
Write-Host "  - whole PC:   click the speaker icon in the taskbar > arrow next to the volume > choose the Virtual Audio Driver"
Write-Host "  - one app:    Settings > System > Sound > Volume mixer > choose an output per app"
Write-Host "  Tip: rename it to 'iPad' in Settings > System > Sound > (device) > Rename."
