# Destacado rows from TMDB

**Destacado** is the first folder of the Portalito channel: a set of curated rows. With a TMDB key, each
row is a **TMDB list** — trending, popular, upcoming, a *discover*
query by country/language/genre… — **reconciled with the portal**: only titles the portal actually has are
listed, so everything in a row plays.

The `FeaturedRows` setting **comes pre-filled with the default rows below**, so a fresh install gets them
as soon as the TMDB key is set. Edit them freely; **clear the field** (or leave the TMDB key empty) to get
the portal-computed rows instead (newest and best-rated per catalog). Descubrir is not affected; it always
browses the portal's own catalog.

## Default rows

| Row | Definition |
|---|---|
| Tendencias | `trending \| window=week` |
| Estrenos en cine | `now-playing` |
| Próximamente | `upcoming` |
| Películas populares | `popular-movies` |
| Series populares | `popular-tv` |
| Originales Netflix | `discover-tv \| network=netflix nogenre=10762,10763,10764,10767 minvotes=100 pages=3` |
| Apple TV+ | same, `network=apple` |
| Disney+ | same, `network=disney` |
| HBO | same, `network=hbo` (HBO and HBO Max) |
| Prime Video | same, `network=prime` |
| Paramount+ | same, `network=paramount` |
| En emisión hoy | `airing-today` |
| Anime del momento | `discover-tv \| country=JP,KR genre=16 pages=3` |
| Películas de anime | `discover-movies \| country=JP,KR genre=16` |
| Series coreanas | `discover-tv \| country=KR nogenre=16,10764,10767 minvotes=20 pages=3` |
| Películas mejor valoradas | `top-movies` |
| Series mejor valoradas | `top-tv` |

The exact string is `PluginConfiguration.DefaultFeaturedRows`. Existing installs get it too: a one-time
migration (0.1.0.6) fills the field when it's empty and was never seeded — including configs that 0.1.0.4
saved empty. After that, clearing the field keeps it cleared. Since 0.1.1.3, a field still holding exactly the
previous 11-row default gets the platform rows the same way; a field anyone edited is left alone (add the
platform rows by hand from the table above).

