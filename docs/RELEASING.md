# Releasing a version

Paths and commands below are relative to the repository root.

The whole procedure, in order. Steps 5–8 publish outside this machine: do each of them only when
the maintainer has asked for it in that session; an earlier go-ahead does not carry over.

**Release texts are English only**: the GitHub release title and notes, the
Thunderstore description, the commit message and the tag. No Russian section in them.
`README.md` is English only too (since 0.7.1) and short: what the mod is, installation, how to
host and join, the main settings, troubleshooting. Gameplay detail goes to `MULTIPLAYER_GUIDE.md`.

1. **Check that the tree is what ships.**
   - `dotnet build SailwindCoop.csproj -c Release` and
     `dotnet build SailwindCoop.csproj -c Release -p:Edition=Thunderstore`: 0 errors, 0 warnings.
   - `dotnet run --project Tools/ProtocolSmoke/ProtocolSmoke.csproj -c Release` and
     `dotnet run --project Tools/RuntimeSmoke/RuntimeSmoke.csproj -c Release`: all pass.
   - `СОСТОЯНИЕ.md` is up to date: the status table, what was and was not verified in game, open tasks.

2. **Choose the number.** Thunderstore accepts only `Major.Minor.Patch` (no letters, no suffix), and
   the number is the same in both editions. The handshake does not compare the mod
   version: builds with the same `Protocol.Version` connect, so a release that
   changes behaviour both sides rely on must bump the protocol, not only the version.
   - Third digit: fixes and small additions, `Protocol.Version` unchanged.
   - Second digit: new features, a changed behaviour players rely on, or a new `Protocol.Version`.
   - First digit stays 0 while the mod is declared early development.
   - Two builds that were ever run on two machines must not share a protocol number if their
     message sets differ: bump `Protocol.Version` in the same change that adds a message.

3. **Write the number everywhere.** All of these, every time:
   - `Plugin.cs` — `Version`;
   - `manifest.json` — `version_number` (`pack.ps1` refuses a mismatch);
   - `CHANGELOG.md` — rename `[Unreleased]` to `[X.Y.Z] - YYYY-MM-DD`; if the protocol changed, the
     first line says that everyone must update;
   - `README.md` — `Current Version`, `Wire Protocol`, the overlay title example and the
     "early development (vX.Y.Z)" line;
   - `MULTIPLAYER_GUIDE.md` — the "current build (X.Y.Z) uses protocol N" line and the overlay
     title example;
   - `CLAUDE.md` and `docs/RUNTIME_REFERENCE.md` — their current-version lines;
   - `СОСТОЯНИЕ.md` — the header line and the release section.
   Then build both editions again.

4. **Build the archives.** `pwsh Tools/pack.ps1 -Edition All` writes
    `dist\SailwindCoop-X.Y.Z.zip` and `dist\SailwindCoop-X.Y.Z-thunderstore.zip`. It builds each
   edition into `dist\stage\`, so a running game does not get in the way. It fails when the version
   in `manifest.json` differs from `Plugin.cs`, a description is longer than 250 characters,
    `icon.png` is not 256x256, a Steam library is missing from either archive, or the Thunderstore
    DLL contains mod download code or text (`Tools/scan-strings.ps1`). The Full ZIP includes official
    BepInEx 5.4.23.5 for Windows x64 and installs by extracting into the folder containing `Sailwind.exe`.
    The official loader ZIP is SHA256-pinned; third-party license texts and Doorstop source ship
    under `BepInEx/licenses/` (the Doorstop source as its archive). They are files of the repository
    (`Tools/ReleaseNotices/`, origins in its `SOURCES.md`), not downloads: only the loader ZIP is fetched, once, into `dist\cache`. The
    Doorstop source must be of the version the loader carries (`.doorstop_version`); the script
    fails when it is not, so changing `$bepInExVersion` means replacing that source archive too.
    "Full" names the edition of the code, not a kind of archive.
    **Where each archive goes:** the full archive (the install bundle) goes to GitHub only; the
    Thunderstore archive goes to Thunderstore, Nexus Mods and anywhere else. There is no archive
    of the plugin alone, and the bundle is not uploaded to Nexus Mods (its automated checks
    quarantine it). Packaging-only replacement:
    `pwsh Tools/pack.ps1 -PublishedArchive <published-archive>` — an install bundle or an archive
    made before it (plugin files at the root); only the plugin is taken from it.
    A trial run that leaves `dist\` alone: `-OutputDirectory <folder>`.
    `dist\` is not tracked by git. When replacing only an existing release's packaging, reuse its
    published DLLs and resources byte-for-byte; do not rebuild newer development code under its tag.

5. **Commit, tag, push.** The maintainer commits; give the commit message in English and short.
   The tag is `vX.Y.Z` on that commit: `git tag vX.Y.Z` then `git push origin main --tags`. Build
   the archives from the tagged commit — if anything changed after `pack.ps1`, run it again.

6. **Release notes.** Write `dist\release-notes-X.Y.Z.md` in English only, from the `[X.Y.Z]`
   section of `CHANGELOG.md`: what is new, what changed, what was fixed, whether everyone must
   update, and that the attached archive is the full edition.

7. **GitHub release.** The asset is the full edition only:
    `gh release create vX.Y.Z dist/SailwindCoop-X.Y.Z.zip --title "Sailwind LAN Co-op X.Y.Z" --notes-file dist/release-notes-X.Y.Z.md`.
   Check it with `gh release view vX.Y.Z`: the tag points at the release commit, one asset, the
   notes have no Russian text.

8. **Thunderstore.** Upload `dist\SailwindCoop-X.Y.Z-thunderstore.zip` by hand at
   <https://thunderstore.io/c/sailwind/create/> (team `pander33`, package `SailwindCoop`). There is
   no script for it. Never upload the full archive there: it carries code that downloads mods, which
   Thunderstore forbids (the Steam libraries are allowed and are in both archives). The page text comes from `README.md` and `CHANGELOG.md` inside the
   archive and the description from `manifest.json`. Check the listing after the upload.
   The same archive is uploaded by hand to Nexus Mods (<https://www.nexusmods.com/sailwind/mods/5>);
   its page must name BepInEx 5 as a requirement.

9. **Announce.** The Discord post goes to `dist\discord-post-X.Y.Z.md` and links the GitHub release
   (the full edition) and the Thunderstore page.
