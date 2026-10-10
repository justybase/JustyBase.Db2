#!/usr/bin/env bash
# Comprehensive repository-local live qualification for JustyBase.Db2.
#
# Runs the full ordinary Db2 live suite once and then repeats the real
# MIT KDC -> Kerberos -> JustyBase.Db2 -> DRDA SECMEC 11 -> Db2 -> SQL E2E suite
# on fresh, independent environments.
#
# Usage:
#   scripts/test-db2-qualification.sh [--kerberos-repeats N] [--verbose-kerberos]
#
# Defaults:
#   --kerberos-repeats 3
#
# The underlying scripts own and clean their environments. Runs are deliberately
# sequential so the ordinary Db2 container and Kerberos qualification environments
# never compete for runner memory at the same time.
set -Eeuo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
KERBEROS_REPEATS=3
KERBEROS_VERBOSE=0

fail() { printf '[db2-qual] FAIL: %s\n' "$*" >&2; exit 2; }
log()  { printf '[db2-qual] %s\n' "$*"; }

while [ "$#" -gt 0 ]; do
  case "$1" in
    --kerberos-repeats)
      [ "$#" -ge 2 ] || fail "--kerberos-repeats requires a value"
      KERBEROS_REPEATS="$2"
      shift 2
      ;;
    --kerberos-repeats=*)
      KERBEROS_REPEATS="${1#*=}"
      shift
      ;;
    --verbose-kerberos)
      KERBEROS_VERBOSE=1
      shift
      ;;
    -h|--help)
      sed -n '2,/^set -Eeuo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'
      exit 0
      ;;
    *)
      fail "unknown argument: $1 (see --help)"
      ;;
  esac
done

case "$KERBEROS_REPEATS" in
  ''|*[!0-9]*) fail "--kerberos-repeats must be a positive integer" ;;
esac
[ "$KERBEROS_REPEATS" -ge 1 ] || fail "--kerberos-repeats must be >= 1"

log "Running full ordinary Db2 live suite..."
"$REPO_ROOT/scripts/test-db2-integration.sh" --full

KERBEROS_ARGS=()
if [ "$KERBEROS_VERBOSE" -eq 1 ]; then KERBEROS_ARGS+=(--verbose); fi

for ((run = 1; run <= KERBEROS_REPEATS; run++)); do
  log "Running fresh Kerberos E2E ${run}/${KERBEROS_REPEATS}..."
  "$REPO_ROOT/scripts/test-db2-kerberos.sh" "${KERBEROS_ARGS[@]}"
done

log "PASS: full Db2 live suite + ${KERBEROS_REPEATS} fresh Kerberos E2E run(s)"
