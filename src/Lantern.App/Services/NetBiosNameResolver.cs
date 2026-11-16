using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Lantern.App.Services;

public static class NetBiosNameResolver
{
    private const int NodeStatusPort = 137;
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
            client.Connect(address, NodeStatusPort);
            var transactionId = (ushort)Random.Shared.Next(ushort.MaxValue + 1);
            var query = BuildNodeStatusQuery(transactionId);
            await client.SendAsync(query, cancellationToken);
            var reply = await client.ReceiveAsync(cancellationToken)
                .AsTask()
                .WaitAsync(ReplyTimeout, cancellationToken);
            return reply.RemoteEndPoint.Address.Equals(address) && reply.RemoteEndPoint.Port == NodeStatusPort
                ? ParseNodeStatusResponse(reply.Buffer, transactionId)
                : null;
        }
        catch (Exception exception) when (
            exception is SocketException or TimeoutException or OperationCanceledException)
        {
            return null;
        }
    }

    public static string? ParseNodeStatusResponse(ReadOnlySpan<byte> packet) =>
        ParseNodeStatusResponse(packet, null);

    public static string? ParseNodeStatusResponse(
        ReadOnlySpan<byte> packet,
        ushort? expectedTransactionId)
    {
        if (packet.Length < 12)
        {
            return null;
        }

        var flags = ReadUInt16(packet, 2);
        if ((flags & 0x8000) == 0 || (flags & 0x7a0f) != 0 ||
            (expectedTransactionId.HasValue && ReadUInt16(packet, 0) != expectedTransactionId.Value))
        {
            return null;
        }

        var questionCount = ReadUInt16(packet, 4);
        var answerCount = ReadUInt16(packet, 6);
        var offset = 12;
        for (var index = 0; index < questionCount; index++)
        {
            if (!TrySkipName(packet, ref offset) || offset + 4 > packet.Length)
            {
                return null;
            }

            offset += 4;
        }

        for (var answer = 0; answer < answerCount; answer++)
        {
            if (!TrySkipName(packet, ref offset) || offset + 10 > packet.Length)
            {
                return null;
            }

            var type = ReadUInt16(packet, offset);
            var recordClass = ReadUInt16(packet, offset + 2);
            var dataLength = ReadUInt16(packet, offset + 8);
            offset += 10;
            if (offset + dataLength > packet.Length)
            {
                return null;
            }

            if (type == 0x0021 && recordClass == 1)
            {
                var name = ParseNodeStatusData(packet.Slice(offset, dataLength));
                if (name is not null)
                {
                    return name;
                }
            }

            offset += dataLength;
        }

        return null;
    }

    private static byte[] BuildNodeStatusQuery(ushort transactionId)
    {
        var query = new byte[50];
        WriteUInt16(query, 0, transactionId);
        WriteUInt16(query, 4, 1);
        query[12] = 32;
        Span<byte> netBiosName = stackalloc byte[16];
        netBiosName.Clear();
        netBiosName[0] = (byte)'*';
        for (var index = 0; index < netBiosName.Length; index++)
        {
            query[13 + (index * 2)] = (byte)('A' + (netBiosName[index] >> 4));
            query[14 + (index * 2)] = (byte)('A' + (netBiosName[index] & 0x0f));
        }

        query[45] = 0;
        WriteUInt16(query, 46, 0x0021);
        WriteUInt16(query, 48, 0x0001);
        return query;
    }

    private static string? ParseNodeStatusData(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1)
        {
            return null;
        }

        var count = data[0];
        if (1 + (count * 18) > data.Length)
        {
            return null;
        }

        for (var index = 0; index < count; index++)
        {
            var entryOffset = 1 + (index * 18);
            var suffix = data[entryOffset + 15];
            var flags = ReadUInt16(data, entryOffset + 16);
            var isGroup = (flags & 0x8000) != 0;
            if (suffix != 0 || isGroup)
            {
                continue;
            }

            var name = Encoding.ASCII.GetString(data.Slice(entryOffset, 15)).Trim();
            if (name.Length > 0 && name != "*" &&
                !name.Any(char.IsControl) && !IPAddress.TryParse(name, out _))
            {
                return name;
            }
        }

        return null;
    }

    private static bool TrySkipName(ReadOnlySpan<byte> packet, ref int offset)
    {
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
                return true;
            }

            if ((length & 0xc0) == 0xc0)
            {
                if (cursor >= packet.Length)
                {
                    return false;
                }

                var pointer = ((length & 0x3f) << 8) | packet[cursor++];
                if (pointer < 12 || pointer >= cursor - 2 || ++jumps > 128)
                {
                    return false;
                }

                resumeOffset = resumeOffset >= 0 ? resumeOffset : cursor;
                cursor = pointer;
                continue;
            }

            if ((length & 0xc0) != 0 || cursor + length > packet.Length)
            {
                return false;
            }

            expandedLength += length + 1;
            if (expandedLength > 254)
            {
                return false;
            }

            cursor += length;
        }

        return false;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset) =>
        (ushort)((bytes[offset] << 8) | bytes[offset + 1]);

    private static void WriteUInt16(Span<byte> bytes, int offset, ushort value)
    {
        bytes[offset] = (byte)(value >> 8);
        bytes[offset + 1] = (byte)value;
    }
}
