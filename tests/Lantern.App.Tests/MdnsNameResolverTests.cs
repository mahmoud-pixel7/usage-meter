using System.IO;
using System.Net;
using System.Text;
using Lantern.App.Services;
using Xunit;

namespace Lantern.App.Tests;

public sealed class MdnsNameResolverTests
{
    private const ushort TransactionId = 0x4c41;
    private const string ReverseOwner = "213.31.168.192.in-addr.arpa";
    private static readonly IPAddress QueriedAddress = IPAddress.Parse("192.168.31.213");
    private static readonly IPEndPoint Source = new(QueriedAddress, 5353);

    [Fact]
    public void ParsePtrResponse_ReturnsLocalHostNameForTheQueriedIpv4OwnerAndSource()
    {
        var packet = BuildResponse();

        var name = MdnsNameResolver.ParsePtrResponse(packet, QueriedAddress, Source, TransactionId);

        Assert.Equal("Pixel-10-Pro", name);
    }

    [Fact]
    public void ParsePtrResponse_LegacyOverloadStillParsesAReverseResponse()
    {
        Assert.Equal("Pixel-10-Pro", MdnsNameResolver.ParsePtrResponse(BuildResponse()));
    }

    [Fact]
    public void ParsePtrResponse_AcceptsUnsolicitedReverseAnswerAndCacheFlushClass()
    {
        var packet = BuildResponse(recordClass: 0x8001, includeQuestion: false);

        Assert.Equal("Pixel-10-Pro", MdnsNameResolver.ParsePtrResponse(packet, QueriedAddress, Source));
    }

    [Theory]
    [InlineData("214.31.168.192.in-addr.arpa")]
    [InlineData("_http._tcp.local")]
    [InlineData("")]
    public void ParsePtrResponse_RejectsAnUnrelatedOrMissingRecordOwner(string owner)
    {
        var packet = BuildResponse(owner: owner);

        Assert.Null(MdnsNameResolver.ParsePtrResponse(packet, QueriedAddress, Source, TransactionId));
    }

    [Theory]
    [InlineData("_http._tcp.local")]
    [InlineData("999.31.168.192.in-addr.arpa")]
    [InlineData("")]
    public void ParsePtrResponse_LegacyOverloadDoesNotTreatServicePtrsAsDeviceNames(string owner)
    {
        Assert.Null(MdnsNameResolver.ParsePtrResponse(BuildResponse(owner: owner)));
    }

    [Fact]
    public void ParsePtrResponse_SkipsAnUnrelatedPtrBeforeTheMatchingAnswer()
    {
        var unrelated = BuildResponse(owner: "_http._tcp.local", target: "Wrong-device.local", includeQuestion: false);
        var matching = BuildResponse(includeQuestion: false);
        byte[] packet = [.. unrelated, .. matching.AsSpan(12)];
        packet[7] = 2;

        Assert.Equal("Pixel-10-Pro",
            MdnsNameResolver.ParsePtrResponse(packet, QueriedAddress, Source, TransactionId));
    }

    [Theory]
    [InlineData("192.168.31.214", 5353)]
    [InlineData("192.168.31.213", 5354)]
    [InlineData("192.168.31.213", 137)]
    public void ParsePtrResponse_RejectsAnUnrelatedReplySource(string address, int port)
    {
        var source = new IPEndPoint(IPAddress.Parse(address), port);

        Assert.Null(MdnsNameResolver.ParsePtrResponse(BuildResponse(), QueriedAddress, source, TransactionId));
    }

    [Theory]
    [InlineData(0x0400)]
    [InlineData(0x8403)]
    [InlineData(0x8c00)]
    [InlineData(0x8600)]
    public void ParsePtrResponse_RejectsQueriesErrorsOtherOpcodesAndTruncatedResponses(ushort flags)
    {
        var packet = BuildResponse(flags: flags);

        Assert.Null(MdnsNameResolver.ParsePtrResponse(packet, QueriedAddress, Source, TransactionId));
    }

    [Fact]
    public void ParsePtrResponse_RejectsAnUnrelatedTransaction()
    {
        Assert.Null(MdnsNameResolver.ParsePtrResponse(BuildResponse(), QueriedAddress, Source, 0x4c42));
    }

    [Fact]
    public void ParsePtrResponse_RejectsANonIpv4Query()
    {
        Assert.Null(MdnsNameResolver.ParsePtrResponse(BuildResponse(), IPAddress.IPv6Loopback));
    }

    [Fact]
    public void ParsePtrResponse_DoesNotRestoreAGoodbyeRecord()
    {
        Assert.Null(MdnsNameResolver.ParsePtrResponse(BuildResponse(timeToLive: 0), QueriedAddress, Source));
    }

