#!/usr/bin/env bash
# Local Db2 integration tests for JustyBase.Db2.
#
# Starts a throwaway IBM Db2 Community container, waits until it really accepts
# SQL, runs the live integration tests against THAT container only, and removes
# the container (and its volumes) afterwards, also on failure or Ctrl-C.
#
# Usage: scripts/test-db2-integration.sh [--full]
#   (default)  small smoke suite: connect, DML/types/NULLs, parameters,
#              transactions, result shape, error propagation
#   --full     every existing live test of this repository that needs only the
#              one database started here (takes several minutes)
#
# Environment overrides (all optional):
#   DB2_IT_IMAGE          Db2 image (default below; must not be a floating tag)
#   DB2_IT_READY_TIMEOUT  seconds to wait for Db2 (default 480)
#   DB2_IT_REPORT_DIR     optional directory for redacted console and TRX results
#
# This script never reads .env files or DB2_* variables from the caller's
# environment: they are cleared before the tests run, so the tests can only
# reach the container started by this invocation.
set -Eeuo pipefail

SLUG="justybase-db2"
# Pinned Db2 LUW 12.1.5 Community image from the IBM Container Registry. The
# default image is verified against REQUIRED_DIGEST; an override must pin its own
# digest (image@sha256:... or DB2_IT_EXPECTED_DIGEST) or the run fails closed.
DEFAULT_IMAGE="icr.io/db2_community/db2:12.1.5.0"
REQUIRED_DIGEST="sha256:2de8151713c261843868c5c3411b57be6ae79d99d70a5b3022337836776bfda6"
IMAGE="${DB2_IT_IMAGE:-$DEFAULT_IMAGE}"
READY_TIMEOUT="${DB2_IT_READY_TIMEOUT:-480}"
REPORT_DIR_INPUT="${DB2_IT_REPORT_DIR:-}"
DB_NAME="testdb"
DB_USER="db2inst1"

# Expected digest: the image's own @sha256, an explicit override, or the default
# digest when the default image is used. Empty means a tag-only override.
case "$IMAGE" in
  *@sha256:*) IMAGE_DIGEST="${IMAGE##*@}" ;;
  *) IMAGE_DIGEST="" ;;
esac
if [ -n "${DB2_IT_EXPECTED_DIGEST:-}" ]; then
  EXPECTED_DIGEST="$DB2_IT_EXPECTED_DIGEST"
elif [ -n "$IMAGE_DIGEST" ]; then
  EXPECTED_DIGEST="$IMAGE_DIGEST"
elif [ -z "${DB2_IT_IMAGE:-}" ]; then
  EXPECTED_DIGEST="$REQUIRED_DIGEST"
else
  EXPECTED_DIGEST=""
fi

SUITE="smoke"
for arg in "$@"; do
  case "$arg" in
    --full) SUITE="full" ;;
    -h|--help) sed -n '2,/^set -Eeuo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) printf '[db2-it] FAIL: unknown argument: %s (see --help)\n' "$arg" >&2; exit 2 ;;
  esac
done

log()  { printf '[db2-it] %s\n' "$*"; }
warn() { printf '[db2-it] WARN: %s\n' "$*" >&2; }
die()  { printf '[db2-it] FAIL: %s\n' "$*" >&2; exit 1; }

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
STAGE="startup"
CONTAINER=""
WORKDIR=""
PASSWORD=""
REPORT_DIR=""
CONFIG_PERMISSIONS_FIXED=0

# ---------------------------------------------------------------- cleanup ---
# Runs on every exit path. The original exit status is preserved; a container
# that could not be removed turns an otherwise green run into a failure.
cleanup() {
  local rc=$?
  trap - EXIT INT TERM
  set +e
  local cleanup_failed=0

  if [ "$rc" -ne 0 ]; then
    if [ "$rc" -eq 130 ] || [ "$rc" -eq 143 ]; then
      log "Interrupted during stage: $STAGE"
    else
      printf '[db2-it] FAIL: stage "%s" failed (exit %s)\n' "$STAGE" "$rc" >&2
      if [ -n "$CONTAINER" ]; then
        { echo "[db2-it] ---- docker ps -a ----"
          docker ps -a
          echo "[db2-it] ---- docker logs $CONTAINER (last 200 lines) ----"
          docker logs --tail 200 "$CONTAINER" 2>&1 | redact
        } >&2
      fi
    fi
  fi

  if [ -n "$CONTAINER" ]; then
    log "Removing Db2 container $CONTAINER..."
    docker rm -f -v "$CONTAINER" >/dev/null 2>&1
    if docker inspect "$CONTAINER" >/dev/null 2>&1; then
      warn "container $CONTAINER still exists; remove it with: docker rm -f -v $CONTAINER"
      cleanup_failed=1
    fi
  fi
  if [ -n "$WORKDIR" ]; then rm -rf "$WORKDIR"; fi

  if [ "$cleanup_failed" -ne 0 ] && [ "$rc" -eq 0 ]; then rc=1; fi
  exit "$rc"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

redact() { if [ -n "$PASSWORD" ]; then sed "s/$PASSWORD/[REDACTED]/g"; else cat; fi; }

redact_report() {
  local file="$1"
  [ -f "$file" ] || return 0
  local temporary="${file}.redacted"
  if ! redact < "$file" > "$temporary" || ! mv "$temporary" "$file"; then
    rm -f "$temporary" "$file"
    return 1
  fi
}

# -------------------------------------------------------------- pre-flight ---
STAGE="pre-flight"
log "Checking environment..."
[ "$(uname -s)" = "Linux" ] || die "Linux is required (found $(uname -s))."
case "$(uname -m)" in
  x86_64|amd64) ;;
  *) die "x86_64 is required: the IBM Db2 Community image is linux/amd64 only (found $(uname -m))." ;;
