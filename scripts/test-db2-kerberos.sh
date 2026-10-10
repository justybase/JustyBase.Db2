#!/usr/bin/env bash
# Real Kerberos end-to-end test for JustyBase.Db2 (DRDA security mechanism 11).
#
#   fresh MIT KDC -> client/service principals -> GSS-API credentials (keytab, ccache)
#     -> JustyBase.Db2 -> SECMEC 11 -> IBM Db2 Community 12.1.5 (IBMkrb5) -> authenticated SQL
#
# Every invocation builds a brand-new, isolated environment: its own Docker network,
# its own KDC with a new realm database, new principals/keytabs/ccaches, and a new Db2
# container. Nothing is reused, and everything is removed afterwards (also on failure,
# Ctrl-C and SIGTERM). Names, passwords and keys are generated per run; nothing is
# read from your environment, /etc/krb5.conf or any real Kerberos infrastructure.
#
# Usage: scripts/test-db2-kerberos.sh [--verbose] [--keep-on-failure]
#   --verbose          log extra provisioning detail
#   --keep-on-failure  leave containers, network and work directory in place when a
#                      run fails (also: DB2_KRB_KEEP_ON_FAILURE=1); remove them manually
#
# Environment overrides (all optional):
#   DB2_KRB_IMAGE          Db2 image (default below; must be pinned, digest-verified)
#   DB2_KRB_READY_TIMEOUT  seconds to wait for Db2 (default 600)
#   DB2_KRB_REPORT_DIR     optional directory for redacted console and TRX results
#
# Prerequisites: Linux x86_64, Docker (privileged containers allowed), network access to
# pull the pinned base images on first use. The .NET SDK is NOT needed on the host: the
# tests run in a pinned SDK container that is attached to the test network.
set -Eeuo pipefail

SLUG="justybase-db2-krb"
REALM="DB2TEST.LOCAL"
KDC_FQDN="kdc.db2test.local"
DB2_FQDN="db2.db2test.local"
DB2_INSTANCE="db2inst1"
DB_NAME="testdb"
CLIENT_USER="db2test"
CLIENT_PRINCIPAL="${CLIENT_USER}@${REALM}"
OTHER_USER="db2other"
OTHER_PRINCIPAL="${OTHER_USER}@${REALM}"
DB2_SPN="${DB2_INSTANCE}/${DB2_FQDN}@${REALM}"
SPN_UNKNOWN_TO_DB2="db2wrong/${DB2_FQDN}@${REALM}"
SPN_UNKNOWN_TO_KDC="${DB2_INSTANCE}/nosuchhost.db2test.local@${REALM}"
WRONG_REALM_PRINCIPAL="${CLIENT_USER}@WRONG.REALM"

# Same pinned Db2 LUW 12.1.5 Community image and digest as scripts/test-db2-integration.sh.
DEFAULT_IMAGE="icr.io/db2_community/db2:12.1.5.0"
REQUIRED_DIGEST="sha256:2de8151713c261843868c5c3411b57be6ae79d99d70a5b3022337836776bfda6"
IMAGE="${DB2_KRB_IMAGE:-$DEFAULT_IMAGE}"
READY_TIMEOUT="${DB2_KRB_READY_TIMEOUT:-600}"
# Test runner: .NET SDK 10 on Ubuntu 24.04 (has libgssapi_krb5), pinned by digest.
RUNNER_IMAGE="mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317"
KDC_IMAGE="justybase-db2-krb-kdc:local"

case "$IMAGE" in
  *@sha256:*) IMAGE_DIGEST="${IMAGE##*@}" ;;
  *) IMAGE_DIGEST="" ;;
esac
if [ -n "${DB2_KRB_EXPECTED_DIGEST:-}" ]; then
  EXPECTED_DIGEST="$DB2_KRB_EXPECTED_DIGEST"
elif [ -n "$IMAGE_DIGEST" ]; then
  EXPECTED_DIGEST="$IMAGE_DIGEST"
elif [ -z "${DB2_KRB_IMAGE:-}" ]; then
  EXPECTED_DIGEST="$REQUIRED_DIGEST"
else
  EXPECTED_DIGEST=""
fi

