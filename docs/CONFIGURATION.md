# Configuration reference

All settings live on **Dashboard → Plugins → Portalito** and are persisted by Jellyfin in the plugin's
config XML. Nothing service-specific is baked into the build: every field defaults to empty (or `false`) — the one
exception is `FeaturedRows`, whose default is a set of generic TMDB lists — and the plugin is inert until
the required ones are set. Saving takes effect immediately — no restart.

Each field also has a `.env`-style key (`PORTALITO_*`, listed in [`.env.example`](../.env.example)) used by
the page's **Import / export** box. Keep your real values in a git-ignored `.env`; never commit them.

## Required

| Field | `.env` key | What it is |
|---|---|---|
| `TripleDesKeyHex` | `PORTALITO_TRIPLE_DES_KEY_HEX` | The portal's request-body 3DES key, 32 or 48 hex chars. |
| `Hosts` | `PORTALITO_HOSTS` | Portal API hosts, comma-separated, primary first (scheme/path stripped). |
| `AppId` | `PORTALITO_APP_ID` | The app id sent as `appId`, the `apk` header and the CDN `App` header. |

## Client identity

| Field | `.env` key | What it is |
|---|---|---|
| `ApkVersion` | `PORTALITO_APK_VERSION` | Sent as `apkVersion` (body) and `App-Version` (CDN). |
| `DeviceSn` | `PORTALITO_DEVICE_SN` | The free-tier device serial. **Leave blank to auto-provision** (see `SnTokenSalt`); the serial the plugin gets is saved here. |
| `DeviceDrmId`, `DeviceToken`, `DeviceReserve1` | `PORTALITO_DEVICE_DRM_ID`, `…_TOKEN`, `…_RESERVE1` | Optional device fields; the portal accepts them empty. |
| `SnTokenSalt` | `PORTALITO_SN_TOKEN_SALT` | Derives a new device serial as `MD5(snToken + salt)` during provisioning, when the portal doesn't return one. Also enables automatic re-provisioning when a saved serial stops working (`aaa100080`). |

## Account (optional)

| Field | `.env` key | What it is |
|---|---|---|
| `AccountEmail`, `AccountPassword` | `PORTALITO_ACCOUNT_EMAIL`, `…_PASSWORD` | Log in with an account instead of an anonymous device. **Live TV needs an account**; catalog and VOD don't. An account holds one session at a time — logging in here signs out the device that used it before, so a dedicated account is best. Both or neither. |

## Portal protocol (Advanced)

| Field | `.env` key | What it is |
|---|---|---|
| `PortalCode` | `PORTALITO_PORTAL_CODE` | Sent as `portalCode`. |
| `ApiBasePath` | `PORTALITO_API_BASE_PATH` | The path prefix on each host, e.g. `/api/core/`. |
| `PortalApkVer` | `PORTALITO_PORTAL_APK_VER` | The `apkVer` header (distinct from `ApkVersion`). |
| `PortalSpkgVer` | `PORTALITO_PORTAL_SPKG_VER` | The `spkgVer` header and the body's `sysVersion`. |
| `PortalUserAgent` | `PORTALITO_PORTAL_USER_AGENT` | User-Agent for API calls. |
| `CdnUserAgent` | `PORTALITO_CDN_USER_AGENT` | User-Agent for CDN (stream) requests. |
| `LoginPasswordSalt` | `PORTALITO_LOGIN_PASSWORD_SALT` | Appended to the password before hashing at login. |
| `LiveColumnCode` | `PORTALITO_LIVE_COLUMN_CODE` | The column code whose children are the live categories. |
| `AllChannelsColumnId` | `PORTALITO_ALL_CHANNELS_COLUMN_ID` | A live column listing every channel (optional). |
| `Catalogs` | `PORTALITO_CATALOGS` | VOD catalogs for **Descubrir**: `code:Label` pairs, comma-separated, e.g. `movies_root:Películas, series_root:Series`. Order matters (item ids use the index). Empty = no VOD catalogs. |

If `apk`/`apkVer`/`spkgVer` are wrong or missing, portals of this kind typically answer that the app version
is discontinued.

## CDN Content-Auth signing (Advanced)

Live playlists and segments carry a per-request signature:
`sign2 = MD5("token={token}&sign2_method={method}&instance=0&start_moment={ms}" + salt)`.

