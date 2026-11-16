using System.IO;
using System.Text;
using Lantern.App.Services;
using Xunit;

namespace Lantern.App.Tests;

public sealed class NetBiosNameResolverTests
{
    private const ushort TransactionId = 0x1234;
    private const int AnswerOwnerOffset = 50;

    [Fact]
    public void ParseNodeStatusResponse_ReturnsWorkstationNameForTheMatchingTransaction()
    {
        var packet = BuildResponse();

        var name = NetBiosNameResolver.ParseNodeStatusResponse(packet, TransactionId);

        Assert.Equal("HAYTHEM-PC", name);
    }

    [Fact]
    public void ParseNodeStatusResponse_LegacyOverloadRemainsCompatible()
    {
        Assert.Equal("HAYTHEM-PC", NetBiosNameResolver.ParseNodeStatusResponse(BuildResponse()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x1235)]
    public void ParseNodeStatusResponse_RejectsAnUnrelatedTransaction(ushort expectedTransactionId)
    {
        Assert.Null(NetBiosNameResolver.ParseNodeStatusResponse(BuildResponse(), expectedTransactionId));
    }

    [Fact]
    public void ParseNodeStatusResponse_IgnoresGroupNames()
    {
        var packet = BuildResponse([new NameEntry("WORKGROUP", Flags: 0x8000)]);

        Assert.Null(NetBiosNameResolver.ParseNodeStatusResponse(packet, TransactionId));
    }

    [Fact]
    public void ParseNodeStatusResponse_FindsAWorkstationAfterGroupAndServerNames()
    {
        var packet = BuildResponse(
        [
            new NameEntry("WORKGROUP", Flags: 0x8000),
            new NameEntry("FILE-SERVER", Suffix: 0x20),
            new NameEntry("HAYTHEM-PC"),
        ]);

        Assert.Equal("HAYTHEM-PC", NetBiosNameResolver.ParseNodeStatusResponse(packet, TransactionId));
    }

    [Theory]
    [InlineData(0x0500)]
    [InlineData(0x8503)]
    [InlineData(0x8d00)]
    [InlineData(0x8700)]
    public void ParseNodeStatusResponse_RejectsQueriesErrorsOtherOpcodesAndTruncatedResponses(ushort flags)
    {
        Assert.Null(NetBiosNameResolver.ParseNodeStatusResponse(BuildResponse(flags: flags), TransactionId));
    }

    [Theory]
    [InlineData(0x0020, 1)]
    [InlineData(0x0021, 3)]
    public void ParseNodeStatusResponse_RejectsOtherRecordTypesAndClasses(ushort type, ushort recordClass)
    {
        var packet = BuildResponse(recordType: type, recordClass: recordClass);

        Assert.Null(NetBiosNameResolver.ParseNodeStatusResponse(packet, TransactionId));
    }

    [Fact]
    public void ParseNodeStatusResponse_RequiresTheWholeDeclaredNameList()
    {
        var packet = BuildResponse(declaredNameCount: 2);

        Assert.Null(NetBiosNameResolver.ParseNodeStatusResponse(packet, TransactionId));
    }

    [Fact]
    public void ParseNodeStatusResponse_RejectsAnEmptyNameList()
    {
        Assert.Null(NetBiosNameResolver.ParseNodeStatusResponse(BuildResponse([]), TransactionId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("192.168.31.213")]
    [InlineData("PC\0other")]
    [InlineData("PC\nother")]
    public void ParseNodeStatusResponse_DoesNotUseAnEmptyWildcardIpOrControlCharacterName(string name)
    {
        var packet = BuildResponse([new NameEntry(name)]);

        Assert.Null(NetBiosNameResolver.ParseNodeStatusResponse(packet, TransactionId));
    }

    [Fact]
    public void ParseNodeStatusResponse_RejectsEveryTruncatedPrefix()
    {
        var packet = BuildResponse();

        for (var length = 0; length < packet.Length; length++)
        {
            Assert.Null(NetBiosNameResolver.ParseNodeStatusResponse(packet.AsSpan(0, length), TransactionId));
        }
    }

    [Fact]
    public void ParseNodeStatusResponse_RejectsACompressionPointerCycle()
    {
        var packet = BuildResponse();
        packet[AnswerOwnerOffset] = 0xc0;
        packet[AnswerOwnerOffset + 1] = AnswerOwnerOffset;

        Assert.Null(NetBiosNameResolver.ParseNodeStatusResponse(packet, TransactionId));
    }

    [Fact]
    public void ParseNodeStatusResponse_RejectsAnOutOfBoundsCompressionPointer()
    {
        var packet = BuildResponse();
        packet[AnswerOwnerOffset] = 0xff;
        packet[AnswerOwnerOffset + 1] = 0xff;

        Assert.Null(NetBiosNameResolver.ParseNodeStatusResponse(packet, TransactionId));
    }

    private static byte[] BuildResponse(
        NameEntry[]? names = null,
        ushort flags = 0x8500,
        ushort recordType = 0x0021,
        ushort recordClass = 1,
        byte? declaredNameCount = null)
    {
        names ??= [new NameEntry("HAYTHEM-PC")];
        using var stream = new MemoryStream();
        WriteUInt16(stream, TransactionId);
        WriteUInt16(stream, flags);
        WriteUInt16(stream, 1);
        WriteUInt16(stream, 1);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        stream.WriteByte(32);
        var wildcard = new byte[16];
        wildcard[0] = (byte)'*';
        foreach (var value in wildcard)
        {
            stream.WriteByte((byte)('A' + (value >> 4)));
            stream.WriteByte((byte)('A' + (value & 0x0f)));
        }

        stream.WriteByte(0);
        WriteUInt16(stream, 0x0021);
        WriteUInt16(stream, 1);
        stream.Write([0xc0, 0x0c]);
        WriteUInt16(stream, recordType);
        WriteUInt16(stream, recordClass);
        stream.Write(new byte[4]);
        WriteUInt16(stream, (ushort)(1 + (names.Length * 18)));
        stream.WriteByte(declaredNameCount ?? (byte)names.Length);
        foreach (var entry in names)
        {
            stream.Write(Encoding.ASCII.GetBytes(entry.Name.PadRight(15)));
            stream.WriteByte(entry.Suffix);
            WriteUInt16(stream, entry.Flags);
        }

        return stream.ToArray();
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private sealed record NameEntry(string Name, byte Suffix = 0, ushort Flags = 0);
}
