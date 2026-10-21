using System.Buffers.Binary;
using System.Text;
using System.Security.Cryptography;
using JustyBase.Db2;
using JustyBase.Db2.Converters;
using JustyBase.Db2.Protocol;
using JustyBase.Db2.Types;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void InterruptRequestUsesIntrdbrqsAndOriginalInterruptToken()
    {
        Assert.Equal((ushort)0x2003, (ushort)CodePoint.INTRDBRQS);
        Assert.Equal((ushort)0x2007, (ushort)CodePoint.DRPPKG);
        Assert.Equal(new byte[] { 0, 10, 0x20, 0x03, 0, 6, 0x21, 0x03, 0x04, 0x1c },
            DdmCodec.PackInterruptRdbRequest([0x04, 0x1c]));
    }

    [Fact]
    public void DescribeProcedureCursorTargetsItsReturnedPackageSection()
    {
        var section = new ResultSetPackageSection(
            Encoding.ASCII.GetBytes("TESTDB"),
            Encoding.ASCII.GetBytes("APP"),
            Encoding.ASCII.GetBytes("CURSORPKG"),
            [1, 2, 3, 4, 5, 6, 7, 8],
            66);

        var request = DdmCodec.PackDescribeSqlStatement(section);
        var requestParameters = DdmCodec.ParseParameters(request.AsSpan(4));
        var package = Assert.Contains((ushort)CodePoint.PKGNAMCSN, requestParameters);

        Assert.Equal((ushort)CodePoint.DSCSQLSTT, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(2)));
        Assert.Equal(66, BinaryPrimitives.ReadUInt16BigEndian(package.AsSpan(62)));
        Assert.Equal(new byte[] { 0x01 }, requestParameters[0x2146]); // PRCTYP = ParamTypeSqlda.
    }

    [Fact]
    public void SqlDardDecodesUtf8AliasesFromRealDb2DescriptorVector()
    {
        const string descriptorHex =
            "0000000000303030303053514c3132303135000000000000000000010000000100000021ffffff00000000" +
            "2020202020202020202020001254455354444220202020202020202020202000000000ffff0200000000000400000000000000" +
            "f001000000000000000000000000000000000361626300000000000000000000ffffff000000000400000000000000f0010000" +
            "00000000000000000000000000000b7a616dc3b37769656e696100000000000000000000ffffff";
        var payload = Convert.FromHexString(descriptorHex);
        var nameDiagnostics = new List<(int Name, int Label, bool NameMatches, bool LabelMatches)>();

        var columns = DdmCodec.ParseSqlDard(payload, littleEndian: true, synthesizeUnnamedNames: false,
            expectedAlias: "zamówienia", nameDiagnostics: (name, label, nameMatches, labelMatches) =>
                nameDiagnostics.Add((name, label, nameMatches, labelMatches)));

        Assert.Collection(columns,
            first => Assert.Equal("abc", first.Name),
            second => Assert.Equal("zamówienia", second.Name));
        Assert.Equal(2, nameDiagnostics.Count);
        Assert.True(nameDiagnostics[1].NameMatches);
        Assert.False(nameDiagnostics[1].LabelMatches);
        Assert.Equal(160, payload.AsSpan().IndexOf(Encoding.UTF8.GetBytes("zamówienia")));
    }

    [Fact]
    public void DescriptorDiagnosticWindowPreservesOffsetsAndMasksOtherAscii()
    {
        var payload = new byte[180];
        Encoding.ASCII.GetBytes("SECRET").CopyTo(payload, 130);
        Encoding.ASCII.GetBytes("PAYLOAD").CopyTo(payload, 144);
        payload[142] = 0x07;

        var window = DdmCodec.FormatRedactedDescriptorWindow(payload, "PAYLOAD", 128, 152);

        Assert.Contains("130:.", window);
        Assert.Contains("142:\\x07", window);
        Assert.Contains("144:PAYLOAD", window);
        Assert.DoesNotContain("SECRET", window);
    }

    [Fact]
    public void ModernCursorDescriptorPrefersLabelAfterColumnName()
    {
        using var descriptor = new MemoryStream();
        descriptor.WriteByte(0xff); // SQLDHGRP is null.
        descriptor.Write([2, 0]); // Two result columns.
        descriptor.Write(new byte[16]);
        descriptor.Write(new byte[10]); // SQLARREXTENT and SQLAM 10 descriptor bytes.
        descriptor.WriteByte(0xff); // First SQLDAGRP has no SQLDOPTGRP.
        descriptor.Write(new byte[16]);
        descriptor.Write(new byte[10]); // SQLARREXTENT and SQLAM 10 descriptor bytes.
        descriptor.WriteByte(0); // SQLDOPTGRP is present.
        descriptor.Write([0, 0]); // SQLUNNAMED.
        WriteFdocaStringPair(descriptor, "HV00008"); // SQLNAME.
        WriteFdocaStringPair(descriptor, "PAYLOAD"); // SQLLABEL.
        WriteFdocaStringPair(descriptor, "COMMENT"); // SQLCOMMENTS must not replace SQLLABEL.
        descriptor.Write([0xff, 0xff]); // No SQLUDTGRP or SQLDXGRP.
        descriptor.WriteByte(0xff); // SQLAM 10 trailing optional extension marker.

        var parserOffsets = string.Empty;
        var columns = DdmCodec.ParseSqlCursorInfo(descriptor.ToArray(), "PAYLOAD",
            (_, _, _, _) => { }, value => parserOffsets = value);

        Assert.Equal(2, columns.Length);
        Assert.Equal("PAYLOAD", columns[1].Name);
        Assert.Contains("label=7", parserOffsets);
        Assert.Contains("selected=7", parserOffsets);
    }

    [Fact]
    public void ModernCursorDescriptorUsesSqlDxNameAndKeepsNameWhenExtendedNameIsAbsent()
    {
        static byte[] BuildDescriptor(bool includeSqlDxName, string label = "")
        {
            using var descriptor = new MemoryStream();
            descriptor.WriteByte(0xff);
            descriptor.Write([2, 0]);
            descriptor.Write(new byte[16]);
            descriptor.Write(new byte[10]);
            descriptor.WriteByte(0xff);
            var secondColumn = new byte[16];
            BinaryPrimitives.WriteUInt16LittleEndian(secondColumn.AsSpan(12), (ushort)SqlType.FakeUDT);
            descriptor.Write(secondColumn);
            descriptor.Write(new byte[10]);
            descriptor.WriteByte(0);
            descriptor.Write([0, 0]);
            WriteFdocaStringPair(descriptor, includeSqlDxName ? "" : "T");
            WriteFdocaStringPair(descriptor, label);
            WriteFdocaStringPair(descriptor, "");
            if (includeSqlDxName)
            {
                descriptor.WriteByte(0); // SQLUDTGRP present.
                descriptor.Write(new byte[4]); // SQLUDTXTYPE.
                WriteFdocaVcs(descriptor, "DB"); // SQLUDTRDBNAM.
                WriteFdocaStringPair(descriptor, "APP"); // SQLUDTSCHEMA.
                WriteFdocaStringPair(descriptor, "TYPE_NAME"); // SQLUDTNAME.
                WriteFdocaStringPair(descriptor, "MODULE"); // SQLUDTMODULE (SQLAM >= 10).
                descriptor.WriteByte(0); // SQLDXGRP present.
                descriptor.Write(new byte[12]); // SQLDX flags for SQLAM >= 9.
                WriteFdocaVcs(descriptor, "DB"); // SQLXRDBNAM.
                WriteFdocaStringPair(descriptor, "T"); // SQLXCORNAME.
                WriteFdocaStringPair(descriptor, ""); // SQLXBASENAME.
                WriteFdocaStringPair(descriptor, ""); // SQLXSCHEMA.
                WriteFdocaStringPair(descriptor, "PAYLOAD"); // SQLXNAME.
                WriteFdocaStringPair(descriptor, ""); // SQLXMODULE (SQLAM >= 10).
            }
            else descriptor.Write([0xff, 0xff]); // No UDT or extended group.
            descriptor.WriteByte(0xff); // SQLAM 10 trailing optional extension marker.
            return descriptor.ToArray();
        }

        var offsets = string.Empty;
        var withXName = DdmCodec.ParseSqlCursorInfo(BuildDescriptor(includeSqlDxName: true), "PAYLOAD",
            (_, _, _, _) => { }, value => offsets = value);
        var withLabel = DdmCodec.ParseSqlCursorInfo(BuildDescriptor(includeSqlDxName: true, label: "LABEL"));
        var withoutXName = DdmCodec.ParseSqlCursorInfo(BuildDescriptor(includeSqlDxName: false));

        Assert.True(withXName[1].Name == "PAYLOAD", offsets);
        Assert.Equal("LABEL", withLabel[1].Name);
        Assert.Contains("sqlXName=7", offsets);
        Assert.Contains("sqlam=10", offsets);
        Assert.Equal("T", withoutXName[1].Name);
    }

    [Fact]
    public void ProgressiveLobRequestsMatchObservedDrdaFieldsAndReplyMarkers()
    {
        var request = DdmCodec.PackGetNextChunk("SQLC0X0", "", 2, "TESTDB",
            0x0102030405060708, 0x1122334455667788, int.MaxValue);
        Assert.Equal((ushort)CodePoint.GETNXTCHK, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(2)));
        var parameters = DdmCodec.ParseParameters(request.AsSpan(4));
        Assert.Equal(64, parameters[(ushort)CodePoint.PKGNAMCSN].Length);
        Assert.Equal(0x0102030405060708UL, BinaryPrimitives.ReadUInt64BigEndian(parameters[(ushort)CodePoint.QRYINSID]));
        Assert.Equal(0x1122334455667788UL, BinaryPrimitives.ReadUInt64BigEndian(parameters[(ushort)CodePoint.GETNXTREF]));
        Assert.Equal((ulong)int.MaxValue, BinaryPrimitives.ReadUInt64BigEndian(parameters[(ushort)CodePoint.GETNXTLEN]));

        var header = new DssHeader(0, DssCodec.Object, false, false, false, 1);
        var intermediate = DdmCodec.ParseProgressiveChunkReply([
            new DssPacket(header, (ushort)CodePoint.EXTDTA, [0, 1, 2, 3]),
            new DssPacket(header, (ushort)CodePoint.ENDDTARM,
                JoinBytes(DdmCodec.PackUInt16(CodePoint.SVRCOD, 0), DdmCodec.PackString(CodePoint.RDBNAM, "TESTDB")))
        ]);
        Assert.Equal(new byte[] { 1, 2, 3 }, intermediate.Data);
        Assert.False(intermediate.EndOfReference);
        Assert.False(intermediate.IsLastChunk);

        var final = DdmCodec.ParseProgressiveChunkReply([
            new DssPacket(header, (ushort)CodePoint.EXTDTA, [0, 4, 5]),
            new DssPacket(header, (ushort)CodePoint.ENDDTARM,
                JoinBytes(DdmCodec.PackUInt16(CodePoint.SVRCOD, 0),
                    DdmCodec.PackBytes(CodePoint.ENDREF, [0xF1]), DdmCodec.PackBytes(CodePoint.LSTCHK, [0xF1])))
        ]);
        Assert.Equal(new byte[] { 4, 5 }, final.Data);
        Assert.True(final.EndOfReference);
        Assert.True(final.IsLastChunk);
        Assert.Throws<InvalidDataException>(() => DdmCodec.ParseProgressiveChunkReply([]));
    }

    [Fact]
    public void ProgressiveLobChunkParserRejectsMalformedRepliesAndAllowsAnEmptyFinalChunk()
    {
        var header = new DssHeader(0, DssCodec.Object, false, false, false, 1);
        DssPacket Packet(CodePoint codePoint, byte[] payload) => new(header, (ushort)codePoint, payload);
        var success = DdmCodec.PackUInt16(CodePoint.SVRCOD, 0);
        DssPacket End(params byte[][] fields) => Packet(CodePoint.ENDDTARM, JoinBytes(fields));

        void Reject(string expectedMessage, params DssPacket[] replies)
        {
            var error = Assert.Throws<InvalidDataException>(() => DdmCodec.ParseProgressiveChunkReply(replies));
            Assert.Contains(expectedMessage, error.Message);
        }

        Reject("did not contain ENDDTARM");
        Reject("invalid EXTDTA status byte", Packet(CodePoint.EXTDTA, []), End(success));
        Reject("invalid EXTDTA status byte", Packet(CodePoint.EXTDTA, [1, 0xaa]), End(success));
        Reject("omitted its two-byte SVRCOD", End());
        Reject("omitted its two-byte SVRCOD", End(DdmCodec.PackBytes(CodePoint.SVRCOD, [0])));
        Reject("returned SVRCOD 8", End(DdmCodec.PackUInt16(CodePoint.SVRCOD, 8)));
        Reject("multiple ENDDTARM", End(success), End(success));
        Reject("EXTDTA after ENDDTARM", End(success), Packet(CodePoint.EXTDTA, [0, 0x11]));
        Reject("ENDREF Boolean value", End(success, DdmCodec.PackBytes(CodePoint.ENDREF, [1])));
        Reject("LSTCHK Boolean value", End(success, DdmCodec.PackBytes(CodePoint.LSTCHK, [0xf2])));

        var emptyIntermediate = DdmCodec.ParseProgressiveChunkReply([
            Packet(CodePoint.EXTDTA, [0]), End(success)
        ]);
        Assert.Empty(emptyIntermediate.Data);
        Assert.False(emptyIntermediate.EndOfReference);
        Assert.False(emptyIntermediate.IsLastChunk);

        var emptyFinal = DdmCodec.ParseProgressiveChunkReply([
            Packet(CodePoint.EXTDTA, [0]), End(success,
                DdmCodec.PackBytes(CodePoint.ENDREF, [0xf1]), DdmCodec.PackBytes(CodePoint.LSTCHK, [0xf1]))
        ]);
        Assert.Empty(emptyFinal.Data);
        Assert.True(emptyFinal.EndOfReference);
        Assert.True(emptyFinal.IsLastChunk);
    }

    [Fact]
    public void QueryDataDecodesProgressiveLobLengthAndOpaqueReferenceSeparately()
    {
        var row = new List<byte> { 0xff, 0x00, 0x00, 0x03 };
        var length = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(length, 2_500_123);
        row.AddRange(length);
        row.AddRange([0xff, 0xff, 0xff, 0xff, 0xe0, 0xb3, 0x40, 0x79]);

        var decoded = Db2Session.DecodeRowsIncremental([new FieldDescriptor(0xc9, 0x80, 9)], row.ToArray());

        Assert.Equal(row.Count, decoded.Consumed);
        var reference = Assert.IsType<Db2ProgressiveLobReference>(Assert.Single(decoded.Rows)[0]);
        Assert.Equal(2_500_123UL, reference.Length);
        Assert.Equal(0xffff_ffff_e0b3_4079UL, reference.Reference);
        Assert.True(Db2Session.HasUnresolvedExternalLocator(
            [new FieldDescriptor(0xc9, 0x80, 9)], decoded.Rows));
    }

    [Fact]
    public void QueryDataRejectsInlineLobAboveMaterializationBudgetBeforeReadingItsBody()
    {
        var row = new List<byte> { 0xff, 0x00, 0x00, 0x01 };
        var length = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(length, 16 * 1024 * 1024 + 1);
        row.AddRange(length);

        var error = Assert.Throws<Db2ResourceLimitException>(() =>
            Db2Session.DecodeRowsIncremental([new FieldDescriptor(0xc9, 0x80, 9)], row.ToArray()));

        Assert.Contains("materialization limit", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void QueryRowsDecodeAndExposeWarningsFromFullSqlcaGroups()
    {
        var sqlca = new List<byte> { 0x00 }; // SQLCAGRP is present.
        sqlca.AddRange(Int32LittleEndian(0));
        sqlca.AddRange(Encoding.ASCII.GetBytes("01003"));
        sqlca.AddRange(Encoding.ASCII.GetBytes("SQLRI6B1")); // SQLERRPROC.
        sqlca.Add(0x00); // SQLCAXGRP is present.
        sqlca.AddRange(new byte[24]); // SQLERRD(1..6).
        sqlca.AddRange(Encoding.ASCII.GetBytes("W W        ")); // SQLWARN(0..A).
        sqlca.AddRange(LengthPrefixed("TESTDB            "));
        sqlca.AddRange(LengthPrefixed(""));
        sqlca.AddRange(LengthPrefixed(""));
        sqlca.Add(0xff); // SQLDIAGGRP is absent.
        sqlca.Add(0x00); // Row data is present.
        sqlca.Add(0x00); // Nullable INTEGER is not NULL.
        sqlca.AddRange(Int32LittleEndian(40));
        sqlca.AddRange([0xff, 0x00, 0x00]); // A normal row immediately follows.
        sqlca.AddRange(Int32LittleEndian(41));

        var decoded = Db2Session.DecodeRowsWithWarnings([new FieldDescriptor(0x03, 0, 4)], sqlca.ToArray());

        Assert.Equal(sqlca.Count, decoded.Consumed);
        Assert.Equal(new[] { 40, 41 }, decoded.Rows.Select(row => Assert.IsType<int>(Assert.Single(row.Values))));
        var warning = Assert.IsType<Db2Warning>(decoded.Rows[0].Warning);
        Assert.Equal(0, warning.SqlCode);
        Assert.Equal("01003", warning.SqlState);
        Assert.StartsWith("W W", warning.WarningFlags);
        Assert.Null(decoded.Rows[1].Warning);

        var (partialRows, partialConsumed) = Db2Session.DecodeRowsWithWarnings([new FieldDescriptor(0x03, 0, 4)], sqlca.Take(14).ToArray());
        Assert.Empty(partialRows);
        Assert.Equal(0, partialConsumed);

        var errorRow = new List<byte> { 0x00 };
        errorRow.AddRange(Int32LittleEndian(-802));
        errorRow.AddRange(Encoding.ASCII.GetBytes("22003"));
        errorRow.AddRange(Encoding.ASCII.GetBytes("SQLRI6B1"));
        errorRow.Add(0xff); // No SQLCAXGRP.
        errorRow.Add(0xff); // No SQLDIAGGRP.
        errorRow.Add(0xff); // No row data follows an error.
        var error = Assert.Throws<Db2SqlException>(() =>
            Db2Session.DecodeRowsWithWarnings([new FieldDescriptor(0x03, 0, 4)], errorRow.ToArray()));
        Assert.Equal(-802, error.SqlCode);
        Assert.Equal("22003", error.SqlState);
    }

    [Fact]
    public void UnsupportedQueryFieldTypeIsATypedUnsupportedFeatureError()
    {
        var error = Assert.Throws<Db2UnsupportedFeatureException>(() =>
            Db2Session.DecodeRowsIncremental([new FieldDescriptor(0x77, 0, 0)], [0xff, 0x00]));
        Assert.Equal(Db2ErrorKind.Unsupported, error.ErrorKind);
        Assert.Null(error.SqlCode);
        Assert.Null(error.SqlState);
        Assert.Contains("0x77", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlDiagGroupInAQueryRowIsATypedUnsupportedFeatureError()
    {
        var row = new List<byte> { 0x00 }; // SQLCAGRP is present.
        row.AddRange(Int32LittleEndian(0));
        row.AddRange(Encoding.ASCII.GetBytes("01003"));
        row.AddRange(Encoding.ASCII.GetBytes("SQLRI6B1")); // SQLERRPROC.
        row.Add(0xff); // SQLCAXGRP is absent.
        row.Add(0x00); // SQLDIAGGRP is present, which this driver does not decode.

        var error = Assert.Throws<Db2UnsupportedFeatureException>(() =>
            Db2Session.DecodeRowsWithWarnings([new FieldDescriptor(0x03, 0, 4)], row.ToArray()));
        Assert.Equal(Db2ErrorKind.Unsupported, error.ErrorKind);
        Assert.Contains("SQLDIAGGRP", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacySqlcirowLayoutIsATypedUnsupportedFeatureError()
    {
        var error = Assert.Throws<Db2UnsupportedFeatureException>(() =>
            DdmCodec.ParseSqlCursorInfo(new byte[0], 6, null, null));
        Assert.Equal(Db2ErrorKind.Unsupported, error.ErrorKind);
        Assert.Contains("SQLAM", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TemporalUtf8FieldsPreserveUnicodeTrimSemantics()
    {
        var encoded = Encoding.UTF8.GetBytes("\u00a02026-10-02\u00a0");
        var row = new byte[2 + encoded.Length];
        row[0] = 0xff;
        row[1] = 0x00;
        encoded.CopyTo(row, 2);

        var decoded = Db2Session.DecodeRowsIncremental([new FieldDescriptor(0x20, 0, checked((byte)encoded.Length))], row);

        Assert.Equal(row.Length, decoded.Consumed);
        Assert.Equal("2026-10-02", Assert.IsType<string>(Assert.Single(decoded.Rows)[0]));
    }

    private static byte[] Int32LittleEndian(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] LengthPrefixed(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var result = new byte[2 + bytes.Length];
        BinaryPrimitives.WriteUInt16BigEndian(result, checked((ushort)bytes.Length));
        bytes.CopyTo(result, 2);
        return result;
    }

    private static byte[] JoinBytes(params byte[][] values) => values.SelectMany(value => value).ToArray();

    [Fact]
    public void Cp500MatchesDb2ReferenceStringsAndRejectsUnmappedText()
    {
        foreach (var value in new[] { "pydrda", "go-db2", "TESTDB", "USER123", "localhost", "SELECT 1 FROM SYSIBM.SYSDUMMY1" })
            Assert.Equal(value, Ebcdic.DecodeCp500(Ebcdic.EncodeCp500(value)));
        Assert.Throws<EncoderFallbackException>(() => Ebcdic.EncodeCp500("😀"));
    }

    [Fact]
    public void SqlErrorDiagnosticsPreferUtf8AndFallBackToCp500()
    {
        var utf8 = DdmCodec.PackBytes(CodePoint.SRVDGN, Encoding.UTF8.GetBytes("SQLERRP:SQLRI2D0 SQLCODE:-952"));
        var utf8Error = Assert.IsType<Db2ServerErrorException>(Db2Session.ServerError(utf8));
        Assert.Contains("SQLCODE:-952", utf8Error.Message);
        Assert.Equal(-952, utf8Error.SqlCode);
        Assert.Null(utf8Error.SqlState);
        Assert.Equal(Db2ErrorKind.SqlError, utf8Error.ErrorKind);

        var cp500 = DdmCodec.PackBytes(CodePoint.SRVDGN, Ebcdic.EncodeCp500("SQLCODE:-952 SQLSTATE:57014"));
        var cp500Error = Assert.IsType<Db2ServerErrorException>(Db2Session.ServerError(cp500));
        Assert.Contains("SQLCODE:-952", cp500Error.Message);
        Assert.Equal(-952, cp500Error.SqlCode);
        Assert.Equal("57014", cp500Error.SqlState);
        Assert.Equal(Db2ErrorKind.SqlError, cp500Error.ErrorKind);
    }

    [Fact]
    public void SqlDardSqlcardErrorsExposeStableSqlFields()
    {
        var sqlCard = new byte[54];
        BinaryPrimitives.WriteInt32LittleEndian(sqlCard.AsSpan(1), -911);
        Encoding.ASCII.GetBytes("40001").CopyTo(sqlCard, 5);

        var error = Assert.Throws<Db2SqlException>(() => DdmCodec.ParseSqlDard(sqlCard));

        Assert.Equal(Db2ErrorKind.SqlError, error.ErrorKind);
        Assert.Equal(-911, error.SqlCode);
        Assert.Equal("40001", error.SqlState);
        Assert.Contains("DB2 SQLCODE=-911 SQLSTATE=40001", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DdmHandshakeBuildersUseReferenceCodePointsAndCp500()
    {
        var excsat = DdmCodec.PackExcsat("JustyBase.Db2", "myhost", "JustyBase.Db2-v0.1", "JustyBase.Db2");
        Assert.Equal(excsat.Length, BinaryPrimitives.ReadUInt16BigEndian(excsat));
        Assert.Equal((ushort)CodePoint.EXCSAT, BinaryPrimitives.ReadUInt16BigEndian(excsat.AsSpan(2)));
        var parameters = DdmCodec.ParseParameters(excsat.AsSpan(4));
        Assert.Equal("JustyBase.Db2", Ebcdic.DecodeCp500(parameters[(ushort)CodePoint.EXTNAM]));
        Assert.Equal("myhost", Ebcdic.DecodeCp500(parameters[(ushort)CodePoint.SRVNAM]));
        Assert.Equal(DdmCodec.DefaultManagerLevels().Length * 4, parameters[(ushort)CodePoint.MGRLVLLS].Length);

        var accsec = DdmCodec.ParseParameters(DdmCodec.PackAccsec("TESTDB", 3, [1, 2]).AsSpan(4));
        Assert.Equal((ushort)3, BinaryPrimitives.ReadUInt16BigEndian(accsec[(ushort)CodePoint.SECMEC]));
        Assert.Equal("TESTDB", Ebcdic.DecodeCp500(accsec[(ushort)CodePoint.RDBNAM]));
        Assert.Equal([1, 2], accsec[(ushort)CodePoint.SECTKN]);

        var usrencpwdAccsec = DdmCodec.ParseParameters(DdmCodec.PackAccsec("TESTDB", 7, [3, 4], 1).AsSpan(4));
        Assert.Equal((ushort)7, BinaryPrimitives.ReadUInt16BigEndian(usrencpwdAccsec[(ushort)CodePoint.SECMEC]));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16BigEndian(usrencpwdAccsec[(ushort)CodePoint.ENCALG]));
        Assert.DoesNotContain((ushort)CodePoint.ENCALG, accsec.Keys);

        byte[] ciphertext = [0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80];
        var usrencpwd = DdmCodec.ParseParameters(DdmCodec.PackSecchkUsrencpwd("TESTDB", "db2inst1", ciphertext).AsSpan(4));
        Assert.Equal((ushort)7, BinaryPrimitives.ReadUInt16BigEndian(usrencpwd[(ushort)CodePoint.SECMEC]));
        Assert.Equal("db2inst1", Ebcdic.DecodeCp500(usrencpwd[(ushort)CodePoint.USRID]));
        // Db2 LUW requires the encrypted password in SECTKN; PASSWORD is rejected (SECCHKCD=16).
        Assert.Equal(ciphertext, usrencpwd[(ushort)CodePoint.SECTKN]);
        Assert.DoesNotContain((ushort)CodePoint.PASSWORD, usrencpwd.Keys);
        Assert.DoesNotContain((ushort)CodePoint.ENCALG, usrencpwd.Keys);

        var userOnly = DdmCodec.ParseParameters(DdmCodec.PackSecchk(
            4, [], "TESTDB", "db2inst1", Encoding.UTF8.GetBytes("must-not-be-sent"), false, DdmStringEncoding.Cp500).AsSpan(4));
        Assert.Equal((ushort)4, BinaryPrimitives.ReadUInt16BigEndian(userOnly[(ushort)CodePoint.SECMEC]));
        Assert.Equal("db2inst1", Ebcdic.DecodeCp500(userOnly[(ushort)CodePoint.USRID]));
        Assert.DoesNotContain((ushort)CodePoint.PASSWORD, userOnly.Keys);
        Assert.Throws<ArgumentOutOfRangeException>(() => DdmCodec.PackSecchk(
            9, [], "TESTDB", "db2inst1", Encoding.UTF8.GetBytes("must-not-be-sent"), false, DdmStringEncoding.Cp500));
    }

    [Fact]
    public void PackageStatementAndQueryObjectsHaveValidLengths()
    {
        var package = DdmCodec.PackPackageNameCsn("TESTDB", "SQLC0X0", "", 0);
        Assert.Equal(package.Length, BinaryPrimitives.ReadUInt16BigEndian(package));
        Assert.Equal((ushort)CodePoint.PKGNAMCSN, BinaryPrimitives.ReadUInt16BigEndian(package.AsSpan(2)));
        Assert.Equal(4 + 18 + 18 + 18 + 8 + 2, package.Length);
        var sql = DdmCodec.PackSqlStatement("SELECT 1");
        Assert.Equal((ushort)CodePoint.SQLSTT, BinaryPrimitives.ReadUInt16BigEndian(sql.AsSpan(2)));
        Assert.Equal((ushort)CodePoint.RDBCMM, BinaryPrimitives.ReadUInt16BigEndian(DdmCodec.PackCommit().AsSpan(2)));
        Assert.Equal((ushort)CodePoint.RDBRLLBCK, BinaryPrimitives.ReadUInt16BigEndian(DdmCodec.PackRollback().AsSpan(2)));
    }

    [Fact]
    public void ParameterizedResultQueryRequestsProgressiveDataFormatForLobReads()
    {
        var request = DdmCodec.PackOpenQueryWithParams("SQLC0X0", "", 0, "TESTDB", 65535);

        Assert.Equal((ushort)CodePoint.OPNQRY, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(2)));
        var parameters = DdmCodec.ParseParameters(request.AsSpan(4));
        Assert.Contains((ushort)CodePoint.MAXBLKEXT, parameters.Keys);
        Assert.Contains((ushort)CodePoint.QRYCLSIMP, parameters.Keys);
        Assert.Equal(new byte[] { 0xF1 }, parameters[(ushort)CodePoint.DYNDTAFMT]);
    }

    [Fact]
    public void ExecuteStatementPacksResultSetOptionsInDrdaOrder()
    {
        var command = DdmCodec.PackExecuteSqlStatement("SQLC0X0", "", 0, "TESTDB", ushort.MaxValue);
        var offset = 4 + BinaryPrimitives.ReadUInt16BigEndian(command.AsSpan(4));
        Assert.Equal((ushort)CodePoint.RDBCMTOK, BinaryPrimitives.ReadUInt16BigEndian(command.AsSpan(offset + 2)));
        offset += BinaryPrimitives.ReadUInt16BigEndian(command.AsSpan(offset));
        Assert.Equal((ushort)CodePoint.OUTEXP, BinaryPrimitives.ReadUInt16BigEndian(command.AsSpan(offset + 2)));
        offset += BinaryPrimitives.ReadUInt16BigEndian(command.AsSpan(offset));
        Assert.Equal((ushort)CodePoint.QRYBLKSZ, BinaryPrimitives.ReadUInt16BigEndian(command.AsSpan(offset + 2)));
        Assert.Equal(32767U, BinaryPrimitives.ReadUInt32BigEndian(command.AsSpan(offset + 4)));
        offset += BinaryPrimitives.ReadUInt16BigEndian(command.AsSpan(offset));
        Assert.Equal((ushort)CodePoint.MAXBLKEXT, BinaryPrimitives.ReadUInt16BigEndian(command.AsSpan(offset + 2)));
        offset += BinaryPrimitives.ReadUInt16BigEndian(command.AsSpan(offset));
        Assert.Equal((ushort)CodePoint.MAXRSLCNT, BinaryPrimitives.ReadUInt16BigEndian(command.AsSpan(offset + 2)));
        Assert.Equal(ushort.MaxValue, BinaryPrimitives.ReadUInt16BigEndian(command.AsSpan(offset + 4)));
        offset += BinaryPrimitives.ReadUInt16BigEndian(command.AsSpan(offset));
        Assert.Equal((ushort)CodePoint.RSLSETFLG, BinaryPrimitives.ReadUInt16BigEndian(command.AsSpan(offset + 2)));
        Assert.Equal((byte)0x04, command[offset + 4]);
        offset += BinaryPrimitives.ReadUInt16BigEndian(command.AsSpan(offset));
        Assert.Equal((ushort)CodePoint.QRYROWSET, BinaryPrimitives.ReadUInt16BigEndian(command.AsSpan(offset + 2)));
        Assert.Equal(64U, BinaryPrimitives.ReadUInt32BigEndian(command.AsSpan(offset + 4)));

        var standardRows = DdmCodec.PackExecuteSqlStatement("SQLC0X0", "", 0, "TESTDB", ushort.MaxValue, queryRowsetSize: null);
        Assert.Equal(offset, standardRows.Length);
    }

    [Fact]
    public void ProcedureResultSetMetadataParsesColumnAndCursorSummaries()
    {
        using var cursorInfo = new MemoryStream();
        cursorInfo.WriteByte(0xff); // SQLDHGRP is null.
        cursorInfo.Write([1, 0]); // SQLNUMROW: one result column.
        cursorInfo.Write([10, 0, 0, 0]); // precision and scale.
        cursorInfo.WriteByte(4); cursorInfo.Write(new byte[7]); // SQLLENGTH: four bytes.
        cursorInfo.Write([0xf1, 1, 0, 0]); // nullable INTEGER and CCSID 0.
        cursorInfo.WriteByte(0); // SQLDOPTGRP is present.
        cursorInfo.Write([0, 0]); // SQLUNNAMED.
        WriteFdocaStringPair(cursorInfo, "VALUE");
        WriteFdocaStringPair(cursorInfo, "VALUE");
        WriteFdocaStringPair(cursorInfo, "");
        cursorInfo.WriteByte(0xff); // no UDT group.
        cursorInfo.WriteByte(0xff); // no extended group.

        var columns = DdmCodec.ParseSqlCursorInfo(cursorInfo.ToArray(), 7, null, null);
        var column = Assert.Single(columns);
        Assert.Equal("VALUE", column.Name);
        Assert.Equal(SqlType.NInteger, column.SqlType);
        Assert.True(column.Nullable);
        Assert.Equal(4UL, column.Length);
        Assert.Equal((ushort)10, column.Precision);

        using var cursorInfoWithHeader = new MemoryStream();
        cursorInfoWithHeader.WriteByte(0); // SQLDHGRP is present.
        cursorInfoWithHeader.Write(new byte[12]); // Cursor flags through SQLDKEYTYPE.
        cursorInfoWithHeader.Write(new byte[12]); // Header VCS/VCM fields use four-byte lengths.
        cursorInfoWithHeader.Write([1, 0]); // SQLNUMROW.
        cursorInfoWithHeader.Write([10, 0, 0, 0]); // SQLPRECISION and SQLSCALE.
        cursorInfoWithHeader.Write([4, 0, 0, 0, 0, 0, 0, 0]); // SQLLENGTH.
        cursorInfoWithHeader.Write([0xf1, 1, 0, 0]); // Nullable INTEGER and CCSID 0.
        cursorInfoWithHeader.WriteByte(0); // SQLDOPTGRP is present.
        cursorInfoWithHeader.Write([0, 0]); // SQLUNNAMED.
        WriteFdocaStringPair(cursorInfoWithHeader, "VALUE");
        WriteFdocaStringPair(cursorInfoWithHeader, "VALUE");
        WriteFdocaStringPair(cursorInfoWithHeader, "");
        cursorInfoWithHeader.WriteByte(0xff); // No UDT group.
        cursorInfoWithHeader.WriteByte(0xff); // No extended group.

        var headerColumns = DdmCodec.ParseSqlCursorInfo(cursorInfoWithHeader.ToArray(), 7, null, null);
        var headerColumn = Assert.Single(headerColumns);
        Assert.Equal("VALUE", headerColumn.Name);
        Assert.Equal(SqlType.NInteger, headerColumn.SqlType);
        Assert.Equal(4UL, headerColumn.Length);

        using var summary = new MemoryStream();
        summary.Write([2, 0]);
        summary.Write(new byte[24]);
        Assert.Equal(2, DdmCodec.ParseSqlResultSetCount(summary.ToArray()));
        Assert.Throws<InvalidDataException>(() => DdmCodec.ParseSqlResultSetCount([2, 0]));

        var packageSections = DdmCodec.PackObject(CodePoint.PKGSNLST,
            DdmCodec.PackPackageNameCsn("TESTDB", "SQLC0X0", "", 1)
                .Concat(DdmCodec.PackPackageNameCsn("TESTDB", "SQLC0X0", "", 2)).ToArray());
        var message = DdmCodec.PackObject(CodePoint.RSLSETRM, packageSections);
        var (_, messageBody) = DssCodec.ReadDdmObject(message);
        var returnedSections = DdmCodec.ParseResultSetSections(messageBody);
        Assert.Equal(new ushort[] { 1, 2 }, returnedSections.Select(section => section.SectionNumber));
        Assert.Equal("SQLC0X0", Encoding.UTF8.GetString(returnedSections[0].PackageName).Trim());
        var standardContinue = DdmCodec.PackContinueQuery(returnedSections[0], 32767, 19);
        Assert.Equal((ushort)CodePoint.CNTQRY, BinaryPrimitives.ReadUInt16BigEndian(standardContinue.AsSpan(2)));
        Assert.Equal(returnedSections[0].SectionNumber, BinaryPrimitives.ReadUInt16BigEndian(standardContinue.AsSpan(70)));
        Assert.Equal((ushort)CodePoint.QRYBLKSZ, BinaryPrimitives.ReadUInt16BigEndian(standardContinue.AsSpan(74)));
        Assert.Equal(32767U, BinaryPrimitives.ReadUInt32BigEndian(standardContinue.AsSpan(76)));
        Assert.Equal((ushort)CodePoint.QRYINSID, BinaryPrimitives.ReadUInt16BigEndian(standardContinue.AsSpan(82)));
        Assert.Equal(19UL, BinaryPrimitives.ReadUInt64BigEndian(standardContinue.AsSpan(84)));
        Assert.Equal(92, standardContinue.Length);

        var continueQuery = DdmCodec.PackContinueQuery(returnedSections[0], 32767, 19, 64);
        Assert.Equal(returnedSections[0].SectionNumber, BinaryPrimitives.ReadUInt16BigEndian(continueQuery.AsSpan(70)));
        Assert.Equal((ushort)CodePoint.CNTQRY, BinaryPrimitives.ReadUInt16BigEndian(continueQuery.AsSpan(2)));
        Assert.Equal((ushort)CodePoint.QRYBLKSZ, BinaryPrimitives.ReadUInt16BigEndian(continueQuery.AsSpan(74)));
        Assert.Equal(32767U, BinaryPrimitives.ReadUInt32BigEndian(continueQuery.AsSpan(76)));
        Assert.Equal((ushort)CodePoint.QRYINSID, BinaryPrimitives.ReadUInt16BigEndian(continueQuery.AsSpan(88)));
        Assert.Equal(19UL, BinaryPrimitives.ReadUInt64BigEndian(continueQuery.AsSpan(90)));
        Assert.Equal((ushort)CodePoint.QRYROWSET, BinaryPrimitives.ReadUInt16BigEndian(continueQuery.AsSpan(100)));
        Assert.Equal(64U, BinaryPrimitives.ReadUInt32BigEndian(continueQuery.AsSpan(102)));
        Assert.Equal(106, continueQuery.Length);

        var returnAll = DdmCodec.PackContinueQuery(returnedSections[0], 32767, 19, 64, returnAllData: true);
        Assert.Equal((ushort)CodePoint.RTNEXTDTA, BinaryPrimitives.ReadUInt16BigEndian(returnAll.AsSpan(108)));
        Assert.Equal((byte)0x02, returnAll[110]);
        Assert.Equal(111, returnAll.Length);

        var procedureFetch = DdmCodec.PackContinueProcedureResultSet(returnedSections[0], 32_767, 19);
        Assert.Equal((ushort)CodePoint.CNTQRY, BinaryPrimitives.ReadUInt16BigEndian(procedureFetch.AsSpan(2)));
        Assert.Equal((ushort)CodePoint.PKGNAMCSN, BinaryPrimitives.ReadUInt16BigEndian(procedureFetch.AsSpan(6)));
        Assert.Equal((ushort)CodePoint.QRYBLKSZ, BinaryPrimitives.ReadUInt16BigEndian(procedureFetch.AsSpan(74)));
        Assert.Equal(32_767U, BinaryPrimitives.ReadUInt32BigEndian(procedureFetch.AsSpan(76)));
        Assert.Equal((ushort)CodePoint.QRYINSID, BinaryPrimitives.ReadUInt16BigEndian(procedureFetch.AsSpan(82)));
        Assert.Equal(19UL, BinaryPrimitives.ReadUInt64BigEndian(procedureFetch.AsSpan(84)));
        Assert.Equal((ushort)CodePoint.RTNEXTDTA, BinaryPrimitives.ReadUInt16BigEndian(procedureFetch.AsSpan(94)));
        Assert.Equal((byte)0x02, procedureFetch[96]);
        Assert.Equal((ushort)CodePoint.FREPRVREF, BinaryPrimitives.ReadUInt16BigEndian(procedureFetch.AsSpan(99)));
        Assert.Equal((byte)0xf0, procedureFetch[101]);
        Assert.Equal(102, procedureFetch.Length);

        var procedureClose = DdmCodec.PackCloseProcedureResultSet(returnedSections[0], 19);
        Assert.Equal((ushort)CodePoint.CLSQRY, BinaryPrimitives.ReadUInt16BigEndian(procedureClose.AsSpan(2)));
        Assert.Equal((ushort)CodePoint.QRYINSID, BinaryPrimitives.ReadUInt16BigEndian(procedureClose.AsSpan(74)));
        Assert.Equal(19UL, BinaryPrimitives.ReadUInt64BigEndian(procedureClose.AsSpan(76)));
        Assert.Equal(84, procedureClose.Length);
    }

    [Fact]
    public void ProcedureCursorUsesColumnLabelWhenSqlNameIsEmpty()
    {
        using var cursorInfo = new MemoryStream();
        cursorInfo.WriteByte(0xff); // SQLDHGRP is null.
        cursorInfo.Write([1, 0]); // One result column.
        cursorInfo.Write([10, 0, 0, 0]); // Precision and scale.
        cursorInfo.WriteByte(4);
        cursorInfo.Write(new byte[7]); // SQLLENGTH: four bytes.
        cursorInfo.Write([0xf1, 1, 0, 0]); // Nullable INTEGER and CCSID 0.
        cursorInfo.WriteByte(0); // SQLDOPTGRP is present.
        cursorInfo.Write([0, 0]); // SQLUNNAMED.
        WriteFdocaStringPair(cursorInfo, "");
        WriteFdocaStringPair(cursorInfo, "PAYLOAD");
        WriteFdocaStringPair(cursorInfo, "");
        cursorInfo.WriteByte(0xff); // No UDT group.
        cursorInfo.WriteByte(0xff); // No extended group.

        var column = Assert.Single(DdmCodec.ParseSqlCursorInfo(cursorInfo.ToArray(), 7, null, null));
        Assert.Equal("PAYLOAD", column.Name);
    }

    [Fact]
    public void ProcedureDescriptorFillsMissingCursorNamesByOrdinalAndPreservesCursorTypes()
    {
        Db2ColumnMetadata[] cursorColumns =
        [
            new("N", SqlType.NSmall, 2, 5, 0, false),
            new("", SqlType.NBlob, 1_048_576, 0, 0, true),
        ];
        Db2ColumnMetadata[] describedColumns =
        [
            new("DESCRIBED_N", SqlType.NInteger, 4, 10, 0, true),
            new("PAYLOAD", SqlType.NVarBinary, 32, 0, 0, false),
        ];

        var merged = DdmCodec.MergeCursorColumnNames(cursorColumns, describedColumns);

        Assert.Equal("N", merged[0].Name);
        Assert.Equal(cursorColumns[0].SqlType, merged[0].SqlType);
        Assert.Equal(cursorColumns[0].Length, merged[0].Length);
        Assert.Equal(cursorColumns[0].Precision, merged[0].Precision);
        Assert.Equal(cursorColumns[0].Nullable, merged[0].Nullable);
        Assert.Equal("PAYLOAD", merged[1].Name);
        Assert.Equal(cursorColumns[1].SqlType, merged[1].SqlType);
        Assert.Equal(cursorColumns[1].Length, merged[1].Length);
        Assert.Equal(cursorColumns[1].Nullable, merged[1].Nullable);
    }

    [Theory]
    [InlineData(19UL, (ushort)0)]
    [InlineData(23UL, (ushort)3)]
    [InlineData(26UL, (ushort)6)]
    [InlineData(32UL, (ushort)12)]
    public void TimestampScaleIsRecoveredFromSqlLengthForQueryAndProcedureMetadata(ulong length, ushort expectedScale)
    {
        static byte[] Descriptor(ulong timestampLength)
        {
            using var stream = new MemoryStream();
            stream.Write([0xff, 0xff]); // No SQLDHGRP or SQLDOPTGRP.
            stream.Write([1, 0]); // One SQLVAR, little-endian SQLNUMROW.
            Span<byte> sqlvar = stackalloc byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(sqlvar[4..12], timestampLength);
            BinaryPrimitives.WriteUInt16LittleEndian(sqlvar[12..14], (ushort)SqlType.NTimestamp);
            stream.Write(sqlvar);
            return stream.ToArray();
        }

        var queryColumn = Assert.Single(DdmCodec.ParseSqlDard(Descriptor(length)));
        Assert.Equal(expectedScale, queryColumn.Scale);

        using var cursorInfo = new MemoryStream();
        cursorInfo.WriteByte(0xff); // No SQLDHGRP.
        cursorInfo.Write([1, 0]); // One result column.
        Span<byte> cursorColumn = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(cursorColumn[4..12], length);
        BinaryPrimitives.WriteUInt16LittleEndian(cursorColumn[12..14], (ushort)SqlType.NTimestamp);
        cursorInfo.Write(cursorColumn);
        cursorInfo.WriteByte(0xff); // No SQLDOPTGRP.
        var procedureColumn = Assert.Single(DdmCodec.ParseSqlCursorInfo(cursorInfo.ToArray(), 7, null, null));
        Assert.Equal(expectedScale, procedureColumn.Scale);
    }

    [Fact]
    public void EmptyLobLocatorDoesNotConsumeTheNextXmlExternalValue()
    {
        static byte[] Locator(ulong length)
        {
            var result = new byte[9];
            result[0] = 0x02;
            BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(1), length);
            return result;
        }
        static byte[] External(params byte[] value) => value;

        FieldDescriptor[] fields =
        [
            new(0xc9, 0x80, 9), new(0xcf, 0x80, 9), new(0xcd, 0x80, 9), new(0xc7, 0x80, 9),
        ];
        IList<object?[]> rows = new List<object?[]>
        {
            new object?[] { Locator(2), Locator(1), Locator(1), Locator(ulong.MaxValue) },
            new object?[] { null, Locator(0), null, Locator(ulong.MaxValue) },
        };
        byte[][] values =
        [
            External(0, 0xaa), External([0, (byte)'C']),
            External([0, .. Encoding.BigEndianUnicode.GetBytes("D")]), External([0, .. Encoding.UTF8.GetBytes("<x/>")]),
            External([0, .. Encoding.UTF8.GetBytes("<empty/>")]),
        ];

        Assert.Equal(values.Length, Db2Session.StitchExtdta(fields, rows, values));
        // 0xc9 is the nullable BLOB descriptor, so its external value carries
        // the 0x00 nullable indicator and the single data byte remains 0xaa.
        Assert.Equal(new byte[] { 0xaa }, Assert.IsType<byte[]>(rows[0][0]));
        Assert.Equal("C", Assert.IsType<string>(rows[0][1]));
        Assert.Equal("D", Assert.IsType<string>(rows[0][2]));
        Assert.Equal("<x/>", Assert.IsType<string>(rows[0][3]));
        Assert.Null(rows[1][0]);
        Assert.Equal(string.Empty, Assert.IsType<string>(rows[1][1]));
        Assert.Null(rows[1][2]);
        Assert.Equal("<empty/>", Assert.IsType<string>(rows[1][3]));
        Assert.False(Db2Session.HasUnresolvedExternalLocator(fields, rows));

        IList<object?[]> outputParameter = new List<object?[]> { new object?[] { Locator(1), null, null, null } };
        Assert.Equal(1, Db2Session.StitchExtdta(fields, outputParameter, [[0, 0xaa]], externalValuesHaveNullIndicator: true));
        Assert.Equal(new byte[] { 0xaa }, Assert.IsType<byte[]>(outputParameter[0][0]));
    }

    [Fact]
    public void ExternalLobAboveMaterializationBudgetIsRejected()
    {
        var locator = new byte[9];
        locator[0] = 0x02;
        BinaryPrimitives.WriteUInt64BigEndian(locator.AsSpan(1), 16 * 1024 * 1024 + 1);
        IList<object?[]> rows = new List<object?[]> { new object?[] { locator } };

        var error = Assert.Throws<Db2ResourceLimitException>(() =>
            Db2Session.StitchExtdta(
                [new FieldDescriptor(0xc8, 0x80, 9)],
                rows,
                [new byte[16 * 1024 * 1024 + 1]]));

        Assert.Contains("materialization limit", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingExternalLobPayloadLeavesAnUnresolvedLocator()
    {
        static byte[] Locator(ulong length)
        {
            var result = new byte[9];
            result[0] = 0x02;
            BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(1), length);
            return result;
        }

        FieldDescriptor[] fields = [new(0xc9, 0x80, 9), new(0xcf, 0x80, 9)];
        IList<object?[]> complete = new List<object?[]> { new object?[] { Locator(1), Locator(1) } };
        Assert.Equal(2, Db2Session.StitchExtdta(fields, complete, [[0xaa], [(byte)'C']]));
        Assert.False(Db2Session.HasUnresolvedExternalLocator(fields, complete));

        IList<object?[]> missing = new List<object?[]> { new object?[] { Locator(1), Locator(1) } };
        Assert.Equal(1, Db2Session.StitchExtdta(fields, missing, [[0xaa]]));
        Assert.True(Db2Session.HasUnresolvedExternalLocator(fields, missing));

        IList<object?[]> emptyLocator = new List<object?[]> { new object?[] { Locator(0), null } };
        Assert.Equal(0, Db2Session.StitchExtdta(fields, emptyLocator, []));
        Assert.False(Db2Session.HasUnresolvedExternalLocator(fields, emptyLocator));

        IList<object?[]> nullCell = new List<object?[]> { new object?[] { null, Locator(1) } };
        Assert.Equal(1, Db2Session.StitchExtdta(fields, nullCell, [[(byte)'C']]));
        Assert.False(Db2Session.HasUnresolvedExternalLocator(fields, nullCell));
    }

    [Fact]
    public void NullableLobDescriptorsStripTheIndicatorWhileNotNullOnesPreserveLeadingZero()
    {
        static byte[] Locator(ulong length)
        {
            var result = new byte[9];
            result[0] = 0x02;
            BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(1), length);
            return result;
        }

        FieldDescriptor[] notNullBlob = [new(0xc8, 0x80, 9)];
        IList<object?[]> blobNotNull = new List<object?[]> { new object?[] { Locator(3) } };
        Assert.Equal(1, Db2Session.StitchExtdta(notNullBlob, blobNotNull, [[0, 1, 2]]));
        Assert.Equal(new byte[] { 0, 1, 2 }, Assert.IsType<byte[]>(blobNotNull[0][0]));

        FieldDescriptor[] nullableBlob = [new(0xc9, 0x80, 9)];
        IList<object?[]> blobNullable = new List<object?[]> { new object?[] { Locator(3) } };
        Assert.Equal(1, Db2Session.StitchExtdta(nullableBlob, blobNullable, [[0, 1, 2]]));
        Assert.Equal(new byte[] { 1, 2 }, Assert.IsType<byte[]>(blobNullable[0][0]));

        FieldDescriptor[] notNullClob = [new(0xce, 0x80, 9)];
        IList<object?[]> clobNotNull = new List<object?[]> { new object?[] { Locator(2) } };
        Assert.Equal(1, Db2Session.StitchExtdta(notNullClob, clobNotNull, [[0, (byte)'a']]));
        Assert.Equal("\u0000a", Assert.IsType<string>(clobNotNull[0][0]));

        FieldDescriptor[] nullableClob = [new(0xcf, 0x80, 9)];
        IList<object?[]> clobNullable = new List<object?[]> { new object?[] { Locator(2) } };
        Assert.Equal(1, Db2Session.StitchExtdta(nullableClob, clobNullable, [[0, (byte)'a']]));
        Assert.Equal("a", Assert.IsType<string>(clobNullable[0][0]));

        FieldDescriptor[] outputNotNullBlob = [new(0xc8, 0x80, 9)];
        IList<object?[]> outputValue = new List<object?[]> { new object?[] { Locator(3) } };
        Assert.Equal(1, Db2Session.StitchExtdta(outputNotNullBlob, outputValue, [[0, 1, 2]], externalValuesHaveNullIndicator: true));
        Assert.Equal(new byte[] { 1, 2 }, Assert.IsType<byte[]>(outputValue[0][0]));
    }

    private static void WriteFdocaStringPair(Stream output, string value)
    {
        output.Write([0, 0]); // VCM is empty.
        var bytes = Encoding.UTF8.GetBytes(value);
        output.WriteByte((byte)(bytes.Length >> 8));
        output.WriteByte((byte)bytes.Length);
        output.Write(bytes);
    }

    private static void WriteFdocaVcs(Stream output, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)bytes.Length));
        output.Write(length);
        output.Write(bytes);
    }

    [Fact]
    public async Task DssRoundTripsRequestAndSegmentedExtendedObject()
    {
        var small = DdmCodec.PackObject(CodePoint.EXCSAT, [0xde, 0xad, 0xbe, 0xef]);
        var request = DssCodec.EncodeRequest(small, 1, false, true);
        Assert.Equal((ushort)2, request.NextCorrelationId);
        var packet = await DssCodec.ReadAsync(new MemoryStream(request.Bytes));
        Assert.Equal(DssCodec.Request, packet.Header.Type);
        Assert.Equal((ushort)1, packet.Header.CorrelationId);
        Assert.Equal((ushort)CodePoint.EXCSAT, packet.CodePoint);
        Assert.Equal([0xde, 0xad, 0xbe, 0xef], packet.Payload);

        var data = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("abcdefghij", 20_000)));
        var extended = new byte[8 + data.Length];
        BinaryPrimitives.WriteUInt16BigEndian(extended, 0x8008);
        BinaryPrimitives.WriteUInt16BigEndian(extended.AsSpan(2), (ushort)CodePoint.EXTDTA);
        BinaryPrimitives.WriteUInt32BigEndian(extended.AsSpan(4), (uint)data.Length);
        data.CopyTo(extended, 8);
        var response = DssCodec.EncodeReply(extended, true, 7);
        var largePacket = await DssCodec.ReadAsync(new MemoryStream(response));
        Assert.True(largePacket.Header.Chained);
        Assert.Equal((ushort)7, largePacket.Header.CorrelationId);
        Assert.Equal((ushort)CodePoint.EXTDTA, largePacket.CodePoint);
        Assert.Equal(data, largePacket.Payload);
    }

    [Fact]
    public async Task DssRejectsInvalidMagicAndFrameLengths()
    {
        await Assert.ThrowsAsync<InvalidDataException>(async () => await DssCodec.ReadAsync(new MemoryStream([0, 10, 0xaa, 1, 0, 1])));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await DssCodec.ReadAsync(new MemoryStream([0, 4, 0xd0, 1, 0, 1])));
        Assert.Throws<InvalidDataException>(() => DssCodec.ReadDdmObject(new byte[] { 0, 2, 0x24, 0x12 }));
    }

    [Fact]
    public void SqlcardAndGroupedQueryDescriptorsDecode()
    {
        var card = new byte[54];
        BinaryPrimitives.WriteInt32LittleEndian(card.AsSpan(1), 0);
        System.Text.Encoding.ASCII.GetBytes("00000").CopyTo(card, 5);
        BinaryPrimitives.WriteInt32LittleEndian(card.AsSpan(27), 12);
        Assert.Equal(12, DdmCodec.ParseSqlCard(card).RowsAffected);

        var warningCard = new byte[54];
        BinaryPrimitives.WriteInt32LittleEndian(warningCard.AsSpan(1), 0);
        Encoding.ASCII.GetBytes("01003").CopyTo(warningCard, 5);
        warningCard[18] = 0; // SQLCAXGRP is present.
        Encoding.ASCII.GetBytes("W W        ").CopyTo(warningCard, 43);
        var parsedWarning = DdmCodec.ParseSqlCard(warningCard);
        Assert.Equal("W W        ", parsedWarning.WarningFlags);
        Assert.Equal("01003", Assert.IsType<Db2Warning>(parsedWarning.Warning).SqlState);

        var descriptors = DdmCodec.ParseQueryDescriptor([9, 0x76, 0xd0, 0x80, 1, 2, 0x81, 3, 4, 6, 0x76, 0xd0, 0x82, 5, 6]);
        Assert.Equal(new byte[] { 0x80, 0x81, 0x82 }, descriptors.Select(field => field.Type));
        Assert.Equal((byte)5, descriptors[2].Precision);
        Assert.Equal((byte)6, descriptors[2].Scale);
    }

    [Fact]
    public void QueryDescriptorRejectsMalformedGroups()
    {
        Assert.Throws<InvalidDataException>(() => DdmCodec.ParseQueryDescriptor([1, 2]));
        Assert.Throws<InvalidDataException>(() => DdmCodec.ParseQueryDescriptor([2, 0x76, 0xd0]));
        Assert.Throws<InvalidDataException>(() => DdmCodec.ParseQueryDescriptor([9, 0x76, 0xd0, 1, 2, 3]));
        Assert.Throws<InvalidDataException>(() => DdmCodec.ParseQueryDescriptor([7, 0x76, 0xd0, 1, 2, 3, 4]));
        Assert.Throws<InvalidDataException>(() => DdmCodec.ParseQueryDescriptor([3, 0x76, 0xd0, 0x00]));
        Assert.Throws<InvalidDataException>(() => DdmCodec.ParseQueryDescriptor([6, 0x7f, 0, 1, 2, 3]));
    }

    [Theory]
    [InlineData(83)]
    [InlineData(84)]
    [InlineData(85)]
    [InlineData(169)]
    [InlineData(170)]
    public void QueryDescriptorContinuesCptGroupsWithoutLosingMetadata(int count)
    {
        using var encoded = new MemoryStream();
        for (var start = 0; start < count; start += 84)
        {
            var groupCount = Math.Min(84, count - start);
            encoded.WriteByte((byte)((groupCount + 1) * 3));
            encoded.WriteByte(start == 0 ? (byte)0x76 : (byte)0x7f);
            encoded.WriteByte(start == 0 ? (byte)0xd0 : (byte)0x00);
            for (var index = start; index < start + groupCount; index++)
            {
                encoded.WriteByte((byte)index);
                encoded.WriteByte((byte)(index >> 8));
                encoded.WriteByte((byte)(255 - (index % 256)));
            }
        }

        var parsed = DdmCodec.ParseQueryDescriptor(encoded.ToArray());
        Assert.Equal(count, parsed.Length);
        for (var index = 0; index < count; index++)
            Assert.Equal(new FieldDescriptor((byte)index, (byte)(index >> 8), (byte)(255 - (index % 256))), parsed[index]);

        Assert.Throws<InvalidDataException>(() => DdmCodec.ParseQueryDescriptor([6, 0x7f, 0, 1, 2, 3]));
    }

    [Fact]
    public void QueryDataDecodesInlineBlobClobDbclobAndXmlValues()
    {
        FieldDescriptor[] fields = new byte[] { 0xc9, 0xcf, 0xcd, 0xc7 }
            .Select(type => new FieldDescriptor(type, 0x80, 9))
            .ToArray();
        var blob = new byte[] { 0, 0x7f, 0x80, 0xff };
        var clob = Encoding.UTF8.GetBytes("CLOB café");
        var dbclob = Encoding.BigEndianUnicode.GetBytes("DBCLOB 東京");
        var xml = Encoding.UTF8.GetBytes("<root/>");
        using var row = new MemoryStream();
        row.WriteByte(0xff);
        row.WriteByte(0);
        Span<byte> encodedLength = stackalloc byte[8];
        foreach (var (value, length) in new[]
        {
            (blob, (ulong)blob.Length),
            (clob, (ulong)clob.Length),
            (dbclob, (ulong)(dbclob.Length / 2)),
            (xml, (ulong)xml.Length),
        })
        {
            row.WriteByte(0);
            row.WriteByte(0x01);
            BinaryPrimitives.WriteUInt64BigEndian(encodedLength, length);
            row.Write(encodedLength);
            row.Write(value);
        }

        var data = row.ToArray();
        var (rows, consumed) = Db2Session.DecodeRowsIncremental(fields, data);
        Assert.Equal(data.Length, consumed);
        var decoded = Assert.Single(rows);
        Assert.Equal(blob, Assert.IsType<byte[]>(decoded[0]));
        Assert.Equal("CLOB café", Assert.IsType<string>(decoded[1]));
        Assert.Equal("DBCLOB 東京", Assert.IsType<string>(decoded[2]));
        Assert.Equal("<root/>", Assert.IsType<string>(decoded[3]));

        var (partialRows, partialConsumed) = Db2Session.DecodeRowsIncremental(fields, data[..^3]);
        Assert.Empty(partialRows);
        Assert.Equal(0, partialConsumed);
    }

    [Fact]
    public void SqlDtardSplitsParameterDescriptorsFromOutputValues()
    {
        var descriptor = new byte[] { 9, 0x76, 0xd0, 0x04, 0, 2, 0x03, 0, 4 };
        var values = new byte[] { 0xff, 0, 0x2a, 0, 0, 0, 0x64, 0, 0, 0 };
        var payload = DdmCodec.PackObject(CodePoint.FDODSC, descriptor)
            .Concat(DdmCodec.PackObject(CodePoint.FDODTA, values)).ToArray();

        var parsed = DdmCodec.ParseSqlDtaReply(payload);
        Assert.Equal(new byte[] { 0x04, 0x03 }, parsed.Fields.Select(field => field.Type));
        Assert.Equal(values, parsed.Data);
        Assert.Throws<InvalidDataException>(() => DdmCodec.ParseSqlDtaReply([0, 2, 0x14, 0x13]));
    }

    [Fact]
    public void OutputParameterReplyRejectsRowsShorterThanTheRequestedPositions()
    {
        var descriptor = new byte[] { 9, 0x76, 0xd0, 0x04, 0, 2, 0x03, 0, 4 };
        var values = new byte[] { 0xff, 0, 0x2a, 0, 0, 0, 0x64, 0, 0, 0 };
        var payload = DdmCodec.PackObject(CodePoint.FDODSC, descriptor)
            .Concat(DdmCodec.PackObject(CodePoint.FDODTA, values)).ToArray();

        // The value row carries two values, so two output positions are complete.
        var covered = new object?[2];
        Db2Session.ApplyOutputParameterReply(
            payload, [Db2ParameterDirection.Output, Db2ParameterDirection.Output], covered, []);

        // Requesting a third position must fail instead of silently reporting the
        // missing output as its placeholder value.
        var shortRow = new object?[3];
        var error = Assert.Throws<InvalidDataException>(() => Db2Session.ApplyOutputParameterReply(
            payload,
            [Db2ParameterDirection.Output, Db2ParameterDirection.Output, Db2ParameterDirection.Output],
            shortRow,
            []));
        Assert.Contains("position 2 was requested as an output", error.Message);
    }

    [Fact]
    public void OutputParameterReplyRejectsMissingOrEmptySqlDtardData()
    {
        Assert.Throws<InvalidDataException>(() => Db2Session.ApplyOutputParameterReply(
            [], [Db2ParameterDirection.Output], [null], []));
        Assert.Throws<InvalidDataException>(() => Db2Session.ApplyOutputParameterReply(
            [0xff, 0, 0xff], [Db2ParameterDirection.Output], [null], []));

        var descriptor = new byte[] { 6, 0x76, 0xd0, 0x03, 0, 4 };
        var rowWithoutValues = DdmCodec.PackObject(CodePoint.FDODSC, descriptor)
            .Concat(DdmCodec.PackObject(CodePoint.FDODTA, [0xff, 0])).ToArray();
        Assert.Throws<InvalidDataException>(() => Db2Session.ApplyOutputParameterReply(
            rowWithoutValues, [Db2ParameterDirection.Output], [null], []));

        // An empty SQLDTARD is harmless only when the caller requested no output values.
        Db2Session.ApplyOutputParameterReply(
            [], [Db2ParameterDirection.Input], [null], []);
    }

    [Fact]
    public void ProcedureOutputCallRejectsMissingSqlDtardReply()
    {
        Assert.Throws<InvalidDataException>(() => Db2Session.EnsureOutputParameterDataReturned(
            [Db2ParameterDirection.Output], dataReturned: false));

        Db2Session.EnsureOutputParameterDataReturned(
            [Db2ParameterDirection.Output], dataReturned: true);
        Db2Session.EnsureOutputParameterDataReturned(
            [Db2ParameterDirection.Input], dataReturned: false);
    }

    [Fact]
    public void SqlTypeIdentifiersPreserveNullabilityAndBaseType()
    {
        Assert.True(SqlTypeNames.IsNullable(SqlType.NDecimal));
        Assert.Equal(SqlType.Decimal, SqlTypeNames.BaseType(SqlType.NDecimal));
        Assert.Equal("DECIMAL (NULLABLE)", SqlTypeNames.Get(SqlType.NDecimal));
        Assert.Equal("VARCHAR", SqlTypeNames.Get(SqlType.VarChar));
    }

    [Fact]
    public void SqlDtaEncoderMatchesGoReferenceVectorByteForByte()
    {
        Db2ColumnMetadata[] columns =
        [
            new("PARAM1", SqlType.NInteger, 4, 0, 0, true),
            new("PARAM2", SqlType.NVarChar, 100, 0, 0, true),
            new("PARAM3", SqlType.NFloat, 8, 0, 0, true),
            new("PARAM4", SqlType.NBoolean, 2, 0, 0, true),
            new("PARAM5", SqlType.NDate, 10, 0, 0, true),
        ];
        var actual = Convert.ToHexString(SqlDtaEncoder.Build(columns, [1, "Teclado Mecânico", 250.50, true, new DateOnly(2026, 8, 26)])).ToLowerInvariant();
        Assert.Equal("00642412001c00101276d0030004393fff0b000805000221000a0671e4d000010044147a000001000000000010005400650063006c00610064006f0020004d0065006300e2006e00690063006f000000000000506f4000010000323032362d30382d3236", actual);
    }

    [Theory]
    [InlineData(83, 1)]
    [InlineData(84, 1)]
    [InlineData(85, 2)]
    [InlineData(148, 2)]
    public void SqlDtaEncoderContinuesWideDescriptorWithCptAndKeepsValueOrder(int count, int expectedGroups)
    {
        var columns = Enumerable.Range(0, count)
            .Select(index => new Db2ColumnMetadata($"P{index}", SqlType.NSmall, 2, 0, 0, true))
            .ToArray();
        var values = Enumerable.Range(0, count)
            .Select(index => index % 11 == 0 ? null : (object?)index)
            .ToArray();
        var encoded = SqlDtaEncoder.Build(columns, values);
        var objects = DdmCodec.ParseParameters(encoded.AsSpan(4));
        var descriptor = objects[(ushort)CodePoint.FDODSC];
        var data = objects[(ushort)CodePoint.FDODTA];

        var offset = 0;
        var groups = 0;
        var fields = 0;
        while (offset < descriptor.Length - 6)
        {
            var groupLength = descriptor[offset];
            Assert.InRange(groupLength, 6, 255);
            Assert.Equal(0, (groupLength - 3) % 3);
            Assert.Equal(groups == 0 ? (byte)0x76 : (byte)0x7f, descriptor[offset + 1]);
            Assert.Equal(groups == 0 ? (byte)0xd0 : (byte)0x00, descriptor[offset + 2]);
            var groupFields = (groupLength - 3) / 3;
            for (var field = 0; field < groupFields; field++)
                Assert.Equal(new byte[] { 0x05, 0x00, 0x02 }, descriptor.AsSpan(offset + 3 + field * 3, 3).ToArray());
            fields += groupFields;
            groups++;
            offset += groupLength;
        }

        Assert.Equal(expectedGroups, groups);
        Assert.Equal(count, fields);
        Assert.Equal(new byte[] { 0x06, 0x71, 0xe4, 0xd0, 0x00, 0x01 }, descriptor.AsSpan(offset).ToArray());

        Assert.Equal(0, data[0]); // FDODTA leading alignment byte.
        offset = 1;
        for (var index = 0; index < count; index++)
        {
            if (values[index] is null)
            {
                Assert.Equal(0xff, data[offset++]);
                continue;
            }
            Assert.Equal(0, data[offset++]);
            Assert.Equal(index, BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, 2)));
            offset += 2;
        }
        Assert.Equal(data.Length, offset);
    }

    [Fact]
    public void SqlDtaEncoderNormalizesDb2DateTimeTextWithoutChangingItsMeaning()
    {
        static void AssertText(SqlType type, int length, string value, string expected)
        {
            var column = new Db2ColumnMetadata("TEMPORAL", (SqlType)type, checked((ulong)length), 0, 0, true);
            var encoded = SqlDtaEncoder.Build([column], [value]);
            var suffix = Encoding.ASCII.GetBytes($"\0{expected}");
            Assert.True(encoded.AsSpan().EndsWith(suffix), $"{type} input '{value}' did not encode as '{expected}'.");
        }

        AssertText(SqlType.Date, 10, "2024-2-9", "2024-02-09");
        AssertText(SqlType.Date, 10, "02/29/2024", "2024-02-29");
        AssertText(SqlType.Time, 8, "12:34", "12:34:00");
        AssertText(SqlType.Time, 8, "23.59.58", "23:59:58");
        AssertText(SqlType.Timestamp, 32, "2024-02-29T23:59:58.123456", "2024-02-29-23.59.58.123456      ");
        AssertText(SqlType.Timestamp, 32, "2024-02-29T23:59:58.123456789012", "2024-02-29-23.59.58.123456789012");
        AssertText(SqlType.Timestamp, 32, "2024-02-29", "2024-02-29-00.00.00.000000      ");

        foreach (var (type, length, value) in new[]
        {
            (SqlType.Date, 10, "2024-02-30"),
            (SqlType.Time, 8, "23:59:58.123456"),
            (SqlType.Time, 8, "24:00:01"),
            (SqlType.Timestamp, 32, "2024-02-30 23:59:58"),
            (SqlType.Timestamp, 32, "2024-02-29 23:59:58+02:00"),
        })
        {
            var column = new Db2ColumnMetadata("TEMPORAL", (SqlType)type, checked((ulong)length), 0, 0, true);
            Assert.Throws<FormatException>(() => SqlDtaEncoder.Build([column], [value]));
        }
    }

    [Fact]
    public void SqlDtaEncoderIncludesAlignmentByteForEvenDecimalPayloads()
    {
        var column = new Db2ColumnMetadata("DECIMAL", SqlType.NDecimal, 0, 4, 2, true);
        var encoded = Convert.ToHexString(SqlDtaEncoder.Build([column], [1.25m])).ToLowerInvariant();
        Assert.EndsWith("0009147a000000125c", encoded);
    }

    [Fact]
    public void SqlDtaEncoderDescribesNullableVarbinaryWithItsTypeAndLength()
    {
        var encoded = SqlDtaEncoder.Build(
            [new Db2ColumnMetadata("B", SqlType.NVarBinary, 10, 0, 0, true)],
            [null]);

        Assert.True(encoded.AsSpan().IndexOf(new byte[] { 0xc3, 0, 10 }) >= 0);
    }

    [Fact]
    public void SqlDtaEncoderUsesBinarySourceDescriptorAndPreservesRawCharacterBytes()
    {
        var value = new byte[] { 0xe9 };
        var targets = new[] { SqlType.NVarChar, SqlType.NChar, SqlType.NLong, SqlType.NClob, SqlType.NXML };
        foreach (var target in targets)
        {
            var encoded = SqlDtaEncoder.Build(
                [new Db2ColumnMetadata("P", (SqlType)target, 20, 0, 0, true)],
                [value]);

            Assert.True(encoded.AsSpan().IndexOf(new byte[] { 0x3f, 0x7f, 0xff }) >= 0,
                $"{target} must describe binary-to-character input with NVarMix.");
            Assert.True(encoded.AsSpan().EndsWith(new byte[] { 0, 0, 1, 0xe9 }),
                $"{target} must preserve the source byte and use its byte length.");
        }
    }

    [Fact]
    public void SqlDtaEncoderRejectsIntegerOverflowAndDecimalIntegerDigits()
    {
        var small = new Db2ColumnMetadata("SMALL", SqlType.NSmall, 2, 0, 0, true);
        var big = new Db2ColumnMetadata("BIG", SqlType.NBigInt, 8, 0, 0, true);
        Assert.Throws<OverflowException>(() => SqlDtaEncoder.Build([small], [32768]));
        Assert.Equal(long.MinValue, BinaryPrimitives.ReadInt64LittleEndian(SqlDtaEncoder.Build([big], ["-9223372036854775808.0"]).AsSpan()[^8..]));
        Assert.Throws<OverflowException>(() => SqlDtaEncoder.Build([big], ["-9223372036854775809.0"]));
        Assert.Throws<OverflowException>(() => SqlDtaEncoder.Build([big], ["-1e19"]));
        Assert.EndsWith("000000123c", Convert.ToHexString(SqlDtaEncoder.Build(
            [new Db2ColumnMetadata("DECIMAL", SqlType.NDecimal, 0, 5, 2, true)], ["1.236"])).ToLowerInvariant());
        Assert.Throws<OverflowException>(() => SqlDtaEncoder.Build(
            [new Db2ColumnMetadata("DECIMAL", SqlType.NDecimal, 0, 2, 2, true)], ["1.25"]));
    }

    [Theory]
    [InlineData(-3.75, -3)]
    [InlineData(-1.5, -1)]
    [InlineData(-0.5, 0)]
    [InlineData(0.5, 0)]
    [InlineData(1.5, 1)]
    [InlineData(3.75, 3)]
    public void SqlDtaEncoderTruncatesFractionalFloatingPointForIntegerParameters(double value, int expected)
    {
        var small = new Db2ColumnMetadata("SMALL", SqlType.NSmall, 2, 0, 0, true);
        var encoded = SqlDtaEncoder.Build([small], [value]);

        Assert.Equal(expected, (int)BinaryPrimitives.ReadInt16LittleEndian(encoded.AsSpan(encoded.Length - sizeof(short))));
    }

    [Fact]
    public void SqlDtaEncoderTruncatesFractionalDecimalForIntegerParameters()
    {
        var small = new Db2ColumnMetadata("SMALL", SqlType.NSmall, 2, 0, 0, true);
        var encoded = SqlDtaEncoder.Build([small], [-1.5m]);

        Assert.Equal(-1, (int)BinaryPrimitives.ReadInt16LittleEndian(encoded.AsSpan(encoded.Length - sizeof(short))));
    }

    [Fact]
    public void SqlDtaIntegerParametersMatchDb2FractionalAndBooleanConversions()
    {
        var small = new Db2ColumnMetadata("SMALL", SqlType.NSmall, 2, 0, 0, true);
        (object Value, short Expected)[] accepted =
        [
            ("-3.75", -3), ("1.5", 1), (" 1.5 ", 1), ("1e0", 1),
            (".5", 0), ("1.", 1), ("0.000", 0), ("+0001.50", 1),
            (true, 1), (false, 0),
        ];

        foreach (var (value, expected) in accepted)
        {
            var encoded = SqlDtaEncoder.Build([small], [value]);
            Assert.Equal(expected, BinaryPrimitives.ReadInt16LittleEndian(encoded.AsSpan(encoded.Length - sizeof(short))));
        }

        foreach (var invalid in new object[] { "1,5", "abc", "" })
            Assert.Throws<FormatException>(() => SqlDtaEncoder.Build([small], [invalid]));
        Assert.Throws<OverflowException>(() => SqlDtaEncoder.Build([small], ["32768.0"]));
    }

    [Fact]
    public void SqlDtaBooleanParametersMatchDb2TextAndIntegerConversions()
    {
        var boolean = new Db2ColumnMetadata("BOOLEAN", SqlType.NBoolean, 1, 0, 0, true);
        (object Value, bool Expected)[] accepted =
        [
            (true, true), (false, false),
            ("true", true), (" FALSE ", false),
            ("YES", true), ("no", false), ("y", true), ("N", false),
            ("On", true), ("OFF", false), ("0", false), ("2", true),
            ("-0", false), (" +2 ", true), ("99999999999999999999999999999999", true),
            (0, false), (2, true), (-1, true), (long.MaxValue, true),
        ];

        foreach (var (value, expected) in accepted)
        {
            var encoded = SqlDtaEncoder.Build([boolean], [value]);
            Assert.Equal((byte)(expected ? 1 : 0), encoded[^1]);
        }

        foreach (var invalid in new object[] { "", " ", "1.0", "maybe", 1.5d, 1.5m })
            Assert.Throws<FormatException>(() => SqlDtaEncoder.Build([boolean], [invalid]));
    }

    [Fact]
    public void SqlDtaMatchesIbmCliSourceTypeRulesForNumericTextAndLobTargets()
    {
        var varchar = new Db2ColumnMetadata("VARCHAR", SqlType.NVarChar, 20, 0, 0, true);
        foreach (var (value, expected) in new object?[] { true, false }.Zip(new byte[] { (byte)'1', (byte)'0' }))
        {
            var encoded = SqlDtaEncoder.Build([varchar], [value]);
            Assert.Equal(new byte[] { 0, expected }, encoded[^2..]);
        }
        var utf8Text = SqlDtaEncoder.Build([varchar], [new byte[] { 0xe6, 0x9d, 0xb1 }]);
        Assert.Equal(new byte[] { 0, 3, 0xe6, 0x9d, 0xb1 }, utf8Text[^5..]);
        var singleRawByteText = SqlDtaEncoder.Build([varchar], [new byte[] { 0xe9 }]);
        Assert.Equal(new byte[] { 0, 1, 0xe9 }, singleRawByteText[^3..]);

        var numericTargets = new[]
        {
            new Db2ColumnMetadata("SMALLINT", SqlType.NSmall, 2, 0, 0, true),
            new Db2ColumnMetadata("INTEGER", SqlType.NInteger, 4, 0, 0, true),
            new Db2ColumnMetadata("BIGINT", SqlType.NBigInt, 8, 0, 0, true),
            new Db2ColumnMetadata("REAL", SqlType.NFloat, 4, 0, 0, true),
            new Db2ColumnMetadata("DOUBLE", SqlType.NFloat, 8, 0, 0, true),
            new Db2ColumnMetadata("DECIMAL", SqlType.NDecimal, 0, 9, 3, true),
            new Db2ColumnMetadata("DECFLOAT", SqlType.NDecFloat, 16, 0, 0, true),
        };
        foreach (var target in numericTargets)
        {
            Assert.NotEmpty(SqlDtaEncoder.Build([target], [true]));
            Assert.NotEmpty(SqlDtaEncoder.Build([target], [false]));
            Assert.Throws<InvalidCastException>(() => SqlDtaEncoder.Build([target], [new byte[] { 49, 50, 51 }]));
        }

        var binaryTargets = new[]
        {
            new Db2ColumnMetadata("BINARY", SqlType.NBinary, 4, 0, 0, true),
            new Db2ColumnMetadata("VARBINARY", SqlType.NVarBinary, 10, 0, 0, true),
            new Db2ColumnMetadata("BLOB", SqlType.NBlob, 10, 0, 0, true),
        };
        foreach (var target in binaryTargets)
        {
            foreach (var value in new object?[] { true, 123, 12.5m })
                Assert.Throws<InvalidCastException>(() => SqlDtaEncoder.Build([target], [value]));
            Assert.NotEmpty(SqlDtaEncoder.Build([target], ["313233"]));
            Assert.NotEmpty(SqlDtaEncoder.Build([target], [new byte[] { 49, 50, 51 }]));
        }
        Assert.Equal(new byte[] { 0, 0x31, 0x32, 0x33, 0 }, SqlDtaEncoder.Build([binaryTargets[0]], ["313233"])[^5..]);
        Assert.Equal(new byte[] { 0, 0, 3, 0x31, 0x32, 0x33 }, SqlDtaEncoder.Build([binaryTargets[1]], ["313233"])[^6..]);
        Assert.Equal(new byte[] { 0, 0, 0 }, SqlDtaEncoder.Build([binaryTargets[1]], [""])[^3..]);
        Assert.Throws<InvalidCastException>(() => SqlDtaEncoder.Build([binaryTargets[1]], ["123"]));
        Assert.Throws<InvalidCastException>(() => SqlDtaEncoder.Build([binaryTargets[1]], ["12G4"]));
        Assert.Throws<InvalidCastException>(() => SqlDtaEncoder.Build([new Db2ColumnMetadata("VARBINARY", SqlType.NVarBinary, 2, 0, 0, true)], ["313233"]));
        var blobText = SqlDtaEncoder.BuildWithExternalData([binaryTargets[2]], ["313233"]);
        Assert.True(blobText.ExternalData.Single().DdmObject.AsSpan().IndexOf(new byte[] { 0, 0x31, 0x32, 0x33 }) >= 0);

        var textLobTargets = new[]
        {
            new Db2ColumnMetadata("CLOB", SqlType.NClob, 20, 0, 0, true),
            new Db2ColumnMetadata("DBCLOB", SqlType.NDbClob, 20, 0, 0, true),
            new Db2ColumnMetadata("XML", SqlType.XML, 20, 0, 0, true),
        };
        foreach (var target in textLobTargets)
        {
            foreach (var value in new object?[] { true, 123, 1.5d, 12.5m })
                Assert.Throws<InvalidCastException>(() => SqlDtaEncoder.Build([target], [value]));
            Assert.NotEmpty(SqlDtaEncoder.Build([target], ["text"]));
            Assert.NotEmpty(SqlDtaEncoder.Build([target], [new byte[] { 116, 101, 120, 116 }]));
        }
        var clobText = SqlDtaEncoder.Build([textLobTargets[0]], [new byte[] { 116, 101, 120, 116 }]);
        Assert.True(clobText.AsSpan().IndexOf(new byte[] { 0, 0, 0, 4, 116, 101, 120, 116 }) >= 0);
        var dbClobText = SqlDtaEncoder.Build([textLobTargets[1]], [new byte[] { 0x31, 0x32, 0x33 }]);
        Assert.Equal(new byte[] { 0, 0, 1, 0x31, 0x32 }, dbClobText[^5..]);
        var xmlText = SqlDtaEncoder.Build([textLobTargets[2]], [new byte[] { 0x3c, 0x72, 0x2f, 0x3e }]);
        Assert.True(xmlText.AsSpan().IndexOf(new byte[] { 0, 0, 0, 4, 0x3c, 0x72, 0x2f, 0x3e }) >= 0);
    }

    [Fact]
    public void XmlSqlDtaUsesUtf16CharacterCountForItsLength()
    {
        const string xml = "<root><message>XML café</message></root>";
        var column = new Db2ColumnMetadata("XML", SqlType.XML, 0, 0, 0, true);
        var encoded = SqlDtaEncoder.Build([column], [xml]);
        var utf16 = Encoding.BigEndianUnicode.GetBytes(xml);
        var expected = new byte[3 + utf16.Length];
        expected[0] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(expected.AsSpan(1), (ushort)xml.Length);
        utf16.CopyTo(expected, 3);
        Assert.True(encoded.AsSpan().EndsWith(expected));
    }

    [Theory]
    [InlineData(8, "0", "2238000000000000", "0")]
    [InlineData(8, "123.456", "222c000000028e56", "123.456")]
    [InlineData(8, "-987.654", "a22c0000000e3f54", "-987.654")]
    [InlineData(8, "1000", "2238000000000400", "1000")]
    [InlineData(8, "0.000123", "22200000000000a3", "0.000123")]
    [InlineData(8, "1.23E+2", "22380000000000a3", "123")]
    [InlineData(8, "-Infinity", "f800000000000000", "-Infinity")]
    [InlineData(8, "NaN", "7c00000000000000", "NaN")]
    [InlineData(16, "0", "22080000000000000000000000000000", "0")]
    [InlineData(16, "12345678901234567890.12345678901234", "2604934b9c1e28e56f3c127177823534", "12345678901234567890.12345678901234")]
    [InlineData(16, "-98765432109876543210.98765432109876", "ee04b7cb0d10e3f546845ef96322277c", "-98765432109876543210.98765432109876")]
    [InlineData(16, "9.87654321E+20", "220b0000000000000000000038fd51a1", "9.87654321E+20")]
    [InlineData(16, "Infinity", "78000000000000000000000000000000", "Infinity")]
    [InlineData(16, "NaN", "7c000000000000000000000000000000", "NaN")]
    public void DecfloatEncodingMatchesGoDpdVectors(int length, string input, string expectedHex, string expectedDecoded)
    {
        var encoded = Decfloat.Encode(input, length);
        Assert.Equal(expectedHex, Convert.ToHexString(encoded).ToLowerInvariant());
        Assert.Equal(expectedDecoded, Decfloat.Decode(encoded));
    }

    [Fact]
    public void DecfloatEncodingRejectsMalformedValuesInvalidWidthsAndOutOfRangeExponents()
    {
        Assert.Throws<FormatException>(() => Decfloat.Encode("12x", 8));
        Assert.Throws<FormatException>(() => Decfloat.Encode("1E+1000", 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => Decfloat.Encode("1", 12));
    }

    [Fact]
    public void SqlDtaExternalizesLargeBlobsAndRetainsParameterOrder()
    {
        var first = Enumerable.Repeat((byte)0xa5, 70_000).ToArray();
        var second = Enumerable.Repeat((byte)0x5a, 65_537).ToArray();
        var columns = new[]
        {
            new Db2ColumnMetadata("BLOB", (SqlType)405, 0, 0, 0, true),
            new Db2ColumnMetadata("INTEGER", (SqlType)496, 4, 0, 0, true),
            new Db2ColumnMetadata("BLOB", (SqlType)405, 0, 0, 0, true),
        };
        var encoded = SqlDtaEncoder.BuildWithExternalData(columns, [first, 7, second]);
        Assert.True(encoded.SqlDta.Length < 256);
        Assert.Equal(2, encoded.ExternalData.Count);
        Assert.Equal(2, Count(encoded.SqlDta, [0xc9, 0x80, 0x09]));
        Assert.Equal(1, Count(encoded.SqlDta, [0x00, 0x00, 0x02, 0, 0, 0, 0, 0, 0x01, 0x11, 0x70]));
        Assert.Equal(1, Count(encoded.SqlDta, [0x00, 0x00, 0x02, 0, 0, 0, 0, 0, 0x01, 0x00, 0x01]));
        AssertExternalBlob(encoded.ExternalData[0].DdmObject!, first);
        AssertExternalBlob(encoded.ExternalData[1].DdmObject!, second);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void MixedEmptyExternalBlobsKeepDescriptorsAndOnlySendNonEmptyPayload(int emptyPosition)
    {
        Db2ColumnMetadata[] columns = [new("B1", (SqlType)405, 100, 0, 0, true), new("B2", (SqlType)405, 100, 0, 0, true), new("B3", (SqlType)405, 100, 0, 0, true)];
        object?[] values = [new byte[] { 1 }, new byte[] { 2 }, new byte[] { 3 }];
        values[emptyPosition] = Array.Empty<byte>();
        var encoded = SqlDtaEncoder.BuildWithExternalData(columns, values);
        Assert.Equal(3, Count(encoded.SqlDta, [0xc9, 0x80, 0x09]));
        Assert.Equal(2, encoded.ExternalData.Count);
        var next = 0;
        for (var index = 0; index < values.Length; index++)
            if (index != emptyPosition) Assert.Equal(DdmCodec.PackObject(CodePoint.EXTDTA, [0, .. (byte[])values[index]!]), encoded.ExternalData[next++].DdmObject!);
    }

    [Fact]
    public void StandaloneEmptyExternalBlobRetainsItsTerminalDataObject()
    {
        var encoded = SqlDtaEncoder.BuildWithExternalData([new("B", (SqlType)405, 100, 0, 0, true)], [Array.Empty<byte>()]);
        Assert.Single(encoded.ExternalData);
        Assert.Equal(DdmCodec.PackObject(CodePoint.EXTDTA, [0]), encoded.ExternalData[0].DdmObject!);
    }

    [Fact]
    public void LargeLobSqlDtaUsesIbmExternalBlobDescriptorAndLengthVector()
    {
        var blob = Enumerable.Range(0, 70_003).Select(index => (byte)(index % 251)).ToArray();
        var encoded = SqlDtaEncoder.BuildWithExternalData(
            [new Db2ColumnMetadata("BLOB", (SqlType)405, 1_048_576, 0, 0, true)], [blob]);
        Assert.Equal(1, Count(encoded.SqlDta, [0xc9, 0x80, 0x09]));
        Assert.Equal(1, Count(encoded.SqlDta, [0x00, 0x00, 0x02, 0, 0, 0, 0, 0, 0x01, 0x11, 0x73]));
        var request = DssCodec.EncodeRequest(encoded.ExternalData[0].DdmObject!, 1, false, true);
        Assert.Equal((byte)DssCodec.Object, (byte)(request.Bytes[3] & 0x0f));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32_762)]
    [InlineData(32_763)]
    [InlineData(32_767)]
    [InlineData(32_768)]
    [InlineData(70_003)]
    public async Task StreamedBlobDssBytesMatchMaterializedEncodingAtLengthBoundaries(int size)
    {
        var bytes = Enumerable.Range(0, size).Select(index => (byte)(index % 251)).ToArray();
        var source = new MemoryStream(bytes, writable: false);
        var destination = new MemoryStream();

        var nextId = await DssCodec.WriteExternalBlobAsync(destination, source, size, 19, nextHasSameId: false, lastPacket: true);
        var ddm = DdmCodec.PackObject(CodePoint.EXTDTA, [0, .. bytes]);
        var expected = DssCodec.EncodeRequest(ddm, 19, nextHasSameId: false, lastPacket: true);

        Assert.Equal(expected.Bytes, destination.ToArray());
        Assert.Equal(expected.NextCorrelationId, nextId);
        Assert.Equal(size, source.Position);
    }

    [Fact]
    public async Task StreamedBlobSendsOnlyDeclaredBytesAndLeavesSeekableRemainderUnread()
    {
        var source = new MemoryStream([0x11, 0x22, 0x33], writable: false);
        var destination = new MemoryStream();

        await DssCodec.WriteExternalBlobAsync(destination, source, 2, 19, nextHasSameId: false, lastPacket: true);

        var ddm = DdmCodec.PackObject(CodePoint.EXTDTA, [0, 0x11, 0x22]);
        var expected = DssCodec.EncodeRequest(ddm, 19, nextHasSameId: false, lastPacket: true);
        Assert.Equal(expected.Bytes, destination.ToArray());
        Assert.Equal(2, source.Position);
    }

    [Fact]
    public void SqlDtaExternalizesBlobStreamWithoutReadingOrBufferingIt()
    {
        var source = new MemoryStream(new byte[80_000], writable: false);
        var encoded = SqlDtaEncoder.BuildWithExternalData(
            [new Db2ColumnMetadata("BLOB", (SqlType)405, 1_048_576, 0, 0, true)],
            [new Db2BlobStream(source, 80_000)]);

        Assert.True(encoded.SqlDta.Length < 256);
        var external = Assert.Single(encoded.ExternalData);
        Assert.Null(external.DdmObject);
        Assert.Same(source, external.Source);
        Assert.Equal(80_000, external.Length);
        Assert.Equal(0, source.Position);
    }

    [Fact]
    public void SqlDtaExternalizesUtf8ClobStreamWithTheCharacterLobDescriptor()
    {
        var source = new MemoryStream(new byte[80_000], writable: false);
        var encoded = SqlDtaEncoder.BuildWithExternalData(
            [new Db2ColumnMetadata("CLOB", (SqlType)409, 1_048_576, 0, 0, true)],
            [new Db2ClobStream(source, 80_000)]);

        Assert.True(encoded.SqlDta.Length < 256);
        Assert.True(encoded.SqlDta.AsSpan().IndexOf(new byte[] { 0xcf, 0x80, 0x09 }) >= 0);
        var external = Assert.Single(encoded.ExternalData);
        Assert.Null(external.DdmObject);
        Assert.Same(source, external.Source);
        Assert.Equal(80_000, external.Length);
        Assert.Equal(0, source.Position);
    }

    [Fact]
    public void SqlDtaRejectsBlobStreamsForNonBlobParametersAndNegativeLengths()
    {
        Assert.Throws<ArgumentException>(() => SqlDtaEncoder.BuildWithExternalData(
            [new Db2ColumnMetadata("INTEGER", (SqlType)496, 4, 0, 0, true)],
            [new Db2BlobStream(new MemoryStream(), 0)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => SqlDtaEncoder.BuildWithExternalData(
            [new Db2ColumnMetadata("BLOB", (SqlType)405, 1_048_576, 0, 0, true)],
            [new Db2BlobStream(new MemoryStream(), -1)]));
    }

    [Fact]
    public void SqlDtaRejectsClobStreamsForNonClobParametersAndNegativeLengths()
    {
        Assert.Throws<ArgumentException>(() => SqlDtaEncoder.BuildWithExternalData(
            [new Db2ColumnMetadata("BLOB", (SqlType)405, 1_048_576, 0, 0, true)],
            [new Db2ClobStream(new MemoryStream(), 0)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => SqlDtaEncoder.BuildWithExternalData(
            [new Db2ColumnMetadata("CLOB", (SqlType)409, 1_048_576, 0, 0, true)],
            [new Db2ClobStream(new MemoryStream(), -1)]));
    }

    [Fact]
    public void SqlDtaRejectsEveryMismatchedTypedLobStreamBeforeReadingItsSource()
    {
        (ushort SqlType, string Name)[] targets =
        [
            (405, "BLOB"),
            (409, "CLOB"),
            (413, "DBCLOB"),
            (989, "XML"),
        ];
        (string Name, Func<MemoryStream, object> Create)[] streams =
        [
            ("BLOB", source => new Db2BlobStream(source, 3)),
            ("CLOB", source => new Db2ClobStream(source, 3)),
            ("DBCLOB", source => new Db2DbClobStream(source, 3)),
            ("XML", source => new Db2XmlStream(source, 3)),
        ];

        foreach (var target in targets)
        foreach (var stream in streams)
        {
            if (target.Name == stream.Name) continue;
            var source = new MemoryStream([0x61, 0x62, 0x63], writable: false);

            Assert.Throws<ArgumentException>(() => SqlDtaEncoder.BuildWithExternalData(
                [new Db2ColumnMetadata(target.Name, (SqlType)target.SqlType, 1_048_576, 0, 0, true)],
                [stream.Create(source)]));
            Assert.Equal(0, source.Position);
        }
    }

    [Fact]
    public void SqlDtaUsesTypedExternalDescriptorsForStreamedDbClobAndXml()
    {
        var dbClobSource = new MemoryStream(new byte[64], writable: false);
        var xmlSource = new MemoryStream(new byte[64], writable: false);
        var encoded = SqlDtaEncoder.BuildWithExternalData(
            [new Db2ColumnMetadata("DBCLOB", (SqlType)413, 1_048_576, 0, 0, true), new Db2ColumnMetadata("XML", (SqlType)989, 1_048_576, 0, 0, true)],
            [new Db2DbClobStream(dbClobSource, 64), new Db2XmlStream(xmlSource, 64)]);

        Assert.True(encoded.SqlDta.AsSpan().IndexOf(new byte[] { 0xcf, 0x80, 0x09 }) >= 0);
        Assert.True(encoded.SqlDta.AsSpan().IndexOf(new byte[] { 0xc7, 0x80, 0x09 }) >= 0);
        Assert.Same(dbClobSource, encoded.ExternalData[0].Source);
        Assert.Same(xmlSource, encoded.ExternalData[1].Source);
    }

    private static int Count(byte[] source, byte[] pattern)
    {
        var count = 0;
        for (var index = 0; index <= source.Length - pattern.Length; index++)
            if (source.AsSpan(index, pattern.Length).SequenceEqual(pattern)) count++;
        return count;
    }

    private static void AssertExternalBlob(byte[] ddm, byte[] bytes)
    {
        Assert.Equal(new byte[] { 0x80, 0x08, 0x14, 0x6c }, ddm[..4]);
        Assert.Equal((uint)(bytes.Length + 1), BinaryPrimitives.ReadUInt32BigEndian(ddm.AsSpan(4, 4)));
        Assert.Equal(0, ddm[8]);
        Assert.Equal(bytes, ddm[9..]);
    }

    [Fact]
    public void Db2PoolRequiresPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Db2Pool("db2://user:password@localhost/TESTDB", new Db2PoolOptions(0)));
    }
}