VERBOSE=0
KEEP_ON_FAILURE="${DB2_KRB_KEEP_ON_FAILURE:-0}"
REPORT_DIR_INPUT="${DB2_KRB_REPORT_DIR:-}"
for arg in "$@"; do
  case "$arg" in
    --verbose) VERBOSE=1 ;;
    --keep-on-failure) KEEP_ON_FAILURE=1 ;;
    -h|--help) sed -n '2,/^set -Eeuo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) printf '[db2-krb] FAIL: unknown argument: %s (see --help)\n' "$arg" >&2; exit 2 ;;
  esac
done

log()  { printf '[db2-krb] %s\n' "$*"; }
vlog() { if [ "$VERBOSE" -eq 1 ]; then printf '[db2-krb]   %s\n' "$*"; fi; }
warn() { printf '[db2-krb] WARN: %s\n' "$*" >&2; }
die()  { printf '[db2-krb] FAIL: %s\n' "$*" >&2; exit 1; }

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
STAGE="startup"
RUN_TAG=""
NET=""
KDC=""
DB2=""
WORKDIR=""
REPORT_DIR=""
DB2_PASSWORD=""
CLIENT_PASSWORD=""
KDC_MASTER_PASSWORD=""
DB2_UID=""

redact() {
  local filter=cat secret
  local expr=""
  for secret in "$DB2_PASSWORD" "$CLIENT_PASSWORD" "$KDC_MASTER_PASSWORD"; do
    [ -n "$secret" ] && expr="${expr}s/${secret}/[REDACTED]/g;"
  done
  if [ -n "$expr" ]; then sed "$expr"; else $filter; fi
}

redact_report() {
  local file="$1"
  [ -f "$file" ] || return 0
  local temporary="${file}.redacted"
  if ! redact < "$file" > "$temporary" || ! mv "$temporary" "$file"; then
    rm -f "$temporary" "$file"
    return 1
  fi
}

diagnostics() {
  {
    echo "[db2-krb] ---- diagnostics (secrets redacted) ----"
    echo "[db2-krb] realm=$REALM client=$CLIENT_PRINCIPAL service-principal=$DB2_SPN"
    echo "[db2-krb] ---- docker ps (this run) ----"
    docker ps -a --filter "label=${SLUG}.run=${RUN_TAG}" --format '{{.Names}}  {{.Status}}' 2>&1
    if [ -n "$DB2" ] && docker inspect "$DB2" >/dev/null 2>&1; then
      echo "[db2-krb] ---- Db2 authentication settings ----"
      docker exec -u "$DB2_INSTANCE" "$DB2" bash -lc 'db2 get dbm cfg | grep -i -E "AUTHENTICATION|SRVCON_AUTH|GSSPLUGIN|CLNT_KRB"' 2>&1 | redact
      echo "[db2-krb] ---- Db2 diagnostic log (Kerberos/security entries, tail) ----"
      docker exec "$DB2" bash -c "grep -h -A6 -E 'IBMkrb5|ADM13001E|SQL30082|GSS' /home/${DB2_INSTANCE}/sqllib/db2dump/DIAG0000/db2diag.log | tail -n 60" 2>&1 | redact
    fi
    if [ -n "$KDC" ] && docker inspect "$KDC" >/dev/null 2>&1; then
      echo "[db2-krb] ---- KDC log (tail) ----"
      docker logs --tail 40 "$KDC" 2>&1 | redact
      echo "[db2-krb] ---- keytab metadata (principal names only) ----"
      docker exec "$KDC" klist -k /work/db2.keytab 2>&1 | head -n 8
      docker exec "$KDC" klist -k /work/client.keytab 2>&1 | head -n 8
    fi
    if [ -n "$WORKDIR" ] && [ -f "$WORKDIR/test-output.log" ]; then
      echo "[db2-krb] ---- test output (tail) ----"
      tail -n 80 "$WORKDIR/test-output.log" | redact
    fi
  } >&2
}

