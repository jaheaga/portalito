# Changelog

## 0.1.0.4 — 2026-09-30

- **Destacado rows from TMDB.** New `FeaturedRows` setting: each row is a TMDB list (trending, popular,
  top rated, now playing, upcoming, airing today, on the air, or discover by country/language/genre),
  reconciled with the portal so only titles the portal has are listed. Empty keeps the portal's own rows.
  See [docs/DESTACADO.md](docs/DESTACADO.md).

## 0.1.0.3 — 2026-09-30

- **Device self-provisioning.** With `DeviceSn` blank, the plugin registers a fresh free-tier device
  (`v3/snToken` → serial → `v8/active`) and saves the serial. A saved serial that stops working
  (`aaa100080`) is re-provisioned once. New optional `SnTokenSalt` setting.

## 0.1.0.2 — 2026-09-30

- **Import / export box** on the config page: paste a `.env` or JSON object to fill every field; export the
  current values to move them to another server.

## 0.1.0.1 — 2026-09-29

- First working release: built against the Jellyfin 10.11 ABI floor so it loads on any 10.11.x server.

## 0.1.0.0 — 2026-09-29 (withdrawn)

- Initial public release. Loaded as `NotSupported` on servers older than 10.11.11; replaced by 0.1.0.1.
