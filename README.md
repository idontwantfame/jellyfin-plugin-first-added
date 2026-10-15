# First Added for Jellyfin

Preserve the original date a movie or episode entered your library when a quality upgrade replaces its file. A genuinely new title keeps its current date. No Sonarr or Radarr connection or credentials are needed.

Version **0.1.0.0**, targeting **Jellyfin 12 / .NET 10**. Built against Jellyfin 12.0.0 APIs and verified on Jellyfin 12.2.0. Automated tests cover replacement matching, manual edits, locks, durable journals, rollback and event delivery using actual Jellyfin assemblies with a simulated repository.

Download a ZIP from [Releases](https://github.com/idontwantfame/jellyfin-plugin-first-added/releases). To install through Jellyfin, add this plugin repository URL under Dashboard → Plugins → Repositories, then install First Added from the catalogue and restart:

```text
https://raw.githubusercontent.com/idontwantfame/jellyfin-plugin-first-added/main/manifest.json
```

## Setup

1. Build a package using the commands below. Extract its DLL into a dedicated folder beneath Jellyfin's plugin directory and restart Jellyfin.
2. Open Dashboard → Plugins → First Added. Leave preservation disabled initially.
3. Select libraries and item types, then save settings.
4. Initialize selected libraries. This records their **current Jellyfin dates** without changing media metadata.
5. Enable preservation and save. Preview shows the current comparison; Preserve dates now runs a reconciliation immediately.

Library events handle subsequent imports and updates. An hourly scheduled task catches missed updates. Newly selected libraries or item types require initialization. Settings changes take effect after saving; action buttons use the saved selection.

## Matching and dates

Movies use TMDb, TVDb or IMDb IDs. Episodes use their own provider IDs and/or their series provider IDs plus season and episode numbers, including season zero specials. Matching works across changes to filenames, paths and Jellyfin item IDs. Conflicting provider IDs, reidentified items, virtual items, unidentified titles and combined multi-episode files are skipped conservatively.

The ledger groups the same identity across selected libraries and versions. Initializing multiple existing versions records their earliest current date. Later initialization preserves existing recorded baselines. Records survive deletions, so reacquiring the same title still uses its recorded date. Different cuts sharing the same provider identity share a baseline.

Live manual edits to Date added become the baseline by default. Other metadata edits that leave the date unchanged do not change the baseline. Disable that setting to enforce stored dates instead. Fully locked metadata items are always protected. Offline edits made while the plugin is stopped cannot be distinguished from refreshes.

Only the movie or episode's `DateCreated` metadata is updated through Jellyfin's repository API. Titles, watched state, source files and filesystem timestamps are untouched. Jellyfin's standard latest-items query sorts movies/episodes by this field before grouping them ([Jellyfin source](https://github.com/jellyfin/jellyfin/blob/master/Emby.Server.Implementations/Library/UserViewManager.cs)). Clients using other sort fields or caching may behave differently; event-based repair can briefly expose a replacement before it is processed. Series/season creation dates are not rewritten.

**Already lost dates cannot be inferred from a replacement file.** Initialize before future upgrades; this version does not import historic Sonarr/Radarr acquisition dates.

## Recovery and storage

The private `first-added` folder under Jellyfin's data directory contains `ledger.json` and a previous-state backup. It stores provider identities, Jellyfin item IDs, timestamps, initialized scopes and a recovery journal; no media names, paths or external credentials. Include this folder in your server backups. Runtime state is never part of a release archive.

Each metadata change is journaled and flushed to disk before writing to Jellyfin. Failed writes remain retryable after restart. Invalid state fails closed; inspect the server log, stop Jellyfin and restore a known-good ledger backup before restarting. The previous-state file is a convenience, not a substitute for independent backups.

Rollback revokes **all** initialized scopes before restoring dates still owned by the plugin. It skips locks, changed identities and later manual edits, and retains incomplete journal entries for retry. It only restores items still present under their recorded Jellyfin IDs. Preservation stays inactive until you explicitly initialize scopes again, even if Enable preservation remains checked. Reinitializing retains stored baselines; it is not a history reset. Stop Jellyfin and back up the ledger before manually removing the plugin's state to start over.

All plugin HTTP endpoints require an elevated Jellyfin administrator. No direct database edits, NFO writes or remote lookups are used.

## Build and test

Prerequisite: .NET 10 SDK. No additional testing framework is required.

```sh
dotnet build Jellyfin.Plugin.FirstAdded.Tests -c Release --warnaserror
dotnet run --project Jellyfin.Plugin.FirstAdded.Tests -c Release --no-build
python3 -m unittest discover -s tests
python3 scripts/package.py
```

The ZIP, SHA-256/MD5 checksums, release metadata and catalogue entry are written to `dist/`. Only the plugin DLL and license are included in the ZIP; debug symbols and machine-specific build outputs are excluded. Source paths are mapped to a neutral path when compiling. The CI workflow builds, runs safety tests and uploads the package as an artifact without publishing a release.

For a fork, pass `--repository owner/repository` when packaging. Publish the ZIP and checksums under the matching four-part version tag, then copy `dist/manifest.json` into the repository root only after the download exists and its checksum has been verified. Do not replace the assets of an existing version with different bytes.

## License

MIT; see [LICENSE](LICENSE). This is an independent plugin, not an official Jellyfin component.