cleanup() {
  local rc=$?
  trap - EXIT INT TERM
  set +e
  local cleanup_failed=0
  if [ "$rc" -ne 0 ]; then
    if [ "$rc" -eq 130 ] || [ "$rc" -eq 143 ]; then
      log "Interrupted during stage: $STAGE"
    else
      printf '[db2-krb] FAIL: stage "%s" failed (exit %s)\n' "$STAGE" "$rc" >&2
      if [ -n "$RUN_TAG" ]; then diagnostics; fi
    fi
    if [ "$KEEP_ON_FAILURE" = "1" ] && [ -n "$RUN_TAG" ]; then
      warn "keeping environment for inspection: containers labelled ${SLUG}.run=${RUN_TAG}, network $NET, work dir $WORKDIR"
      warn "remove it with: docker rm -f -v \$(docker ps -aq --filter label=${SLUG}.run=${RUN_TAG}); docker network rm $NET; rm -rf $WORKDIR"
      exit "$rc"
    fi
  fi
  if [ -n "$RUN_TAG" ]; then
    log "Removing containers, network and temporary files..."
    local ids
    ids="$(docker ps -aq --filter "label=${SLUG}.run=${RUN_TAG}" 2>/dev/null)"
    if [ -n "$ids" ]; then docker rm -f -v $ids >/dev/null 2>&1; fi
    if [ -n "$(docker ps -aq --filter "label=${SLUG}.run=${RUN_TAG}" 2>/dev/null)" ]; then
      warn "containers of run $RUN_TAG still exist"; cleanup_failed=1
    fi
    if [ -n "$NET" ]; then
      docker network rm "$NET" >/dev/null 2>&1
      if docker network inspect "$NET" >/dev/null 2>&1; then warn "network $NET still exists"; cleanup_failed=1; fi
    fi
  fi
  if [ -n "$WORKDIR" ]; then rm -rf "$WORKDIR"; fi
  if [ "$cleanup_failed" -ne 0 ] && [ "$rc" -eq 0 ]; then rc=1; fi
  exit "$rc"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

STAGE="pre-flight"
log "Checking environment..."
[ "$(uname -s)" = "Linux" ] || die "Linux is required (found $(uname -s))."
case "$(uname -m)" in x86_64|amd64) ;; *) die "x86_64 is required: the IBM Db2 Community image is linux/amd64 only (found $(uname -m))." ;; esac
command -v docker >/dev/null 2>&1 || die "Docker CLI not found."
docker info >/dev/null 2>&1 || die "Docker daemon is not reachable."
case "$IMAGE" in *:latest|*:) die "DB2_KRB_IMAGE must be pinned to an exact version, not a floating tag: $IMAGE" ;; *:*) ;; *) die "DB2_KRB_IMAGE must include an explicit tag: $IMAGE" ;; esac
[ -n "$EXPECTED_DIGEST" ] || die "DB2_KRB_IMAGE override must pin a digest (image@sha256:...) or set DB2_KRB_EXPECTED_DIGEST."
command -v tar >/dev/null 2>&1 || die "tar not found."

random_hex() { od -An -N"$1" -tx1 /dev/urandom | tr -d ' \n'; }
RUN_TAG="$(random_hex 3)"
NET="${SLUG}-net-${RUN_TAG}"
KDC="${SLUG}-kdc-${RUN_TAG}"
DB2="${SLUG}-db2-${RUN_TAG}"
umask 077
WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/${SLUG}.XXXXXX")"
mkdir -p "$WORKDIR/kdc" "$WORKDIR/client" "$WORKDIR/src"
if [ -n "$REPORT_DIR_INPUT" ]; then
  mkdir -p "$REPORT_DIR_INPUT"
  REPORT_DIR="$(cd "$REPORT_DIR_INPUT" && pwd)"
else
  REPORT_DIR="$WORKDIR/test-results"
  mkdir -p "$REPORT_DIR"
fi
DB2_PASSWORD="$(random_hex 12)"
CLIENT_PASSWORD="$(random_hex 12)"
KDC_MASTER_PASSWORD="$(random_hex 16)"
LABEL="${SLUG}.run=${RUN_TAG}"

STAGE="image verification"
if ! docker image inspect "$IMAGE" >/dev/null 2>&1; then log "Pulling $IMAGE (first run only; the image is several GB)..."; docker pull "$IMAGE" >/dev/null || die "could not pull $IMAGE"; fi
actual_digests="$(docker image inspect --format '{{range .RepoDigests}}{{println .}}{{end}}' "$IMAGE" 2>/dev/null || true)"
case "$actual_digests" in *"$EXPECTED_DIGEST"*) log "Db2 image digest verified: $EXPECTED_DIGEST" ;; *) die "Db2 image digest mismatch for $IMAGE: expected $EXPECTED_DIGEST, found: ${actual_digests:-<none>}" ;; esac
if ! docker image inspect "$RUNNER_IMAGE" >/dev/null 2>&1; then log "Pulling the pinned .NET SDK test runner image..."; docker pull "$RUNNER_IMAGE" >/dev/null || die "could not pull $RUNNER_IMAGE"; fi