esac
command -v docker >/dev/null 2>&1 || die "Docker CLI not found. Install Docker Engine and make sure 'docker' is on PATH."
docker info >/dev/null 2>&1 || die "Docker daemon is not reachable. Start Docker and check that your user may run 'docker info'."
case "$IMAGE" in
  *:latest|*:) die "DB2_IT_IMAGE must be pinned to an exact version, not a floating tag: $IMAGE" ;;
  *:*) ;;
  *) die "DB2_IT_IMAGE must include an explicit tag: $IMAGE" ;;
esac
[ -n "$EXPECTED_DIGEST" ] || die "DB2_IT_IMAGE override must pin a digest (image@sha256:...) or set DB2_IT_EXPECTED_DIGEST."
command -v dotnet >/dev/null 2>&1 || die "dotnet not found. Install the .NET 10 SDK."

# ----------------------------------------------------------------- startup ---
RUN_TAG="$(od -An -N3 -tx1 /dev/urandom | tr -d ' \n')"
CONTAINER="${SLUG}-db2-it-${RUN_TAG}"
SCHEMA="IT_$(printf '%s' "$RUN_TAG" | tr 'a-f' 'A-F')"
PASSWORD="$(od -An -N12 -tx1 /dev/urandom | tr -d ' \n')"
WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/${SLUG}-db2-it.XXXXXX")"
if [ -n "$REPORT_DIR_INPUT" ]; then
  mkdir -p "$REPORT_DIR_INPUT"
  REPORT_DIR="$(cd "$REPORT_DIR_INPUT" && pwd)"
else
  REPORT_DIR="$WORKDIR/test-results"
  mkdir -p "$REPORT_DIR"
fi

STAGE="image pull"
if ! docker image inspect "$IMAGE" >/dev/null 2>&1; then
  log "Pulling $IMAGE (first run only; the image is several GB)..."
  docker pull "$IMAGE" >/dev/null || die "could not pull $IMAGE"
fi

STAGE="image digest verification"
actual_digests="$(docker image inspect --format '{{range .RepoDigests}}{{println .}}{{end}}' "$IMAGE" 2>/dev/null || true)"
case "$actual_digests" in
  *"$EXPECTED_DIGEST"*) log "Image digest verified: $EXPECTED_DIGEST" ;;
  *) die "Db2 image digest mismatch for $IMAGE: expected $EXPECTED_DIGEST, found: ${actual_digests:-<none>}" ;;
esac

STAGE="container start"
log "Starting IBM Db2 ($IMAGE)..."
# The password goes through an env file (mode 600) so it never appears in a
# process list. The file lives in WORKDIR and is removed by cleanup.
umask 077
cat > "$WORKDIR/db2.env" <<EOF
LICENSE=accept
DB2INSTANCE=$DB_USER
DB2INST1_PASSWORD=$PASSWORD
DBNAME=$DB_NAME
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
# CONTAINER is already set, so cleanup also removes a container that docker
# created but failed to start.
docker run --detach --name "$CONTAINER" --privileged \
    --label "db2-it.repo=$SLUG" --label "db2-it.run=$RUN_TAG" \
    --publish 127.0.0.1::50000 --env-file "$WORKDIR/db2.env" "$IMAGE" >/dev/null \
  || die "docker run failed"
log "Container: $CONTAINER"

# The pinned Db2 image creates /database/config as root:root (0755), then its
# supervisor runs db2cfexp as the instance user with that directory as output.
# Give that user write access inside this disposable, test-owned container.
fix_config_permissions() {
  [ "$CONFIG_PERMISSIONS_FIXED" -eq 0 ] || return 0
  if docker exec --user 0:0 "$CONTAINER" bash -c '
      instance="${DB2INSTANCE:-db2inst1}"
      getent passwd "$instance" >/dev/null 2>&1 || exit 1
      [ -d /database/config ] || exit 1
      chown "$instance:db2iadm1" /database/config && chmod u+rwx /database/config || exit 1
      if [ -e /database/config/dbdbmcfg ]; then
        chown "$instance:db2iadm1" /database/config/dbdbmcfg && chmod u+rw /database/config/dbdbmcfg || exit 1
      fi
    ' >/dev/null 2>&1; then
    CONFIG_PERMISSIONS_FIXED=1
    log "Adjusted disposable Db2 config directory permissions for $DB_USER."
  fi
}

