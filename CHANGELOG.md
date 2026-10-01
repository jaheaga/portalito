# Changelog

## Unreleased

- **Portalito series now show up in Jellyfin's "Next Up".** Jellyfin builds Next Up only from libraries, never
  from channels, so the plugin now keeps a library called **"Portalito · Siguiendo"** with just the series people
  are watching (played in the last 60 days, or marked favorite), not the whole catalog. It's hidden from everyone's
  menus; its episodes appear only in Next Up and Continue Watching, and play through the plugin like the channel.
  Each person sees only the series they watched.
- What you already watched in the channel carries over, and the sync runs right after you stop an episode, so the
  next one is waiting on the home screen. New episodes and seasons appear within 12 hours.
- New settings: **Keep the hidden "Portalito · Siguiendo" library** (on by default) and its folder. New scheduled
  task **"Sincronizar Portalito · Siguiendo"** (at startup, every 30 minutes).
- Known issue: if the portal's CDN times out when an episode starts, the player shows an error; pressing play again
  works. The channel has the same behavior.

## 0.1.0.9 — 2026-10-01

- **Movies and episodes now show up in Jellyfin's "Continue Watching" and resume where you left off.** Jellyfin
  only keeps a playback position when it knows a title's length; Portalito never told it, so Jellyfin marked
  everything as watched the moment it started. The plugin now gives each title its runtime as it starts playing
  (measured once, then remembered) and in later listings.
- **Episodes now carry their season number and show name**, so they sort properly inside a series.
- New scheduled task **"Reparar series Portalito"** fills in the runtime and season on titles Jellyfin already had;
  it runs automatically once after updating, then weekly.
- Upgrading: a title becomes resumable the first time it's played after updating. Series still don't appear in
  Jellyfin's "Next Up" row — Jellyfin only builds that row from libraries, never from channels; a later version
  will address it.

## 0.1.0.8 — 2026-10-01

- **A logo for the channel.** "Portalito VOD" now shows the Portalito logo (a lit doorway with a play button)
  instead of a blank tile: the square logo as its main image, a banner as its thumb and backdrop. They're
  built in, so they work even before the portal is configured. The plugin also shows the banner in Jellyfin's
  plugin catalog.
- **New optional setting "Channel image URL"** to use your own image instead.
- Upgrading: Jellyfin keeps the channel's old (empty) image until it refreshes it — open the channel's
  ⋮ menu → **Refresh metadata** → *Replace existing images*, or wait for the next library scan.

## 0.1.0.7 — 2026-09-30

- Fix 0.1.0.6's cache key: it was a hash of the configuration, so reverting a setting to an earlier value
  within 3 hours reused that value's old cache file — and on a cache hit Jellyfin serves the folder's last
  stored items, i.e. the in-between listing. The key is now a change counter plus a per-start nonce, so it
  never repeats.

## 0.1.0.6 — 2026-09-30

- **Setting changes show up immediately in the channel.** Jellyfin caches every channel folder's listing for
  3 hours; the channel now gives Jellyfin a cache key that follows the configuration, so saving the Destacado
  rows (or catalogs, or the TMDB key) is reflected in the next listing. Before, the old rows could stay for
  up to 3 hours.
- **Configs saved by 0.1.0.4 get the default Destacado rows.** 0.1.0.4 saved an empty `FeaturedRows`, which
  kept 0.1.0.5's new default from applying. A one-time migration fills an empty, never-seeded field with the
  default rows; clearing the field afterwards still turns them off.

## 0.1.0.5 — 2026-09-30

- **Default Destacado rows.** `FeaturedRows` now comes pre-filled with 11 TMDB rows (Tendencias, Estrenos en
  cine, Próximamente, Películas/Series populares, En emisión hoy, Anime del momento, Películas de anime,
  Series coreanas, Películas/Series mejor valoradas), active as soon as a TMDB key is set. Clear the field
  to use the portal's own rows.

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