STAGE="building KDC image"
log "Building the MIT KDC image (pinned Ubuntu base)..."
docker build -q -t "$KDC_IMAGE" "$REPO_ROOT/tests/kerberos/kdc" >/dev/null || die "KDC image build failed"
STAGE="creating network"
docker network create --label "$LABEL" "$NET" >/dev/null || die "could not create network $NET"
STAGE="starting KDC"
log "Starting KDC for realm $REALM..."
cat > "$WORKDIR/kdc.env" <<EOF
KRB_REALM=$REALM
KRB_CLIENT_PRINCIPAL=$CLIENT_PRINCIPAL
KRB_DB2_PRINCIPAL=$DB2_SPN
KRB_OTHER_PRINCIPAL=$OTHER_PRINCIPAL
KRB_SPN_UNKNOWN_TO_DB2=$SPN_UNKNOWN_TO_DB2
KRB_MASTER_PASSWORD=$KDC_MASTER_PASSWORD
KRB_OWNER_UID=$(id -u)
KRB_OWNER_GID=$(id -g)
EOF
docker run -d --name "$KDC" --hostname "$KDC_FQDN" --network "$NET" --network-alias "$KDC_FQDN" --label "$LABEL" -v "$WORKDIR/kdc:/work" --env-file "$WORKDIR/kdc.env" "$KDC_IMAGE" >/dev/null || die "could not start the KDC container"
cat > "$WORKDIR/client/krb5.conf" <<EOF
[libdefaults]
  default_realm = $REALM
  dns_lookup_kdc = false
  dns_lookup_realm = false
  rdns = false
  udp_preference_limit = 1
[realms]
  $REALM = {
    kdc = $KDC_FQDN:88
  }
[domain_realm]
  .db2test.local = $REALM
  $DB2_FQDN = $REALM
EOF
STAGE="waiting for KDC"
log "Waiting for the KDC to issue tickets..."
started=$SECONDS; kdc_ready=0
while [ $((SECONDS - started)) -lt 90 ]; do
  state="$(docker inspect -f '{{.State.Status}}' "$KDC" 2>/dev/null || echo gone)"; [ "$state" = "running" ] || die "KDC container stopped unexpectedly (state: $state)."
  if [ -f "$WORKDIR/kdc/client.keytab" ] && [ -f "$WORKDIR/kdc/other.keytab" ] && docker exec "$KDC" kinit -kt /work/client.keytab -c /tmp/probe.cc "$CLIENT_PRINCIPAL" >/dev/null 2>&1; then kdc_ready=1; break; fi
  sleep 1
done
[ "$kdc_ready" -eq 1 ] || die "KDC did not become ready within 90 seconds."
log "KDC is ready ($((SECONDS - started))s)"

STAGE="starting Db2"
cat > "$WORKDIR/db2.env" <<EOF
LICENSE=accept
DB2INSTANCE=$DB2_INSTANCE
DB2INST1_PASSWORD=$DB2_PASSWORD
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