The platform rows list each platform's **own** series (TMDB *networks*: what it produced), most popular first.
Without `minvotes=100` and the excluded genres (kids, news, reality, talk) popularity alone ranks daily shows and
regional fillers first (measured 2026-10-01: *WWE Raw* and *Sesame Street* topped Netflix's list).

Changes to the rows show up in the **next** listing: the channel's cache key follows the configuration, so
Jellyfin's 3-hour channel cache doesn't hide them. (A client app may still show its own cached screen until
you refresh it.)

## Syntax

Rows are separated by `;` (or newlines), each:

```
Label | source | key=value key=value ...
```

- `Label` — the folder name shown in Jellyfin.
- `source` — one of the sources below (case-insensitive).
- parameters — optional, space-separated. **Commas mean "or"** (`country=JP,KR`).

Rows with no label or an unknown source are skipped.

## Sources

| Source | TMDB list | Lists |
|---|---|---|
| `trending` | `trending/{type}/{window}` | movies and series (see `type`) |
| `popular-movies` / `popular-tv` | `movie/popular` / `tv/popular` | movies / series |
| `top-movies` / `top-tv` | `movie/top_rated` / `tv/top_rated` | movies / series |
| `now-playing` | `movie/now_playing` | movies |
| `upcoming` | `movie/upcoming` | movies |
| `airing-today` / `on-air` | `tv/airing_today` / `tv/on_the_air` | series |
| `discover-movies` / `discover-tv` | `discover/movie` / `discover/tv` | movies / series, filtered |

## Parameters

| Parameter | Applies to | TMDB filter | Example |
|---|---|---|---|
| `type` | trending | `movie`, `tv` or `all` (default) | `type=tv` |
| `window` | trending | `day` or `week` (default) | `window=day` |
| `country` | discover | `with_origin_country` (ISO 3166-1) | `country=KR`, `country=JP,KR` |
| `lang` | discover | `with_original_language` (ISO 639-1) | `lang=ko` |
| `genre` | discover | `with_genres` (TMDB genre id) | `genre=16` |
| `nogenre` | discover | `without_genres` | `nogenre=16,10764,10767` |
| `sort` | discover | `sort_by` (default `popularity.desc`) | `sort=vote_average.desc` |
| `year` | discover | `primary_release_year` / `first_air_date_year` | `year=2024` |
| `minvotes` | discover | `vote_count.gte` (filters obscure titles) | `minvotes=20` |
| `network` | discover-tv | `with_networks`: the platform that **made** the series (its originals) | `network=netflix`, `network=hbo,prime` |
| `provider` | discover | `with_watch_providers`: titles **included in the subscription** of a platform in `region`, licensed ones too | `provider=netflix region=CO` |
| `region` | discover, with `provider` | `watch_region` (ISO 3166-1, default `US`) | `region=CO` |
| `pages` | all | TMDB pages of 20 to fetch, 1–3 (default 2) | `pages=3` |

Platform names for `network`: `netflix`, `apple`, `disney`, `hbo` (HBO + HBO Max), `max`, `prime`, `paramount`,
`hulu`, `peacock`, `crunchyroll`. For `provider`: `netflix`, `prime`, `apple`, `disney`, `hbo`/`max`, `paramount`,
`vix`, `crunchyroll`, `mubi`. Both also take raw TMDB ids (`network=213`). Unknown names are ignored.

**network or provider?** `network` gives a platform's identity (its originals, the same everywhere);
`provider` gives what you can watch there in your country, which mixes in lots of licensed catalog and changes
over time. For "the Netflix row", `network` is usually what people mean.

Useful TMDB genre ids: 16 Animation, 28 Action, 35 Comedy, 18 Drama, 27 Horror, 10749 Romance,
878 Science Fiction, 99 Documentary, 10751 Family; TV only: 10759 Action & Adventure, 10762 Kids,
10764 Reality, 10767 Talk, 10765 Sci-Fi & Fantasy.

## More examples

```
Tendencias | trending | window=week;
Estrenos en cine | now-playing;
Películas populares | popular-movies;
Series populares | popular-tv;
Anime del momento | discover-tv | country=JP,KR genre=16 pages=3;
Películas de anime | discover-movies | country=JP,KR genre=16;
Series coreanas | discover-tv | country=KR nogenre=16,10764,10767 minvotes=20 pages=3;
Películas mejor valoradas | top-movies;
Series mejor valoradas | top-tv
```

- **Anime (Japanese/Korean only):** `discover-tv` with `country=JP,KR genre=16` (animation from Japan or Korea).
- **A platform's originals:** `Originales Netflix | discover-tv | network=netflix nogenre=10762,10763,10764,10767 minvotes=100 pages=3`.
- **What's on a platform in your country** (movies too): `En Prime Video | discover-movies | provider=prime region=CO minvotes=100`.
- **Korean TV series:** `discover-tv` with `country=KR`, excluding animation, reality and talk shows
  (`nogenre=16,10764,10767`) and obscure entries (`minvotes=20`).

## How matching works

For each TMDB title (fetched in **es-MX** and **en-US**):

1. Search the portal (`v3/searchByName`) by the Spanish title, then the English and original titles.
2. A portal result matches when it is the same kind (movie vs. series) and its `name` or `alias` —
   season marker (`T2`, `S2`, `Temporada 2`…) removed, accents/case/punctuation ignored — equals one of the
   TMDB titles.
3. **Movies** must also be within one year of TMDB's release year. **Series** take the season closest to
   the show's first air date (a season's own date is its release, not the show's).
4. Unmatched titles are dropped; the rest keep TMDB's order, one entry per show, up to 60 per row.

The row's items are ordinary portal items — the same ids, seasons, episodes and playback as in Descubrir.

## Caching and performance

- TMDB lists are cached 6 h; a reconciled row 6 h; each title's portal match (or miss) a day.
- The Destacado folder itself only fetches the TMDB lists (their posters make the row thumbnails).
- **Opening a row the first time** runs its portal searches (up to ~40 titles × 1–3 searches, 4 at a
  time): expect several seconds. After that it's cached.

## Limits

- **Order.** Jellyfin sorts channel items with the *client's* sort — A–Z by default in the web app — and a
  channel plugin can't set a rank. Rows show the right titles, not TMDB's ranking; use the sort menu
  (Rating, Release date…) if you prefer. The rows themselves also appear A–Z.
- **Coverage.** Only what the portal carries is shown. Trending, popular, top-rated and country/genre
  discover rows fill well; `airing-today` and `upcoming` are often thin, since a VOD portal rarely has
  titles that are still airing or not yet released.
- **Matching.** Title-based: an occasional title filed under a very different name won't match (it's
  dropped, never mislabeled — unless the portal has a different title with the same name and year).
- **Thumbnails** use TMDB's posters, so a row's collage may show a title the portal doesn't have.
- Needs the TMDB key. Without it, or with the field cleared, Destacado falls back to the portal's own rows.
