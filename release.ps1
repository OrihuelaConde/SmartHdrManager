# ---------------------------------------------------------------------------
# Smart HDR Manager - release build
#
# Builds two clean packages into "S:\Smart HDR Manager":
#   SmartHdrManager-vX.Y.Z-win-x64             small exe, needs the .NET 10 Desktop Runtime
#   SmartHdrManager-vX.Y.Z-win-x64-standalone  larger exe, runs on any 64-bit Windows 10/11
# plus a .zip of each and SHA256 checksums.
#
# Only whitelisted files are copied (exe, README, LICENSE, userscript), so your own
# config.json, config.json.old, logs and backup folders can never end up in a release.
#
# Run from PowerShell:
#   cd S:\Tools\SmartHdrManager
#   powershell -ExecutionPolicy Bypass -File .\release.ps1
# ---------------------------------------------------------------------------
param(
    [string]$Output = 'S:\Smart HDR Manager'
)

$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$project = Join-Path $src 'SmartHdrManager.csproj'

function Step($text) { Write-Host ''; Write-Host "==> $text" -ForegroundColor Cyan }
function Fail($text) { Write-Host ''; Write-Host "ERROR: $text" -ForegroundColor Red; exit 1 }

# ---- checks ----
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail 'dotnet was not found. Install the .NET 10 SDK.' }
if (-not (Test-Path $project)) { Fail "Project not found: $project" }
foreach ($f in 'README.md', 'LICENSE.txt', 'SmartHdrManager_YouTube.user.js') {
    if (-not (Test-Path (Join-Path $src $f))) { Fail "Missing $f next to release.ps1" }
}

[xml]$xml = Get-Content $project
$version = @($xml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
if (-not $version) { $version = '0.0.0' }
$baseName = "SmartHdrManager-v$version-win-x64"
Write-Host "Smart HDR Manager $version" -ForegroundColor Green

# ---- clean build folders ----
Step 'Cleaning old build output'
$temp = Join-Path $env:TEMP 'SmartHdrManager-release'
foreach ($dir in @($temp, (Join-Path $src 'bin\Release'), (Join-Path $src 'obj'))) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
}
New-Item -ItemType Directory -Path $temp | Out-Null

# ---- build ----
$common = @('-c', 'Release', '-r', 'win-x64', '-p:PublishSingleFile=true', '-p:DebugType=None', '-p:DebugSymbols=false', '--nologo')

Step 'Building the small version (needs .NET 10 Desktop Runtime)'
dotnet publish $project @common --self-contained false -o (Join-Path $temp 'fd')
if ($LASTEXITCODE -ne 0) { Fail 'Build failed (small version). See the messages above.' }

Step 'Building the standalone version (no .NET install needed)'
dotnet publish $project @common --self-contained true '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:EnableCompressionInSingleFile=true' -o (Join-Path $temp 'sc')
if ($LASTEXITCODE -ne 0) { Fail 'Build failed (standalone version). See the messages above.' }

# ---- assemble packages (whitelist only) ----
Step "Writing packages to $Output"
# A test copy started from the release folder locks its exe: close it first
$running = Get-Process -Name 'SmartHdrManager' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($Output, [System.StringComparison]::OrdinalIgnoreCase) }
if ($running) {
    Write-Host 'Closing the test copy that is running from the release folder ...' -ForegroundColor Yellow
    $running | Stop-Process -Force
    Start-Sleep -Seconds 2
}
if (Test-Path $Output) { Remove-Item (Join-Path $Output '*') -Recurse -Force }
else { New-Item -ItemType Directory -Path $Output | Out-Null }

$packages = @(
    @{ Name = $baseName;                Build = 'fd' },
    @{ Name = "$baseName-standalone";   Build = 'sc' }
)

foreach ($p in $packages) {
    $exe = Join-Path (Join-Path $temp $p.Build) 'SmartHdrManager.exe'
    if (-not (Test-Path $exe)) { Fail "Build output missing: $exe" }

    $dest = Join-Path $Output $p.Name
    New-Item -ItemType Directory -Path $dest | Out-Null
    Copy-Item $exe $dest
    Copy-Item (Join-Path $src 'README.md') $dest
    Copy-Item (Join-Path $src 'LICENSE.txt') $dest
    Copy-Item (Join-Path $src 'SmartHdrManager_YouTube.user.js') $dest

    # Safety net: nothing personal may be in a release
    $bad = Get-ChildItem $dest -Recurse | Where-Object { $_.Name -match '^config\.json|\.log$|^log\.txt$|^portable\.txt$|\.bak$|^backup' }
    if ($bad) { Fail ("Personal files found in the package: " + ($bad.Name -join ', ')) }

    Compress-Archive -Path (Join-Path $dest '*') -DestinationPath (Join-Path $Output "$($p.Name).zip") -CompressionLevel Optimal
}

# ---- checksums ----
Step 'Writing SHA256 checksums'
$sums = Get-ChildItem $Output -Filter '*.zip' | ForEach-Object {
    "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower())  $($_.Name)"
}
$sums += Get-ChildItem $Output -Recurse -Filter 'SmartHdrManager.exe' | ForEach-Object {
    "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower())  $($_.Directory.Name)\$($_.Name)"
}
Set-Content -Path (Join-Path $Output 'SHA256SUMS.txt') -Value $sums -Encoding ASCII

Remove-Item $temp -Recurse -Force

# ---- summary ----
Step 'Done'
Get-ChildItem $Output -Recurse -File | ForEach-Object {
    '{0,10:N0} KB  {1}' -f [math]::Ceiling($_.Length / 1KB), $_.FullName.Substring($Output.Length + 1)
}
Write-Host ''
Write-Host 'Before publishing:' -ForegroundColor Yellow
Write-Host '  1. Run both exes once and do the quick test (see the chat instructions).'
Write-Host '  2. Upload both zips to https://www.virustotal.com and keep the report links for your release notes.'
Write-Host '  3. Attach the two zips and SHA256SUMS.txt to the release.'
