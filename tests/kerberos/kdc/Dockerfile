# Hermetic MIT Kerberos KDC for scripts/test-db2-kerberos.sh.
# The base image is pinned by digest (ubuntu:24.04). Packages come from the Ubuntu
# 24.04 archive; the realm database, principals and keytabs are created per run.
FROM ubuntu:24.04@sha256:534baea6a22c03a63003dbc8dbe78fe34bc0d7e595d9a9dc9834884ff530eb55
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
 && apt-get install -y --no-install-recommends krb5-kdc krb5-admin-server krb5-user \
 && rm -rf /var/lib/apt/lists/*
COPY bootstrap-kdc.sh /usr/local/bin/bootstrap-kdc
RUN chmod 0755 /usr/local/bin/bootstrap-kdc
ENTRYPOINT ["/usr/local/bin/bootstrap-kdc"]
