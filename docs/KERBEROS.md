# Kerberos (GSS-API) authentication

JustyBase.Db2 authenticates to Db2 LUW with Kerberos through DRDA security mechanism 11 (`SECMEC 11`). The driver
asks the system MIT Kerberos library (`libgssapi_krb5`) for a service ticket and a GSS-API security context,
sends the resulting token in the DRDA handshake, and verifies the server's reply token (mutual authentication).
**No password is sent**, and a failed Kerberos attempt never turns into a password login.

> Status: highly experimental alpha. Explicit `krb5_keytab` / `krb5_ccache` support is implemented and verified
> **on Linux x86_64 only**, against an MIT Kerberos KDC and IBM Db2 Community 12.1.5 (`IBMkrb5` plug-in). Other
> operating systems support only the `gssapi_token_file` mode below. See [Limitations](#limitations).

## Connection properties

Keywords are case-insensitive. Use `Authentication=KERBEROS` (or `security_mechanism=11`).

| Property (aliases) | Meaning |
| --- | --- |
| `Authentication=KERBEROS` / `security_mechanism=11` | Select SECMEC 11. `User` is optional; the identity comes from the ticket. |
| `spn` (`service_principal`, `server_principal`) | The Db2 service principal, e.g. `db2inst1/db2.example.com@EXAMPLE.COM`. Required except with `gssapi_token_file`. |
| `krb5_ccache` (`ccache`) | An existing credential cache to take the TGT from. |
| `krb5_keytab` (`keytab`) | A client keytab; needs `krb5_principal`. The driver obtains its own ticket from it. |
| `krb5_principal` (`kerberos_principal`, `client_principal`) | Client principal (`user@REALM`). Required with `krb5_keytab`, optional with `krb5_ccache`. |
| `realm` (`krb5_realm`) | Appended to `spn` when it has no realm; a different realm in `spn` is a configuration error. |
| `krb5_config` (`krb5_conf`) | Must equal the process's `KRB5_CONFIG` (see [krb5.conf](#krb5conf-is-process-wide)). |
| `gssapi_token_file` (`gssapi_token`) | Advanced/compatibility: a ready-made GSS-API token read from a file. |

Unrecognized options that start with `krb5`, `kerberos` or `gssapi` are rejected so that a typo can never silently
fall back to different credentials.

### Credential sources and precedence

Exactly one explicit source may be configured. Setting more than one of `gssapi_token_file`, `krb5_ccache`,
`krb5_keytab` is a configuration error (`Db2ConfigurationException`, raised before any socket is opened); the
driver never guesses which one should win.

| Source | When | Behaviour |
| --- | --- | --- |
| `krb5_keytab` | preferred for services | A fresh ticket is obtained from the keytab for every physical connection and kept in a private in-memory cache that is destroyed afterwards. The default credential cache is neither read nor written. |
| `krb5_ccache` | preferred for users | Uses the TGT in that cache. The caller renews it (`kinit`/`k5start`); libkrb5 may add service tickets to it. |
| default | none of the above set | Uses the platform default credential cache (`KRB5CCNAME`/`/tmp/krb5cc_<uid>`), but `spn` must be set. |
| `gssapi_token_file` | advanced | The file content is sent as the GSS token. No mutual authentication; the token is single-use. |

`krb5_keytab` and `krb5_ccache` are the normal ways to use Kerberos. `gssapi_token_file` remains for
integration with external token providers, protocol testing and backward compatibility.

### Examples

```text
# ccache (a user who already ran kinit)
Authentication=KERBEROS;Server=db2.example.com:50000;Database=TESTDB;
  spn=db2inst1/db2.example.com@EXAMPLE.COM;krb5_ccache=/run/user/1000/krb5cc

# keytab (a service)
Authentication=KERBEROS;Server=db2.example.com:50000;Database=TESTDB;
  spn=db2inst1/db2.example.com@EXAMPLE.COM;krb5_keytab=/etc/myapp/app.keytab;krb5_principal=myapp@EXAMPLE.COM
```

```csharp
await using var session = await Db2Session.ConnectAsync(new Db2Config
{
    Host = "db2.example.com", Port = 50000, Database = "TESTDB",
    SecurityMechanism = 11,
    KerberosSpn = "db2inst1/db2.example.com@EXAMPLE.COM",
    Krb5KeytabFile = "/etc/myapp/app.keytab",
    KerberosPrincipal = "myapp@EXAMPLE.COM",
});
```

## Service principal and identity

* Db2 LUW's service principal is `<instance>/<fully-qualified-host>@<REALM>` (for example
  `db2inst1/db2.example.com@EXAMPLE.COM`). Db2 reads it from its keytab, so the host part must equal the server's
  fully qualified host name (`hostname -f`). The driver does **not** guess it: configure `spn`. It also does not
  trust a principal name supplied by the server for ticket requests.
* Connect to the same host name the principal contains where practical; the ticket is requested for the configured
  `spn` regardless of the address in `Server=`.
* Db2 (`IBMkrb5`) maps the client principal's short name to an operating-system user on the server; the Db2
  authorization ID is that name in upper case. Verified: `db2test@DB2TEST.LOCAL` → OS user `db2test` →
  `CURRENT USER = DB2TEST`.

## Db2 server side (verified recipe)

1. The server needs the MIT Kerberos runtime libraries and an `IBMkrb5` plug-in (shipped with Db2).
2. Create the service principal (`addprinc -randkey db2inst1/<fqdn>`) and export it to a keytab readable only by the
   instance owner; install a matching `/etc/krb5.conf`.