db2_cli() { docker exec -u "$DB2_INSTANCE" "$DB2" bash -lc "$1"; }
fix_db2_config_permissions() {
  docker exec --user 0:0 "$DB2" bash -c '
    instance="${DB2INSTANCE:-db2inst1}"
    getent passwd "$instance" >/dev/null 2>&1 || exit 1
    [ -d /database/config ] || exit 1
    chown "$instance:db2iadm1" /database/config && chmod u+rwx /database/config || exit 1
    if [ -e /database/config/dbdbmcfg ]; then
      chown "$instance:db2iadm1" /database/config/dbdbmcfg && chmod u+rw /database/config/dbdbmcfg || exit 1
    fi
  ' >/dev/null 2>&1
}
db_ready() { db2_cli "db2 connect to $DB_NAME >/dev/null 2>&1 && db2 terminate >/dev/null 2>&1"; }
wait_for_database() { local deadline=$((SECONDS + $1)); while [ "$SECONDS" -lt "$deadline" ]; do if db_ready; then return 0; fi; sleep 3; done; return 1; }
provision_db2_attempt() {
  log "Starting IBM Db2 ($IMAGE) as $DB2_FQDN..."
  docker run -d --name "$DB2" --privileged --hostname "$DB2_FQDN" --network "$NET" --network-alias "$DB2_FQDN" --label "$LABEL" --env-file "$WORKDIR/db2.env" "$IMAGE" >/dev/null || { warn "could not start the Db2 container"; return 1; }
  log "Waiting for Db2 (timeout ${READY_TIMEOUT}s)..."
  local started=$SECONDS next_progress=30 ready=0 elapsed state config_permissions_fixed=0
  while [ $((SECONDS - started)) -lt "$READY_TIMEOUT" ]; do
    state="$(docker inspect -f '{{.State.Status}}' "$DB2" 2>/dev/null || echo gone)"; [ "$state" = "running" ] || { warn "Db2 container stopped unexpectedly (state: $state)"; return 1; }
    if [ "$config_permissions_fixed" -eq 0 ] && fix_db2_config_permissions; then
      config_permissions_fixed=1
      log "Adjusted disposable Db2 config directory permissions for $DB2_INSTANCE."
    fi
    if docker logs "$DB2" 2>&1 | grep -q 'Setup has completed'; then ready=1; break; fi
    elapsed=$((SECONDS - started)); if [ "$elapsed" -ge "$next_progress" ]; then log "  still waiting... ${elapsed}s"; next_progress=$((next_progress + 30)); fi; sleep 3
  done
  [ "$ready" -eq 1 ] || { warn "Db2 setup did not finish within ${READY_TIMEOUT} seconds"; return 1; }
  if wait_for_database 45; then return 0; fi
  log "Database $DB_NAME missing after image setup; creating it..."
  local create_output; create_output="$(db2_cli "db2 create database $DB_NAME 2>&1" || true)"; vlog "create database: $(printf '%s' "$create_output" | redact | tr '\n' ' ' | cut -c1-200)"
  if wait_for_database 60; then return 0; fi
  warn "database creation failed: $(printf '%s' "$create_output" | redact | tr '\n' ' ' | cut -c1-300)"; return 1
}

STAGE="provisioning Db2"
db2_ok=0
for attempt in 1 2 3; do if provision_db2_attempt; then db2_ok=1; break; fi; warn "Db2 provisioning attempt $attempt of 3 failed; discarding the container"; docker rm -f -v "$DB2" >/dev/null 2>&1; done
[ "$db2_ok" -eq 1 ] || die "could not provision a Db2 container with database $DB_NAME after 3 attempts"
db2_cli "db2 connect to $DB_NAME >/dev/null && db2 -x 'VALUES 1' && db2 terminate >/dev/null" >/dev/null || die "Db2 does not answer SQL locally"
log "Db2 is up (local SQL works)"

STAGE="configuring Db2 for Kerberos"
log "Configuring IBMkrb5 on the Db2 instance..."
docker cp "$WORKDIR/client/krb5.conf" "$DB2:/etc/krb5.conf" || die "could not install krb5.conf in the Db2 container"
docker exec "$DB2" chown root:root /etc/krb5.conf
docker exec "$DB2" chmod 644 /etc/krb5.conf
docker exec "$DB2" mkdir -p /etc/db2krb
docker cp "$WORKDIR/kdc/db2.keytab" "$DB2:/etc/db2krb/db2.keytab" || die "could not install the Db2 service keytab"
docker exec "$DB2" groupadd -f db2krb
docker exec "$DB2" usermod -a -G db2krb "$DB2_INSTANCE"
if docker exec "$DB2" id db2fenc1 >/dev/null 2>&1; then docker exec "$DB2" usermod -a -G db2krb db2fenc1; fi
docker exec "$DB2" chown root:db2krb /etc/db2krb/db2.keytab
docker exec "$DB2" chmod 440 /etc/db2krb/db2.keytab
docker exec "$DB2" useradd -m "$CLIENT_USER" || die "could not create OS user $CLIENT_USER"
docker exec "$DB2" bash -c "echo '${CLIENT_USER}:${CLIENT_PASSWORD}' | chpasswd" || die "could not set the OS password"
docker exec "$DB2" useradd -m "$OTHER_USER" || die "could not create OS user $OTHER_USER"