PORT_LINE="$(docker port "$CONTAINER" 50000/tcp | head -n 1)"
HOST_PORT="${PORT_LINE##*:}"
case "$HOST_PORT" in ''|*[!0-9]*) die "could not determine the mapped host port (got '$PORT_LINE')" ;; esac
log "Host port: $HOST_PORT"

# --------------------------------------------------------------- readiness ---
# Ready = the image's setup finished, the database accepts a real SQL
# statement, and the mapped TCP port accepts connections.
db2_sql() {
  docker exec "$CONTAINER" su - "$DB_USER" -c \
    "db2 connect to $DB_NAME >/dev/null && db2 -x \"$1\" ; rc=\$?; db2 terminate >/dev/null 2>&1; exit \$rc"
}
tcp_open() { (exec 3<>"/dev/tcp/127.0.0.1/$HOST_PORT") 2>/dev/null; }

STAGE="waiting for Db2"
log "Waiting for Db2 (timeout ${READY_TIMEOUT}s)..."
started=$SECONDS
next_progress=30
ready=0
setup_completed_at=""
while [ $((SECONDS - started)) -lt "$READY_TIMEOUT" ]; do
  state="$(docker inspect -f '{{.State.Status}}' "$CONTAINER" 2>/dev/null || echo gone)"
  [ "$state" = "running" ] || die "Db2 container stopped unexpectedly (state: $state)."
  fix_config_permissions
  if docker logs "$CONTAINER" 2>&1 | grep -q 'Setup has completed'; then
    if db2_sql "VALUES 1" >/dev/null 2>&1 && tcp_open; then ready=1; break; fi
    if [ -z "$setup_completed_at" ]; then
      setup_completed_at=$SECONDS
    elif [ $((SECONDS - setup_completed_at)) -ge 45 ]; then
      log "Db2 reported setup complete but database $DB_NAME is not connectable; database directories:"
      docker exec --user 0:0 "$CONTAINER" su - "$DB_USER" -c \
        'db2 list db directory; db2 list db directory on /database/data' 2>&1 | redact || true
      die "Db2 setup completed but the test database did not become ready within 45 seconds."
    fi
  fi
  elapsed=$((SECONDS - started))
  if [ "$elapsed" -ge "$next_progress" ]; then log "  still waiting... ${elapsed}s"; next_progress=$((next_progress + 30)); fi
  sleep 3
done
[ "$ready" -eq 1 ] || die "Db2 did not become ready within ${READY_TIMEOUT} seconds."
log "Db2 is ready ($((SECONDS - started))s)"

# ---------------------------------------------------------------- fixtures ---
STAGE="preparing fixtures"
log "Preparing fixtures (schema $SCHEMA)..."
db2_sql "CREATE SCHEMA $SCHEMA" >/dev/null || die "could not create schema $SCHEMA"
db2_sql "SELECT COUNT(*) FROM SYSCAT.SCHEMATA WHERE SCHEMANAME = '$SCHEMA'" | grep -qx '[[:space:]]*1' \
  || die "schema $SCHEMA was not created"

# ------------------------------------------------------------- test run ------
# Clear everything that could point the tests at some other database, then set
# exactly one connection description: the container started above.
while IFS= read -r name; do unset "$name"; done < <(compgen -e | grep '^DB2_' || true)
export DB2_TEST_DSN="db2://${DB_USER}:${PASSWORD}@127.0.0.1:${HOST_PORT}/${DB_NAME}"
export DB2_LIVE_TEST_HOST="127.0.0.1"
export DB2_LIVE_TEST_PORT="$HOST_PORT"
export DB2_LIVE_TEST_DATABASE="$DB_NAME"
export DB2_LIVE_TEST_USER="$DB_USER"
export DB2_LIVE_TEST_PASSWORD="$PASSWORD"
export DB2_IT_SCHEMA="$SCHEMA"

STAGE="running tests"
log "Running integration tests ($SUITE suite)..."
cd "$REPO_ROOT"
if [ "$SUITE" = "full" ]; then
  TEST_FILTER="Category=Live"
else
  TEST_FILTER="Category=Db2Integration"
fi
RESULT_NAME="${RUN_TAG}-${SUITE}"
TEST_LOG="$REPORT_DIR/${RESULT_NAME}.log"
TEST_TRX="$REPORT_DIR/${RESULT_NAME}.trx"
set +e
dotnet test src/Db2.Tests/Db2.Tests.csproj --filter "$TEST_FILTER" \
    --logger "console;verbosity=normal" \
    --logger "trx;LogFileName=${RESULT_NAME}.trx" \
    --results-directory "$REPORT_DIR" 2>&1 | redact | tee "$TEST_LOG"
PIPE_STATUS=("${PIPESTATUS[@]}")
set -e
redact_report "$TEST_TRX"
[ "${PIPE_STATUS[0]}" -eq 0 ] || exit "${PIPE_STATUS[0]}"
if [ "${PIPE_STATUS[1]:-0}" -ne 0 ] || [ "${PIPE_STATUS[2]:-0}" -ne 0 ]; then
  die "could not redact or save the test output"
fi

log "PASS"
