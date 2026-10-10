using System.Buffers.Binary;
using System.Text;
namespace JustyBase.Db2.Protocol;

internal static partial class DdmCodec
{
    private static byte[] CreateObjectBuffer(CodePoint codePoint, int bodyLength, out int bodyOffset)
    {
        var shortLength = checked(bodyLength + 4);
        bodyOffset = shortLength <= 0x7fff ? 4 : 8;
        var output = new byte[checked(bodyOffset + bodyLength)];
        BinaryPrimitives.WriteUInt16BigEndian(output, bodyOffset == 4 ? (ushort)shortLength : (ushort)0x8008);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(2), (ushort)codePoint);
        if (bodyOffset == 8) BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(4), (uint)bodyLength);
        return output;
    }

    private static void WritePackageNameCsn(Span<byte> output, string database, string package, string token, ushort section)
    {
        var dbLength = Math.Max(18, Encoding.UTF8.GetByteCount(database));
        var packageLength = Math.Max(18, Encoding.UTF8.GetByteCount(package));
        var tokenLength = Math.Max(8, Encoding.UTF8.GetByteCount(token));
        BinaryPrimitives.WriteUInt16BigEndian(output, (ushort)output.Length);
        BinaryPrimitives.WriteUInt16BigEndian(output[2..], (ushort)CodePoint.PKGNAMCSN);
        var offset = 4;
        output.Slice(offset, dbLength).Fill((byte)' ');
        Encoding.UTF8.GetBytes(database.AsSpan(), output.Slice(offset, dbLength)); offset += dbLength;
        "NULLID            "u8.CopyTo(output[offset..]); offset += 18;
        output.Slice(offset, packageLength).Fill((byte)' ');
        Encoding.UTF8.GetBytes(package.AsSpan(), output.Slice(offset, packageLength)); offset += packageLength;
        var tokenBytes = Encoding.UTF8.GetByteCount(token);
        if (tokenBytes == 0) DefaultConsistencyToken.CopyTo(output[offset..]);
        else
        {
            output.Slice(offset, tokenLength).Fill((byte)' ');
            Encoding.UTF8.GetBytes(token.AsSpan(), output.Slice(offset + tokenLength - tokenBytes, tokenBytes));
        }
        offset += tokenLength;
        BinaryPrimitives.WriteUInt16BigEndian(output[offset..], section);
    }

    private static void WritePackageNameCsn(Span<byte> output, ResultSetPackageSection section)
    {
        BinaryPrimitives.WriteUInt16BigEndian(output, 68);
        BinaryPrimitives.WriteUInt16BigEndian(output[2..], (ushort)CodePoint.PKGNAMCSN);
        CopyFixedName(section.Database, output.Slice(4, 18));
        CopyFixedName(section.Schema, output.Slice(22, 18));
        CopyFixedName(section.PackageName, output.Slice(40, 18));
        if (section.ConsistencyToken.Length != 8) throw new InvalidDataException("RSLSETRM returned a package consistency token that is not eight bytes long.");
        section.ConsistencyToken.CopyTo(output[58..]);
        BinaryPrimitives.WriteUInt16BigEndian(output[66..], section.SectionNumber);
    }

    private static void WriteUInt16Parameter(Span<byte> destination, CodePoint codePoint, ushort value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(destination, 6);
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], (ushort)codePoint);
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..], value);
    }
    private static void WriteUInt32Parameter(Span<byte> destination, CodePoint codePoint, uint value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(destination, 8);
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], (ushort)codePoint);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], value);
    }
    private static void WriteUInt64Parameter(Span<byte> destination, CodePoint codePoint, ulong value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(destination, 12);
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], (ushort)codePoint);
        BinaryPrimitives.WriteUInt64BigEndian(destination[4..], value);
    }
    private static void WriteByteParameter(Span<byte> destination, CodePoint codePoint, byte value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(destination, 5);
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], (ushort)codePoint);
        destination[4] = value;
    }

    private static byte[] PackCommandDirect(CodePoint command, string database, string package, string token, ushort section, byte[]? suffix)
    {
        var packageLength = PackageLength(database, package, token);
        var output = CreateObjectBuffer(command, checked(packageLength + (suffix?.Length ?? 0)), out var offset);
        WritePackageNameCsn(output.AsSpan(offset, packageLength), database, package, token, section);
        suffix.AsSpan().CopyTo(output.AsSpan(offset + packageLength));
        return output;
    }

    private static byte[] PackQueryDirect(CodePoint command, string package, string token, ushort section, string database, uint blockSize, ulong? queryId, ResultSetPackageSection? resultSetSection)
    {
        var packageLength = resultSetSection is null ? PackageLength(database, package, token) : 68;
        var suffixLength = queryId.HasValue ? 25 : 19 + (command == CodePoint.OPNQRY ? 5 : 0);
        var output = CreateObjectBuffer(command, checked(packageLength + suffixLength), out var offset);
        var body = output.AsSpan(offset);
        if (resultSetSection is null) WritePackageNameCsn(body[..packageLength], database, package, token, section);
        else WritePackageNameCsn(body[..packageLength], resultSetSection);
        offset = packageLength;
        WriteUInt32Parameter(body[offset..], CodePoint.QRYBLKSZ, blockSize); offset += 8;
        if (queryId.HasValue)
        {
            WriteUInt64Parameter(body[offset..], CodePoint.QRYINSID, queryId.Value); offset += 12;
            WriteByteParameter(body[offset..], CodePoint.RTNEXTDTA, 2);
        }
        else
        {
            // Preserve the existing MAXBLKEXT value exactly; protocol tuning is separate.
            WriteUInt16Parameter(body[offset..], CodePoint.MAXBLKEXT, (ushort)blockSize); offset += 6;
            ParamCloseImplicit.CopyTo(body[offset..]); offset += 5;
            if (command == CodePoint.OPNQRY) WriteByteParameter(body[offset..], CodePoint.DYNDTAFMT, 0xf1);
        }
        return output;
    }
    private static byte[] PackRecordCommand(CodePoint command, ResultSetPackageSection section, ReadOnlySpan<byte> suffix)
    {
        var output = CreateObjectBuffer(command, checked(68 + suffix.Length), out var offset);
        WritePackageNameCsn(output.AsSpan(offset, 68), section);
        suffix.CopyTo(output.AsSpan(offset + 68)); return output;
    }

    private static byte[] PackLocatorChunkRequest(string package, string token, ushort section, string database, ulong queryId, ulong locator, ulong requestedLength)
    {
        var packageLength = PackageLength(database, package, token);
        var output = CreateObjectBuffer(CodePoint.GETNXTCHK, checked(packageLength + 36), out var offset);
        WritePackageNameCsn(output.AsSpan(offset, packageLength), database, package, token, section); offset += packageLength;
        WriteUInt64Parameter(output.AsSpan(offset), CodePoint.QRYINSID, queryId); offset += 12;
        WriteUInt64Parameter(output.AsSpan(offset), CodePoint.GETNXTREF, locator); offset += 12;
        WriteUInt64Parameter(output.AsSpan(offset), CodePoint.GETNXTLEN, requestedLength); return output;
    }

    private static byte[] PackRecordContinue(ResultSetPackageSection section, uint blockSize, ulong id, uint? rowsetSize, bool returnAllData, bool procedure)
    {
        var length = 68 + 8 + 12 + (rowsetSize.HasValue ? 14 : 0) + (returnAllData ? 5 : 0) + (procedure ? 5 : 0);
        var output = CreateObjectBuffer(CodePoint.CNTQRY, length, out var offset);
        var body = output.AsSpan(offset); WritePackageNameCsn(body[..68], section); offset = 68;
        WriteUInt32Parameter(body[offset..], CodePoint.QRYBLKSZ, blockSize); offset += 8;
        if (rowsetSize.HasValue) { WriteUInt16Parameter(body[offset..], CodePoint.MAXBLKEXT, ushort.MaxValue); offset += 6; }
        WriteUInt64Parameter(body[offset..], CodePoint.QRYINSID, id); offset += 12;
        if (rowsetSize.HasValue) { WriteUInt32Parameter(body[offset..], CodePoint.QRYROWSET, rowsetSize.Value); offset += 8; }
        if (returnAllData) { WriteByteParameter(body[offset..], CodePoint.RTNEXTDTA, 2); offset += 5; }
        if (procedure) WriteByteParameter(body[offset..], CodePoint.FREPRVREF, 0xf0);
        return output;
    }

    private static byte[] PackExecuteWithResults(string package, string token, ushort section, string database, ushort maximumResultSets, uint queryBlockSize, uint? queryRowsetSize, bool outputExpected)
    {
        var packageLength = PackageLength(database, package, token);
        var output = CreateObjectBuffer(CodePoint.EXCSQLSTT, checked(packageLength + 35 + (queryRowsetSize.HasValue ? 8 : 0)), out var offset);
        var body = output.AsSpan(offset); WritePackageNameCsn(body[..packageLength], database, package, token, section); offset = packageLength;
        ParamCommitToken.CopyTo(body[offset..]); offset += 5;
        WriteByteParameter(body[offset..], CodePoint.OUTEXP, outputExpected ? (byte)0xf1 : (byte)0xf0); offset += 5;
        WriteUInt32Parameter(body[offset..], CodePoint.QRYBLKSZ, queryBlockSize); offset += 8;
        WriteUInt16Parameter(body[offset..], CodePoint.MAXBLKEXT, ushort.MaxValue); offset += 6;
        WriteUInt16Parameter(body[offset..], CodePoint.MAXRSLCNT, maximumResultSets); offset += 6;
        WriteByteParameter(body[offset..], CodePoint.RSLSETFLG, 4); offset += 5;
        if (queryRowsetSize.HasValue) WriteUInt32Parameter(body[offset..], CodePoint.QRYROWSET, queryRowsetSize.Value);
        return output;
    }
}
