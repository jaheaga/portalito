# Changelog

## Unreleased

- **Destacado rows are listed best-rated first** instead of A–Z. TMDB rows use TMDB's rating (from 10 votes up), else
  the portal's; unrated titles go last. Another order picked in the app's sort menu still applies. New setting
  `SortFeaturedByRating` (on by default) turns it off.

## 0.1.2.0 — 2026-10-02

The rest of the 2026-10-02 code review.

- **Each series is one item in Jellyfin.** A show's folder was named after whichever season a listing happened to show
  first, so the same show appeared as several series, and the old one was deleted with its seasons when a newer season
  took the lead. Shows are now identified by their name, the same in every folder. Upgrading: old duplicates go away
  as their folders are browsed again.
- **New: "Rotate signing secret"** (config page, Advanced). Revokes every stream URL the plugin has handed out,
  including the permanent ones in the "Siguiendo" files, which are rewritten right away. Use it if a link leaked.
- **Saving a setting no longer interrupts playback or signs the account in again** unless the setting is about the
  portal itself (a TMDB key or a Destacado row used to restart everything).
- **A TMDB outage no longer leaves titles without synopsis or poster for a week**, and a Destacado row built while
  the portal hiccuped is no longer kept for six hours.
- **"Siguiendo": playback is forced through the server for every client call**, subtitles are only fetched for users
  who can see the episode, and copying progress from the channel never undoes newer progress.
- Streams: a CDN that connects but never answers now fails over after 20 s instead of hanging; a live channel whose
  license is in use elsewhere no longer re-registers every few seconds; posters can't point the server at its own LAN.
- Faster start-up (the series repair no longer looks up 30,000 seasons), fewer portal calls when the channel is opened
  cold, runtimes saved in batches, and a damaged runtimes file is kept aside instead of overwritten.
- Destacado rows with the same name stay separate; a film with no TMDB year only matches an unambiguous portal title.
- The log now records any Portalito movie or episode Jellyfin removes (to measure whether movies need the same
  treatment as series).
- Known issue (Jellyfin): a portal subtitle that has to be burned into the video (a client that can't show SRT)
  fails to play; Jellyfin reads the local subtitle file as if it were remote. Clients that show subtitles themselves
  (web, Android TV) aren't affected.

## 0.1.1.6 — 2026-10-02

Fixes from a code review.

- **"Siguiendo" keeps your progress if a show is renamed.** Jellyfin tied a show's watch history to its folder, and
  the folder follows the show's name; each show now carries a stable id that Jellyfin keys the history by.
- **"Siguiendo" series are hidden from users who can't open the Portalito channel.** Someone allowed into every
  library (a kids profile with channels turned off, say) could find and play them in search; such users now get the
  series blocked.
- **Changing the plugin's signing secret or server address no longer breaks "Siguiendo" playback** for up to 12
  hours: the episode links are rewritten on the next sync.
- **A wrong "Siguiendo library folder" can't take over a real library.** A folder that is another library's, inside
  or around one, or full of other files is refused (with a log message); after changing the folder, the library left
  on the old one is removed.
- **A portal hiccup that returns a season with no episodes no longer deletes that season** from "Siguiendo".
- **With a Portalito account, "signed in on another device" no longer breaks the plugin until a restart.** It now
  waits 10 minutes, then signs back in (once; another takeover starts a new wait).
- **Saving the settings no longer registers a new free device each time** when Device SN is left blank.
- **TMDB posters are no longer deleted** by the weekly "Reparar imágenes Portalito".
- **Destacado works with fewer than four catalogs** (without TMDB rows it used to fail entirely).
- Lighter "Siguiendo" syncs; the release workflow now checks the tag matches the version.
- Repository: the scrub guard no longer lists the values it guards against (it stores hashes), and the
  repository's history was rewritten to remove them.

## 0.1.1.5 — 2026-10-01

- **The channel gets the Portalito logo on its own.** Jellyfin only asks a channel for its images when it first
  creates it, so a server that had the channel before 0.1.0.8 never got the logo without a manual "Refresh metadata".
  A new task, **"Logo del canal Portalito"** (at startup and weekly), fills in whichever of the channel's images are
  missing: the square logo, the banner and the backdrop. An image you set by hand is kept.

## 0.1.1.4 — 2026-10-01

- **Each Destacado row shows its own picture again.** Rows were identified by their position in the list, so adding
  rows (like 0.1.1.3's platform rows) gave the rows after them the position, and the picture Jellyfin had stored, of
  another row: Disney+ showed "Películas de anime", HBO showed "Series coreanas". Rows are now identified by their
  name, so reordering or adding rows never mixes them up. Renaming a row gives it a fresh picture.

## 0.1.1.3 — 2026-10-01

- **Destacado rows by streaming platform.** Six new default rows list each platform's own series, most popular
  first, keeping only what your portal has: **Originales Netflix, Apple TV+, Disney+, HBO, Prime Video and
  Paramount+**. Measured on a test server: 34 to 60 playable series per row.
- **New row options** for your own rows: `network=` (a platform's own series: `netflix`, `apple`, `disney`, `hbo`,
  `prime`, `paramount`, …) and `provider=` with `region=` (what you can stream on a platform in your country, movies
  too, e.g. `provider=netflix region=CO`). See docs/DESTACADO.md.
- Upgrading: if your Destacado rows are still the previous default, they get the platform rows automatically. If you
  changed them, they're left alone; add the rows you want from docs/DESTACADO.md.

## 0.1.1.2 — 2026-10-01

- **Subtitles in "Next Up" episodes.** Episodes played from the hidden "Portalito · Siguiendo" library now offer the
  portal's subtitle files (e.g. Spanish and English), like the channel does. They're fetched the first time an
  episode is played.
- **A series you're watching no longer drops out of "Siguiendo".** Only finished or half-watched episodes counted as
  watching, so an episode stopped in the first minute, or one that failed to start, could make the sync drop the
  series and delete its files. Any play in the last 60 days now counts.
- **A series that leaves the library really leaves.** When the last series was removed, Jellyfin skipped scanning the
  now-empty folder and kept listing its episodes, which then failed to play. The folder now always holds a small
  `portalito-siguiendo.txt`, so Jellyfin always scans it.
- Upgrading: a series dropped by mistake comes back on the next sync (at startup).

## 0.1.1.1 — 2026-10-01

- **Resuming or jumping into the middle of a movie or episode no longer fails at random.** To seek, Jellyfin's
  ffmpeg makes 20-30 quick requests, each opening a new connection to the portal's CDN; when one of them took longer
  than 4 seconds to connect, the seek failed and playback stopped with an error (about 1 try in 5 when resuming an
  episode). The plugin now retries such a connection up to 3 times. Measured: 0 failures in 15 resumes, from 2 in 10.

## 0.1.1.0 — 2026-10-01

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
