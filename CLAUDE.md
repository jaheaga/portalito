# Portalito — notes for AI agents

A Jellyfin 10.11 channel + Live TV plugin (.NET 9) for IPTV "portal" middleware. Public repo. Read
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) and [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) first.

## Non-negotiable

- **Nothing service-specific in the tree.** No real portal name, hosts, keys, salts, signing constants,
  captured tokens, accounts or private paths — not in code, tests, docs or commit messages. Every such value
  is a config field that defaults to empty (the only non-empty default, `FeaturedRows`, is generic TMDB lists). `./scripts/scrub-check.sh` must print `scrub-check: clean`, and
  `ScrubGuardTests` enforces the same denylist in `dotnet test`. Test data is synthetic.
- **Real values live only in git-ignored places:** `.env` (every setting as `PORTALITO_*`) and `private/`
  (operator notes). Never `git add -A`; stage files explicitly and check `git status` shows neither.
- **Test on a separate test server, never a production one.** The operator's `private/` notes say which is
  which.
- Commits: author `Jaime Aguilar <jaheaga@gmail.com>`, **no `Co-Authored-By` trailer**. Push over SSH.
- Code, comments, docs and commit messages in English; user-facing folder/task names in Spanish.

## Measured traps

- Build against the ABI floor (`Jellyfin.Controller` 10.11.0) or the plugin loads as `NotSupported` on older
  10.11.x servers. Never reuse a release version/tag (the asset CDN serves stale zips). See
  [docs/RELEASING.md](docs/RELEASING.md).
- Bump `DataVersion` in `PortalitoVodChannel` whenever a code change alters listings — Jellyfin caches them 3 h. Config
  changes are covered by the channel's `GetCacheKey` (a config hash).
- A new non-empty default doesn't reach configs that already saved the field: use `PluginConfiguration.ApplyMigrations`.
- Jellyfin re-sorts channel items client-side (A–Z by default); a plugin can't impose an order.
- A new setting needs the property, a page input + `textFields` entry, the runtime `Fingerprint`, and a
  `.env.example` key (checklist in docs/DEVELOPMENT.md).
- The release workflow commits `manifest.json` to `main` after a tag — pull before pushing again.
- Updating a server: wait until `GET /Packages` shows the new version *before* uninstalling the old one.
