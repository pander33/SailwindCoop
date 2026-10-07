<#
.SYNOPSIS
  Builds a release archive of one or both editions into dist\.

.DESCRIPTION
  Thunderstore  dist\SailwindCoop-<version>-thunderstore.zip   no mod download
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
$fullDescription = 'Play Sailwind with friends over Steam, LAN or VPN. Shared sailing, mod sharing, shared wallet, gestures and crew sleep.'

# The Steam libraries. Both editions carry them: Thunderstore allows them (support, 2026-10-07).
$steamLibraries = @('steam_api64.dll', 'Facepunch.Steamworks.Win64.dll')

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

# The documents hold both editions' text. <!--full--> ... <!--/full--> is left out of the Thunderstore
# archive; <!--thunderstore: ... --> is text only that archive gets. The full archive keeps the first
# and drops the second. Mod download must not be described on the Thunderstore page.
$forbiddenInThunderstoreDocs = '(?i)full edition|полн\w+ редакци|sharing|ShareMods|AllowModDownload|\*\*Download\*\*|coop-installed|mods?\b.{0,60}download|download.{0,60}\bmods?\b|скач\w*.{0,40}мод|мод\w*.{0,40}скач'

function EditionDocument([string]$source, [string]$target, [string]$name) {
    $text = [System.IO.File]::ReadAllText($source)
    if ($name -eq 'Thunderstore') {
        # A marker alone on its line takes the line with it; one inside a line leaves the line break.
        $text = [regex]::Replace($text, '(?ms)^<!--full-->\r?\n.*?^<!--/full-->\r?\n', '')
        $text = [regex]::Replace($text, '(?s)<!--full-->.*?<!--/full-->', '')
        $text = [regex]::Replace($text, '(?s)<!--thunderstore:(.*?)-->', '$1')
    }
    else {
        $text = [regex]::Replace($text, '(?m)^<!--/?full-->\r?\n', '')
        $text = [regex]::Replace($text, '<!--/?full-->', '')
        $text = [regex]::Replace($text, '(?s)<!--thunderstore:.*?-->', '')
    }
    if ($text -match '<!--/?full-->|<!--thunderstore:') { Fail "unbalanced edition marker in $source" }
    if ($name -eq 'Thunderstore') {
        $number = 0
        foreach ($line in ($text -split '\r?\n')) {
            $number++
            if ($line -match $forbiddenInThunderstoreDocs) { Fail "the Thunderstore $(Split-Path -Leaf $source) line $number describes mod download or the full edition: $line" }
        }
    }
    [System.IO.File]::WriteAllText($target, $text, [System.Text.UTF8Encoding]::new($false))
}

function Pack([string]$name) {
    $stage = Join-Path $dist "stage\$name"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force $stage | Out-Null

    Write-Host "== $name edition $version"
    & dotnet build (Join-Path $repo 'SailwindCoop.csproj') -c Release --nologo -v quiet "-p:Edition=$name" "-p:OutputPath=$stage\"
    if ($LASTEXITCODE -ne 0) { Fail "$name build failed" }
    Get-ChildItem $stage -Filter *.pdb | Remove-Item -Force

    Copy-Item (Join-Path $repo 'Avatars\avatar.bundle') $stage
    foreach ($file in 'README.md', 'CHANGELOG.md', 'MULTIPLAYER_GUIDE.md') {
        EditionDocument (Join-Path $repo $file) (Join-Path $stage $file) $name
    }
    foreach ($file in 'LICENSE', 'icon.png') {
        Copy-Item (Join-Path $repo $file) $stage
    }

    if ($name -eq 'Thunderstore') {
        Copy-Item $manifestPath $stage
        # The edition must not carry the code that sends, receives or installs mod files...
        $dll = [System.IO.File]::ReadAllBytes((Join-Path $stage 'SailwindCoop.dll'))
        $text = [System.Text.Encoding]::ASCII.GetString($dll)
        # Type and member names, case-sensitive: 'Download' and 'Upload' cover every name built on them.
        foreach ($word in 'Download', 'Upload', 'ModFile', 'ModTransfer', 'ShareMods', 'RestartRequired', 'InstalledNames') {
            if ($text.Contains($word)) { Fail "the Thunderstore SailwindCoop.dll contains '$word'" }
        }
        # ...nor the menu and log text of that feature.
        & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'scan-strings.ps1') (Join-Path $stage 'SailwindCoop.dll')
        if ($LASTEXITCODE -ne 0) { Fail 'the Thunderstore SailwindCoop.dll contains mod download text (see above)' }
    }
    else {
        $full = $manifest | ConvertTo-Json -Depth 5 | ConvertFrom-Json
        $full.description = $fullDescription
        $full | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $stage 'manifest.json') -Encoding utf8NoBOM
    }

    foreach ($file in @('SailwindCoop.dll', 'LiteNetLib.dll', 'sounds\landho.wav') + $steamLibraries) {
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
