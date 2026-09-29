#!/usr/bin/env python3
"""Insert (or replace) one version entry in the root manifest.json the Jellyfin plugin repository serves.

    python3 packaging/update_manifest.py <repo> <tag> <version> <md5> <targetAbi>

The newest version goes first. Re-running for a version already present replaces its entry, so a rebuilt
release updates its own checksum instead of duplicating.
"""
import datetime
import json
import sys
from pathlib import Path

MANIFEST = Path(__file__).resolve().parent.parent / "manifest.json"


def main() -> None:
    repo, tag, version, md5, abi = sys.argv[1:6]
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    versions = [v for v in manifest[0].get("versions", []) if v.get("version") != version]
    versions.insert(0, {
        "version": version,
        "targetAbi": abi,
        "sourceUrl": f"https://github.com/{repo}/releases/download/{tag}/Portalito_{version}.zip",
        "checksum": md5,
        "changelog": f"Portalito {version}",
        "timestamp": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    })
    manifest[0]["versions"] = versions
    MANIFEST.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
