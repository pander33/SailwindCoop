<#
.SYNOPSIS
  Lists the string literals of a .NET assembly that match a pattern.

.DESCRIPTION
  Used by Tools/pack.ps1 to prove that the Thunderstore edition carries no text of the mod download it
  was built without. Reads the #US (user string) heap, where every literal of the code lives.
  Exits with status 1 when something matches.

.EXAMPLE
  pwsh Tools/scan-strings.ps1 bin\Thunderstore\SailwindCoop.dll
#>
param(
    [Parameter(Mandatory = $true)][string]$Path,
    # Download wording in any case; the two captions exactly as the full edition shows them.
    [string]$Pattern = '(?i:download)|Sharing:|RESTART THE GAME|mod chunk|mods were installed'
)

$ErrorActionPreference = 'Stop'
$stream = [System.IO.File]::OpenRead((Resolve-Path $Path))
try {
    $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
    $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
    $found = [System.Collections.Generic.SortedSet[string]]::new([System.StringComparer]::Ordinal)
    $handle = [System.Reflection.Metadata.Ecma335.MetadataTokens]::UserStringHandle(1)
    while (-not $handle.IsNil) {
        $text = $metadata.GetUserString($handle)
        if ($text -cmatch $Pattern) { [void]$found.Add($text) }
        $handle = [System.Reflection.Metadata.Ecma335.MetadataReaderExtensions]::GetNextHandle($metadata, $handle)
    }
}
finally { $stream.Dispose() }

foreach ($text in $found) {
    $line = $text -replace '\r?\n', '\n'
    Write-Host ('  ' + $line.Substring(0, [Math]::Min(150, $line.Length)))
}
Write-Host "$($found.Count) matching string(s) in $Path"
exit ([int]($found.Count -gt 0))
