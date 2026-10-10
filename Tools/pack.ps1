<#
.SYNOPSIS
  Builds a release archive of one or both editions into dist\.

.DESCRIPTION
  Thunderstore  dist\SailwindCoop-<version>-thunderstore.zip   no mod download; mod manager package
  Full          dist\SailwindCoop-<version>.zip               everything; the install bundle with BepInEx

  "Full" and "Thunderstore" name the two editions of the code (with and without mod download), not
  kinds of archive. The full edition has exactly one archive, the install bundle, and it goes to
  GitHub only. The Thunderstore archive goes everywhere else (Thunderstore, Nexus Mods). There is
  no archive of the plugin alone.

  Each edition is built into its own staging folder under dist\stage\, so the game's plugin folder
  is not touched and a running game does not lock the build. The version comes from Plugin.cs and
  must match manifest.json.

.EXAMPLE
  pwsh Tools/pack.ps1                      # complete manual-install bundle
  pwsh Tools/pack.ps1 -Edition All          # also build Thunderstore
  pwsh Tools/pack.ps1 -Edition Thunderstore
  pwsh Tools/pack.ps1 -PublishedArchive dist\SailwindCoop-0.4.3.zip   # new packaging, published plugin
  pwsh Tools/pack.ps1 -Edition All -OutputDirectory $env:TEMP\pack-trial   # dist\ is not touched
