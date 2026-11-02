# Builds a single self-contained IPadScreenHost.exe into .\dist (no .NET install needed to run it).
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
dotnet publish IPadScreenHost.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "`nBuilt: $PSScriptRoot\dist\IPadScreenHost.exe"
