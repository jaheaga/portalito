# Portalito TV

Portalito TV is a second plugin in this repository (`src/Jellyfin.Plugin.PortalitoTV`, plugin id
`5f1d6c3e-8a4b-4f0e-9c2d-7b1e3a9d4c60`). It turns what you heart in Portalito into **24/7 linear TV channels**: a fixed-time
schedule or a continuous shuffle, a guide, and a stream you tune in Jellyfin's **Live TV**. It carries no service-specific
value; everything comes from Jellyfin (the favorites, the Portalito library) and from its own settings page.

It does **not** talk to a portal: it only reads the items the Portalito plugin already put in Jellyfin. Install and configure
Portalito first.

## Requirements

- Jellyfin **10.11** (target ABI `10.11.0.0`, like Portalito).
- The Portalito plugin, configured, with **Siguiendo** on (`FollowLibraryEnabled`, the default). A series you heart (in the
  Portalito channel or in the library) is mirrored into the hidden "Portalito · Siguiendo" library, which is where Portalito
  TV finds its episodes. Hearted **movies** are read from the Portalito channel.
- A GPU Jellyfin can use through **VAAPI** (`/dev/dri/renderD128` by default; set another one in the settings). Every
  channel is encoded to 1080p H.264 + stereo AAC on it, so the pieces of a channel chain without re-encoding.
- Optional: a TMDB v4 read token, for episode names, synopses and images in the guide.

## What it does

1. **Reads your hearts.** The *favorites user* (the first administrator unless you name one) hearts series and movies;
   those are the content a channel can use.
2. **Probes them.** ffprobe measures each episode's duration and audio tracks (in the background, a few at a time) and the
   result is cached in `probe.json`. Only items with a known duration are scheduled.
3. **Builds the grid.** Each channel's schedule is built 48 h ahead and saved (`playouts/<number>.json`); a scheduled task
   extends it every 30 min.
4. **Publishes it as Live TV.** An M3U list and an XMLTV guide are registered in Jellyfin as a tuner and a guide provider
   (so it is a normal Live TV channel: the guide, any client).
5. **Streams it.** Opening a channel starts an "outer" ffmpeg that chains one request per program to the plugin; each of those
   starts an "inner" ffmpeg that encodes just that stretch of the right episode (with the chosen audio track and, if asked
   for, the Spanish subtitle burned in), at real-time speed, from where the grid says it should be now.

M3U is used instead of Jellyfin's `ILiveTvService` on purpose: Jellyfin marks every stream of a plugin service as interlaced
and re-encodes it with yadif on the CPU.

## The settings page

**Dashboard → Plugins → Portalito TV.** Four tabs and a **Guardar y aplicar** button (it saves and rebuilds the schedule from
the program on air; a `Hay cambios sin guardar` notice shows until you do).

| Tab | What it does |
|---|---|
| **Series** | Your hearted series: use it in channels or not, which seasons, which audio, and how its seasons line up with TMDB. **Sincronizar con Portalito** runs the Siguiendo sync and re-reads the hearts, with a progress bar. |
| **Películas** | Your hearted movies and their audio. |
| **Canales** | The channels, and the page's settings (below). |
| **Horario** | A 24 h bar per channel and day. Drag a series or a movie from the palette onto it to make a block, drag a block to move it, drag its edges to resize it, double-click an edge to fill the gap up to the neighbor, `Supr` or the ✕ to delete. |

### Channels and blocks

A channel has a number, a name, a logo and a mode:

- **Schedule**: blocks that start at a local time (and optionally end at one, even the next day; days of the week can be
  limited). A block airs whole episodes of its series while they fit; what is left until the next block is **filler** cut to
  size, so the next block always starts on time. The filler series are chosen per channel.
- **Shuffle**: a continuous random mix of the chosen series (and your hearted movies, if you tick it), going through every
  title before repeating.

A block can be a series, `movies` (a random hearted movie each time) or one specific movie (a fixed-length block that airs it
once). A series block is *shuffle* (random, no repeat until all aired) or *chronological* (in order, resuming where the last
block left off).

### Settings (tab Canales)

