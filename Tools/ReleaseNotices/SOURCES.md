# Third-party notices shipped in the install bundle

`Tools/pack.ps1` copies every file of this folder except this one into `BepInEx/licenses/` of
`SailwindCoop-<version>.zip`. They belong to the loader the bundle carries (BepInEx 5.4.23.5 for
Windows x64) and are kept in the repository so that the archive is the same on every run and builds
without a network.

| File | Taken from |
|---|---|
| `BepInEx-LICENSE.txt` | the BepInEx repository, `LICENSE` (added by hand with the bundle, 2026-10-09; the exact revision was not recorded) |
| `Doorstop-LICENSE.txt` | <https://raw.githubusercontent.com/NeighTools/UnityDoorstop/v4.5.0/LICENSE> |
| `Harmony-LICENSE.txt` | <https://raw.githubusercontent.com/BepInEx/BepInEx.Harmony/d4cdcb4cdeac14a0b77012165f5f5a9f5032a9fa/LICENSE> |
| `Cecil-LICENSE.txt` | <https://raw.githubusercontent.com/jbevain/cecil/0.10.4/LICENSE.txt> |
| `MonoMod-LICENSE.txt` | <https://raw.githubusercontent.com/MonoMod/MonoMod/reorganize/LICENSE> (a branch, read 2026-10-09) |
| `UnityDoorstop-4.5.0-source.zip` | <https://codeload.github.com/NeighTools/UnityDoorstop/zip/refs/tags/v4.5.0> |

Doorstop is LGPL, so its source ships beside its binary and must be of the same version. The
version is whatever the loader archive carries (its `.doorstop_version` file): `pack.ps1` reads it
and fails when `UnityDoorstop-<that version>-source.zip` is not here, or when a source archive of
another version is. After changing `$bepInExVersion` in `pack.ps1`, replace the source archive and
check the license files against the new loader.