# Kerberos authentication maps the principals to DB2TEST / DB2OTHER. Db2 12.1
# does not grant CONNECT to these fresh authorization IDs automatically in this
# container setup, so provision only the database privileges required by the E2E
# scenarios. DB2TEST also creates a transient table in its current schema;
# DB2OTHER is used only to prove identity separation.
db2_cli "db2 connect to $DB_NAME >/dev/null && db2 \"GRANT CONNECT, CREATETAB, IMPLICIT_SCHEMA ON DATABASE TO USER ${CLIENT_USER^^}\" >/dev/null && db2 \"GRANT USE OF TABLESPACE USERSPACE1 TO USER ${CLIENT_USER^^}\" >/dev/null && db2 \"GRANT CONNECT ON DATABASE TO USER ${OTHER_USER^^}\" >/dev/null && db2 terminate >/dev/null" \
  || die "could not grant database/tablespace privileges to the Kerberos test users"

docker exec "$DB2" bash -c "echo 'export KRB5_KTNAME=/etc/db2krb/db2.keytab' >> /home/${DB2_INSTANCE}/sqllib/userprofile"
db2_cli "db2set DB2ENVLIST=KRB5_KTNAME" || die "db2set DB2ENVLIST failed"

db2_server_ready() {
  # Readiness after enabling IBMkrb5 must not perform a local Db2 login. With
  # CLNT_KRB_PLUGIN=IBMkrb5, an implicit local CLP connection would require a
  # default client credential cache and can fail even when the server is ready.
  # The IBM reference-client check below performs the actual authenticated probe.
  docker exec "$DB2" bash -c 'ps -eo comm= | grep -qx db2sysc && (exec 3<>/dev/tcp/127.0.0.1/50000) 2>/dev/null'
}

configure_db2_auth() {
  local mode="$1"
  db2_cli "db2 update dbm cfg using AUTHENTICATION SERVER SRVCON_GSSPLUGIN_LIST IBMkrb5 SRVCON_AUTH $mode CLNT_KRB_PLUGIN IBMkrb5 >/dev/null" || die "could not set incoming Db2 authentication mode to $mode"
  docker exec -u "$DB2_INSTANCE" "$DB2" test -r /etc/db2krb/db2.keytab || die "Db2 instance owner cannot read the service keytab"
  if docker exec "$DB2" id db2fenc1 >/dev/null 2>&1; then docker exec -u db2fenc1 "$DB2" test -r /etc/db2krb/db2.keytab || die "Db2 fenced user cannot read the service keytab"; fi
  db2_cli "export KRB5_KTNAME=/etc/db2krb/db2.keytab; db2 force application all >/dev/null 2>&1; db2stop force >/dev/null; db2start >/dev/null" || die "Db2 did not restart with authentication mode $mode"
  local waited=0
  until db2_server_ready; do
    waited=$((waited + 1))
    [ "$waited" -lt 60 ] || die "Db2 engine/DRDA listener did not come back after the restart"
    sleep 2
  done
}

STAGE="acquiring client tickets"
log "Acquiring client ticket (ccache) and fixtures..."
cp "$WORKDIR/kdc/client.keytab" "$WORKDIR/client/client.keytab"
cp "$WORKDIR/kdc/other.keytab" "$WORKDIR/client/other.keytab"
docker exec "$KDC" kinit -kt /work/client.keytab -c /work/client.ccache "$CLIENT_PRINCIPAL" || die "kinit for the client ccache failed"
docker exec "$KDC" kinit -l 1s -kt /work/client.keytab -c /work/expired.ccache "$CLIENT_PRINCIPAL" || die "kinit for the short-lived ccache failed"
docker exec "$KDC" chown "$(id -u):$(id -g)" /work/client.ccache /work/expired.ccache
cp "$WORKDIR/kdc/client.ccache" "$WORKDIR/client/client.ccache"
cp "$WORKDIR/kdc/expired.ccache" "$WORKDIR/client/expired.ccache"
chmod 600 "$WORKDIR/client/"*.keytab "$WORKDIR/client/"*.ccache