| Field | Default | What it is |
|---|---|---|
| Usuario de los ♥ (`FavoritesUser`) | empty = the first administrator | Whose hearts define the content. |
| Zona horaria (`TimeZone`) | empty = the server's | IANA id the block times are read in. An unknown id falls back to UTC. |
| Dirección de este Jellyfin (`ServerUrl`) | `http://127.0.0.1:8096` | How ffmpeg and the tuner reach this Jellyfin (no trailing slash). Set it if Jellyfin listens elsewhere or behind TLS. It is written into the tuner's URL at the next start. |
| Dispositivo VAAPI (`VaapiDevice`) | `/dev/dri/renderD128` | The render device the channels are encoded on. |
| Token de TMDB (`TmdbToken`) | empty | A **secret**: shown as a password field and never sent back to the page; leave it empty to keep it. |
| `StreamKey` | generated | The key in the stream and guide URLs (Jellyfin's tuner calls them without a user session). Not on the page. |

Blank fields mean "use the default"; saving a blank one resets it.

## Audio and subtitles

Each series and movie has a language choice:

- **Automático**: the Latin American Spanish track if there is one (a `lat` / `es-419` tag, a title that says so, or the
  first `spa` track); otherwise English audio with the Spanish subtitle burned in.
- **Español – 1ª pista (latino)**, **Español – 2ª pista**: for files with two unnamed Spanish tracks, where the first one
  is Castilian. They can only be told apart by listening, so the page has **Escuchar**: 15 s of a track, taken from the stretch
  with the most speech (where the track differs from another one).
- **Inglés + subtítulos**: English audio and the Spanish subtitle burned in.

Subtitles are fetched through Jellyfin (it gets them from the portal) and cached as `subs/<id>.spa.srt`.

## The guide

Each program is published with the series name as its title and `T3 E5 - Episode name` as its subtitle. Placeholder names
("Episodio 3", "&lt;Show&gt; Season 27 - 1") are dropped and only the numbers stay. Movies and the black slate have no subtitle.
`<episode-num>` is **not** written on purpose: Jellyfin turns it into season and episode numbers and its clients then put
"S3 - 6." in front of the subtitle.

### TMDB numbering

Portalito does not always number episodes like TMDB. For each series the plugin picks the TMDB numbering (broadcast order or
an episode group) that matches the most seasons. A season is trusted when it has the same number of episodes, or when
Portalito is missing episodes but the ones it has keep their number (its highest number equals TMDB's total). In the other
seasons the guide shows no per-episode TMDB data (that would be another episode's), and the **Series** tab flags them with ⚠
and lets you **assign the season to a TMDB season by hand**; episodes are then paired by number.

### Multi-part episodes

Episodes that are parts of one ("(primera parte)" / "(segunda parte)", "Parte 2", "Part Two", "(1)" / "(2)", "(1/2)") are
detected by title, when they are consecutive (also across a season boundary) and numbered 1, 2, 3 (up to 5). Their group
airs **whole, in order and in the same block**: if it doesn't fit in what is left of the block, the block's remainder is filler
and the group is tried later. In chronological blocks the order continues after the last part. Filler treats every episode
on its own.

## Files and endpoints

Everything the plugin writes is under `<data>/portalito-tv/`: `playouts/`, `probe.json`, `tmdb.json`, `subs/`, `tmp/`. The
plugin's own settings are in Jellyfin's plugin configuration XML.

| Route | Auth | What it is |
|---|---|---|
| `GET /PortalitoTV/channels.m3u?k=` | stream key | The M3U list. |
| `GET /PortalitoTV/xmltv.xml?k=` | stream key | The XMLTV guide (48 h ahead, 2 h back). |
| `GET /PortalitoTV/channel/{n}/live.ts?k=` | stream key | A channel's continuous stream. |
| `GET /PortalitoTV/channel/{n}/item.ts?k=&s=` | stream key + session | One program of that stream (internal). |
| `/PortalitoTV/admin/*` | administrator | The settings page's API (state, preview, apply, sample, sync). |

The input URLs of the episodes are signed by Portalito: they are never logged (ffmpeg's stderr is redacted).

## Scheduled task

| Task | Schedule | What it does |
|---|---|---|
| Actualizar canales | at startup and every 30 min | reads the hearts, probes up to 20 min of new episodes, extends the grids to 48 h |

## Development

```sh
dotnet build src/Jellyfin.Plugin.PortalitoTV -c Release   # must be 0 warnings
dotnet test  Jellyfin.Plugin.Portalito.sln -c Release     # both plugins' suites, offline
```

The scheduling engine (`Scheduling/PlayoutBuilder`), the multi-part grouping (`Content/MultiPart`), the TMDB matching and
the sync runner have no Jellyfin dependency and are unit-tested with fake content.

Like Portalito, it is built against the `Jellyfin.Controller` **10.11.0** floor and has to run on every 10.11.x, but a few
members were renamed between patches: `IUserManager.Users` (a property in 10.11.0) is `GetUsers()` from a later one, and a direct
call throws `MissingMethodException` on a newer server even though it compiles. The plugin therefore reaches the user list by
reflection (`PortalitoLibrary.UsersOf`, covered by `UsersOfTests`). Compiling cannot catch this class of bug: before releasing,
check the built DLL's references against the assemblies of a newer 10.11.x server (a name that no longer exists is the symptom)
and exercise the plugin on one. `packaging/meta-tv.json` holds the
catalog metadata; `PackagingMetaTests` keeps it in step with the code.
