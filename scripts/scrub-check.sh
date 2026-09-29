#!/usr/bin/env bash
# Fails if any service-specific or private token appears in the tree. Portalito is a generic, blank plugin: it must
# never carry a real portal's name, hosts, keys, signing constants or captured values. Run locally and in CI.
set -euo pipefail
cd "$(dirname "$0")/.."

# Case-insensitive denylist. Each entry is an extended regex.
deny=(
  '***REMOVED***' '***REMOVED***' '***REMOVED***' '***REMOVED***' '***REMOVED***' 'kino-?light' '***REMOVED***' '***REMOVED***' '***REMOVED***'
  '***REMOVED***' '***REMOVED***' '***REMOVED***' '***REMOVED***' 'okhttp/3\.12\.12' 'com\.android\.msandroid'
  '***REMOVED***' '***REMOVED***' '***REMOVED***' '***REMOVED***'
  '***REMOVED***' '***REMOVED***'
  '***REMOVED***'
  '100\.64\.0\.5' '***REMOVED***' '***REMOVED***' '***REMOVED***'
  '***REMOVED***' '***REMOVED***'
)

pattern=$(IFS='|'; echo "${deny[*]}")
# Everything tracked-ish, minus build output and VCS metadata, and minus this script (it lists the tokens).
hits=$(grep -rInEi --exclude-dir=bin --exclude-dir=obj --exclude-dir=.git --exclude-dir=artifacts \
  --exclude='scrub-check.sh' --exclude='ScrubGuardTests.cs' "$pattern" . || true)

if [[ -n "$hits" ]]; then
  echo "Forbidden tokens found:" >&2
  echo "$hits" >&2
  exit 1
fi
echo "scrub-check: clean"