STAGE="infrastructure self-tests"
db2_engine_env() { docker exec "$DB2" bash -c 'for p in $(ps -eo pid,comm | awk "\$2==\"db2sysc\"{print \$1}"); do tr "\0" "\n" < /proc/$p/environ; done'; }
selftest_infra() {
  local failures=0
  check() { local description="$1"; shift; if "$@" >/dev/null 2>&1; then vlog "ok: $description"; else warn "self-test failed: $description"; failures=$((failures + 1)); fi; }
  check "KDC knows the client principal" docker exec "$KDC" kadmin.local -q "getprinc $CLIENT_PRINCIPAL"
  check "KDC knows the Db2 service principal" docker exec "$KDC" kadmin.local -q "getprinc $DB2_SPN"
  check "KDC knows the unknown-to-Db2 service principal" docker exec "$KDC" kadmin.local -q "getprinc $SPN_UNKNOWN_TO_DB2"
  check "service keytab holds the Db2 service principal" bash -c "docker exec '$KDC' klist -k /work/db2.keytab | grep -q -F '$DB2_SPN'"
  check "client keytab holds the client principal" bash -c "docker exec '$KDC' klist -k /work/client.keytab | grep -q -F '$CLIENT_PRINCIPAL'"
  check "other keytab does not hold the client principal" bash -c "! docker exec '$KDC' klist -k /work/other.keytab | grep -q -F '$CLIENT_PRINCIPAL'"
  check "client ccache holds a TGT" bash -c "docker exec '$KDC' klist -c /work/client.ccache | grep -q 'krbtgt/$REALM@$REALM'"
  check "short-lived ccache is already expired" bash -c "! docker exec '$KDC' klist -s -c /work/expired.ccache"
  check "Db2 hostname equals the service principal host" bash -c "[ \"\$(docker exec '$DB2' hostname -f)\" = '$DB2_FQDN' ]"
  check "Db2 resolves the KDC hostname" docker exec "$DB2" getent hosts "$KDC_FQDN"
  check "Db2 can reach the KDC on TCP/88" docker exec "$DB2" bash -c "(exec 3<>/dev/tcp/$KDC_FQDN/88) 2>/dev/null"
  check "Db2 can read its keytab" docker exec -u "$DB2_INSTANCE" "$DB2" test -r /etc/db2krb/db2.keytab
  if db2_engine_env | grep -q "^KRB5_KTNAME=/etc/db2krb/db2.keytab"; then vlog "ok: Db2 engine sees KRB5_KTNAME"; else warn "self-test failed: Db2 engine process has no KRB5_KTNAME"; failures=$((failures + 1)); fi
  [ "$failures" -eq 0 ] || die "$failures infrastructure self-test(s) failed; this is an environment problem, not a driver result."
}

selftest_reference_client() {
  docker cp "$WORKDIR/client/client.ccache" "$DB2:/tmp/client.cc" || die "could not copy the client ccache into the Db2 container"
  docker exec "$DB2" chown "$DB2_INSTANCE" /tmp/client.cc
  db2_cli "db2 uncatalog db krbdb >/dev/null 2>&1 || true; db2 uncatalog node krbnode >/dev/null 2>&1 || true; db2 catalog tcpip node krbnode remote $DB2_FQDN server 50000 >/dev/null 2>&1; db2 catalog db $DB_NAME as krbdb at node krbnode authentication kerberos target principal '$DB2_SPN' >/dev/null 2>&1; db2 terminate >/dev/null 2>&1" || true
  local output
  output="$(db2_cli "export KRB5CCNAME=/tmp/client.cc; db2 connect to krbdb 2>&1; db2 terminate >/dev/null 2>&1")" || true
  case "$output" in *"SQL authorization ID   = ${CLIENT_USER^^}"*) vlog "IBM CLP authenticated as ${CLIENT_USER^^} with Kerberos" ;; *) die "reference IBM client could not authenticate with Kerberos: $(printf '%s' "$output" | redact | head -n 6)" ;; esac
}

STAGE="preparing test sources"
tar -C "$REPO_ROOT" --exclude='*/bin' --exclude='*/obj' --exclude='*/TestResults' -cf - src benchmarks/Db2ReplayServer LICENSE THIRD-PARTY-NOTICES.md | tar -C "$WORKDIR/src" -xf - || die "could not stage the test sources"
HOST_NUGET="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
mkdir -p "$HOST_NUGET"

