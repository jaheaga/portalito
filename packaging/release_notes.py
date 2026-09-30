#!/usr/bin/env python3
"""Release notes for one version, taken from CHANGELOG.md.

    python3 packaging/release_notes.py 0.1.0.8           # the version's section, markdown (GitHub Release body)
    python3 packaging/release_notes.py 0.1.0.8 --short   # one plain line (manifest.json "changelog", shown in Jellyfin)

Exits non-zero when CHANGELOG.md has no section for the version, so a release can't go out with empty notes.
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
REPO = "https://github.com/jaheaga/portalito"
MAX_SHORT = 300

INSTALL = """
### Install / update
Jellyfin → **Dashboard → Plugins → Repositories → +** →
`https://raw.githubusercontent.com/jaheaga/portalito/main/manifest.json`, then **Catalog → Portalito**.
Requires Jellyfin 10.11.x. Restart Jellyfin after installing.

Full history: [CHANGELOG.md](""" + REPO + "/blob/main/CHANGELOG.md)"


def section(version: str) -> str:
    text = (ROOT / "CHANGELOG.md").read_text(encoding="utf-8")
    match = re.search(rf"^## {re.escape(version)}\b.*?$\n(.*?)(?=^## |\Z)", text, re.M | re.S)
    if not match or not match.group(1).strip():
        sys.exit(f"CHANGELOG.md has no section for {version}")
    return match.group(1).strip()


def short(body: str) -> str:
    """The section as one plain line: bullets joined, markdown and links stripped, capped."""
    items = [re.sub(r"\s+", " ", b).strip() for b in re.split(r"^\s*- ", body, flags=re.M) if b.strip()]
    plain = " ".join(items)
    plain = re.sub(r"\[([^\]]+)\]\([^)]+\)", r"\1", plain)  # links -> their text
    plain = re.sub(r"[*`]", "", plain)
    return plain if len(plain) <= MAX_SHORT else plain[: MAX_SHORT - 1].rsplit(" ", 1)[0] + "…"


def main() -> None:
    version = sys.argv[1]
    body = section(version)
    print(short(body) if "--short" in sys.argv[2:] else body + "\n" + INSTALL)


if __name__ == "__main__":
    main()
