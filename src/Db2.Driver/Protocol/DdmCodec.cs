using System.Buffers.Binary;
using System.Text;
using JustyBase.Db2;
using JustyBase.Db2.Converters;
using JustyBase.Db2.Types;

namespace JustyBase.Db2.Protocol;

internal enum DdmStringEncoding { Cp500, Utf8 }
internal sealed record ManagerLevel(CodePoint Manager, ushort Level);
internal sealed record FieldDescriptor(byte Type, byte Precision, byte Scale);
internal sealed record ResultSetPackageSection(byte[] Database, byte[] Schema, byte[] PackageName, byte[] ConsistencyToken, ushort SectionNumber);
internal sealed record SqlCard(int SqlCode, string SqlState, string Message, long RowsAffected)
{
    public string WarningFlags { get; init; } = "";

    public Db2Warning? Warning
    {
        get
        {
            var warning = (SqlCode > 0 && SqlCode != 100)
                || SqlState.StartsWith("01", StringComparison.Ordinal)
                || WarningFlags.Length > 0 && WarningFlags[0] == 'W';
            if (!warning) return null;
            var message = string.IsNullOrWhiteSpace(Message)
                ? $"DB2 returned a warning (SQLCODE={SqlCode}, SQLSTATE={SqlState})."
                : Message;
            return new Db2Warning(SqlCode, SqlState, message, WarningFlags);
        }
    }
}
internal sealed record ProgressiveChunkReply(byte[] Data, bool EndOfReference, bool IsLastChunk);

internal static partial class DdmCodec
{
    private static readonly byte[] DefaultCorrelationToken = [0xd5,0xc6,0xf0,0xf0,0xf0,0xf0,0xf0,0xf1,0x2e,0xc3,0xf0,0xc1,0xf5,0x01,0x55,0x63,0x0d,0x5a,0x11];
    private static readonly byte[] DefaultTypeOverrides = [0x00,0x06,0x11,0x9c,0x04,0xb8,0x00,0x06,0x11,0x9d,0x04,0xb0,0x00,0x06,0x11,0x9e,0x04,0xb8];
    private static readonly byte[] DefaultConsistencyToken = [1,1,1,1,1,1,1,1];
    private static readonly byte[] ParamCommitToken = [0x00,0x05,0x21,0x05,0xf1];
    private static readonly byte[] ParamReturnSqlda = [0x00,0x05,0x21,0x16,0xf1];
    private static readonly byte[] ParamTypeSqlda = [0x00,0x05,0x21,0x46,0x01];
    private static readonly byte[] ParamCloseImplicit = [0x00,0x05,0x21,0x5d,0x01];

