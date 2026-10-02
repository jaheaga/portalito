#!/usr/bin/env bash
# Fails if any service-specific or private token appears in the tree. Portalito is a generic, blank plugin: it must
# never carry a real portal's name, hosts, keys, signing constants or captured values. Run locally and in CI.
#
# The tokens themselves are not in the repo: scripts/scrub-denylist.sha256 holds only each one's length and the SHA-256
# of its lowercase form (see scripts/scrub-hash.py), and every same-length window of every file is hashed and compared.
# Matching is case-insensitive and finds a token anywhere, including inside a longer word. A hit is reported by file,
# line and length only, never by printing the line.
set -euo pipefail
cd "$(dirname "$0")/.."

python3 - <<'PY'
import hashlib, os, sys

wanted = {}
for line in open("scripts/scrub-denylist.sha256", encoding="utf-8"):
    line = line.strip()
    if line and not line.startswith("#"):
        length, digest = line.split()
        wanted.setdefault(int(length), set()).add(bytes.fromhex(digest))

# Build output and VCS metadata are skipped. .env and private/ hold the operator's own real values (and the private
# denylist); they are git-ignored and never published, so they are skipped too (.env.example is still scanned).
skip_dirs = {".git", "bin", "obj", "artifacts", "private"}
skip_files = {".env", ".env.local"}
hits = []
for base, dirs, files in os.walk("."):
    dirs[:] = [d for d in dirs if d not in skip_dirs]
    for name in files:
        if name in skip_files:
            continue
        path = os.path.join(base, name)
        data = open(path, "rb").read()
        if b"\0" in data[:8000]:
            continue  # binary
        for number, line in enumerate(data.lower().split(b"\n"), 1):
            for length, digests in wanted.items():
                if any(hashlib.sha256(line[i:i + length]).digest() in digests for i in range(len(line) - length + 1)):
                    hits.append(f"{path[2:]}:{number}: a forbidden {length}-byte token")
                    break

if hits:
    print("Forbidden tokens found:", *hits, sep="\n", file=sys.stderr)
    sys.exit(1)
print("scrub-check: clean")
PY