run_tests() {
  local phase="$1" expected="$2"
  local runner="${SLUG}-runner-${phase}-${RUN_TAG}"
  local result_name="${RUN_TAG}-${phase}"
  local phase_log="$REPORT_DIR/${result_name}.log"
  cat > "$WORKDIR/runner.env" <<EOF
DB2_KRB_PHASE=$phase
DB2_KRB_HOST=$DB2_FQDN
DB2_KRB_PORT=50000
DB2_KRB_DATABASE=$DB_NAME
DB2_KRB_SPN=$DB2_SPN
DB2_KRB_SPN_UNKNOWN_TO_DB2=$SPN_UNKNOWN_TO_DB2
DB2_KRB_SPN_UNKNOWN_TO_KDC=$SPN_UNKNOWN_TO_KDC
DB2_KRB_PRINCIPAL=$CLIENT_PRINCIPAL
DB2_KRB_WRONG_REALM_PRINCIPAL=$WRONG_REALM_PRINCIPAL
DB2_KRB_KEYTAB=/krb/client.keytab
DB2_KRB_CCACHE=/krb/client.ccache
DB2_KRB_OTHER_KEYTAB=/krb/other.keytab
DB2_KRB_OTHER_PRINCIPAL=$OTHER_PRINCIPAL
DB2_KRB_OTHER_AUTHID=${OTHER_USER^^}
DB2_KRB_UID=$(id -u)
DB2_KRB_EXPIRED_CCACHE=/krb/expired.ccache
DB2_KRB_MISSING_KEYTAB=/krb/does-not-exist.keytab
DB2_KRB_EXPECTED_AUTHID=${CLIENT_USER^^}
DB2_KRB_USER=$CLIENT_USER
DB2_KRB_PASSWORD=$CLIENT_PASSWORD
KRB5_CONFIG=/krb/krb5.conf
HOME=/tmp
DOTNET_CLI_HOME=/tmp
NUGET_PACKAGES=/nuget
DOTNET_NOLOGO=1
DOTNET_CLI_TELEMETRY_OPTOUT=1
EOF
  if [ "$phase" = "default_ccache" ]; then echo "KRB5CCNAME=FILE:/krb/client.ccache" >> "$WORKDIR/runner.env"; fi
  log "Running Kerberos E2E tests (phase: $phase)..."
  set +e
  docker run --rm --name "$runner" --network "$NET" --label "$LABEL" --user "$(id -u):$(id -g)" --env-file "$WORKDIR/runner.env" -v "$WORKDIR/src:/work" -v "$WORKDIR/client:/krb" -v "$REPORT_DIR:/test-results" -v "$HOST_NUGET:/nuget" -w /work "$RUNNER_IMAGE" dotnet test src/Db2.Tests/Db2.Tests.csproj --nologo --filter "Category=KerberosIntegration" --logger "console;verbosity=normal" --logger "trx;LogFileName=${result_name}.trx" --results-directory /test-results 2>&1 | redact | tee -a "$WORKDIR/test-output.log" "$phase_log"
  local pipeline_status=("${PIPESTATUS[@]}")
  set -e
  redact_report "$REPORT_DIR/${result_name}.trx"
  [ "${pipeline_status[0]}" -eq 0 ] || return "${pipeline_status[0]}"
  if [ "${pipeline_status[1]:-0}" -ne 0 ] || [ "${pipeline_status[2]:-0}" -ne 0 ]; then
    warn "could not redact or save the test output for phase $phase"
    return 1
  fi
  local passed failed
  passed="$(tail -n 20 "$WORKDIR/test-output.log" | sed -n 's/^ *Passed: *\([0-9][0-9]*\).*/\1/p' | tail -n 1)"
  failed="$(tail -n 20 "$WORKDIR/test-output.log" | sed -n 's/^ *Failed: *\([0-9][0-9]*\).*/\1/p' | tail -n 1)"
  if [ "$passed" != "$expected" ] || [ "${failed:-0}" != "0" ]; then printf '[db2-krb] FAIL: phase %s expected %s passing and 0 failing tests, saw passed=%s failed=%s\n' "$phase" "$expected" "${passed:-?}" "${failed:-0}" >&2; return 1; fi
  log "Phase $phase: $passed test(s) passed"
}

STAGE="phase 1 setup (SRVCON_AUTH KERBEROS)"
configure_db2_auth KERBEROS
selftest_infra
selftest_reference_client
log "Db2 incoming connections require Kerberos and the IBM reference client authenticates"
STAGE="phase 1 tests"
log "Running keytab E2E and ccache E2E (and negative cases)..."
run_tests kerberos 17 || exit 1
STAGE="default credential cache tests"
log "Running the platform default credential cache E2E..."
run_tests default_ccache 1 || exit 1
STAGE="phase 2 setup (SRVCON_AUTH KRB_SERVER_ENCRYPT)"
log "Reconfiguring Db2 to also accept passwords (to prove there is no password fallback)..."
configure_db2_auth KRB_SERVER_ENCRYPT
selftest_reference_client
STAGE="phase 2 tests"
run_tests krb_server_encrypt 1 || exit 1
log "PASS"
