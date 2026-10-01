# Development

## Build and test

Requires the .NET 9 SDK.

```sh
dotnet build src/Jellyfin.Plugin.Portalito -c Release   # must be 0 warnings
dotnet test  Jellyfin.Plugin.Portalito.sln -c Release   # the whole suite, offline
./scripts/scrub-check.sh                                 # must print "scrub-check: clean"
python3 packaging/package.py                             # -> artifacts/Portalito_<version>.zip + its md5
```

The tests never touch the network: the portal (`FakePortalTransport`, which decrypts each request and answers
a scripted, encrypted envelope), TMDB (`FakeTmdbTransport`) and ffprobe are faked. The config page is checked
against `PluginConfiguration` by reflection (`ConfigPageTests`), and a Kestrel pipeline test exercises the
proxy routes end to end.

## Layout

```
src/Jellyfin.Plugin.Portalito/     the plugin (see ARCHITECTURE.md)
tests/Jellyfin.Plugin.Portalito.Tests/
packaging/                         meta.json (plugin metadata), package.py, update_manifest.py
scripts/scrub-check.sh             the no-secrets gate
assets/branding/                   the logo (icon, banner) and make_logo.py, which draws them; embedded in the DLL
manifest.json                      the Jellyfin plugin-repository manifest (updated by the release CI)
.env.example                       every setting as a PORTALITO_* key, blank
.github/workflows/                 test.yml (every push), release.yml (v* tags)
docs/                              this documentation
private/                           git-ignored operator notes (never committed)
```

## The rule: nothing service-specific in the tree

Portalito is a blank framework. The tree must never contain a real portal's name, hosts, keys, salts,
signing constants, captured tokens, account data or private paths. This is enforced three ways:

1. `scripts/scrub-check.sh` — a case-insensitive denylist grep over the tree;
2. `ScrubGuardTests` — the same denylist as an xUnit test, so `dotnet test` fails too;
3. the CI `test` workflow runs both.

Keep the script's `deny` array and the test's `Deny` list identical. Both skip `bin/`, `obj/`, `.git/`,
`artifacts/`, the real `.env` and the git-ignored `private/` directory (they hold the operator's own values
and are never published); `.env.example` is scanned.

Real values belong in the git-ignored `.env` or `private/`, and in each server's plugin configuration.
Test data must be synthetic (neutral hosts like `host-a.test`, keys like `PortalCipherTests.TestKeyHex`).

## Conventions

- Code, comments, docs, commit messages: English. Channel folder names and task names are Spanish (what
  users see).
- Match the surrounding code's comment density: comments explain *why* (often a measured fact about the
  portal or Jellyfin, with the date it was measured).
- No dead code behind flags.

## Adding a setting

1. `Configuration/PluginConfiguration.cs` — the property, `= string.Empty` (never a real default), with a doc
   comment.
2. `Configuration/configPage.html` — an `<input id="X" name="X" ...>` (`type="password"` for secrets), and `X`
   in the `textFields` array. `ConfigPageTests` fails if either is missing.
3. `PortalitoRuntime.Fingerprint` — add the field, or saving it won't take effect until a restart.
4. If the portal client needs it: `Portal/PortalOptions.cs` (record property + `FromConfiguration`).
5. `.env.example` — a blank `PORTALITO_*` key (the import box matches keys loosely, so the derived name works).
6. Docs: `docs/CONFIGURATION.md`.

## Changing the channel tree

- New folder kinds need a `VodItemKind`, a factory, and `ToString`/`TryParse` cases in `Catalog/VodItemId.cs`.
- Anything that changes what a listing contains or how ids are read: **bump `DataVersion`** in
  `PortalitoVodChannel` (and add a line to its history comment). Jellyfin caches listings for 3 hours by
  (channel, folder, `DataVersion`, cache key) and won't re-fetch otherwise. Configuration changes are already
  covered: the cache key changes on every config change (`PortalitoRuntime.CacheStamp` — a counter, not a
  config hash, because on a cache hit Jellyfin serves the folder's last stored items).
- A saved config that needs upgrading (a new default, a renamed value): add it to
  `PluginConfiguration.ApplyMigrations`, which runs once at plugin load, guarded by a flag property.
- Folders that should be searchable must be opened by `CatalogIndexWalk.ShouldOpen`.
- Remember that Jellyfin re-sorts channel items client-side (A–Z by default); a listing's order is not
  under the plugin's control.

## Testing against a real server

Use a **separate test server**, never a production one. Install from the plugin repository (see
[RELEASING.md](RELEASING.md)), configure it (the import box takes your `.env`), then use **Save and test
connection**. Useful API calls, with an admin API key:

```sh
curl "$SERVER/Plugins?api_key=$KEY"                                   # plugin status (Active / NotSupported / Restart)
curl -X POST "$SERVER/Portalito/Admin/Test?api_key=$KEY"              # the connection test
curl "$SERVER/Channels?api_key=$KEY"                                  # the channel's id
curl "$SERVER/Channels/$CHANNEL/Items?userId=$USER&api_key=$KEY"      # root folders
curl "$SERVER/Channels/$CHANNEL/Items?userId=$USER&folderId=$ITEM&api_key=$KEY"   # a folder (Jellyfin item id)
curl "$SERVER/System/Logs/Log?name=log_YYYYMMDD.log&api_key=$KEY"     # the server log
```

`folderId` is Jellyfin's item id (a GUID from a previous listing), not the plugin's internal id.
