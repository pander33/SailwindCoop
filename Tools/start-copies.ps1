<#
.SYNOPSIS
  Starts several copies of the game for a co-op test, each with its own Unity log.

.DESCRIPTION
  Copies of the game started by hand all write to the same Player.log, line by line in turn, and
  the lines carry no time: what one copy logged cannot be told from the other. This script starts
  the first copy as usual (Player.log) and every further copy with Unity's -logFile, so copy 2
  writes Player-2.log, copy 3 Player-3.log, next to Player.log. The log of the previous run of a
  copy is kept as Player-N-prev.log, as the game does for Player.log.

  BepInEx needs nothing: the second copy already writes BepInEx/LogOutput.log.1.

.EXAMPLE
  pwsh Tools/start-copies.ps1
  pwsh Tools/start-copies.ps1 -Copies 3
  pwsh Tools/start-copies.ps1 -First 2      # one more copy next to a game that is already running
#>
param(
    [ValidateRange(1, 5)][int]$Copies = 2,
    # Number of the first copy to start; 1 is the copy that writes Player.log.
    [ValidateRange(1, 5)][int]$First = 1,
    [string]$GameDir = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent),
    # Pause between copies, so the first one takes the BepInEx log and port 7778 of DevConsole.
    [ValidateRange(0, 60)][int]$DelaySeconds = 3
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $GameDir 'Sailwind.exe'
if (-not (Test-Path $exe)) { throw "Sailwind.exe not found in $GameDir; pass -GameDir." }

$logDir = Join-Path $env:USERPROFILE 'AppData\LocalLow\Raw Lion Workshop\Sailwind'
New-Item -ItemType Directory -Force $logDir | Out-Null

$last = $First + $Copies - 1
for ($copy = $First; $copy -le $last; $copy++) {
    if ($copy -eq 1) {
        Start-Process -FilePath $exe -WorkingDirectory $GameDir
        Write-Host "copy 1: $(Join-Path $logDir 'Player.log')"
    }
    else {
        $log = Join-Path $logDir "Player-$copy.log"
        if (Test-Path $log) { Move-Item -Force $log (Join-Path $logDir "Player-$copy-prev.log") }
        Start-Process -FilePath $exe -WorkingDirectory $GameDir -ArgumentList '-logFile', "`"$log`""
        Write-Host "copy ${copy}: $log"
    }

    if ($copy -lt $last) { Start-Sleep -Seconds $DelaySeconds }
}
