# Portalito

A [Jellyfin](https://jellyfin.org) channel plugin for self-hosted IPTV **portal** middleware — the
kind that exposes Live TV and VOD (movies and series) over an HTTP JSON API and serves streams from a
CDN behind a per-request signed token.

Portalito is a **blank framework**. It ships with **no service built in**: no portal name, no hosts,
no keys, no signing secrets, no catalog. You point it at *your own* portal and paste *your own*
parameters into the plugin's configuration page. Until it is fully configured it stays inert — the
channel hides itself and the connection test reports "not configured".

> **You must supply everything yourself.** This plugin includes no credentials, no server addresses,
> and no proprietary algorithm constants. It is a client shell for a protocol you already have lawful
> access to. Only use it with a service you are authorized to use.

## What it does

- Adds a **channel** with your portal's VOD catalogs (movies / series / kids / anime, whatever you
  configure) and a **Live TV** section with the portal's channels and EPG.
- Plays streams through an **in-plugin re-signing proxy**: Jellyfin's player talks to the plugin, and
  the plugin mints a fresh CDN Content-Auth signature per playlist and per segment. No external
  helper service is required — everything runs inside Jellyfin.
- Optional **TMDB** enrichment for artwork and metadata (you supply your own TMDB API key).

## Requirements

- Jellyfin **10.11** (target ABI `10.11.0.0`).
- A portal account and the protocol parameters it uses (see below).

## Install

### From the plugin repository (recommended)

1. In Jellyfin: **Dashboard → Plugins → Repositories → +**.
2. Add this repository's raw manifest URL:
   `https://raw.githubusercontent.com/jaheaga/portalito/main/manifest.json`
3. **Catalog** tab → find **Portalito** → **Install**.
4. Restart Jellyfin when prompted.

### Manual

Download the release zip, extract `Jellyfin.Plugin.Portalito.dll` and `meta.json` into a
`plugins/Portalito/` folder under your Jellyfin data directory, and restart.

## Configure

Open **Dashboard → Plugins → Portalito** and fill in your values. The essentials:

- **3DES key (hex)** — the portal's request-body cipher key.
- **Hosts** — one or more API hosts (comma-separated).
- **App id**, **APK version**, **device serial** — the client identity your portal expects.
- **Account** / anonymous activation — how you log in.

Then, under the **Advanced** sections, the protocol details your portal uses:

- **Portal protocol** — portal code, API base path, `apkVer`/`spkgVer`, User-Agents, the login
  password salt, the live column code and the all-channels column id, and your catalog list
  (`code:Label` pairs).
- **CDN Content-Auth signing** — the signing method name and salt. The signer is ordinary MD5 by
  default; if your portal uses a non-standard MD5, you can supply the round-1 schedule and K
  overrides. **No reverse-engineered constant is baked into this plugin** — unconfigured, it is plain
  RFC-1321 MD5.
- **TMDB API key** (optional).

Press **Save**, then **Save and test connection** — it walks activation, login and a catalog fetch and
reports exactly where it stops.

**Filling it faster:** the config page has an **Import / export configuration** box at the top. Paste a
`.env` (`PORTALITO_*=value` lines) or a JSON object and press **Import into form** to fill every field
at once, then review and **Save**. **Export current values** dumps the current settings back out so you
can copy them to another server. Nothing is saved until you press Save, and the import runs entirely in
your browser — no values are sent anywhere.

## Build from source

```sh
dotnet build src/Jellyfin.Plugin.Portalito -c Release
dotnet test  Jellyfin.Plugin.Portalito.sln -c Release
python3 packaging/package.py        # -> artifacts/Portalito_<version>.zip (+ its md5)
```

`scripts/scrub-check.sh` guards the tree against service-specific or private tokens; it also runs as an
xUnit test (`ScrubGuardTests`) and in CI.

## License

[MIT](LICENSE) © 2026 Jaime Aguilar.
