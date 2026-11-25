# Builds Plaxis ProMax into .\dist: the window (self-contained, so no .NET install is needed on the PC), the Plaxis
# worker next to it, and the launcher Plaxis runs from its Expert menu.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if (Test-Path dist) { Remove-Item dist -Recurse -Force }
dotnet publish src/PlaxisProMax/PlaxisProMax.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=false -o dist/app
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
if (-not (Test-Path dist/app/plaxis_worker.py)) { throw "The Plaxis worker is missing from the build" }
Copy-Item "plaxis/Plaxis ProMax.py" dist/
Get-ChildItem dist -Recurse -Filter *.pdb | Remove-Item
Write-Host "Built into $(Resolve-Path dist)"
