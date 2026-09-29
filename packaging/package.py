#!/usr/bin/env python3
"""Package the built plugin DLL + meta.json into the zip Jellyfin's plugin catalog expects.

    dotnet build src/Jellyfin.Plugin.Portalito -c Release
    python3 packaging/package.py                # -> artifacts/Portalito_<version>.zip

The zip holds exactly two files at its root: the plugin DLL and a meta.json (packaging/meta.json plus a build
timestamp). Jellyfin supplies Jellyfin.Controller and the ASP.NET framework itself, so nothing else ships. Nothing from
the reference/ tree, no .env, and no native blob can end up in the archive: the file list is fixed here.
Prints the MD5 the catalog manifest needs as `checksum`.
"""
import argparse
import hashlib
import json
import sys
import zipfile
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DLL_NAME = "Jellyfin.Plugin.Portalito.dll"
DEFAULT_DLL = ROOT / "src" / "Jellyfin.Plugin.Portalito" / "bin" / "Release" / "net9.0" / DLL_NAME


def build_zip(dll: Path, meta_path: Path, out_dir: Path, now: datetime) -> Path:
    if not dll.is_file():
        sys.exit(f"missing {dll} - run: dotnet build src/Jellyfin.Plugin.Portalito -c Release")
    meta = json.loads(meta_path.read_text(encoding="utf-8"))
    meta["timestamp"] = now.strftime("%Y-%m-%dT%H:%M:%SZ")

    out_dir.mkdir(parents=True, exist_ok=True)
    target = out_dir / f"Portalito_{meta['version']}.zip"
    with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as z:
        # Entry dates follow the build timestamp rather than the file mtimes, so they match meta.json.
        for name, data in ((DLL_NAME, dll.read_bytes()), ("meta.json", (json.dumps(meta, indent=2) + "\n").encode("utf-8"))):
            info = zipfile.ZipInfo(name, date_time=(now.year, now.month, now.day, now.hour, now.minute, now.second))
            info.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(info, data)
    return target


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--dll", type=Path, default=DEFAULT_DLL)
    p.add_argument("--meta", type=Path, default=ROOT / "packaging" / "meta.json")
    p.add_argument("--out", type=Path, default=ROOT / "artifacts")
    args = p.parse_args()

    target = build_zip(args.dll, args.meta, args.out, datetime.now(timezone.utc))
    print(target)
    print("md5", hashlib.md5(target.read_bytes()).hexdigest())


if __name__ == "__main__":
    main()
