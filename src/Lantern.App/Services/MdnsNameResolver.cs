using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Lantern.App.Services;

public static class MdnsNameResolver
{
    private const int MdnsPort = 5353;
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromMilliseconds(750);

    public static async Task<string?> ResolveAsync(
        IPAddress address,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        try
        {
            using var client = new UdpClient(AddressFamily.InterNetwork);
            client.Connect(address, MdnsPort);
            var transactionId = (ushort)Random.Shared.Next(ushort.MaxValue + 1);
            var query = BuildReverseLookupQuery(address, transactionId);
            await client.SendAsync(query, cancellationToken);
            var reply = await client.ReceiveAsync(cancellationToken)
                .AsTask()
                .WaitAsync(ReplyTimeout, cancellationToken);
            return ParsePtrResponse(reply.Buffer, address, reply.RemoteEndPoint, transactionId);
        }
        catch (Exception exception) when (
            exception is SocketException or TimeoutException or OperationCanceledException)
        {
            return null;
        }
    }

    public static string? ParsePtrResponse(ReadOnlySpan<byte> packet) =>
        ParsePtrResponseCore(packet, null, null, null);

    public static string? ParsePtrResponse(
        ReadOnlySpan<byte> packet,
        IPAddress queriedAddress,
        IPEndPoint? source = null,
        ushort? expectedTransactionId = null)
    {
        ArgumentNullException.ThrowIfNull(queriedAddress);
        return ParsePtrResponseCore(packet, queriedAddress, source, expectedTransactionId);
    }

    private static string? ParsePtrResponseCore(
        ReadOnlySpan<byte> packet,
        IPAddress? queriedAddress,
        IPEndPoint? source,
        ushort? expectedTransactionId)
    {
        if (packet.Length < 12 ||
            (queriedAddress is not null && queriedAddress.AddressFamily != AddressFamily.InterNetwork) ||
            (source is not null &&
             (source.Port != MdnsPort || !source.Address.Equals(queriedAddress))))
        {
            return null;
        }

        var flags = ReadUInt16(packet, 2);
        if ((flags & 0x8000) == 0 || (flags & 0x7a0f) != 0 ||
            (expectedTransactionId.HasValue && ReadUInt16(packet, 0) != expectedTransactionId.Value))
        {
            return null;
        }

        var expectedOwner = queriedAddress is null ? null : GetReverseLookupName(queriedAddress);
        var questionCount = ReadUInt16(packet, 4);
        var recordCount = ReadUInt16(packet, 6) +
                          ReadUInt16(packet, 8) +
                          ReadUInt16(packet, 10);
        var offset = 12;
        for (var index = 0; index < questionCount; index++)
        {
            if (!TryReadName(packet, ref offset, out _) || offset + 4 > packet.Length)
            {
                return null;
            }

            offset += 4;
        }

        for (var index = 0; index < recordCount; index++)
        {
            if (!TryReadName(packet, ref offset, out var owner) || offset + 10 > packet.Length)
            {
                return null;
            }

            var type = ReadUInt16(packet, offset);
            var recordClass = ReadUInt16(packet, offset + 2) & 0x7fff;
            var timeToLive = ReadUInt32(packet, offset + 4);
            var dataLength = ReadUInt16(packet, offset + 8);
            offset += 10;
            if (offset + dataLength > packet.Length)
            {
                return null;
            }

            if (type == 12 && recordClass == 1 && timeToLive != 0 &&
                (expectedOwner is null
                    ? IsIpv4ReverseLookupName(owner)
                    : string.Equals(owner, expectedOwner, StringComparison.OrdinalIgnoreCase)))
            {
                var dataOffset = offset;
                if (!TryReadName(packet, ref dataOffset, out var target) ||
                    dataOffset != offset + dataLength)
                {
                    return null;
                }

                var normalized = NormalizeHostName(target);
                if (normalized is not null)
                {
                    return normalized;
                }
            }

            offset += dataLength;
        }

        return null;
    }

    private static byte[] BuildReverseLookupQuery(IPAddress address, ushort transactionId)
    {
        using var stream = new MemoryStream();
        WriteUInt16(stream, transactionId);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 1);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        foreach (var label in GetReverseLookupName(address).Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }

        stream.WriteByte(0);
        WriteUInt16(stream, 12);
        WriteUInt16(stream, 1);
        return stream.ToArray();
    }

    private static string GetReverseLookupName(IPAddress address) =>
        string.Join('.', address.GetAddressBytes()
            .Reverse()
            .Select(value => value.ToString(CultureInfo.InvariantCulture))
            .Concat(["in-addr", "arpa"]));

    private static bool IsIpv4ReverseLookupName(string value)
    {
        var labels = value.Split('.');
        return labels.Length == 6 &&
               string.Equals(labels[4], "in-addr", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(labels[5], "arpa", StringComparison.OrdinalIgnoreCase) &&
               labels.Take(4).All(label =>
                   byte.TryParse(label, NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }

    private static bool TryReadName(
        ReadOnlySpan<byte> packet,
        ref int offset,
        out string name)
    {
        var labels = new List<string>();
        var cursor = offset;
        var resumeOffset = -1;
        var expandedLength = 0;
        var jumps = 0;
        while (cursor < packet.Length)
        {
            var length = packet[cursor++];
            if (length == 0)
            {
                offset = resumeOffset >= 0 ? resumeOffset : cursor;
                name = string.Join('.', labels);
                return true;
            }

            if ((length & 0xc0) == 0xc0)
            {
                if (cursor >= packet.Length)
                {
                    break;
                }

                var pointer = ((length & 0x3f) << 8) | packet[cursor++];
                if (pointer < 12 || pointer >= cursor - 2 || ++jumps > 128)
                {
                    break;
                }

                resumeOffset = resumeOffset >= 0 ? resumeOffset : cursor;
                cursor = pointer;
                continue;
            }

            if ((length & 0xc0) != 0 || cursor + length > packet.Length)
            {
                break;
            }

            expandedLength += length + (labels.Count == 0 ? 0 : 1);
            if (expandedLength > 253)
            {
                break;
            }

            var label = Encoding.UTF8.GetString(packet.Slice(cursor, length));
            if (label.Contains('.') || label.Any(char.IsControl))
            {
                break;
            }

            labels.Add(label);
            cursor += length;
        }

        name = string.Empty;
        return false;
    }

    private static string? NormalizeHostName(string value)
    {
        var name = value.Trim().TrimEnd('.');
        if (name.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^6];
        }

        return name.Length is > 0 and <= 253 && !IPAddress.TryParse(name, out _)
            ? name
            : null;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset) =>
        (ushort)((bytes[offset] << 8) | bytes[offset + 1]);

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
        ((uint)bytes[offset] << 24) |
        ((uint)bytes[offset + 1] << 16) |
        ((uint)bytes[offset + 2] << 8) |
        bytes[offset + 3];

    private static void WriteUInt16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }
}