3. As the instance owner: `db2set DB2ENVLIST=KRB5_KTNAME`, export `KRB5_KTNAME=<keytab>` for the instance
   (for example in `sqllib/userprofile`), and ensure `/etc/krb5.conf` plus the service keytab are readable by the Db2
   instance/security processes.
4. Keep local instance authentication on `SERVER` and require Kerberos for incoming DRDA connections with
   `db2 update dbm cfg using AUTHENTICATION SERVER SRVCON_GSSPLUGIN_LIST IBMkrb5 SRVCON_AUTH KERBEROS CLNT_KRB_PLUGIN IBMkrb5`.
   Use `SRVCON_AUTH KRB_SERVER_ENCRYPT` for the explicit no-password-fallback test, then `db2stop force; db2start`.
5. Create the OS user that the client principal maps to.

`scripts/test-db2-kerberos.sh` performs exactly these steps in a throw-away container.

## krb5.conf is process-wide

MIT libkrb5 reads `KRB5_CONFIG` once per process, so one connection cannot select its own `krb5.conf` without changing
process-global state, which the driver never does. Start the process with `KRB5_CONFIG` (or use `/etc/krb5.conf`).
If `krb5_config` is set it must equal the process's `KRB5_CONFIG`, otherwise connecting fails fast with a clear message.
The driver never calls `setenv`, never changes `KRB5CCNAME`, `KRB5_KTNAME`, `KRB5_CONFIG` or `KRB5_CLIENT_KTNAME`, and
never starts `kinit`; per-connection ccache/keytab selection uses the credential-store API
(`gss_acquire_cred_from`, MIT krb5 1.11+).

## Security properties

| Property | Guarantee |
| --- | --- |
| Password sent during Kerberos authentication | never (`SECCHK` carries only the mechanism, the database name and the token) |
| Fallback to SECMEC 3/9 after a Kerberos failure | not possible; a server answering with another mechanism is refused and no credentials are sent |
| Process-global Kerberos state | not modified |
| Tokens, tickets, keys in messages or logs | never; errors carry GSS-API status text and codes only |
| Server authentication | mutual authentication is requested and the server's reply token must complete the context |
| Resources | GSS context, credential and name handles are `SafeHandle`s released on every path (also on failure, timeout and cancellation); keytab connections destroy their private memory cache |

The connect timeout and the cancellation token also bound Kerberos work: a blocked KDC request stops the wait; the
abandoned native call finishes on its own and then releases its handles.

## Errors

* `Db2ConfigurationException` – bad or ambiguous options, missing keytab/ccache/token file (before any socket).
* `Db2UnsupportedFeatureException` – a Kerberos source other than `gssapi_token_file` on a non-Linux platform, or the
  MIT libraries are missing.
* `Db2KerberosException` – no/expired credentials, unknown realm or principal, unreachable KDC, unknown service
  principal, or a server token the context rejects (`ErrorKind = Authentication`).
* `Db2AuthenticationException` – Db2 itself rejected the token (`SECCHKCD`), for example a service principal whose key
  the server does not hold.

## Pooling

The ADO.NET pool key is a hash of the whole connection string, so different `krb5_principal`, `krb5_ccache`,
`krb5_keytab` or `spn` values never share a pool. Credentials belong to the physical connection; a leased session is not
re-authenticated on checkout. Ticket renewal is the caller's job (renew the ccache; keytab connections get a new ticket
per physical connection). An expired ccache fails deterministically with `Db2KerberosException`.

## Limitations

* Explicit keytab/ccache support: **Linux only** (tested on Linux x86_64). Windows and macOS are not supported for these
  sources; use `gssapi_token_file` there.
* Tested only with an MIT KDC and Db2 LUW 12.1.5 (`IBMkrb5`); Active Directory KDCs and Db2 for z/OS are untested.
* No automatic ticket renewal; one GSS exchange round (AP-REQ/AP-REP), as Db2 uses.
* The `krb5.conf` is process-wide, as described above.

### Managed provider feasibility

**Kerberos.NET is not integrated.** The NativeAOT feasibility spike against version `4.6.168` was a no-go: its publish
reported 10 detailed trim/AOT diagnostics, including four `IL3050` warnings for `Type.MakeGenericType` and trim warnings
`IL2067`, `IL2070`, and `IL2075` in `ConfigurationSectionList`. Running the produced `linux-x64` NativeAOT binary with
a syntactically valid test keytab showed `Krb5Config.CurrentUser(...)` leaving `Krb5Config.Defaults` null; client
authentication failed in `KerberosClient.AuthenticateCore()` with `NullReferenceException` before KDC contact, and
cleanup also failed in `KerberosClient.SetupCache()`. The spike therefore did not reach credential acquisition,
AP-REQ creation, or AP-REP verification. No managed provider or Kerberos.NET dependency was added, and Windows/macOS
credential acquisition remains unsupported.

## Verifying it yourself

`scripts/test-db2-kerberos.sh` builds a fresh MIT KDC, a fresh realm with generated principals/keytabs/ccaches, and a
fresh Db2 Community 12.1.5 container on a private Docker network, configures `IBMkrb5`, proves the setup with IBM's own
client, then runs `dotnet test --filter Category=KerberosIntegration` in a pinned SDK container (keytab E2E, ccache E2E,
second/parallel connections, wrong SPN/realm/principal, missing keytab, expired ccache, no password fallback). It needs
Docker and takes several minutes; it is a local/manual test and is not part of the push CI.
