# Installs the latest Plaxis ProMax release for this Windows user:
#   irm https://raw.githubusercontent.com/ahisham92/plaxis-promax/main/get.ps1 | iex
$ErrorActionPreference = 'Stop'
$repo = 'ahisham92/plaxis-promax'
$release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers @{ 'User-Agent' = 'PlaxisProMax-installer' }
$asset = $release.assets | Where-Object { $_.name -like 'PlaxisProMax-*.zip' } | Select-Object -First 1
if (-not $asset) { throw "The latest release has no PlaxisProMax zip." }
$work = Join-Path $env:TEMP ("PlaxisProMax-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
$zip = Join-Path $work $asset.name
Write-Host "Downloading $($asset.name)..."
Invoke-WebRequest $asset.browser_download_url -OutFile $zip -UseBasicParsing
Expand-Archive $zip -DestinationPath $work
& (Get-ChildItem $work -Recurse -Filter install.ps1 | Select-Object -First 1).FullName
