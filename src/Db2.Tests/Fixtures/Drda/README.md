# Independent synthetic DRDA golden vectors (P-00b)

These files are hand-constructed protocol vectors, **not captures from a Db2
server**. No production DSS/DDM encoder produced their bytes. They characterize
the formats already accepted by the driver and ProtocolTests; they do not prove
additional server capabilities or z/OS/IBM i interoperability.

All DSS/DDM lengths, correlation IDs and QRYINSID values are big-endian.
QRYDTA INTEGER values and row SQLCODE/SQLERRD values are little-endian.
SQLCA diagnostic text is ASCII/UTF-8. No authentication material is included.

| File | Content / expectation |
| --- | --- |
| open-query.bin | QRYDSC INTEGER, OPNQRYRM QRYINSID=1, QRYDTA=42, ENDQRYRM; correlation ID 1 |
| split-row-first.bin | QRYDTA containing the first five bytes of the six-byte INTEGER row 42 |
| split-row-last.bin | Remaining byte of row 42, complete row 43, ENDQRYRM |
| continued-dss.bin | QRYDTA rows 42/43 across two DSS segments, correlation ID 7; continuation occurs inside row 42 |
| null-warning.bin | Nullable INTEGER rows 40/NULL/41; only row 40 carries full SQLCA with SQLSTATE 01003 and SQLWARN `W W        ` |
| extended-extdta.bin | Four-byte extended DDM content length, EXTDTA payload `00 DE AD BE EF` |

Open/query chaining and short transport reads are tested independently from row
boundaries. Existing ProtocolTests retain maximum-segment and large external
LOB boundary coverage. Add separately labelled, sanitized LUW captures once
capture access is available; do not relabel these synthetic vectors as captures.