    [Fact]
    public void ParsePtrResponse_RejectsANonInternetClassRecord()
    {
        Assert.Null(MdnsNameResolver.ParsePtrResponse(BuildResponse(recordClass: 3), QueriedAddress, Source));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void ParsePtrResponse_RequiresTheTargetToFitExactlyInsideItsRecordData(int dataLengthAdjustment)
    {
        var packet = BuildResponse(dataLengthAdjustment: dataLengthAdjustment);

        Assert.Null(MdnsNameResolver.ParsePtrResponse(packet, QueriedAddress, Source));
    }

    [Fact]
    public void ParsePtrResponse_RejectsEveryTruncatedPrefix()
    {
        var packet = BuildResponse();

        for (var length = 0; length < packet.Length; length++)
        {
            Assert.Null(MdnsNameResolver.ParsePtrResponse(packet.AsSpan(0, length), QueriedAddress, Source));
        }
    }

    [Fact]
    public void ParsePtrResponse_RejectsACompressionPointerCycle()
    {
        var packet = BuildResponse();
        var ownerOffset = 12 + EncodeName(ReverseOwner).Length + 4;
        packet[ownerOffset] = (byte)(0xc0 | (ownerOffset >> 8));
        packet[ownerOffset + 1] = (byte)ownerOffset;

        Assert.Null(MdnsNameResolver.ParsePtrResponse(packet, QueriedAddress, Source));
    }

    [Fact]
    public void ParsePtrResponse_ReadsACompressedTargetWithinItsRecordData()
    {
        using var stream = new MemoryStream();
        WriteHeader(stream, questionCount: 1, answerCount: 2);
        stream.Write(EncodeName(ReverseOwner));
        WriteUInt16(stream, 12);
        WriteUInt16(stream, 1);
        var targetOffset = (int)stream.Position;
        stream.Write(EncodeName("Pixel-10-Pro.local"));
        WriteUInt16(stream, 1);
        WriteUInt16(stream, 1);
        WriteUInt32(stream, 120);
        WriteUInt16(stream, 4);
        stream.Write(QueriedAddress.GetAddressBytes());
        stream.Write([0xc0, 0x0c]);
        WriteUInt16(stream, 12);
        WriteUInt16(stream, 1);
        WriteUInt32(stream, 120);
        WriteUInt16(stream, 2);
        stream.WriteByte((byte)(0xc0 | (targetOffset >> 8)));
        stream.WriteByte((byte)targetOffset);

        Assert.Equal("Pixel-10-Pro",
            MdnsNameResolver.ParsePtrResponse(stream.ToArray(), QueriedAddress, Source, TransactionId));
    }

    [Theory]
    [InlineData("192.168.31.213")]
    [InlineData("192.168.31.213.local")]
    [InlineData("")]
    public void ParsePtrResponse_DoesNotUseAnIpOrEmptyTargetAsAName(string target)
    {
        Assert.Null(MdnsNameResolver.ParsePtrResponse(BuildResponse(target: target), QueriedAddress, Source));
    }

    private static byte[] BuildResponse(
        string owner = ReverseOwner,
        string target = "Pixel-10-Pro.local",
        ushort flags = 0x8400,
        ushort recordClass = 1,
        uint timeToLive = 120,
        bool includeQuestion = true,
        int dataLengthAdjustment = 0)
    {
        using var stream = new MemoryStream();
        WriteHeader(stream, (ushort)(includeQuestion ? 1 : 0), 1, flags);
        if (includeQuestion)
        {
            stream.Write(EncodeName(ReverseOwner));
            WriteUInt16(stream, 12);
            WriteUInt16(stream, 1);
        }

        if (includeQuestion && owner == ReverseOwner)
        {
            stream.Write([0xc0, 0x0c]);
        }
        else
        {
            stream.Write(EncodeName(owner));
        }

        WriteUInt16(stream, 12);
        WriteUInt16(stream, recordClass);
        WriteUInt32(stream, timeToLive);
        var data = EncodeName(target);
        WriteUInt16(stream, (ushort)(data.Length + dataLengthAdjustment));
        stream.Write(data);
        return stream.ToArray();
    }

    private static void WriteHeader(Stream stream, ushort questionCount, ushort answerCount, ushort flags = 0x8400)
    {
        WriteUInt16(stream, TransactionId);
        WriteUInt16(stream, flags);
        WriteUInt16(stream, questionCount);
        WriteUInt16(stream, answerCount);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
    }

    private static byte[] EncodeName(string name)
    {
        using var stream = new MemoryStream();
        if (name.Length > 0)
        {
            foreach (var label in name.Split('.'))
            {
                var bytes = Encoding.UTF8.GetBytes(label);
                stream.WriteByte((byte)bytes.Length);
                stream.Write(bytes);
            }
        }

        stream.WriteByte(0);
        return stream.ToArray();
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }
}
