# Architecture

Portalito is a Jellyfin 10.11 plugin (.NET 9) that exposes an IPTV "portal" middleware as a Jellyfin
**channel** (VOD + a live-channel browser) and a Jellyfin **Live TV service**. Everything runs inside
Jellyfin: there is no helper server. Every service-specific value comes from the plugin configuration —
see [CONFIGURATION.md](CONFIGURATION.md).

## Components

```
Plugin.cs / PluginServiceRegistrator.cs      registration: config page, services, channel, Live TV
│
├── PortalitoRuntime            builds the object graph from the config; rebuilds it when the config changes
│   └── PortalitoServices       Portal, Signer, Proxy, Catalogs, Tmdb?, EpgTimeZone, Discovery?
│
├── Portal/        PortalClient (encrypted API client), HttpPortalTransport, PortalOptions, StreamModels
├── Crypto/        PortalCipher (3DES body cipher), ConfigurableMd5
├── Proxy/         PortalitoProxyService (re-signing proxy), ProxyUrlSigner, HlsRewriter,
│                  ConfigurableContentAuthSigner, StreamSessionCache
├── Api/           PortalitoProxyController (/Portalito/*), PortalitoAdminController (/Portalito/Admin/Test),
│                  ConnectionTester
├── Channels/      PortalitoVodChannel (the channel tree), CatalogIndexTask, ImageRepairTask,
│                  SubtitleFileCache, VodTracks (ffprobe of VOD files)
├── Catalog/       CatalogBrowser (portal catalogs), DiscoveryBrowser (TMDB-driven Destacado rows),
│                  VodItemId, SeasonGrouping, GenreLabels, PageWindow, PortalJson, TtlCache
├── Live/          PortalitoLiveTvService, LiveChannelList, EpgMapper
├── Metadata/      TmdbClient, TmdbDiscovery (TMDB lists → rows)
└── Collage/       CollagePlanner, CollageRenderer (SkiaSharp), CollageService (folder thumbnails)
```

### Configuration → services

`PortalitoRuntime.Get()` reads the Jellyfin-persisted `PluginConfiguration`, computes a fingerprint of every
field, and rebuilds `PortalitoServices` only when the fingerprint changes — so saving the config page takes
effect immediately, without a restart. HTTP clients are created once and shared across rebuilds (rebuilding
them leaked connection pools, disposing them would cut off a playing stream). On first use it also mints the
proxy signing secret and persists it.

An incomplete configuration throws `PortalException`; the channel treats that as "not configured" and hides
itself instead of breaking Jellyfin's channel listing.

## The portal client

`PortalClient` speaks the portal's JSON API:

- **Wire format.** Every request body is `hex(base64(3DES-ECB-PKCS7(json)))` (`PortalCipher`); a response's
  `data` field uses the same scheme. Required headers (`apk`, `apkVer`, `spkgVer`, `User-Agent`) come from
  config (`HttpPortalTransport`). URL: `https://{host}{ApiBasePath}{path}`.
- **Body enrichment.** Every body gets the device fields (`sn`, `appId`, `apkVersion`, `sysVersion`, a
  generic Android-emulator fingerprint…) merged last; calls that need a session also get
  `portalCode`/`userId`/`userToken`.
- **Host failover.** Hosts are tried in order; the last host that answered is preferred next time.
- **Sessions.** The first call activates (anonymous device) or logs in (account). A dead session
  (`aaa100027`/`aaa100028`/`aaa100083`) triggers one re-authentication and a retry. With a real account,
  `aaa100083` ("logged in elsewhere") is *not* retried, to avoid signing another device out in a loop.

### Device activation and self-provisioning

- **Fast path:** `v8/active` with an empty `snToken` and the configured device serial. Works for a device the
  portal already knows.
