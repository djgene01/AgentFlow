<#
.SYNOPSIS
    Builds the AgentFlow installer: artifacts\AgentFlow-<version>-x64.msi
.DESCRIPTION
    1. Publishes the app self-contained for win-x64 (no .NET install needed on the target PC).
    2. Adds the VMCP-Studio video studio (its packaged app, with its own node.exe) as publish\VideoStudio,
       SfxForge (published self-contained from source) as publish\SfxForge and Pixel Art Studio (its own
       publish folder) as publish\PixelArtStudio.
    3. Builds the WiX v6 installer project from that folder.
    The version comes from <Version> in src\AgentFlow.App\AgentFlow.App.csproj.
.PARAMETER VideoStudio
    The VMCP-Studio source folder. Its desktop\payload\payload.zip (made by "npm run build:exe") is what gets installed.
.PARAMETER SfxForge
    The SfxForge source folder (SfxForge.sln).
.PARAMETER PixelArtStudio
    The Pixel Art Studio source folder; its artifacts\publish (self-contained, licensed Release build) is installed.
.EXAMPLE
    .\build-installer.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$VideoStudio = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Development Projects\CV-Studio'),
    [string]$SfxForge = 'Z:\SfxForge',
    [string]$PixelArtStudio = 'Z:\PixelArtStudio'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$publish = Join-Path $root 'artifacts\publish'
$out = Join-Path $root 'artifacts'

[xml]$csproj = Get-Content (Join-Path $root 'src\AgentFlow.App\AgentFlow.App.csproj')
$version = ($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
if (-not $version) { throw 'No <Version> in AgentFlow.App.csproj' }
Write-Host "AgentFlow $version" -ForegroundColor Cyan

Write-Host '1/3 Publishing the app (self-contained, win-x64)...'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $root 'src\AgentFlow.App') -c $Configuration -r win-x64 --self-contained true -o $publish -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
if (-not (Test-Path (Join-Path $publish 'AgentFlow.CtrlC.exe'))) { throw 'The Ctrl+C helper is missing from the publish folder' }

foreach ($f in 'onnxruntime.dll', 'DirectML.dll') {
    if (-not (Test-Path (Join-Path $publish $f))) { throw "The speech engine's $f is missing from the publish folder" }
}

# Voice input (ONNX Runtime) needs the Visual C++ runtime 14.40+; ship Microsoft's redistributable so the
# Voice page can install it on PCs that have an older one. Downloaded once and cached; the signature is checked.
$cache = Join-Path $root 'artifacts\cache'
$redist = Join-Path $cache 'vc_redist.x64.exe'
if (-not (Test-Path $redist)) {
    New-Item -ItemType Directory -Force $cache | Out-Null
    Write-Host '    Downloading the Microsoft Visual C++ redistributable...'
    Invoke-WebRequest 'https://aka.ms/vs/17/release/vc_redist.x64.exe' -OutFile $redist
}
$sig = Get-AuthenticodeSignature $redist
if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
    Remove-Item $redist -Force
    throw "vc_redist.x64.exe is not validly signed by Microsoft ($($sig.Status))"
}
Copy-Item $redist (Join-Path $publish 'vc_redist.x64.exe')

Write-Host '2/3 Adding the video studio...'
$payload = Join-Path $VideoStudio 'desktop\payload\payload.zip'
if (-not (Test-Path $payload)) { throw "No VMCP-Studio payload at $payload. Run 'npm run build:exe' in $VideoStudio first." }
# The payload is made by the studio's EXE build; warn when the studio's source is newer than it.
$newest = Get-ChildItem (Join-Path $VideoStudio 'server'), (Join-Path $VideoStudio 'mcp'), (Join-Path $VideoStudio 'web') -Recurse -File |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($newest -and $newest.LastWriteTime -gt (Get-Item $payload).LastWriteTime) {
    Write-Warning "$($newest.FullName) is newer than the studio payload; run 'npm run build:exe' in $VideoStudio to include it."
}
$studio = Join-Path $publish 'VideoStudio'
Expand-Archive -LiteralPath $payload -DestinationPath $studio -Force
foreach ($f in 'node.exe', 'mcp\server.js', 'server\index.js', '.claude\skills\vmcp-studio\SKILL.md') {
    if (-not (Test-Path (Join-Path $studio $f))) { throw "The video studio is missing $f" }
}
if (Test-Path (Join-Path $studio 'data')) { Remove-Item (Join-Path $studio 'data') -Recurse -Force } # the library lives in %LOCALAPPDATA%

Write-Host '2b/3 Adding SfxForge (self-contained, with its MCP companion)...'
$sfxOut = Join-Path $publish 'SfxForge'
if (-not (Test-Path (Join-Path $SfxForge 'SfxForge.sln'))) { throw "No SfxForge source at $SfxForge" }
# The studio and its stdio companion share one folder, so launch_studio_ui finds SfxForge.exe next to SfxForge.Mcp.exe.
foreach ($proj in 'src\SfxForge.App\SfxForge.App.csproj', 'src\SfxForge.Mcp\SfxForge.Mcp.csproj') {
    dotnet publish (Join-Path $SfxForge $proj) -c Release -r win-x64 --self-contained true -o $sfxOut -nologo -v q `
        -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish of SfxForge ($proj) failed" }
}
foreach ($f in 'SfxForge.exe', 'SfxForge.Mcp.exe') {
    if (-not (Test-Path (Join-Path $sfxOut $f))) { throw "SfxForge is missing $f" }
}

Write-Host '2c/3 Adding Pixel Art Studio (its own self-contained publish)...'
# Its Release build needs a Six Labors licence, so the studio's own publish folder is used as it is.
$pixelPublish = Join-Path $PixelArtStudio 'artifacts\publish'
if (-not (Test-Path (Join-Path $pixelPublish 'PixelArtStudio.exe'))) {
    throw "No PixelArtStudio.exe in $pixelPublish. Publish Pixel Art Studio first (see its README, Installer)."
}
$newest = Get-ChildItem (Join-Path $PixelArtStudio 'src') -Recurse -File -Include *.cs, *.xaml, *.csproj |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($newest -and $newest.LastWriteTime -gt (Get-Item (Join-Path $pixelPublish 'PixelArtStudio.exe')).LastWriteTime) {
    Write-Warning "$($newest.FullName) is newer than Pixel Art Studio's publish folder; publish it again to include the change."
}
$pixelOut = Join-Path $publish 'PixelArtStudio'
New-Item -ItemType Directory -Force $pixelOut | Out-Null
robocopy $pixelPublish $pixelOut /E /XF *.pdb /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copying Pixel Art Studio failed (robocopy $LASTEXITCODE)" }
$global:LASTEXITCODE = 0

Write-Host '3/3 Building the installer...'
# A trailing "\" before a closing quote would escape it, so the output path ends in "/".
dotnet build (Join-Path $root 'installer\AgentFlow.Installer.wixproj') -c $Configuration -nologo -v q `
    "-p:ProductVersion=$version" "-p:PublishDir=$publish" "-p:OutputPath=$out/"
if ($LASTEXITCODE -ne 0) { throw 'The installer build failed' }

$msi = Join-Path $out "AgentFlow-$version-x64.msi"
if (-not (Test-Path $msi)) { $msi = Get-ChildItem $out -Recurse -Filter "AgentFlow-$version-x64.msi" | Select-Object -First 1 -ExpandProperty FullName }
Write-Host ("Done: {0} ({1:N1} MB)" -f $msi, ((Get-Item $msi).Length / 1MB)) -ForegroundColor Green
