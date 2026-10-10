#!/usr/bin/env bash
# Creates a fresh MIT KDC realm with one client principal and one Db2 service
# principal, writes their keytabs to /work, and then runs krb5kdc in the foreground.
set -Eeuo pipefail

: "${KRB_REALM:?}" "${KRB_CLIENT_PRINCIPAL:?}" "${KRB_DB2_PRINCIPAL:?}" "${KRB_MASTER_PASSWORD:?}"
: "${KRB_OWNER_UID:?}" "${KRB_OWNER_GID:?}"
: "${KRB_OTHER_PRINCIPAL:?}" "${KRB_SPN_UNKNOWN_TO_DB2:?}"
OUT=/work

log() { printf '[db2-krb] %s\n' "$*"; }

log "Writing krb5.conf for realm $KRB_REALM"
cat > /etc/krb5.conf <<CONF
[libdefaults]
  default_realm = $KRB_REALM
  dns_lookup_kdc = false
  dns_lookup_realm = false
  rdns = false
  udp_preference_limit = 1

[realms]
  $KRB_REALM = {
    kdc = localhost
    admin_server = localhost
  }
CONF

cat > /etc/krb5kdc/kdc.conf <<CONF
[kdcdefaults]
  kdc_ports = 88
  kdc_tcp_ports = 88

[realms]
  $KRB_REALM = {
    database_name = /var/lib/krb5kdc/principal
    admin_keytab = FILE:/etc/krb5kdc/kadm5.keytab
    acl_file = /etc/krb5kdc/kadm5.acl
    key_stash_file = /etc/krb5kdc/.k5.$KRB_REALM
    max_life = 10h 0m 0s
    max_renewable_life = 7d 0h 0m 0s
    supported_enctypes = aes256-cts-hmac-sha1-96:normal aes128-cts-hmac-sha1-96:normal
  }
CONF

log "Creating realm database"
rm -f /var/lib/krb5kdc/principal*
kdb5_util create -s -r "$KRB_REALM" -P "$KRB_MASTER_PASSWORD" >/dev/null

log "Creating principals"
kadmin.local -q "addprinc -randkey $KRB_CLIENT_PRINCIPAL" >/dev/null
kadmin.local -q "addprinc -randkey $KRB_DB2_PRINCIPAL" >/dev/null
# Negative-test fixtures: a second client principal, and a service principal Db2 holds no key for.
kadmin.local -q "addprinc -randkey $KRB_OTHER_PRINCIPAL" >/dev/null
kadmin.local -q "addprinc -randkey $KRB_SPN_UNKNOWN_TO_DB2" >/dev/null

log "Exporting keytabs"
kadmin.local -q "ktadd -k $OUT/client.keytab $KRB_CLIENT_PRINCIPAL" >/dev/null
kadmin.local -q "ktadd -k $OUT/db2.keytab $KRB_DB2_PRINCIPAL" >/dev/null
kadmin.local -q "ktadd -k $OUT/other.keytab $KRB_OTHER_PRINCIPAL" >/dev/null
chown "$KRB_OWNER_UID:$KRB_OWNER_GID" "$OUT/client.keytab" "$OUT/db2.keytab" "$OUT/other.keytab"
chmod 600 "$OUT/client.keytab" "$OUT/db2.keytab" "$OUT/other.keytab"

log "Starting krb5kdc"
exec krb5kdc -n