    public static byte[] PackObject(CodePoint codePoint, ReadOnlySpan<byte> body)
    {
        var shortLength = checked(body.Length + 4);
        var extensionLength = shortLength <= 0x7fff ? 0 : body.Length <= int.MaxValue ? 4 : 8;
        var headerLength = 4 + extensionLength;
        var result = new byte[checked(headerLength + body.Length)];
        BinaryPrimitives.WriteUInt16BigEndian(result, extensionLength == 0 ? (ushort)shortLength : (ushort)(0x8000 | headerLength));
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2), (ushort)codePoint);
        if (extensionLength == 4) BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), (uint)body.Length);
        else if (extensionLength == 8) BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(4), (ulong)body.Length);
        body.CopyTo(result.AsSpan(headerLength));
        return result;
    }

    public static byte[] PackBytes(CodePoint codePoint, ReadOnlySpan<byte> value) => PackObject(codePoint, value);

    public static byte[] PackUInt16(CodePoint codePoint, ushort value)
    {
        var result = new byte[6]; BinaryPrimitives.WriteUInt16BigEndian(result, 6); BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2), (ushort)codePoint); BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4), value); return result;
    }

    public static byte[] PackUInt32(CodePoint codePoint, uint value)
    {
        var result = new byte[8]; BinaryPrimitives.WriteUInt16BigEndian(result, 8); BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2), (ushort)codePoint); BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), value); return result;
    }

    public static byte[] PackUInt64(CodePoint codePoint, ulong value)
    {
        var result = new byte[12]; BinaryPrimitives.WriteUInt16BigEndian(result, 12); BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2), (ushort)codePoint); BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(4), value); return result;
    }

    public static byte[] PackString(CodePoint codePoint, string value, DdmStringEncoding encoding = DdmStringEncoding.Utf8)
    {
        var bytes = encoding == DdmStringEncoding.Cp500 ? Ebcdic.EncodeCp500(value) : Encoding.UTF8.GetBytes(value);
        return PackObject(codePoint, bytes);
    }

    public static byte[] PackNullString(string? value, DdmStringEncoding encoding)
    {
        if (value is null) return [0xff];
        byte[] bytes;
        try { bytes = encoding == DdmStringEncoding.Cp500 ? Ebcdic.EncodeCp500(value) : Encoding.UTF8.GetBytes(value); }
        catch (EncoderFallbackException) { bytes = Encoding.UTF8.GetBytes(value); }
        var result = new byte[5 + bytes.Length]; result[0] = 0; BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(1), (uint)bytes.Length); bytes.CopyTo(result, 5); return result;
    }

    public static ManagerLevel[] DefaultManagerLevels() =>
    [
        new(CodePoint.AGENT,10), new(CodePoint.SQLAM,11), new(CodePoint.CMNTCPIP,5),
        new(CodePoint.RDB,12), new(CodePoint.SECMGR,9), new(CodePoint.UNICODEMGR,1208),
    ];

    private static byte[] PackManagerLevels(IReadOnlyList<ManagerLevel> levels)
    {
        var result = new byte[levels.Count * 4];
        for (var i = 0; i < levels.Count; i++) { BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(i * 4), (ushort)levels[i].Manager); BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(i * 4 + 2), levels[i].Level); }
        return result;
    }

    public static byte[] PackExcsat(string externalName, string serverName, string release, string serverClass, IReadOnlyList<ManagerLevel>? levels = null)
    {
        levels ??= DefaultManagerLevels();
        return PackObject(CodePoint.EXCSAT, Concat(
            PackString(CodePoint.EXTNAM, externalName, DdmStringEncoding.Cp500),
            PackString(CodePoint.SRVNAM, serverName, DdmStringEncoding.Cp500),
            PackString(CodePoint.SRVRLSLV, release, DdmStringEncoding.Cp500),
            PackBytes(CodePoint.MGRLVLLS, PackManagerLevels(levels)),
            PackString(CodePoint.SRVCLSNM, serverClass, DdmStringEncoding.Cp500)));
    }

    public static byte[] PackExcsatManagerLevels(IReadOnlyList<ManagerLevel> levels) => PackObject(CodePoint.EXCSAT, PackBytes(CodePoint.MGRLVLLS, PackManagerLevels(levels)));

    public static byte[] PackAccsec(string database, ushort securityMechanism, ReadOnlySpan<byte> token = default, ushort? encryptionAlgorithm = null)
    {
        var pieces = new List<byte[]> { PackUInt16(CodePoint.SECMEC, securityMechanism), PackString(CodePoint.RDBNAM, database, DdmStringEncoding.Cp500) };
        if (encryptionAlgorithm is { } algorithm) pieces.Add(PackUInt16(CodePoint.ENCALG, algorithm));
        if (!token.IsEmpty) pieces.Add(PackBytes(CodePoint.SECTKN, token));
        return PackObject(CodePoint.ACCSEC, Concat(pieces.ToArray()));
    }

    public static byte[] PackSecchk(ushort securityMechanism, ReadOnlySpan<byte> token, string database, string user, ReadOnlySpan<byte> password, bool encrypted, DdmStringEncoding encoding)
    {
        var pieces = new List<byte[]> { PackUInt16(CodePoint.SECMEC, securityMechanism) };
        if (!token.IsEmpty) pieces.Add(PackBytes(CodePoint.SECTKN, token));
        pieces.Add(PackString(CodePoint.RDBNAM, database, encoding)); pieces.Add(PackString(CodePoint.USRID, user, encoding));
        if (securityMechanism == 3 && !password.IsEmpty)
        {
            if (encrypted) pieces.Add(PackBytes(CodePoint.PASSWORD, password));
            else pieces.Add(PackString(CodePoint.PASSWORD, Encoding.UTF8.GetString(password), encoding));
        }
        else if (securityMechanism is not (3 or 4))
        {
            throw new ArgumentOutOfRangeException(nameof(securityMechanism), securityMechanism, "Only USRIDPWD (3) and USRIDONL (4) SECCHK requests are supported.");
        }
        return PackObject(CodePoint.SECCHK, Concat(pieces.ToArray()));
    }

    /// <summary>
    /// Builds the EUSRIDPWD (SECMEC 9) SECCHK as Db2 LUW expects it: two SECTKN
    /// fields — the DES-CBC-encrypted userid followed by the DES-CBC-encrypted
    /// password — with no cleartext USRID/PASSWORD. Verified against IBM CLI wire.
    /// </summary>
    public static byte[] PackSecchkEusridpwd(string database, ReadOnlySpan<byte> encryptedUserid, ReadOnlySpan<byte> encryptedPassword) =>
        PackObject(CodePoint.SECCHK, Concat(
            PackUInt16(CodePoint.SECMEC, 9),
            PackString(CodePoint.RDBNAM, database, DdmStringEncoding.Cp500),
            PackBytes(CodePoint.SECTKN, encryptedUserid),
            PackBytes(CodePoint.SECTKN, encryptedPassword)));

    /// <summary>
    /// Builds the USRENCPWD (SECMEC 7) SECCHK: cleartext USRID plus one SECTKN carrying the DES-CBC-encrypted
    /// password (Derby's layout). The client Diffie-Hellman public key already travelled in ACCSEC. Verified
    /// against Db2 LUW SERVER_ENCRYPT: an encrypted PASSWORD parameter instead of SECTKN is rejected with
    /// SECCHKCD=16, and an extra SECTKN with AGNPRMRM.
    /// </summary>
    public static byte[] PackSecchkUsrencpwd(string database, string user, ReadOnlySpan<byte> encryptedPassword) =>
        PackObject(CodePoint.SECCHK, Concat(
            PackUInt16(CodePoint.SECMEC, 7),
            PackString(CodePoint.RDBNAM, database, DdmStringEncoding.Cp500),
            PackString(CodePoint.USRID, user, DdmStringEncoding.Cp500),
            PackBytes(CodePoint.SECTKN, encryptedPassword)));

    /// <summary>
    /// Builds the Kerberos/GSSAPI (SECMEC 11) SECCHK exactly as IBM's JCC sends it to Db2 LUW: only SECMEC and
    /// RDBNAM. No USRID and no password; the principal is established by the token, which travels in the
    /// separate SECTKN object built by <see cref="PackSecurityTokenObject"/>.
    /// </summary>
    public static byte[] PackSecchkGssapi(string database) =>
        PackObject(CodePoint.SECCHK, Concat(
            PackUInt16(CodePoint.SECMEC, 11),
            PackString(CodePoint.RDBNAM, database, DdmStringEncoding.Cp500)));

    /// <summary>
    /// Legacy layout from the first token-file implementation: the token as a SECCHK parameter plus USRID. IBM
    /// clients do not send this; it is kept only until live evidence settles which layouts Db2 accepts.
    /// </summary>
    public static byte[] PackSecchkKerberos(string database, ReadOnlySpan<byte> token, string user) =>
        PackObject(CodePoint.SECCHK, Concat(
            PackUInt16(CodePoint.SECMEC, 11),
            PackBytes(CodePoint.SECTKN, token),
            PackString(CodePoint.RDBNAM, database, DdmStringEncoding.Cp500),
            PackString(CodePoint.USRID, user, DdmStringEncoding.Cp500)));

    /// <summary>Builds the stand-alone SECTKN object (carried in its own OBJECT DSS) that follows a GSSAPI SECCHK.</summary>
    public static byte[] PackSecurityTokenObject(ReadOnlySpan<byte> token) => PackBytes(CodePoint.SECTKN, token);

    public static byte[] PackAccrdb(string productId, string database, DdmStringEncoding encoding) => PackObject(CodePoint.ACCRDB, Concat(
        PackString(CodePoint.RDBNAM, database, encoding), PackUInt16(CodePoint.RDBACCCL, (ushort)CodePoint.SQLAM),
        PackString(CodePoint.PRDID, productId, encoding), PackString(CodePoint.TYPDEFNAM, "QTDSQLX86", encoding),
        PackBytes(CodePoint.CRRTKN, DefaultCorrelationToken), PackBytes(CodePoint.TYPDEFOVR, DefaultTypeOverrides)));

    public static byte[] PackCommit() => PackObject(CodePoint.RDBCMM, []);
    public static byte[] PackRollback() => PackObject(CodePoint.RDBRLLBCK, []);

    private static int PackageLength(string database, string package, string token) => 4 + Math.Max(18, Encoding.UTF8.GetByteCount(database)) + 18 + Math.Max(18, Encoding.UTF8.GetByteCount(package)) + Math.Max(8, Encoding.UTF8.GetByteCount(token)) + 2;

    public static byte[] PackPackageNameCsn(string database, string package, string token, ushort section)
    {
        var output = new byte[PackageLength(database, package, token)];
        WritePackageNameCsn(output, database, package, token, section);
        return output;
    }

    private static byte[] PackPackageNameCsn(ResultSetPackageSection section)
    {
        var output = new byte[68]; WritePackageNameCsn(output, section); return output;
    }

    private static void CopyFixedName(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.Length > destination.Length) throw new InvalidDataException("RSLSETRM returned a package identifier that exceeds its DRDA field width.");
        destination.Fill((byte)' ');
        source.CopyTo(destination);
    }

    private static byte[] PackCommand(CodePoint command, string database, string package, string token, ushort section, byte[]? suffix = null) =>
        PackCommandDirect(command, database, package, token, section, suffix);

    public static byte[] PackExecSqlImmediate(string pkg, string token, ushort section, string db) => PackCommand(CodePoint.EXCSQLIMM, db, pkg, token, section, ParamCommitToken);
    public static byte[] PackPrepareSqlStatement(string pkg, string token, ushort section, string db) => PackCommand(CodePoint.PRPSQLSTT, db, pkg, token, section, ParamReturnSqlda);
    public static byte[] PackDescribeSqlStatement(string pkg, string token, ushort section, string db) => PackCommand(CodePoint.DSCSQLSTT, db, pkg, token, section, ParamTypeSqlda);
    public static byte[] PackDescribeSqlStatement(ResultSetPackageSection section) =>
        PackRecordCommand(CodePoint.DSCSQLSTT, section, ParamTypeSqlda);
    public static byte[] PackExecuteSqlStatement(string pkg, string token, ushort section, string db, ushort? maximumResultSets = null, uint queryBlockSize = 32767, uint? queryRowsetSize = 64, bool outputExpected = true)
    {
        return maximumResultSets.HasValue
            ? PackExecuteWithResults(pkg, token, section, db, maximumResultSets.Value, queryBlockSize, queryRowsetSize, outputExpected)
            : PackCommand(CodePoint.EXCSQLSTT, db, pkg, token, section, ParamCommitToken);
    }
    public static byte[] PackExecuteSqlSet(string pkg, string token, ushort section, string db) => PackCommand(CodePoint.EXCSQLSET, db, pkg, token, section);
    public static byte[] PackInterruptRdbRequest(ReadOnlySpan<byte> token) => PackObject(CodePoint.INTRDBRQS, PackBytes(CodePoint.RDBINTTKN, token));

    /// <summary>Builds one locator-based GETNXTCHK request using the observed IBM/Db2 LUW wire fields.</summary>
    public static byte[] PackGetNextChunk(string package, string token, ushort section, string database, ulong queryId, ulong locator, ulong requestedLength) =>
        PackLocatorChunkRequest(package, token, section, database, queryId, locator, requestedLength);

    /// <summary>Collects EXTDTA bytes and terminal markers from one GETNXTCHK reply.</summary>
    public static ProgressiveChunkReply ParseProgressiveChunkReply(IReadOnlyList<DssPacket> replies)
    {
        using var data = new MemoryStream();
        var sawEnd = false;
        var endOfReference = false;
        var lastChunk = false;
        foreach (var reply in replies)
        {
            if (reply.CodePoint == (ushort)CodePoint.EXTDTA)
            {
                if (sawEnd) throw new InvalidDataException("GETNXTCHK reply contained EXTDTA after ENDDTARM.");
                if (reply.Payload.Length == 0 || reply.Payload[0] != 0)
                    throw new InvalidDataException("GETNXTCHK reply contained an invalid EXTDTA status byte.");
                data.Write(reply.Payload.AsSpan(1));
            }
            else if (reply.CodePoint == (ushort)CodePoint.ENDDTARM)
            {
                if (sawEnd) throw new InvalidDataException("GETNXTCHK reply contained multiple ENDDTARM messages.");
                sawEnd = true;
                var fields = ParseParameters(reply.Payload);
                if (!fields.TryGetValue((ushort)CodePoint.SVRCOD, out var serverCode) || serverCode.Length != 2)
                    throw new InvalidDataException("GETNXTCHK ENDDTARM omitted its two-byte SVRCOD.");
                var serverCodeValue = BinaryPrimitives.ReadUInt16BigEndian(serverCode);
                if (serverCodeValue != 0) throw new InvalidDataException($"GETNXTCHK ENDDTARM returned SVRCOD {serverCodeValue}.");
                endOfReference = ParseOptionalDdmBoolean(fields, CodePoint.ENDREF);
                lastChunk = ParseOptionalDdmBoolean(fields, CodePoint.LSTCHK);
            }
        }
        if (!sawEnd) throw new InvalidDataException("GETNXTCHK reply did not contain ENDDTARM.");
        return new(data.ToArray(), endOfReference, lastChunk);
    }

    private static bool ParseOptionalDdmBoolean(IReadOnlyDictionary<ushort, byte[]> values, CodePoint codePoint)
    {
        if (!values.TryGetValue((ushort)codePoint, out var value)) return false;
        if (value.Length != 1 || value[0] is not (0xF0 or 0xF1))
            throw new InvalidDataException($"ENDDTARM contained an invalid {codePoint} Boolean value.");
        return value[0] == 0xF1;
    }

    private static byte[] PackQuery(CodePoint command, string pkg, string token, ushort section, string database, uint blockSize, ulong? queryId, ResultSetPackageSection? resultSetSection = null)
    {
        return PackQueryDirect(command, pkg, token, section, database, blockSize, queryId, resultSetSection);
    }

    public static byte[] PackOpenQuery(string pkg,string token,ushort section,string db,uint block) => PackQuery(CodePoint.OPNQRY,pkg,token,section,db,block,null);
    public static byte[] PackOpenQueryWithParams(string pkg,string token,ushort section,string db,uint block) => PackQuery(CodePoint.OPNQRY,pkg,token,section,db,block,null);
    public static byte[] PackContinueQuery(string pkg,string token,ushort section,string db,uint block,ulong id) => PackQuery(CodePoint.CNTQRY,pkg,token,section,db,block,id);
    public static byte[] PackContinueQuery(ResultSetPackageSection section,uint block,ulong id,uint? rowsetSize = null, bool returnAllData = false)
    {
        return PackRecordContinue(section, block, id, rowsetSize, returnAllData, procedure: false);
    }

    public static byte[] PackContinueProcedureResultSet(ResultSetPackageSection section, uint blockSize, ulong id)
    {
        // Preserve the observed CLI procedure-cursor shape (including FREPRVREF),
        // which differs from ordinary OPNQRY continuation.
        return PackRecordContinue(section, blockSize, id, null, returnAllData: true, procedure: true);
    }

    public static byte[] PackCloseProcedureResultSet(ResultSetPackageSection section, ulong id)
    {
        var output = CreateObjectBuffer(CodePoint.CLSQRY, 80, out var offset);
        WritePackageNameCsn(output.AsSpan(offset, 68), section);
        WriteUInt64Parameter(output.AsSpan(offset + 68), CodePoint.QRYINSID, id); return output;
    }

    public static byte[] PackSqlStatement(string sql)
    {
        var byteCount = Encoding.UTF8.GetByteCount(sql);
        var output = CreateObjectBuffer(CodePoint.SQLSTT, checked(6 + byteCount), out var offset);
        output[offset] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(offset + 1), (uint)byteCount);
        Encoding.UTF8.GetBytes(sql.AsSpan(), output.AsSpan(offset + 5, byteCount));
        output[^1] = 0xff;
        return output;
    }

    private static byte[] Concat(params byte[][] values)
    {
        var length=values.Sum(value=>value.Length);var output=new byte[length];var offset=0;
        foreach(var value in values){value.CopyTo(output,offset);offset+=value.Length;}return output;
    }

    public static Dictionary<ushort, byte[]> ParseParameters(ReadOnlySpan<byte> data)
    {
        var result=new Dictionary<ushort,byte[]>();var offset=0;
        while(offset<data.Length)
        {
            if(offset+4>data.Length)throw new InvalidDataException("Incomplete DDM parameter header in reply.");
            var length=BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);var codePoint=BinaryPrimitives.ReadUInt16BigEndian(data[(offset+2)..]);
            if(length<4||offset+length>data.Length)throw new InvalidDataException($"Invalid DDM parameter length {length} at offset {offset}.");
            result[codePoint]=data.Slice(offset+4,length-4).ToArray();offset+=length;
        }
        return result;
    }

    internal static bool TryGetParameter(ReadOnlySpan<byte> data, ushort codePoint, out ReadOnlySpan<byte> value)
    {
        value = default;
        var found = false;
        // Validate the entire collection and retain the last duplicate, as the
        // public dictionary adapter does. Finding a key never skips trailing errors.
        foreach (var item in new DdmObjectEnumerator(data))
            if (item.CodePoint == codePoint) { value = item.Payload; found = true; }
        return found;
    }

    private static ushort U16(ReadOnlySpan<byte> data,bool little)=>little?BinaryPrimitives.ReadUInt16LittleEndian(data):BinaryPrimitives.ReadUInt16BigEndian(data);
    private static uint U32(ReadOnlySpan<byte> data,bool little)=>little?BinaryPrimitives.ReadUInt32LittleEndian(data):BinaryPrimitives.ReadUInt32BigEndian(data);
    private static ulong U64(ReadOnlySpan<byte> data,bool little)=>little?BinaryPrimitives.ReadUInt64LittleEndian(data):BinaryPrimitives.ReadUInt64BigEndian(data);

    public static SqlCard ParseSqlCard(ReadOnlySpan<byte> data,bool littleEndian=true)
    {
        if(data.Length==0)throw new InvalidDataException("Empty SQLCARD payload.");
        if(data[0]==0xff)return new(0,"","",0);
        if(data.Length<18)throw new InvalidDataException($"SQLCARD payload too short: {data.Length} bytes.");
        var sqlCode=unchecked((int)U32(data[1..5],littleEndian));var sqlState=Encoding.ASCII.GetString(data[5..10]);
        // SQLCAXGRP has its own null indicator at 18. Six I4 SQLERRD fields
        // start at 19; SQLERRD(3), the affected-row count, starts at 27.
        var hasExtension = data.Length > 18 && data[18] != 0xff;
        if (hasExtension && data.Length < 54)
            throw new InvalidDataException("SQLCARD SQLCAXGRP is truncated.");
        long rows=hasExtension?unchecked((int)U32(data[27..31],littleEndian)):0;
        var message="";
        var warningFlags = data.Length >= 54 && data[18] == 0
            ? Encoding.ASCII.GetString(data.Slice(43, 11))
            : "";
        if(hasExtension && data.Length>54)
        {
            var rest=data[54..];if(rest.Length>=2){var n=BinaryPrimitives.ReadUInt16BigEndian(rest);if(rest.Length>=2+n)rest=rest[(2+n)..];}
            if(rest.Length>=2){var n=BinaryPrimitives.ReadUInt16BigEndian(rest);if(rest.Length>=2+n)message=Encoding.UTF8.GetString(rest.Slice(2,n)).Replace("\xff",", ",StringComparison.Ordinal);}
        }
        return new(sqlCode,sqlState,message,rows) { WarningFlags = warningFlags };
    }

    public static FieldDescriptor[] ParseQueryDescriptor(ReadOnlySpan<byte> data)
    {
        if(data.Length<3)throw new InvalidDataException("QRYDSC payload too short.");var result=new List<FieldDescriptor>();var pos=0;
        var hasDescriptorBase = false;
        while(pos<data.Length)
        {
            // Strict protocol parsing: malformed lengths are errors, never silent
            // partial parses (matching the TS/Rust descriptor readers).
            if(data.Length-pos<3)throw new InvalidDataException($"QRYDSC has {data.Length-pos} trailing bytes.");
            var group=data[pos];
            if(group<3)throw new InvalidDataException($"QRYDSC group at offset {pos} has invalid length {group}.");
            if(pos+group>data.Length)throw new InvalidDataException($"QRYDSC group at offset {pos} with length {group} exceeds payload {data.Length}.");
            var type=data[pos+1];var id=data[pos+2];
            if(type==0x76&&id==0xd0)hasDescriptorBase=true;
            else if(type==0x7f&&id==0x00&&!hasDescriptorBase)
                throw new InvalidDataException("QRYDSC CPT continuation has no preceding NGDA descriptor group.");
            if((type==0x76&&id==0xd0)||(type==0x7f&&id==0x00))
            {
                if((group-3)%3!=0)throw new InvalidDataException("QRYDSC descriptor group has an invalid length.");
                var count=group/3-1;var at=pos+3;
                for(var i=0;i<count&&at+3<=pos+group;i++,at+=3)result.Add(new(data[at],data[at+1],data[at+2]));
            }
            pos+=group;
        }
        return result.ToArray();
    }

    public static (FieldDescriptor[] Fields, byte[] Data) ParseSqlDtaReply(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> descriptor = default;
        ReadOnlySpan<byte> values = default;
        var offset = 0;
        while (offset < data.Length)
        {
            if (data.Length - offset < 4) throw new InvalidDataException("Truncated SQLDTARD DDM object header.");
            var shortLength = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            var codePoint = (CodePoint)BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            var headerLength = 4;
            ulong objectLength = shortLength;
            if ((shortLength & 0x8000) != 0)
            {
                headerLength = shortLength & 0x7fff;
                if (headerLength == 8)
                {
                    if (data.Length - offset < 8) throw new InvalidDataException("Truncated extended SQLDTARD DDM object header.");
                    objectLength = (ulong)BinaryPrimitives.ReadUInt32BigEndian(data[(offset + 4)..]) + 8;
                }
                else if (headerLength == 12)
                {
                    if (data.Length - offset < 12) throw new InvalidDataException("Truncated extended SQLDTARD DDM object header.");
                    objectLength = BinaryPrimitives.ReadUInt64BigEndian(data[(offset + 4)..]) + 12;
                }
                else throw new InvalidDataException($"Unsupported SQLDTARD DDM header length {headerLength}.");
            }
            if (objectLength < (ulong)headerLength || objectLength > (ulong)(data.Length - offset))
                throw new InvalidDataException("Invalid SQLDTARD DDM object length.");
            var body = data.Slice(offset + headerLength, checked((int)objectLength - headerLength));
            if (codePoint == CodePoint.FDODSC) descriptor = body;
            else if (codePoint == CodePoint.FDODTA) values = body;
            offset += checked((int)objectLength);
        }
        var fields = descriptor.IsEmpty ? Array.Empty<FieldDescriptor>() : ParseQueryDescriptor(descriptor);
        return (fields, values.ToArray());
    }

    /// <summary>Parses the SQLAM 7+ SQLCINRD descriptor returned for a procedure cursor.</summary>
    public static Db2ColumnMetadata[] ParseSqlCursorInfo(ReadOnlySpan<byte> data)
        => ParseSqlCursorInfoCore(data, 10, null, null, null);

    internal static Db2ColumnMetadata[] ParseSqlCursorInfo(
        ReadOnlySpan<byte> data,
        string expectedAlias,
        Action<int, int, bool, bool> nameDiagnostics,
        Action<string>? offsetDiagnostics = null) =>
        ParseSqlCursorInfoCore(data, 10, expectedAlias, nameDiagnostics, offsetDiagnostics);

    internal static Db2ColumnMetadata[] ParseSqlCursorInfo(
        ReadOnlySpan<byte> data,
        ushort sqlamLevel,
        string expectedAlias,
        Action<int, int, bool, bool> nameDiagnostics,
        Action<string>? offsetDiagnostics = null) =>
        ParseSqlCursorInfoCore(data, sqlamLevel, expectedAlias, nameDiagnostics, offsetDiagnostics);

    internal static Db2ColumnMetadata[] ParseSqlCursorInfo(
        ReadOnlySpan<byte> data,
        ushort sqlamLevel,
        Action<int, int, bool, bool>? nameDiagnostics = null,
        Action<string>? offsetDiagnostics = null) =>
        ParseSqlCursorInfoCore(data, sqlamLevel, null, nameDiagnostics, offsetDiagnostics);

    private static Db2ColumnMetadata[] ParseSqlCursorInfoCore(
        ReadOnlySpan<byte> data,
        ushort sqlamLevel,
        string? expectedAlias,
        Action<int, int, bool, bool>? nameDiagnostics,
        Action<string>? offsetDiagnostics = null)
    {
        if (sqlamLevel < 7)
            throw new Db2UnsupportedFeatureException($"SQLCINRD SQLAM level {sqlamLevel} uses the legacy SQLCIROW layout, which is not implemented.");

        // SQLAM >= 7 defines SQLCINRD as SQLDHROW, SQLNUMROW, then repeated
        // SQLDAROW/SQLDAGRP. It is not an SQLDARD and has no SQLCARD prefix.
        // Db2 LUW emits 32-bit header name lengths; other servers may use the
        // standard 16-bit VCS/VCM lengths, so only the header width has a
        // conservative format fallback.
        try { return ParseSqlCursorInfoModern(data, 4, sqlamLevel, expectedAlias, nameDiagnostics, offsetDiagnostics); }
        catch (InvalidDataException wideHeaderError)
        {
            try { return ParseSqlCursorInfoModern(data, 2, sqlamLevel, expectedAlias, nameDiagnostics, offsetDiagnostics); }
            catch (InvalidDataException narrowHeaderError)
            {
                throw new InvalidDataException($"SQLCINRD did not match either supported header layout (32-bit lengths: {wideHeaderError.Message}; 16-bit lengths: {narrowHeaderError.Message}).", wideHeaderError);
            }
        }
    }

    private static Db2ColumnMetadata[] ParseSqlCursorInfoModern(
        ReadOnlySpan<byte> data,
        int headerStringLengthWidth,
        ushort sqlamLevel,
        string? expectedAlias,
        Action<int, int, bool, bool>? nameDiagnostics,
        Action<string>? offsetDiagnostics)
    {
        var offset = 0;
        var headerPresent = ReadNullableGroup(data, ref offset); // SQLDHROW / SQLDHGRP (LID 0xE0 / 0xD0).
        if (headerPresent)
        {
            Require(data, offset, 12);
            offset += 12; // SQLDHOLD, SQLDRETURN, SQLDSCROLL, SQLDSENSITIVE, SQLDFCODE, SQLDKEYTYPE.
            _ = ReadFdocaStringWithWidth(data, ref offset, headerStringLengthWidth); // SQLDRDBNAM (VCS).
            _ = ReadFdocaStringPairWithWidth(data, ref offset, headerStringLengthWidth); // SQLDSCHEMA (VCM/VCS).
        }

        var countOffset = offset;
        var count = ReadFdocaUInt16(data, ref offset); // SQLNUMROW (LID 0x68).
        var columns = new Db2ColumnMetadata[count];
        var columnOffsets = new List<string>(count);
        for (var index = 0; index < count; index++)
        {
            var fixedOffset = offset;
            Require(data, offset, 16);
            var precision = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
            var scale = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 2)..]);
            var length = BinaryPrimitives.ReadUInt64LittleEndian(data[(offset + 4)..]);
            var sqlType = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 12)..]);
            offset += 16; // SQLDAGRP: precision, scale, length, type, CCSID.
            if (sqlamLevel >= 9)
            {
                Require(data, offset, 8);
                offset += 8; // SQLARREXTENT (SQLAM >= 9).
                if (sqlamLevel >= 10)
                {
                    Require(data, offset, 2);
                    offset += 2; // SQLAM 10 extension currently marked undecoded by Wireshark.
                }
            }

            var optionalOffset = offset;
            var name = string.Empty;
            var label = string.Empty;
            var sqlXName = string.Empty;
            if (ReadNullableGroup(data, ref offset)) // SQLDOPTGRP (LID 0xD2).
            {
                Require(data, offset, 2);
                offset += 2; // SQLUNNAMED.
                name = ReadFdocaStringPair(data, ref offset); // SQLNAME_m / SQLNAME_s.
                label = ReadFdocaStringPair(data, ref offset); // SQLLABEL_m / SQLLABEL_s.
                _ = ReadFdocaStringPair(data, ref offset); // SQLCOMMENTS_m / SQLCOMMENTS_s.

                if (ReadNullableGroup(data, ref offset)) // SQLUDTGRP (LID 0x5B).
                {
                    if ((sqlType & 0xfffe) == (ushort)SqlType.FakeUDT)
                    {
                        Require(data, offset, 4);
                        offset += 4; // SQLUDTXTYPE.
                        _ = ReadFdocaString(data, ref offset); // SQLUDTRDBNAM.
                        _ = ReadFdocaStringPair(data, ref offset); // SQLUDTSCHEMA.
                        _ = ReadFdocaStringPair(data, ref offset); // SQLUDTNAME.
                        if (sqlamLevel >= 10) _ = ReadFdocaStringPair(data, ref offset); // SQLUDTMODULE.
                    }
                    // For built-in types the nullable SQLUDTGRP marker is the
                    // complete field. Nested UDT values are meaningful only
                    // for a user-defined SQL type.
                }

                if (ReadNullableGroup(data, ref offset)) // SQLDXGRP (LID 0xD4).
                {
                    var dxMarkerOffset = offset - 1;
                    var dxLayoutErrors = new List<string>();
                    var parsed = false;
                    foreach (var flagLength in sqlamLevel >= 9 ? new[] { 12, 8 } : new[] { 8 })
                    {
                        var candidateOffset = offset;
                        try
                        {
                            var fieldsOffset = candidateOffset;
                            Require(data, candidateOffset, flagLength);
                            candidateOffset += flagLength; // SQLAM 9 normally adds SQLXOPTLCK and SQLXHIDDEN.
                            var rdbOffset = candidateOffset;
                            _ = ReadFdocaString(data, ref candidateOffset); // SQLXRDBNAM.
                            var corOffset = candidateOffset;
                            _ = ReadFdocaStringPair(data, ref candidateOffset); // SQLXCORNAME_m / _s.
                            var baseOffset = candidateOffset;
                            _ = ReadFdocaStringPair(data, ref candidateOffset); // SQLXBASENAME_m / _s.
                            var schemaOffset = candidateOffset;
                            _ = ReadFdocaStringPair(data, ref candidateOffset); // SQLXSCHEMA_m / _s.
                            var nameOffset = candidateOffset;
                            var candidateName = ReadFdocaStringPair(data, ref candidateOffset); // SQLXNAME_m / _s.
                            if (sqlamLevel >= 10) _ = ReadFdocaStringPair(data, ref candidateOffset); // SQLXMODULE_m / _s.
                            sqlXName = candidateName;
                            offset = candidateOffset;
                            parsed = true;
                            break;
                        }
                        catch (InvalidDataException error)
                        {
                            dxLayoutErrors.Add($"flags={flagLength} start={offset} failed={error.Message}");
                        }
                    }
                    if (!parsed) throw new InvalidDataException($"SQLCINRD SQLDXGRP at {dxMarkerOffset} has no valid SQLAM {sqlamLevel} layout ({string.Join("; ", dxLayoutErrors)}).");
                }

                if (sqlamLevel >= 10)
                {
                    Require(data, offset, 1);
                    offset++; // Db2/DRDA SQLAM 10 trailing nullable extension (undecoded by Wireshark).
                }
            }

            nameDiagnostics?.Invoke(name.Length, label.Length,
                expectedAlias is not null && StringComparer.Ordinal.Equals(name, expectedAlias),
                expectedAlias is not null && StringComparer.Ordinal.Equals(label, expectedAlias));
            // In procedure result metadata the extended SQLXNAME is the only
            // name that preserves an expression's explicit result alias.
            var selectedName = label.Length > 0 ? label : sqlXName.Length > 0 ? sqlXName : name;
            columnOffsets.Add($"c{index + 1}:fixed={fixedOffset},optional={optionalOffset},name={name.Length},label={label.Length},sqlXName={sqlXName.Length},selected={selectedName.Length}");
            columns[index] = new(selectedName, (SqlType)sqlType, length, precision, NormalizeScale(sqlType, length, scale), (sqlType & 1) != 0);
        }
        if (offset != data.Length)
            throw new InvalidDataException($"SQLCINRD has {data.Length - offset} trailing bytes after {count} SQLDAROW entries at {offset}: {FormatRedactedDescriptorWindow(data, expectedAlias ?? "PAYLOAD", offset, data.Length)}");
        offsetDiagnostics?.Invoke($"sqlam={sqlamLevel} headerWidth={headerStringLengthWidth} headerPresent={headerPresent} countOffset={countOffset} count={count} columns=[{string.Join(';', columnOffsets)}] end={offset}");
        return columns;
    }

    private static string ReadFdocaStringWithWidth(ReadOnlySpan<byte> data, ref int offset, int width) =>
        width == 4 ? ReadFdocaString32(data, ref offset) : ReadFdocaString(data, ref offset);

    private static string ReadFdocaStringPairWithWidth(ReadOnlySpan<byte> data, ref int offset, int width)
    {
        var mbcs = ReadFdocaStringWithWidth(data, ref offset, width);
        var sbcs = ReadFdocaStringWithWidth(data, ref offset, width);
        if (mbcs.Length > 0 && sbcs.Length > 0)
            throw new InvalidDataException("FDOCA VCM/VCS string contains both character encodings.");
        return mbcs.Length > 0 ? mbcs : sbcs;
    }

    /// <summary>
    /// Keeps cursor-specific SQLCINRD types and attributes, filling blank column
    /// names from the matching SQLDARD descriptor when Db2 leaves SQLNAME/SQLLABEL empty.
    /// </summary>
    internal static Db2ColumnMetadata[] MergeCursorColumnNames(
        IReadOnlyList<Db2ColumnMetadata> cursorColumns,
        IReadOnlyList<Db2ColumnMetadata> describedColumns)
    {
        if (cursorColumns.Count == 0) return describedColumns.ToArray();
        if (describedColumns.Count != cursorColumns.Count) return cursorColumns.ToArray();
        var merged = new Db2ColumnMetadata[cursorColumns.Count];
        for (var index = 0; index < cursorColumns.Count; index++)
        {
            var cursor = cursorColumns[index];
            var name = cursor.Name.Length > 0 ? cursor.Name : describedColumns[index].Name;
            merged[index] = cursor with { Name = name };
        }
        return merged;
    }

    private static Db2ColumnMetadata[] ParseSqlCursorInfoStrict(
        ReadOnlySpan<byte> data,
        int headerStringLengthWidth,
        string? expectedAlias,
        Action<int, int, bool, bool>? nameDiagnostics,
        Action<string>? offsetDiagnostics)
    {
        var offset = 0;
        var headerStart = offset;
        if (data.Length == 0) throw new InvalidDataException("Empty SQLCINRD payload.");

        // SQLDHGRP is nullable. When present it contains the cursor flags and
        // database/schema names before SQLNUMROW.
        var headerPresent = ReadNullableGroup(data, ref offset);
        if (headerPresent)
        {
            Require(data, offset, 12);
            offset += 12; // SQLDHOLD through SQLDKEYTYPE
            if (headerStringLengthWidth == 4)
            {
                _ = ReadFdocaString32(data, ref offset); // SQLDRDBNAM (VCS)
                _ = ReadFdocaStringPair32(data, ref offset); // SQLDSCHEMA (VCM/VCS)
            }
            else
            {
                _ = ReadFdocaString(data, ref offset);
                _ = ReadFdocaStringPair(data, ref offset);
            }
        }

        var countOffset = offset;
        var count = ReadFdocaUInt16(data, ref offset);
        if (count == 0 && offset != data.Length)
            throw new InvalidDataException("SQLCINRD reported no columns but contains additional descriptor data.");
        var columns = new Db2ColumnMetadata[count];
        var columnOffsets = new List<string>(count);
        for (var index = 0; index < count; index++)
        {
            var fixedOffset = offset;
            Require(data, offset, 16);
            var precision = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
            var scale = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 2)..]);
            var length = BinaryPrimitives.ReadUInt64LittleEndian(data[(offset + 4)..]);
            var sqlType = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 12)..]);
            offset += 16; // SQLPRECISION, SQLSCALE, SQLLENGTH, SQLTYPE, SQLCCSID

            var optionalOffset = offset;
            var optionalPresent = ReadNullableGroup(data, ref offset);
            var name = string.Empty;
            var nameOffset = -1;
            var nameLength = 0;
            var labelOffset = -1;
            var labelLength = 0;
            if (optionalPresent)
            {
                Require(data, offset, 2);
                offset += 2; // SQLUNNAMED
                nameOffset = offset;
                var beforeName = offset;
                name = ReadFdocaStringPair(data, ref offset);
                nameLength = offset - beforeName;
                labelOffset = offset;
                var beforeLabel = offset;
                var label = ReadFdocaStringPair(data, ref offset); // SQLLABEL
                labelLength = offset - beforeLabel;
                nameDiagnostics?.Invoke(name.Length, label.Length,
                    expectedAlias is not null && StringComparer.Ordinal.Equals(name, expectedAlias),
                    expectedAlias is not null && StringComparer.Ordinal.Equals(label, expectedAlias));
                if (name.Length == 0) name = label;
                _ = ReadFdocaStringPair(data, ref offset); // SQLCOMMENTS

                if (ReadNullableGroup(data, ref offset))
                {
                    Require(data, offset, 4);
                    offset += 4; // SQLUDTXTYPE (SQLAM > 6).
                    _ = ReadFdocaString(data, ref offset); // SQLUDTRDBNAM.
                    _ = ReadFdocaStringPair(data, ref offset); // SQLUDTSCHEMA.
                    _ = ReadFdocaStringPair(data, ref offset); // SQLUDTNAME.
                    _ = ReadFdocaStringPair(data, ref offset); // SQLUDTMODULE (SQLAM >= 10).
                }
                if (ReadNullableGroup(data, ref offset))
                {
                    Require(data, offset, 12);
                    offset += 12; // Six SQLDX integer fields (SQLAM >= 9).
                    _ = ReadFdocaString(data, ref offset); // SQLXRDBNAM.
                    _ = ReadFdocaStringPair(data, ref offset); // SQLXCORNAME.
                    _ = ReadFdocaStringPair(data, ref offset); // SQLXBASENAME.
                    _ = ReadFdocaStringPair(data, ref offset); // SQLXSCHEMA.
                    var sqlXName = ReadFdocaStringPair(data, ref offset); // SQLXNAME.
                    _ = ReadFdocaStringPair(data, ref offset); // SQLXMODULE (SQLAM >= 10).
                    if (label.Length == 0 && sqlXName.Length > 0) name = sqlXName;
                }
            }
            else nameDiagnostics?.Invoke(0, 0, false, false);

            columnOffsets.Add($"c{index + 1}:fixed={fixedOffset},optional={optionalOffset}:{(optionalPresent ? 1 : 0)},name={nameOffset}+{nameLength},label={labelOffset}+{labelLength},end={offset}");

            columns[index] = new(name, (SqlType)sqlType, length, precision, NormalizeScale(sqlType, length, scale), (sqlType & 1) != 0);
        }
        if (offset != data.Length)
            throw new InvalidDataException("SQLCINRD contains optional descriptor extensions.");
        offsetDiagnostics?.Invoke($"headerWidth={headerStringLengthWidth} header={headerStart}..{countOffset} countOffset={countOffset} count={count} columns=[{string.Join(';', columnOffsets)}] end={offset}");
        return columns;
    }

    // Some Db2 releases include optional SQLDA extension groups which are
    // deliberately opaque to this driver. Retain the established SQLDARD
    // column-name scan while reading the fixed SQLDA fields needed by callers.
    private static Db2ColumnMetadata[] ParseSqlCursorInfoWithLegacyNames(
        ReadOnlySpan<byte> data,
        string? expectedAlias = null,
        Action<string>? offsetDiagnostics = null)
    {
        if (data.Length == 0) throw new InvalidDataException("Empty SQLCINRD payload.");
        var hasName = data[0] == 0;
        ReadOnlySpan<byte> rest;
        if (data[0] == 0xff) rest = data[1..];
        else
        {
            if (data.Length < 13) throw new InvalidDataException("Truncated SQLCINRD header group.");
            rest = data[13..]; // nullable marker and SQLDHGRP's six integer fields
            _ = ParseString32(ref rest); // SQLDRDBNAM
            _ = ParseName32(ref rest); // SQLDSCHEMA
        }
        if (rest.Length < 2) throw new InvalidDataException("SQLCINRD is missing SQLNUMROW.");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(rest);
        rest = rest[2..];
        var columns = new Db2ColumnMetadata[count];
        var columnOffsets = new List<string>(count);
        for (var index = 0; index < count; index++)
        {
            var fixedOffset = data.Length - rest.Length;
            if (rest.Length < 16) throw new InvalidDataException($"SQLCINRD column {index + 1} is missing its fixed descriptor.");
            var precision = BinaryPrimitives.ReadUInt16LittleEndian(rest);
            var scale = BinaryPrimitives.ReadUInt16LittleEndian(rest[2..]);
            var length = BinaryPrimitives.ReadUInt64LittleEndian(rest[4..]);
            var sqlType = BinaryPrimitives.ReadUInt16LittleEndian(rest[12..]);
            rest = rest[16..];
            var name = string.Empty;

            var delimiter = -1;
            for (var position = 0; position + 2 < rest.Length; position++)
                if (rest[position] == 0xff && rest[position + 1] == 0xff && rest[position + 2] == 0xff)
                {
                    delimiter = position;
                    break;
                }
            var parsedFromOptionalGroup = false;
            if (rest.Length > 0 && rest[0] != 0xff)
            {
                try
                {
                    var optionalSegmentOffset = data.Length - rest.Length;
                    var optional = rest;
                    var optionalOffset = 0;
                    optionalOffset++; // SQLDOPTGRP marker.
                    Require(optional, optionalOffset, 2);
                    optionalOffset += 2; // SQLUNNAMED.
                    var candidateOffset = optionalOffset;
                    var candidate = ReadFdocaStringPair(optional, ref optionalOffset);
                    var labelOffset = optionalOffset;
                    var label = ReadFdocaStringPair(optional, ref optionalOffset);
                    var commentOffset = optionalOffset;
                    _ = ReadFdocaStringPair(optional, ref optionalOffset); // SQLCOMMENTS.
                    var describedName = label.Length == 0 ? candidate : label;
                    var suffixStart = optionalOffset;
                    var suffixOffset = optionalOffset;
                    var hasSqlDxGroup = TryReadSqlDxName(optional, ref suffixOffset, expectedAlias,
                        out var sqlXName, out var sqlXNameOffset, out var suffixDiagnostics);
                    if (label.Length == 0 && sqlXName.Length > 0)
                    {
                        describedName = sqlXName;
                    }
                    if (describedName.Length > 0)
                    {
                        name = describedName;
                        var tailOffset = hasSqlDxGroup ? suffixOffset : suffixStart;
                        var tail = optional[tailOffset..];
                        var tailDelimiter = -1;
                        for (var position = 0; position + 2 < tail.Length; position++)
                            if (tail[position] == 0xff && tail[position + 1] == 0xff && tail[position + 2] == 0xff)
                            {
                                tailDelimiter = position;
                                break;
                            }
                        rest = tailDelimiter >= 0 ? tail[(tailDelimiter + 3)..] : tail;
                        parsedFromOptionalGroup = true;
                        columnOffsets.Add($"c{index + 1}:fixed={fixedOffset},optionalName={optionalSegmentOffset + candidateOffset}+{candidate.Length},optionalLabel={optionalSegmentOffset + labelOffset}+{label.Length},comments={optionalSegmentOffset + commentOffset},sqlDx={hasSqlDxGroup},sqlXName={optionalSegmentOffset + sqlXNameOffset}+{sqlXName.Length},selectedLength={name.Length},selectedAlias={expectedAlias is not null && StringComparer.Ordinal.Equals(name, expectedAlias)},{suffixDiagnostics}");
                    }
                }
                catch (InvalidDataException)
                {
                    // This legacy descriptor form is not always aligned to SQLDOPTGRP.
                    // Retain the opaque-name scan below for other server layouts.
                }
            }
            if (!parsedFromOptionalGroup && delimiter >= 0)
            {
                var nameSegmentOffset = data.Length - rest.Length;
                name = FindFdocaName(rest[..delimiter], expectedAlias, out var candidates);
                columnOffsets.Add($"c{index + 1}:fixed={fixedOffset},nameSegment={nameSegmentOffset}+{delimiter},delimiter={nameSegmentOffset + delimiter},candidateOffsets=[{candidates}],selectedLength={name.Length},selectedAlias={expectedAlias is not null && StringComparer.Ordinal.Equals(name, expectedAlias)}");
                rest = rest[(delimiter + 3)..];
            }
            else if (!parsedFromOptionalGroup && hasName && rest.Length >= 9)
            {
                rest = rest[9..];
                var candidate = ParseName(ref rest);
                var label = ParseName(ref rest);
                _ = ParseName(ref rest);
                if (rest.Length >= 7) rest = rest[7..];
                name = label.Length == 0 ? candidate : label;
                columnOffsets.Add($"c{index + 1}:fixed={fixedOffset},legacyNameFallback=true,selectedLength={name.Length},selectedAlias={expectedAlias is not null && StringComparer.Ordinal.Equals(name, expectedAlias)}");
            }
            else if (!parsedFromOptionalGroup && !hasName && rest.Length >= 29)
            {
                rest = rest[29..];
                columnOffsets.Add($"c{index + 1}:fixed={fixedOffset},legacyUnnamed=true");
            }
            else if (!parsedFromOptionalGroup) columnOffsets.Add($"c{index + 1}:fixed={fixedOffset},noNameSegment=true");

            columns[index] = new(name, (SqlType)sqlType, length, precision, NormalizeScale(sqlType, length, scale), (sqlType & 1) != 0);
        }
        offsetDiagnostics?.Invoke($"legacy-parser dataLength={data.Length} count={count} columns=[{string.Join(';', columnOffsets)}]");
        return columns;
    }

    private static string FindFdocaName(ReadOnlySpan<byte> data, string? expectedAlias, out string candidates)
    {
        var found = new List<string>();
        var selected = string.Empty;
        for (var position = 0; position < data.Length; position++)
        {
            var length = data[position];
            if (length == 0 || position + 1 + length > data.Length) continue;
            try
            {
                var candidate = new UTF8Encoding(false, true).GetString(data.Slice(position + 1, length));
                if (candidate.Any(char.IsControl)) continue;
                selected = candidate;
                found.Add($"{position}+{length}:{expectedAlias is not null && StringComparer.Ordinal.Equals(candidate, expectedAlias)}");
            }
            catch (DecoderFallbackException) { }
        }
        candidates = string.Join(',', found);
        return selected;
    }

    private static bool TryReadSqlDxName(
        ReadOnlySpan<byte> data,
        ref int offset,
        string? expectedAlias,
        out string sqlXName,
        out int sqlXNameOffset,
        out string diagnostics)
    {
        sqlXName = string.Empty;
        sqlXNameOffset = -1;
        var attempts = new List<string>();
        foreach (var sqlamLevel in new ushort[] { 11, 9, 7 })
        {
            var attemptOffset = offset;
            try
            {
                var udtMarkerOffset = attemptOffset;
                var udtPresent = ReadNullableGroup(data, ref attemptOffset);
                var udtEndOffset = attemptOffset;
                if (udtPresent)
                {
                    if (sqlamLevel > 6)
                    {
                        Require(data, attemptOffset, 4);
                        attemptOffset += 4; // SQLUDTXTYPE.
                        _ = ReadFdocaString(data, ref attemptOffset); // SQLUDTRDBNAM.
                        _ = ReadFdocaStringPair(data, ref attemptOffset); // SQLUDTSCHEMA.
                    }
                    _ = ReadFdocaStringPair(data, ref attemptOffset); // SQLUDTNAME.
                    if (sqlamLevel >= 10) _ = ReadFdocaStringPair(data, ref attemptOffset); // SQLUDTMODULE.
                    udtEndOffset = attemptOffset;
                }

                var dxMarkerOffset = attemptOffset;
                if (!ReadNullableGroup(data, ref attemptOffset))
                {
                    attempts.Add($"sqlam={sqlamLevel}:udt={udtMarkerOffset}:{(udtPresent ? 1 : 0)}@{udtMarkerOffset}..{udtEndOffset},dx=absent@{dxMarkerOffset}");
                    continue;
                }
                var flagLength = sqlamLevel >= 9 ? 12 : 8;
                Require(data, attemptOffset, flagLength);
                attemptOffset += flagLength; // SQLDX integer fields; SQLAM >= 9 adds OPTLCK and HIDDEN.
                _ = ReadFdocaString(data, ref attemptOffset); // SQLXRDBNAM.
                _ = ReadFdocaStringPair(data, ref attemptOffset); // SQLXCORNAME.
                _ = ReadFdocaStringPair(data, ref attemptOffset); // SQLXBASENAME.
                _ = ReadFdocaStringPair(data, ref attemptOffset); // SQLXSCHEMA.
                var nameStart = attemptOffset;
                var parsedName = ReadFdocaStringPair(data, ref attemptOffset); // SQLXNAME.
                if (sqlamLevel >= 10) _ = ReadFdocaStringPair(data, ref attemptOffset); // SQLXMODULE.

                sqlXName = parsedName;
                sqlXNameOffset = nameStart;
                offset = attemptOffset;
                diagnostics = string.Join('|', attempts.Append($"sqlam={sqlamLevel}:udt={udtMarkerOffset}:{(udtPresent ? 1 : 0)}@{udtMarkerOffset}..{udtEndOffset},dx=present@{dxMarkerOffset},flags={flagLength},xname={nameStart}+{parsedName.Length},xnameExpectedMatch={expectedAlias is not null && StringComparer.Ordinal.Equals(parsedName, expectedAlias)},end={attemptOffset}"));
                return true;
            }
            catch (InvalidDataException error)
            {
                attempts.Add($"sqlam={sqlamLevel}:truncate={attemptOffset}:{error.Message}");
            }
        }
        diagnostics = string.Join('|', attempts);
        sqlXName = string.Empty;
        sqlXNameOffset = -1;
        return false;
    }

    private static ushort NormalizeScale(ushort sqlType, ulong length, ushort scale)
    {
        // SQLDA describes TIMESTAMP(p) with a character-form SQLLENGTH:
        // 19 for p=0, otherwise 20+p. Some Db2 LUW DRDA responses leave
        // SQLSCALE at zero even though CLI/ODBC reports p as the scale.
        if ((sqlType & 0xfffe) != 392) return scale;
        if (length == 19) return 0;
        return length is >= 21 and <= 32 ? checked((ushort)(length - 20)) : scale;
    }

    /// <summary>Validates the SQLRSLRD FDOCA array and returns its result-set count.</summary>
    public static int ParseSqlResultSetCount(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        var count = ReadFdocaUInt16(data, ref offset);
        // SQLRSGRP has a four-byte locator and four-byte row count, plus the
        // VCM/VCS cursor name. Names and server extensions are variable length.
        var minimumRowBytes = checked((int)count * 12);
        if (data.Length - offset < minimumRowBytes)
            throw new InvalidDataException($"SQLRSLRD declares {count} result sets but only {data.Length - offset} bytes remain for their row summaries.");
        return count;
    }

    /// <summary>Parses package identifiers and section numbers returned in the RSLSETRM summary.</summary>
    public static ResultSetPackageSection[] ParseResultSetSections(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> packageList = default;
        var foundPackageList = false;
        foreach (var item in new DdmObjectEnumerator(data, extended: true))
        {
            if (item.CodePoint == (ushort)CodePoint.PKGSNLST)
            {
                if (foundPackageList) throw new InvalidDataException("RSLSETRM contains duplicate PKGSNLST objects.");
                packageList = item.Payload;
                foundPackageList = true;
            }
        }
        if (!foundPackageList) throw new InvalidDataException("RSLSETRM does not contain the required PKGSNLST object.");
        var sections = new List<ResultSetPackageSection>();
        foreach (var item in new DdmObjectEnumerator(packageList, extended: true))
        {
            if (item.CodePoint != (ushort)CodePoint.PKGNAMCSN) continue;
            if (item.Payload.Length < 64) throw new InvalidDataException("RSLSETRM contains a truncated PKGNAMCSN object.");
            var payload = item.Payload;
            sections.Add(new ResultSetPackageSection(payload[..18].ToArray(), payload.Slice(18, 18).ToArray(), payload.Slice(36, 18).ToArray(), payload.Slice(54, 8).ToArray(), BinaryPrimitives.ReadUInt16BigEndian(payload[62..])));
        }
        return sections.ToArray();
    }

    private static bool ReadNullableGroup(ReadOnlySpan<byte> data, ref int offset)
    {
        Require(data, offset, 1);
        return data[offset++] != 0xff;
    }

    private static ushort ReadFdocaUInt16(ReadOnlySpan<byte> data, ref int offset)
    {
        Require(data, offset, 2);
        var value = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        offset += 2;
        return value;
    }

    private static string ReadFdocaString(ReadOnlySpan<byte> data, ref int offset)
    {
        Require(data, offset, 2);
        var length = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;
        Require(data, offset, length);
        var value = Encoding.UTF8.GetString(data.Slice(offset, length));
        offset += length;
        return value;
    }

    private static string ReadFdocaStringPair(ReadOnlySpan<byte> data, ref int offset)
    {
        var mbcs = ReadFdocaString(data, ref offset);
        var sbcs = ReadFdocaString(data, ref offset);
        if (mbcs.Length > 0 && sbcs.Length > 0)
            throw new InvalidDataException("FDOCA VCM/VCS string contains both character encodings.");
        return mbcs.Length > 0 ? mbcs : sbcs;
    }

    private static string ReadFdocaString32(ReadOnlySpan<byte> data, ref int offset)
    {
        Require(data, offset, 4);
        var length = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
        offset += 4;
        if (length > int.MaxValue) throw new InvalidDataException("FDOCA string length exceeds the supported range.");
        Require(data, offset, (int)length);
        var value = Encoding.UTF8.GetString(data.Slice(offset, (int)length));
        offset += (int)length;
        return value;
    }

    private static string ReadFdocaStringPair32(ReadOnlySpan<byte> data, ref int offset)
    {
        var mbcs = ReadFdocaString32(data, ref offset);
        var sbcs = ReadFdocaString32(data, ref offset);
        if (mbcs.Length > 0 && sbcs.Length > 0)
            throw new InvalidDataException("FDOCA VCM/VCS string contains both character encodings.");
        return mbcs.Length > 0 ? mbcs : sbcs;
    }

    private static void Require(ReadOnlySpan<byte> data, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > data.Length - length)
            throw new InvalidDataException($"Truncated FDOCA result-set descriptor at offset {offset}: need {length}, have {data.Length - offset}.");
    }

    internal static string FormatRedactedDescriptorWindow(
        ReadOnlySpan<byte> data,
        string expectedAlias,
        int start,
        int end)
    {
        var lower = Math.Clamp(start, 0, data.Length);
        var upper = Math.Clamp(end, lower, data.Length);
        var aliasBytes = Encoding.UTF8.GetBytes(expectedAlias);
        var parts = new List<string>(upper - lower);
        for (var offset = lower; offset < upper; offset++)
        {
            if (offset + aliasBytes.Length <= upper && data.Slice(offset, aliasBytes.Length).SequenceEqual(aliasBytes))
            {
                parts.Add($"{offset}:{expectedAlias}");
                offset += aliasBytes.Length - 1;
                continue;
            }

            var value = data[offset];
            var redacted = value is >= 0x20 and <= 0x7e ? "." : $"\\x{value:x2}";
            parts.Add($"{offset}:{redacted}");
        }
        return string.Join(',', parts);
    }

    private static string ParseString(ref ReadOnlySpan<byte> data)
    {
        if(data.Length<2){data=ReadOnlySpan<byte>.Empty;return "";}var length=BinaryPrimitives.ReadUInt16BigEndian(data);if(length==0||data.Length<2+length){data=data[2..];return "";}var value=Encoding.UTF8.GetString(data.Slice(2,length));data=data[(2+length)..];return value;
    }
    private static string ParseName(ref ReadOnlySpan<byte> data){var first=ParseString(ref data);var second=ParseString(ref data);return first.Length==0?second:first;}
    private static string ParseString32(ref ReadOnlySpan<byte> data){if(data.Length<4){data=ReadOnlySpan<byte>.Empty;return "";}var length=BinaryPrimitives.ReadUInt32BigEndian(data);if(length==0||length>int.MaxValue||data.Length<4+(long)length){data=data[4..];return "";}var value=Encoding.UTF8.GetString(data.Slice(4,(int)length));data=data[(4+(int)length)..];return value;}
    private static string ParseName32(ref ReadOnlySpan<byte> data){var first=ParseString32(ref data);var second=ParseString32(ref data);return first.Length==0?second:first;}

    public static Db2ColumnMetadata[] ParseSqlDard(ReadOnlySpan<byte> data, bool littleEndian = true) =>
        ParseSqlDard(data, littleEndian, synthesizeUnnamedNames: true, expectedAlias: null, nameDiagnostics: null);

    internal static Db2ColumnMetadata[] ParseSqlDard(ReadOnlySpan<byte> data, bool littleEndian, bool synthesizeUnnamedNames) =>
        ParseSqlDard(data, littleEndian, synthesizeUnnamedNames, expectedAlias: null, nameDiagnostics: null);

    internal static Db2ColumnMetadata[] ParseSqlDard(
        ReadOnlySpan<byte> data,
        bool littleEndian,
        bool synthesizeUnnamedNames,
        string? expectedAlias,
        Action<int, int, bool, bool>? nameDiagnostics)
    {
        if(data.Length==0)return[];var hasName=data[0]==0;ReadOnlySpan<byte> rest;
        if(data[0]==0xff)rest=data[1..];
        else{var card=ParseSqlCard(data,littleEndian);if(card.SqlCode<0)throw new JustyBase.Db2.Db2SqlException(card.SqlCode,card.SqlState,$"DB2 SQLCODE={card.SqlCode} SQLSTATE={card.SqlState}: {card.Message}");if(data.Length>54){rest=data[54..];for(var i=0;i<3&&rest.Length>=2;i++){var n=BinaryPrimitives.ReadUInt16BigEndian(rest);if(rest.Length>=2+n)rest=rest[(2+n)..];}if(rest.Length>0&&rest[0]==0xff)rest=rest[1..];}else rest=data;}
        if(rest.Length<2)return[];
        if(rest[0]==0){if(rest.Length>13){rest=rest[13..];_=ParseString(ref rest);_=ParseName(ref rest);}}else rest=rest[1..];
        if(rest.Length<2)return[];var count=U16(rest,littleEndian);rest=rest[2..];var columns=new List<Db2ColumnMetadata>(count);
        for(var index=0;index<count&&rest.Length>=16;index++)
        {
            var precision=U16(rest,littleEndian);var scale=U16(rest[2..],littleEndian);var length=U64(rest[4..],littleEndian);var sqlType=U16(rest[12..],littleEndian);rest=rest[16..];var name="";
            var parsedName = "";
            var parsedLabel = "";
            var delimiter=-1;for(var i=0;i+2<rest.Length;i++)if(rest[i]==0xff&&rest[i+1]==0xff&&rest[i+2]==0xff){delimiter=i;break;}
            if(delimiter>=0){var chunk=rest[..delimiter];rest=rest[(delimiter+3)..];for(var i=0;i+1<chunk.Length;i++){var n=chunk[i];if(n>0&&i+1+n<=chunk.Length){var candidate=chunk.Slice(i+1,n);try{var value=new UTF8Encoding(false,true).GetString(candidate);if(!value.Any(char.IsControl)){parsedName=value;name=value;break;}}catch(DecoderFallbackException){}}}}
            else if(hasName){if(rest.Length>=9){rest=rest[9..];parsedName=ParseName(ref rest);parsedLabel=ParseName(ref rest);_=ParseName(ref rest);if(rest.Length>=7)rest=rest[7..];name=parsedLabel.Length==0?parsedName:parsedLabel;}}
            else if(rest.Length>=29)rest=rest[29..];
            nameDiagnostics?.Invoke(parsedName.Length, parsedLabel.Length,
                expectedAlias is not null && StringComparer.Ordinal.Equals(parsedName, expectedAlias),
                expectedAlias is not null && StringComparer.Ordinal.Equals(parsedLabel, expectedAlias));
            if (name.Length == 0 && synthesizeUnnamedNames) name = $"PARAM{index + 1}";
            columns.Add(new(name, (SqlType)sqlType, length, precision, NormalizeScale(sqlType, length, scale), (sqlType & 1) != 0));
        }
        return columns.ToArray();
    }
}
