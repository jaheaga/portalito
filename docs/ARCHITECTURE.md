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
- **Item ids** (`VodItemId`) encode what an item stands for (`mov:<contentId>`, `shw:…`, `row:<index>:<mode>`,
  `flt:<catalog>:g<tag>`…) in a strict charset, because they end up in portal requests and proxy URLs.
- **`DataVersion`** is part of Jellyfin's cache key for channel listings; bump it whenever the tree's shape or
  contents change meaning, or Jellyfin keeps serving old listings.
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
| Indexar catálogo Portalito | Sundays 04:00 | walks the catalogs and live categories so their items exist in Jellyfin's library and show up in search |
| Reparar imágenes Portalito | Sundays 03:30 | re-applies the folder collages Jellyfin's image providers may have replaced |

## Caches at a glance

| What | Where | Lifetime |
|---|---|---|
| Channel listings | Jellyfin (keyed by `DataVersion`, which includes the date) | until the day or `DataVersion` changes |
| Catalog parent ids, genre/year vocabulary, collage posters, top-rated rankings | `CatalogBrowser` | 24 h |
| TMDB enrichment matches | `TmdbClient` | 7 days |
| TMDB lists (Destacado) | `TmdbClient` | 6 h |
| Reconciled Destacado rows / per-title portal matches | `DiscoveryBrowser` | 6 h / 1 day |
| CDN sessions | `StreamSessionCache` | until shortly before the CDN auth expires (max 2 h) |