- **Provisioning** (`ProvisionDeviceAsync`), used when the serial is blank — or when the fast path answers
  `aaa100080` ("snToken invalid") and an snToken salt is configured:
  1. `v3/snToken` with a fresh, generic device fingerprint and no stored identity;
  2. the serial is the one the portal returns, or `MD5(snToken + SnTokenSalt)`;
  3. `v8/active` with the real `snToken` and that serial.

  On success the serial is written back to the configuration (a callback the runtime wires to
  `config.DeviceSn` + save), so later activations use the fast path.

### Account login

`v8/login` with `MD5(password + LoginPasswordSalt)`. Live TV needs an account; catalog and VOD work with an
anonymous device.

## The channel tree

`PortalitoVodChannel` builds the tree lazily — nothing is fetched until a folder is opened.

```
Portalito VOD
├── Destacado            curated rows (see below)
├── Descubrir            one folder per configured catalog (Catalogs setting)
│   └── <Catalog>        Todo · one folder per genre tag · Por año → one folder per year
└── TV en vivo           the portal's live categories → live channels
```

- **Descubrir** browses the portal's real paginated catalog (`v3/filterByContent` under the catalog root's
  `parentId`, derived at runtime from the root's first shelf). Genre tags and years come from
  `v3/filterGenre` and are shared by all catalogs.
- **Series** are listed once per show (seasons grouped by name, `SeasonGrouping`); opening a show lists its
  seasons from the portal's authoritative season list, and a season lists its episodes.
