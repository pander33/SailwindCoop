<#
.SYNOPSIS
  Builds a release archive of one or both editions into dist\.

.DESCRIPTION
  Thunderstore  dist\SailwindCoop-<version>-thunderstore.zip   no Steam transport, no mod download
  Full          dist\SailwindCoop-<version>-full.zip           everything; published outside Thunderstore

  Each edition is built into its own staging folder under dist\stage\, so the game's plugin folder
  is not touched and a running game does not lock the build. The version comes from Plugin.cs and
  must match manifest.json.

.EXAMPLE
  pwsh Tools/pack.ps1                      # both editions
  pwsh Tools/pack.ps1 -Edition Thunderstore
#>
param(
    [ValidateSet('All', 'Full', 'Thunderstore')]
    [string]$Edition = 'All'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $repo 'dist'

# manifest.json in the repository describes the Thunderstore edition; the full archive gets this text.
$fullDescription = 'Play Sailwind with friends over Steam, LAN or VPN. Shared sailing, mod sharing, gestures and crew sleep.'

# Files that must never reach Thunderstore.
$forbiddenInThunderstore = @('steam_api64.dll', 'Facepunch.Steamworks.Win64.dll')

function Fail([string]$message) { throw "pack: $message" }

$pluginSource = Get-Content (Join-Path $repo 'Plugin.cs') -Raw
if ($pluginSource -notmatch 'public const string Version = "(\d+\.\d+\.\d+)"') { Fail 'Plugin.Version not found in Plugin.cs' }
$version = $Matches[1]

$manifestPath = Join-Path $repo 'manifest.json'
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
if ($manifest.version_number -ne $version) { Fail "manifest.json says $($manifest.version_number), Plugin.cs says $version" }
if ($manifest.description.Length -gt 250) { Fail 'manifest.json description is longer than 250 characters' }
if ($fullDescription.Length -gt 250) { Fail 'the full edition description is longer than 250 characters' }

# Thunderstore requires a 256x256 PNG; width and height sit in the IHDR chunk.
$icon = [System.IO.File]::ReadAllBytes((Join-Path $repo 'icon.png'))
function BigEndianInt([byte[]]$bytes, [int]$at) {
    return ([int]$bytes[$at] * 16777216) + ([int]$bytes[$at + 1] * 65536) + ([int]$bytes[$at + 2] * 256) + [int]$bytes[$at + 3]
}
$iconWidth = BigEndianInt $icon 16
$iconHeight = BigEndianInt $icon 20
if ($iconWidth -ne 256 -or $iconHeight -ne 256) { Fail "icon.png is ${iconWidth}x${iconHeight}, Thunderstore needs 256x256" }

function Pack([string]$name) {
    $stage = Join-Path $dist "stage\$name"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force $stage | Out-Null

    Write-Host "== $name edition $version"
    & dotnet build (Join-Path $repo 'SailwindCoop.csproj') -c Release --nologo -v quiet "-p:Edition=$name" "-p:OutputPath=$stage\"
    if ($LASTEXITCODE -ne 0) { Fail "$name build failed" }
    Get-ChildItem $stage -Filter *.pdb | Remove-Item -Force

    Copy-Item (Join-Path $repo 'Avatars\avatar.bundle') $stage
    foreach ($file in 'README.md', 'CHANGELOG.md', 'MULTIPLAYER_GUIDE.md', 'LICENSE', 'icon.png') {
        Copy-Item (Join-Path $repo $file) $stage
    }

    if ($name -eq 'Thunderstore') {
        Copy-Item $manifestPath $stage
        foreach ($file in $forbiddenInThunderstore) {
            if (Get-ChildItem $stage -Recurse -Filter $file) { Fail "$file is in the Thunderstore archive" }
        }
        # The edition must not even mention the Steam wrapper it was built without...
        $dll = [System.IO.File]::ReadAllBytes((Join-Path $stage 'SailwindCoop.dll'))
        $text = [System.Text.Encoding]::ASCII.GetString($dll)
        foreach ($word in 'Facepunch', 'Steamworks', 'ModUpload', 'SteamLink') {
            if ($text.Contains($word)) { Fail "the Thunderstore SailwindCoop.dll contains '$word'" }
        }
        # ...nor carry the menu and log text of the features that were left out.
        & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'scan-strings.ps1') (Join-Path $stage 'SailwindCoop.dll')
        if ($LASTEXITCODE -ne 0) { Fail 'the Thunderstore SailwindCoop.dll contains Steam or download text (see above)' }
    }
    else {
        $full = $manifest | ConvertTo-Json -Depth 5 | ConvertFrom-Json
        $full.description = $fullDescription
        $full | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $stage 'manifest.json') -Encoding utf8NoBOM
        foreach ($file in $forbiddenInThunderstore) {
            if (-not (Test-Path (Join-Path $stage $file))) { Fail "$file is missing from the full archive" }
        }
    }

    foreach ($file in 'SailwindCoop.dll', 'LiteNetLib.dll', 'sounds\landho.wav') {
        if (-not (Test-Path (Join-Path $stage $file))) { Fail "$file is missing from the $name archive" }
    }

    $zip = Join-Path $dist ("SailwindCoop-$version-" + $name.ToLowerInvariant() + '.zip')
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
    Write-Host ("   " + $zip + "  " + [math]::Round((Get-Item $zip).Length / 1MB, 1) + " MB")
    Get-ChildItem $stage -Recurse -File | ForEach-Object { Write-Host ("     " + $_.FullName.Substring($stage.Length + 1)) }
}

$editions = if ($Edition -eq 'All') { 'Thunderstore', 'Full' } else { , $Edition }
foreach ($name in $editions) { Pack $name }
