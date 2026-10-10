using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Db2.Tests")]
// First-party companion package, versioned and released together with this assembly. It needs the
// importer's IBM-shaped progress and cancellation knobs, which stay internal to the native API.
[assembly: InternalsVisibleTo("JustyBase.Db2.IBMCompat")]