- **Destacado** has two modes:
  - **TMDB rows** (a TMDB key present; `FeaturedRows` defaults to 11 generic rows): each row is a TMDB list
    reconciled with the portal — see [DESTACADO.md](DESTACADO.md).
  - **Portal rows** (no TMDB key, or `FeaturedRows` cleared): newest of the newest year and best-rated
    recent titles, per catalog.

  Either way a row lists best-rated first: `FeaturedSortFilter` (an MVC action filter) turns the app's default A–Z
  request for a row folder into a rating sort — the only way to order a channel folder, since Jellyfin sorts it with
  the request's sort — and the row writes its ratings onto titles Jellyfin saved earlier. See
  [DESTACADO.md](DESTACADO.md#rating-order).
- **Item ids** (`VodItemId`) encode what an item stands for (`mov:<contentId>`, `shw:…`, `row:<index>:<mode>`,
  `flt:<catalog>:g<tag>`…) in a strict charset, because they end up in portal requests and proxy URLs.
- **Listing cache.** Jellyfin caches each folder's listing for 3 hours in a file named from `DataVersion` and
  the channel's `GetCacheKey` (`IHasCacheKey`). On a cache hit Jellyfin doesn't call the channel at all and
  serves the items it last stored for that folder. `GetCacheKey` changes on every configuration change (a
  counter, never a config hash — reverting a config must not reuse an older file) and on every restart, so a
  saved setting relists immediately; bump `DataVersion` whenever a *code* change alters what listings mean.
- **Channel images**: the channel supplies its own Primary (square logo), Thumb and Backdrop (16:9 banner) —
  embedded PNGs from `assets/branding/` (drawn by `make_logo.py`) — or the `ChannelImageUrl` setting's image.
- **Folder thumbnails** are collages rendered to files (`CollageService`), picked so sibling and parent
  folders don't repeat the same posters (`CollagePlanner`).

## Playback: the re-signing proxy

Jellyfin's ffmpeg can't send the CDN's per-request headers, so every media source is a **signed URL on the
plugin's own proxy** (`/Portalito/...`), which fetches from the CDN with the right headers.

| Route | Purpose |
|---|---|
| `GET /Portalito/live/{channel}.m3u8` | live playlist, re-signed and rewritten so every segment goes back through the proxy |
| `GET /Portalito/seg.ts` | one live segment, freshly signed |
| `GET/HEAD /Portalito/vod/{contentId}` | a VOD file, forwarded with HTTP Range so players can seek |
| `GET /Portalito/img` | a portal poster, with its non-standard `image/*` content type normalized |
| `GET /Portalito/ping` | reachability check used by the connection test |

- **URL signing.** The routes are anonymous (ffmpeg has no API key), so each URL carries an expiry and an
  HMAC (`ProxyUrlSigner`). Without it, anyone on the LAN could use Jellyfin as an open relay.
- **Live.** The portal resolves a channel to CDN candidates (`v4/startPlayLive` + `v14/getSlbInfo`, the
  "cfl" entries), a license, and a token. Every playlist and segment request gets a fresh
  `Content-Auth` header: `…&sign2_method={method}&instance=0&start_moment={ms}&sign2={signature}`, where the
  signature is `MD5(prefix + salt)` computed by `ConfigurableMd5` (standard MD5 unless the config supplies a
  round-1 schedule / K overrides). CDNs are tried in order with failover; a rejected signature or a license
  conflict re-resolves the session once; edge 404/5xx on a segment is retried briefly.
- **VOD.** Resolved once (`v10/startPlayVOD`) to a file URL + license; the container (ts/mp4) is known before ffmpeg
  opens it. Subtitles are downloaded to local files (`SubtitleFileCache`) because Jellyfin 10.11 can't read
  http subtitle paths. Tracks are probed with ffprobe (`VodTracks`) so audio/subtitle selection works.
- **Session cache.** Resolved CDN sessions are cached per channel/title and refreshed before they expire
  (`StreamSessionCache`), so the many segment requests don't each hit the portal.

## Continue Watching (resume)

Jellyfin keeps a playback position only when the item has a runtime: with none, `UserDataManager.UpdatePlayState`
marks it Played and drops the position on every progress report. The portal almost never sends a duration, so:

- `GetChannelItemMediaInfo` (runs before playback starts) takes the runtime from its ffprobe of the title, records
  it in `RuntimeStore` (`<data>/portalito/runtimes.json`, keyed by contentId) and sets it on the library item at
  once — Jellyfin names a channel item `GetNewItemId(externalId + channelName + "16", type)` — so the first
  progress report already keeps the position.
- Listings give movies/episodes their known runtime, with `DateModified` = when it was learned (a newer
  `DateModified` is what makes Jellyfin save a changed field on an item it already has).
- Episodes also carry `ParentIndexNumber` (season, from the season's `sameSeasonSeriesList` entry or its "T2" marker)
  and `SeriesName`. Jellyfin copies those only when it first creates an item, so `SeriesRepairTask` ("Reparar series
  Portalito", at startup and weekly) fills them — and known runtimes — into items saved earlier.

Continue Watching (`/UserItems/Resume`) includes channel items. **Next Up does not**: Jellyfin builds it from library
folders only (`TVSeriesManager`), never channels — hence the "Siguiendo" library below.

## Show ids and what Jellyfin does with channel items

Jellyfin re-parents a channel item to whichever folder listed it last (a forced save and a queued refresh) and deletes
the items a folder held but no longer lists (`ChannelManager.GetChannelItemsInternal`). Channel user data is keyed by
the item's `ExternalId`, so a deleted title gets its history back when it's listed again.

- **Shows** are `shw:n_<key>`, the key a hash of the show's name without its season marker (`ShowIndex.KeyFor`), the
  same in every listing. `ShowIndex` (`<data>/portalito/shows.json`) maps the key to the season contentIds the
  listings have seen, which is how the folder is opened. Legacy `shw:<contentId>` ids still open. Before, a show was
  named after the first season a listing showed: one show became several Series items, and the old one was deleted
  with its seasons when a newer season led (measured on production 2026-10-01).
- **Movies and episodes** keep one id everywhere. Production showed no movie/episode deletions over three days;
  `RemovalMonitor` logs any ("Portalito title removed by Jellyfin", with a daily count) to revisit that.

## Next Up: the hidden "Portalito · Siguiendo" library

Next Up only reads libraries, so the plugin mirrors the series people actually watch (not the catalog) into a real TV
library, hidden from every user's menus. Code in `Following/`.

- **Which shows** (`FollowPlanner`): any show someone played an episode of in the last 60 days — any play counts, even
  one stopped under 5% or one that failed (those leave only a play date, not "played"/"in progress") — in the channel or in
  the library — or marked favorite; capped at 100. Played/resumable channel episodes name their season
  (`epi:<season>:<episode>`); the season's `sameSeasonSeriesList` names the show, whose id is its lowest-numbered
  season's contentId. `FollowStore` (`<data>/portalito/following.json`) remembers the mirrored shows and their seasons.
- **Files** (`FollowFiles`, `FollowWriter`): under `FollowLibraryPath` (default `<data>/portalito/siguiendo`),
  `Show [portalito-<id>]/tvshow.nfo` + `poster.*`, and per episode `Season NN/SxxEyy.strm` / `.nfo` / `-thumb.*`. A
  show's episode list is refetched every 12 h; only changed files are rewritten, and episodes the portal dropped are
  deleted.
- **Playback**: each `.strm` holds a never-expiring signed URL, `/Portalito/play/<episode>?series=<season>&s=<hmac>`
  (`ProxyUrlSigner.PlayUrl`; its own HMAC kind, so it can't be swapped for a `vod` URL). Jellyfin probes a `.strm` on
  every playback (`MediaSourceManager.GetPlaybackMediaSources` forces a remote probe), which also gives the episode
  its runtime. The URL is on this server's local address, and jellyfin-web direct-plays any remote http source, so
  `FollowPlaybackFilter` (an MVC action filter on `POST /Items/{id}/PlaybackInfo`) turns off direct play and direct
  stream for items in the library: Jellyfin's ffmpeg opens the URL and serves HLS, as for channel items.
- **The library** (`FollowLibrary`): created on the first followed show (Jellyfin gives an empty library folder no
  item), as a TV library with no metadata/image fetchers, trickplay or chapter images, or file watcher. Scanned
  through its physical folders (a library's top `CollectionFolder` holds no items itself). Every run adds its id to
  each user's `MyMediaExcludes` (hidden from menus; read only by `UserViewManager.GetUserViews`) and, for users
  limited to some libraries who can open the channel, to `EnabledFolders`. `LatestItemExcludes` is never touched:
  Next Up and Continue Watching skip only those.
- **Subtitles** (`FollowSubtitles`): the portal names subtitle files only in its per-episode play call, so they're
  fetched when an episode's playback info is asked for (from `FollowPlaybackFilter`) and saved beside its .strm as
  `SxxEyy.<lang>.srt`. Jellyfin's re-probe of a .strm lists the folder through a singleton, never-cleared
  `DirectoryService`, so the folder is evicted from that cache (by reflection) right after writing them.
- **Stable ids**: `tvshow.nfo` carries a `Custom` uniqueid (`portalito-<showId>`). Jellyfin keys a series' (and its
  episodes') watch data by an IMDb/TVDB/Custom id when there is one, else by the item id, which comes from the folder
  path: without it, a show whose folder moved lost its progress.
- **Who sees it**: users who can open the channel get the library in `EnabledFolders` if they're limited; users who
  can't get the `portalito-siguiendo` tag (every show carries it) in their blocked tags, which Jellyfin applies to the
  show and, inherited, its episodes.
- **Folder checks**: `FollowLibraryPath` must be the plugin's own (not another library's folder, inside or around
  one, or a folder with other files); a library left on a previous folder (recognized by its marker file) is removed.
- **Stamp**: each show records the format and the signing (secret + base URL) its files were written with; a new
  stamp rewrites it on the next run, not 12 h later.
- **Never empty**: `portalito-siguiendo.txt` stays in the folder; Jellyfin skips scanning an empty library folder and
  would keep a removed show's episodes listed.
- **Progress** (`WatchState`): a user's newer play of a channel episode is copied onto its library copy (that's what
  puts the show in Next Up), and the channel copy's resume point is cleared so Continue Watching doesn't list it twice.
- **When**: `FollowSyncTask` ("Sincronizar Portalito · Siguiendo") at startup and every 30 minutes, and
  `FollowTrigger` queues it whenever someone stops a channel episode.

### Signing secret

`ProxySigningSecret` signs every proxy URL (`ProxyUrlSigner`). The config page's **Rotate signing secret**
(`POST /Portalito/Admin/RotateSecret`, admin only) replaces it, revoking every URL handed out (12 h VOD, 7 d live, and
the permanent Siguiendo `.strm` ones), and queues the Siguiendo sync, whose files stamp changes, so the `.strm` files
are re-signed at once.

### Proxy limits

- Upstream segment/VOD/image requests must answer with headers within 20 s (`HeadersTimeout`; scoped to the wait
  for headers, never the body), so a stalled CDN fails over instead of hanging.
- A live playlist answering 409 (license in use elsewhere) re-resolves at most once per 30 s (`ConflictBackoff`).
- Posters on loopback/private/link-local hosts are refused; buffered posters are capped at 10 MB.
- `PortalitoRuntime` rebuilds the portal client, signer and proxy only when portal settings change (`CoreFingerprint`);
  TMDB, rows, image URL and Siguiendo settings don't sign the account in again or drop playing sessions.

## Live TV service

`PortalitoLiveTvService` exposes the portal's channel list and guide to Jellyfin's Live TV section. Each
channel's media source is its signed proxy playlist URL; no portal call is made until ffmpeg fetches it.
The guide comes from `v3/getProgram` (`EpgMapper`); its times carry no zone, so they're read in
`EpgTimeZone`.

## TMDB

`TmdbClient` (optional, needs a key) does two things:

1. **Enrichment** — fills a listing's blank synopsis/poster (and year/rating when already looking one up),
   matched by IMDb id when the portal gives one, else by title + year. Bounded concurrency and budget per
   listing; answers cached a week (misses too).
2. **Destacado lists** — fetches the configured TMDB lists for `DiscoveryBrowser` (see
   [DESTACADO.md](DESTACADO.md)).

TMDB never decides what plays: everything played is the portal's.

## Scheduled tasks

| Task | Schedule | What it does |
|---|---|---|
| Sincronizar Portalito · Siguiendo | at startup, every 30 min, and after a channel episode stops | mirrors followed series into the hidden Next Up library (see above) |
| Logo del canal Portalito | at startup, Sundays 03:20 | gives the channel whichever of its images (logo, banner, backdrop) it lacks; Jellyfin only asks a channel for images when it first creates it. Hand-set images are kept |
| Reparar series Portalito | at startup, Sundays 03:45 | fills runtimes and episode seasons into titles Jellyfin saved before they were known (what Continue Watching needs) |
| Indexar catálogo Portalito | Sundays 04:00 | walks the catalogs and live categories so their items exist in Jellyfin's library and show up in search |
| Reparar imágenes Portalito | Sundays 03:30 | re-applies the folder collages Jellyfin's image providers may have replaced |

## Caches at a glance

| What | Where | Lifetime |
|---|---|---|
| Channel listings | Jellyfin's channel cache, a file per folder keyed by `DataVersion` + the channel's cache key (`IHasCacheKey` → changes on every config change and restart) | 3 h, or until the date, `DataVersion` or the configuration changes |
| Catalog parent ids, genre/year vocabulary, collage posters, top-rated rankings | `CatalogBrowser` | 24 h |
| TMDB enrichment matches | `TmdbClient` | 7 days |
| TMDB lists (Destacado) | `TmdbClient` | 6 h |
| Reconciled Destacado rows / per-title portal matches | `DiscoveryBrowser` | 6 h / 1 day |
| Title runtimes | `RuntimeStore` (`<data>/portalito/runtimes.json`) | permanent |
| Followed shows | `FollowStore` (`<data>/portalito/following.json`) | while followed; episode lists refetched every 12 h |
| CDN sessions | `StreamSessionCache` | until shortly before the CDN auth expires (max 2 h) |