| Field | `.env` key | What it is |
|---|---|---|
| `ContentAuthMethod` | `PORTALITO_CONTENT_AUTH_METHOD` | The `sign2_method` value. |
| `ContentAuthSaltHex` | `PORTALITO_CONTENT_AUTH_SALT_HEX` | The salt bytes, as hex. |
| `ContentAuthMd5Schedule` | `PORTALITO_CONTENT_AUTH_MD5_SCHEDULE` | Optional: a non-standard round-1 message schedule — 16 comma-separated indices, a permutation of 0–15. Empty = standard MD5. |
| `ContentAuthMd5KOverrides` | `PORTALITO_CONTENT_AUTH_MD5_K_OVERRIDES` | Optional: replaced MD5 constants as `round:hex` pairs, e.g. `42:0123abcd`. Empty = standard MD5. |

With both MD5 fields empty, the signer is plain RFC 1321 MD5 — no reverse-engineered constant ships in the
plugin. A wrong value only shows up as live streams that won't play (VOD doesn't use it).

## Metadata and Destacado

| Field | `.env` key | What it is |
|---|---|---|
| `TmdbApiKey` | `PORTALITO_TMDB_API_KEY` | Optional TMDB v3 key. Fills blank synopses/posters, and powers `FeaturedRows`. |
| `FeaturedRows` | `PORTALITO_FEATURED_ROWS` | Builds **Destacado** from TMDB lists, keeping only titles the portal has. **Pre-filled with 11 default rows** (trending, popular, anime JP/KR, Korean series…), active as soon as a TMDB key is set. Clear it to use the portal's own rows. Syntax, sources and the default list: [DESTACADO.md](DESTACADO.md). |

## Server / network

| Field | `.env` key | What it is |
|---|---|---|
| `ChannelImageUrl` | `PORTALITO_CHANNEL_IMAGE_URL` | Optional http(s) image for the channel tile. Empty = the built-in Portalito logo. Jellyfin caches channel images: after changing it, refresh the channel's metadata with *Replace existing images*. |
| `ProxyBaseUrl` | `PORTALITO_PROXY_BASE_URL` | How Jellyfin's own ffmpeg reaches this server's proxy, e.g. `http://127.0.0.1:8096`. Empty = the server's own local address (port, HTTPS and base path included). Must be `http(s)://`. |
| `EpgTimeZone` | `PORTALITO_EPG_TIME_ZONE` | IANA zone the portal's guide times are in (they carry none), e.g. `America/Bogota`. Empty = the server's zone. |
| `SkipPortalTlsVerification` | `PORTALITO_SKIP_PORTAL_TLS_VERIFICATION` | Off by default. Turn on only if the portal hosts present invalid certificates — without verification, anyone on the network path can capture the login. |
| `ProxySigningSecret` | — | Generated and saved on first use; signs the proxy URLs. Not on the page. |

## Import / export

The **Import / export configuration** box at the top of the page runs entirely in the browser:

- **Import into form** — paste `.env` lines (`PORTALITO_*=value`, `#` comments ignored, split on the first
  `=`) or a JSON object; every recognized key fills its field. Keys are matched loosely (case, the
  `PORTALITO_` prefix and underscores don't matter, so property names work too). Nothing is saved until you
  press **Save**.
- **Export current values** — writes the page's current values as `.env` lines, to copy to another server.

Don't put a comment after a value on the same line: everything after the first `=` is the value. A key with an
empty value clears its field — e.g. `PORTALITO_FEATURED_ROWS=` turns the default Destacado rows off.

## Save and test connection

**Save and test connection** saves the page, then reports:

| Check | OK means | Common failures |
|---|---|---|
| Settings | all required fields are valid | names the missing/malformed field |
| Portal | activated (or logged in) and read the catalog | `aaa100080 snToken…`: the device serial isn't registered — blank `DeviceSn` (with `SnTokenSalt` set) to provision one. A transport error: hosts/TLS/key. |
| Live TV | an account is set | warning without an account: VOD works, live TV doesn't |
| Proxy URL | ffmpeg can reach `/Portalito/ping` | set `ProxyBaseUrl` to an address Jellyfin can reach itself on |
| Guide time zone | the zone resolved | warning for an unknown IANA id (falls back to the server zone) |

## Visibility for users

Portalito is a **channel**: it appears under **Channels**, not as a library tile. A user only sees it if
their account has channel access (**Dashboard → Users → user → Access → Enable all channels**, or tick
Portalito VOD).
