#!/usr/bin/env bash
set -Eeuo pipefail

SLUG="justybase-db2-ibm-parity"
IMAGE="icr.io/db2_community/db2:12.1.5.0"
EXPECTED_DIGEST="sha256:2de8151713c261843868c5c3411b57be6ae79d99d70a5b3022337836776bfda6"
READY_TIMEOUT="${DB2_IBM_PARITY_READY_TIMEOUT:-480}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONTAINER=""
WORKDIR=""
PASSWORD=""

cleanup() {
  local rc=$?
  trap - EXIT INT TERM
  set +e
  if [ -n "$CONTAINER" ]; then docker rm -f -v "$CONTAINER" >/dev/null 2>&1 || true; fi
  if [ -n "$WORKDIR" ]; then rm -rf "$WORKDIR"; fi
  exit "$rc"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

fail() { echo "[ibm-parity] FAIL: $*" >&2; exit 1; }
log() { echo "[ibm-parity] $*"; }

[ "$(uname -s)" = "Linux" ] || fail "Linux is required because the official parity package is Net.IBM.Data.Db2-lnx."
case "$(uname -m)" in x86_64|amd64) ;; *) fail "linux/amd64 is required for the pinned Db2 image." ;; esac
command -v docker >/dev/null 2>&1 || fail "docker not found"
command -v dotnet >/dev/null 2>&1 || fail "dotnet not found"
docker info >/dev/null 2>&1 || fail "docker daemon unavailable"

if ! docker image inspect "$IMAGE" >/dev/null 2>&1; then
  log "Pulling pinned IBM Db2 image..."
  docker pull "$IMAGE" >/dev/null
fi
actual_digests="$(docker image inspect --format '{{range .RepoDigests}}{{println .}}{{end}}' "$IMAGE" 2>/dev/null || true)"
case "$actual_digests" in *"$EXPECTED_DIGEST"*) ;; *) fail "Db2 image digest mismatch" ;; esac

RUN_TAG="$(od -An -N3 -tx1 /dev/urandom | tr -d ' \n')"
CONTAINER="${SLUG}-${RUN_TAG}"
PASSWORD="$(od -An -N12 -tx1 /dev/urandom | tr -d ' \n')"
WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/${SLUG}.XXXXXX")"
umask 077
cat > "$WORKDIR/db2.env" <<EOF
LICENSE=accept
DB2INSTANCE=db2inst1
DB2INST1_PASSWORD=$PASSWORD
DBNAME=testdb
BLU=false
ENABLE_ORACLE_COMPATIBILITY=false
UPDATEAVAIL=NO
TO_CREATE_SAMPLEDB=false
REPODB=false
IS_OSXFS=false
PERSISTENT_HOME=false
HADR_ENABLED=false
ETCD_ENDPOINT=
ETCD_USERNAME=
ETCD_PASSWORD=
EOF

log "Starting isolated IBM Db2 12.1.5..."
docker run --detach --name "$CONTAINER" --privileged \
  --publish 127.0.0.1::50000 --env-file "$WORKDIR/db2.env" "$IMAGE" >/dev/null

PORT_LINE="$(docker port "$CONTAINER" 50000/tcp | head -n 1)"
HOST_PORT="${PORT_LINE##*:}"
case "$HOST_PORT" in ''|*[!0-9]*) fail "could not determine host port" ;; esac

db2_sql() {
  docker exec "$CONTAINER" su - db2inst1 -c \
    "db2 connect to testdb >/dev/null && db2 -x \"$1\" ; rc=\$?; db2 terminate >/dev/null 2>&1; exit \$rc"
}

log "Waiting for Db2..."
started=$SECONDS
ready=0
while [ $((SECONDS - started)) -lt "$READY_TIMEOUT" ]; do
  state="$(docker inspect -f '{{.State.Status}}' "$CONTAINER" 2>/dev/null || echo gone)"
  [ "$state" = running ] || fail "Db2 container stopped unexpectedly"
  # The image may leave /database/config owned by root; this is harmless for the
  # database but can prevent its setup supervisor from completing db2cfexp.
  docker exec --user 0:0 "$CONTAINER" bash -c 'chown db2inst1:db2iadm1 /database/config 2>/dev/null || true' >/dev/null 2>&1 || true
  if docker logs "$CONTAINER" 2>&1 | grep -q 'Setup has completed' && db2_sql "VALUES 1" >/dev/null 2>&1; then
    ready=1
    break
  fi
  sleep 3
done
[ "$ready" -eq 1 ] || fail "Db2 did not become ready within ${READY_TIMEOUT}s"

export DB2_LIVE_TEST_HOST=127.0.0.1
export DB2_LIVE_TEST_PORT="$HOST_PORT"
export DB2_LIVE_TEST_DATABASE=testdb
export DB2_LIVE_TEST_USER=db2inst1
export DB2_LIVE_TEST_PASSWORD="$PASSWORD"
export DB2_TEST_DSN="db2://db2inst1:${PASSWORD}@127.0.0.1:${HOST_PORT}/testdb"

cd "$REPO_ROOT"
log "Building IBM parity tests..."
dotnet build src/Db2.IBMParity.Tests/Db2.IBMParity.Tests.csproj -c Release --nologo -v q
IBM_LIB="$REPO_ROOT/src/Db2.IBMParity.Tests/bin/Release/net10.0/clidriver/lib"
[ -d "$IBM_LIB" ] || fail "official IBM clidriver libraries were not restored"
export LD_LIBRARY_PATH="$IBM_LIB:$IBM_LIB/icc${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"

log "Running IBM official-provider parity tests..."
dotnet test src/Db2.IBMParity.Tests/Db2.IBMParity.Tests.csproj -c Release --no-build --nologo -v normal \
  --filter 'Category=IBMParity' \
  --logger 'trx;LogFileName=ibm-parity.trx' \
  --results-directory "${DB2_IBM_PARITY_REPORT_DIR:-$WORKDIR/results}"

log "PASS"