#>
param(
    [ValidateSet('All', 'Full', 'Thunderstore')]
    [string]$Edition = 'Full',
    # Packaging-only update: preserve the published plugin instead of building development code.
    # Takes an install bundle or an archive made before it (the plugin files at the root).
    [string]$PublishedArchive,
    # Where the archives, the staging folders and the loader cache go; dist\ of the repository by
    # default. A trial run into another folder leaves the archives in dist\ as they are.
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dist = if ($OutputDirectory) { [System.IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $repo 'dist' }
if ($PublishedArchive) { $PublishedArchive = [System.IO.Path]::GetFullPath($PublishedArchive) }

# manifest.json in the repository describes the Thunderstore edition; the full archive gets this text.
$fullDescription = 'Play Sailwind with friends over Steam, LAN or VPN. Shared sailing, mod sharing, shared wallet, gestures and crew sleep.'

# The Steam libraries. Both editions carry them: Thunderstore allows them (support, 2026-10-07).
$steamLibraries = @('steam_api64.dll', 'Facepunch.Steamworks.Win64.dll')

# Pin the official loader, not the developer's installed BepInEx/config folder.
$bepInExVersion = '5.4.23.5'
$bepInExSha256 = '82F9878551030F54657792C0740D9D51A09500EEAE1FBA21106B0C441E6732C4'

function IncludeLoader([string]$target) {
    $cache = Join-Path $dist 'cache'
    New-Item -ItemType Directory -Force $cache | Out-Null
    $loaderZip = Join-Path $cache "BepInEx_win_x64_$bepInExVersion.zip"
    if (-not (Test-Path $loaderZip)) {
        Invoke-WebRequest "https://github.com/BepInEx/BepInEx/releases/download/v$bepInExVersion/BepInEx_win_x64_$bepInExVersion.zip" -OutFile $loaderZip
    }
    if ((Get-FileHash $loaderZip -Algorithm SHA256).Hash -ne $bepInExSha256) { Fail 'BepInEx archive SHA256 mismatch' }
    Expand-Archive -LiteralPath $loaderZip -DestinationPath $target -Force
    foreach ($file in 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'BepInEx\core\BepInEx.dll', 'BepInEx\core\BepInEx.Preloader.dll', 'BepInEx\core\0Harmony.dll') {
        if (-not (Test-Path (Join-Path $target $file))) { Fail "BepInEx loader file missing: $file" }
    }

    # The third-party notices and Doorstop's source are files of the repository (Tools\ReleaseNotices),
    # not downloads: the archive is then the same on every run and builds without a network. Where
    # each came from is in Tools\ReleaseNotices\SOURCES.md.
    $notices = Join-Path $PSScriptRoot 'ReleaseNotices'
    foreach ($file in 'BepInEx-LICENSE.txt', 'Doorstop-LICENSE.txt', 'Harmony-LICENSE.txt', 'Cecil-LICENSE.txt', 'MonoMod-LICENSE.txt') {
        if (-not (Test-Path (Join-Path $notices $file))) { Fail "Tools\ReleaseNotices\$file is missing" }
    }
    # Doorstop is LGPL: the source that ships beside the binary must be of the binary's version, and
    # that version is decided by the loader archive, not by this script.
    $doorstopVersion = (Get-Content (Join-Path $target '.doorstop_version') -Raw).Trim()
    $doorstopSource = "UnityDoorstop-$doorstopVersion-source.zip"
    if (-not (Test-Path (Join-Path $notices $doorstopSource))) {
        Fail "the loader carries Doorstop $doorstopVersion, but Tools\ReleaseNotices\$doorstopSource is missing. Add the source of that tag (https://github.com/NeighTools/UnityDoorstop/releases/tag/v$doorstopVersion), check Doorstop-LICENSE.txt against it and remove the older source archive"
    }
    $otherSources = @(Get-ChildItem $notices -Filter 'UnityDoorstop-*-source.zip' | Where-Object { $_.Name -ne $doorstopSource })
    if ($otherSources.Count -gt 0) { Fail "Tools\ReleaseNotices holds Doorstop source of another version: $($otherSources.Name -join ', ')" }

    $licenses = Join-Path $target 'BepInEx\licenses'
    New-Item -ItemType Directory -Force $licenses | Out-Null
    Get-ChildItem $notices -File | Where-Object { $_.Name -ne 'SOURCES.md' } | Copy-Item -Destination $licenses
}

# The plugin files of a published archive: inside BepInEx\plugins\SailwindCoop of an install bundle,
# at the root of an archive made before the bundle (0.4.2 and earlier).
function PublishedPluginFolder([string]$archive, [string]$unpackTo) {
    if (-not (Test-Path -LiteralPath $archive)) { Fail "published archive not found: $archive" }
    if (Test-Path $unpackTo) { Remove-Item $unpackTo -Recurse -Force }
    Expand-Archive -LiteralPath $archive -DestinationPath $unpackTo
    foreach ($candidate in (Join-Path $unpackTo 'BepInEx\plugins\SailwindCoop'), $unpackTo) {
        if ((Test-Path (Join-Path $candidate 'manifest.json')) -and (Test-Path (Join-Path $candidate 'SailwindCoop.dll'))) { return $candidate }
    }
    Fail "$(Split-Path -Leaf $archive) is not a SailwindCoop archive: manifest.json and SailwindCoop.dll were found neither in BepInEx\plugins\SailwindCoop nor at its root"
}

function Fail([string]$message) { throw "pack: $message" }

if ($PublishedArchive -and $Edition -ne 'Full') { Fail '-PublishedArchive requires -Edition Full' }

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
    $archiveStage = Join-Path $dist "stage\$name"
    if (Test-Path $archiveStage) { Remove-Item $archiveStage -Recurse -Force }
    # Full installs by extracting into the game root. Thunderstore keeps its package layout.
    $stage = if ($name -eq 'Full') { Join-Path $archiveStage 'BepInEx\plugins\SailwindCoop' } else { $archiveStage }
    New-Item -ItemType Directory -Force $stage | Out-Null

    Write-Host "== $name edition $version"
    if ($PublishedArchive) {
        # Only the plugin is taken from the published archive; its loader, if it had one, is replaced
        # by the pinned one below like in any other build.
        $unpacked = Join-Path $dist 'stage\published'
        $pluginFolder = PublishedPluginFolder $PublishedArchive $unpacked
        Copy-Item (Join-Path $pluginFolder '*') $stage -Recurse -Force
        Remove-Item $unpacked -Recurse -Force
        $publishedManifest = Get-Content (Join-Path $stage 'manifest.json') -Raw | ConvertFrom-Json
        if ($publishedManifest.version_number -ne $version) { Fail 'published archive version does not match Plugin.Version' }
        # Keep release-specific gameplay instructions; update only installation from the current README.
        $newReadme = Join-Path $archiveStage 'installation-source.md'
        EditionDocument (Join-Path $repo 'README.md') $newReadme 'Full'
        $current = [System.IO.File]::ReadAllText($newReadme)
        $readmePath = Join-Path $stage 'README.md'
        $published = [System.IO.File]::ReadAllText($readmePath)
        foreach ($heading in 'Installation', 'Установка') {
            $pattern = '(?s)### [^\r\n]*' + $heading + '\r?\n.*?(?=\r?\n### )'
            $replacement = [regex]::Match($current, $pattern).Value
            if (-not $replacement -or -not [regex]::IsMatch($published, $pattern)) { Fail "README section missing: $heading" }
            $published = [regex]::Replace($published, $pattern, [System.Text.RegularExpressions.MatchEvaluator]{ param($match) $replacement })
        }
        [System.IO.File]::WriteAllText($readmePath, $published, [System.Text.UTF8Encoding]::new($false))
        Remove-Item $newReadme
    }
    else {
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
    elseif (-not $PublishedArchive) {
        $full = $manifest | ConvertTo-Json -Depth 5 | ConvertFrom-Json
        $full.description = $fullDescription
        $full | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $stage 'manifest.json') -Encoding utf8NoBOM
    }

    foreach ($file in @('SailwindCoop.dll', 'LiteNetLib.dll', 'sounds\landho.wav') + $steamLibraries) {
        if (-not (Test-Path (Join-Path $stage $file))) { Fail "$file is missing from the $name archive" }
    }

    if ($name -eq 'Full') { IncludeLoader $archiveStage }

    $archiveName = if ($name -eq 'Full') { "SailwindCoop-$version.zip" } else { "SailwindCoop-$version-thunderstore.zip" }
    $zip = Join-Path $dist $archiveName
    if (Test-Path $zip) { Remove-Item $zip -Force }
    # CreateFromDirectory also includes the loader's hidden .doorstop_version file.
    [System.IO.Compression.ZipFile]::CreateFromDirectory($archiveStage, $zip)
    Write-Host ("   " + $zip + "  " + [math]::Round((Get-Item $zip).Length / 1MB, 1) + " MB")
    Get-ChildItem $archiveStage -Recurse -File | ForEach-Object { Write-Host ("     " + $_.FullName.Substring($archiveStage.Length + 1)) }
}

$editions = if ($Edition -eq 'All') { 'Thunderstore', 'Full' } else { , $Edition }
foreach ($name in $editions) { Pack $name }
