using System.Text;

namespace JustyBase.Db2;

/// <summary>
/// A one-shot, forward-only DB2 LOB locator exposed by row-streaming APIs. Its bytes remain
/// available only while the row is current; advancing the reader drains any unread data.
/// </summary>
public sealed class Db2LobValue
{
    private const int MaxMaterializedBytes = 16 * 1024 * 1024;
    private readonly Func<Stream> _openStream;
    private readonly Encoding? _textEncoding;
    private readonly object _sync = new();
    private Stream? _stream;
    private bool _opened;
    private object? _materialized;
    private byte[]? _materializedBytes;
    private bool _hasMaterialized;

    internal Db2LobValue(long? byteLength, Encoding? textEncoding, Func<Stream> openStream)
    {
        if (byteLength < 0) throw new ArgumentOutOfRangeException(nameof(byteLength));
        ByteLength = byteLength;
        _textEncoding = textEncoding;
        _openStream = openStream;
    }

    /// <summary>Encoded byte length reported by the DB2 locator.</summary>
    public long? ByteLength { get; }

    /// <summary>True for CLOB, DBCLOB, and XML locator values.</summary>
    public bool IsText => _textEncoding is not null;

    /// <summary>Opens the locator as a one-shot read-only byte stream.</summary>
    public Stream OpenReadStream()
    {
        lock (_sync)
        {
            if (_hasMaterialized) return new MemoryStream(_materializedBytes!, writable: false);
            if (_opened) throw new InvalidOperationException("This DB2 LOB locator has already been opened.");
            _opened = true;
            return _stream = _openStream();
        }
    }

    /// <summary>Opens CLOB, DBCLOB, or XML content using incremental decoding.</summary>
    public TextReader OpenTextReader()
    {
        if (_textEncoding is null) throw new InvalidOperationException("This DB2 LOB value is binary.");
        lock (_sync) if (_hasMaterialized) return new StringReader((string)_materialized!);
        return new StreamReader(OpenReadStream(), _textEncoding, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: false);
    }

    internal object Materialize()
    {
        lock (_sync)
        {
            if (_hasMaterialized) return _materialized!;
            if (_opened) throw new InvalidOperationException("A DB2 LOB locator already has an active stream.");
            _opened = true;
            _stream = _openStream();
        }
        // Read in bounded chunks. Unknown or inaccurate locator lengths must not
        // let GetValue grow a result buffer beyond the materialization budget.
        using var value = new MemoryStream(capacity: (int)Math.Min(ByteLength ?? 0, 65536));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var remaining = MaxMaterializedBytes + 1 - (int)value.Length;
            var count = _stream!.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (count == 0) break;
            if (count > MaxMaterializedBytes - value.Length)
                throw new Db2ResourceLimitException($"DB2 LOB exceeds the {MaxMaterializedBytes}-byte materialization limit.");
            value.Write(buffer, 0, count);
        }
        _stream!.Dispose();
        var bytes = value.TryGetBuffer(out var written) && written.Offset == 0 && written.Count == written.Array!.Length
            ? written.Array! : value.ToArray();
        object result = _textEncoding is null ? bytes : _textEncoding.GetString(bytes);
        lock (_sync) { _materializedBytes = bytes; _materialized = result; _hasMaterialized = true; }
        return result;
    }

    internal async ValueTask DrainAsync(CancellationToken cancellationToken)
    {
        Stream stream;
        lock (_sync)
        {
            if (_stream is { CanRead: false } || _hasMaterialized && _stream is null) return;
            if (_stream is null)
            {
                _opened = true;
                _stream = _openStream();
            }
            stream = _stream;
        }
        if (stream is Db2ProgressiveLobStream progressive)
            await progressive.DrainAsync(cancellationToken).ConfigureAwait(false);
        else
            await stream.DisposeAsync().ConfigureAwait(false);
    }
}
