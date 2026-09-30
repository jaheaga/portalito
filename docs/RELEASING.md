# Releasing

Portalito is distributed as a Jellyfin **plugin repository**: `manifest.json` at the repo root lists every
version with its zip URL (a GitHub Release asset) and MD5. Users add
`https://raw.githubusercontent.com/jaheaga/portalito/main/manifest.json` under **Dashboard → Plugins →
Repositories**.

## Steps

1. Bump the version in **both**:
   - `packaging/meta.json` → `"version": "0.1.0.N"`
   - `src/Jellyfin.Plugin.Portalito/Jellyfin.Plugin.Portalito.csproj` → `<AssemblyVersion>` and `<FileVersion>`
2. Add the version to [`CHANGELOG.md`](../CHANGELOG.md).
3. Check: `dotnet build -c Release` (0 warnings), `dotnet test`, `./scripts/scrub-check.sh`.
4. Commit and push `main`, then tag and push the tag:

   ```sh
   git tag v0.1.0.N && git push origin v0.1.0.N
   ```

The `release` workflow (on `v*` tags) builds and tests the **tagged commit**, runs the scrub check, packages
the zip (`packaging/package.py`: the DLL + `meta.json`, nothing else), creates the GitHub Release with the
zip attached, and then commits the new entry to `manifest.json` on `main`
(`packaging/update_manifest.py`, retrying on a race). Pull `main` afterwards — the workflow's commit lands
after yours.

## Rules learned the hard way

- **Build against the ABI floor.** `Jellyfin.Controller` is pinned to `10.11.0`, matching
  `targetAbi 10.11.0.0`. The referenced Jellyfin assemblies' versions are baked into the DLL; building
  against a newer patch (e.g. 10.11.11) makes the plugin demand shared libraries an older 10.11.x server
  doesn't have, and it loads as **`NotSupported`** ("Could not load file or assembly 'MediaBrowser.Common,
  Version=10.11.11.0'"). Check a built DLL's references with `System.Reflection.Metadata` — `strings` can't
  see them.
- **Never reuse a version or a tag.** Deleting and re-creating a GitHub Release under the same tag keeps
  the same asset URL, and its CDN keeps serving the *old* zip for a while; servers install the stale file.
  Always move forward to a new version.
- The manifest's checksum must match the zip; the workflow computes it. `raw.githubusercontent.com` can
  lag a few minutes after the manifest commit.

## Installing / updating on a server

Through the UI: **Dashboard → Plugins → Catalog → Portalito → Install**, then restart. Through the API
(admin key):

```sh
curl "$SERVER/Packages?api_key=$KEY"                                             # does it see the new version? WAIT until it does
curl -X DELETE "$SERVER/Plugins/$PLUGIN_GUID?api_key=$KEY"                        # remove the old one (optional)
curl -X POST "$SERVER/Packages/Installed/Portalito?version=0.1.0.N&api_key=$KEY"  # install
curl -X POST "$SERVER/System/Restart?api_key=$KEY"                                # activate
curl "$SERVER/Plugins?api_key=$KEY"                                              # expect Status: Active
```

**Don't uninstall until `/Packages` lists the new version.** The server reads the manifest through
`raw.githubusercontent.com`, which can lag several minutes behind the release; uninstalling first and then
installing a version the server can't see yet (HTTP 404) leaves the server with no plugin at all. The saved
configuration survives an uninstall/reinstall.

The plugin GUID is `0bae4700-9bef-4e03-ad7f-cc99bf8f51ab`. `NotSupported` after a restart means the assembly
failed to load — the server log says why.
