using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;

namespace NetPilot;

/// <summary>Bounded DNS wire parsing. No resolver or operating-system DNS calls.</summary>
public static class DnsWire
{
    internal const int MaxMessage = 65535;
    private sealed record Question(byte[] Name, ushort Type, ushort Class);
    private sealed record Record(string Name, ushort Type, ushort Class, int Data, int Length, bool Answer);
    private sealed record Message(ushort Id, ushort Flags, Question Question, List<Record> Records);

    public static byte[] Query(string host, ushort type = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (type == 0) throw new ArgumentException("Недопустимый тип DNS запроса.", nameof(type));
        var labels = host == "." ? [] : host.TrimEnd('.').Split('.');
        if (host.EndsWith("..", StringComparison.Ordinal)) throw new ArgumentException("Пустая метка DNS.", nameof(host));
        using var packet = new MemoryStream();
        packet.Write(new byte[12]);
        var idn = new IdnMapping();
        foreach (var label in labels)
        {
            if (label.Length == 0) throw new ArgumentException("Пустая метка DNS.", nameof(host));
            var ascii = idn.GetAscii(label);
            if (ascii.Length > 63 || ascii.Any(c => c > 127 || c <= 32)) throw new ArgumentException("Недопустимая метка DNS.", nameof(host));
            packet.WriteByte((byte)ascii.Length); packet.Write(System.Text.Encoding.ASCII.GetBytes(ascii));
        }
        packet.WriteByte(0);
        if (packet.Length - 12 > 255) throw new ArgumentException("Имя DNS длиннее 255 байт.", nameof(host));
        Write16(packet, type); Write16(packet, 1);
        var bytes = packet.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)RandomNumberGenerator.GetInt32(65536));
        bytes[2] = 1; bytes[5] = 1; // RD, one IN question; no implicit EDNS or alternate resolver.
        return bytes;
    }

    public static void ValidateResponse(byte[] query, byte[] response)
    {
        var request = Parse(query, false);
        var reply = Parse(response, true, allowTruncated: true);
        if (request.Id != reply.Id || (request.Flags & 0x7800) != (reply.Flags & 0x7800)
            || request.Question.Type != reply.Question.Type || request.Question.Class != reply.Question.Class
            || !request.Question.Name.AsSpan().SequenceEqual(reply.Question.Name))
            throw new InvalidDataException("Ответ DNS не соответствует транзакции и вопросу.");
    }

    public static IReadOnlyList<IPAddress> Addresses(byte[] response)
    {
        var message = Parse(response, true);
        if ((message.Flags & 0x020f) != 0) return [];
        var name = Key(message.Question.Name);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<IPAddress>();
        for (var hops = 0; hops < 64; hops++)
        {
            if (!visited.Add(name)) throw new InvalidDataException("Цикл CNAME в ответе DNS.");
            var records = message.Records.Where(r => r.Answer && r.Class == 1 && r.Name == name).ToArray();
            foreach (var record in records.Where(r => r.Type is 1 or 28))
            {
                var address = new IPAddress(response.AsSpan(record.Data, record.Length));
                if (!result.Contains(address)) result.Add(address);
                if (result.Count == 64) return result;
            }
            var aliases = records.Where(r => r.Type == 5).ToArray();
            if (aliases.Length == 0) return result;
            var target = "";
            foreach (var alias in aliases)
            {
                var offset = alias.Data; var next = Key(ReadName(response, ref offset));
                if (target.Length > 0 && target != next) throw new InvalidDataException("Противоречивые записи CNAME.");
                target = next;
            }
            name = target;
        }
        throw new InvalidDataException("Слишком длинная цепочка CNAME.");
    }

    public static byte[] Failure(byte[] query)
    {
        try
        {
            var request = Parse(query, false);
            return MinimalReply(request, (ushort)(0x8082 | (request.Flags & 0x7910)));
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            var failure = new byte[12];
            if (query is { Length: >= 2 }) { failure[0] = query[0]; failure[1] = query[1]; }
            failure[2] = 0x80; failure[3] = 0x82;
            return failure;
        }
    }

    internal static void ValidateQuery(byte[] query) => _ = Parse(query, false);
    internal static bool IsTruncated(byte[] packet) => packet.Length >= 4 && (packet[2] & 2) != 0;
    internal static int ResponseCode(byte[] response) => response[3] & 15;

    internal static int UdpPayloadSize(byte[] query)
    {
        var message = Parse(query, false);
        var opt = message.Records.FirstOrDefault(r => r.Type == 41);
        return opt == null ? 512 : Math.Clamp((int)opt.Class, 512, 4096);
    }

    internal static byte[] Truncate(byte[] query, byte[] response)
    {
        var request = Parse(query, false);
        var flags = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2)) & 0xf99f);
        flags = (ushort)((flags | 0x8200) & ~0x0020); // TC and QR, no AD assertion for removed records.
        return MinimalReply(request, flags);
    }

    private static byte[] MinimalReply(Message request, ushort flags)
    {
        using var packet = new MemoryStream();
        Write16(packet, request.Id); Write16(packet, flags); Write16(packet, 1);
        Write16(packet, 0); Write16(packet, 0); Write16(packet, 0);
        packet.Write(request.Question.Name); Write16(packet, request.Question.Type); Write16(packet, request.Question.Class);
        return packet.ToArray();
    }

    private static Message Parse(byte[] packet, bool response, bool allowTruncated = false)
    {
        if (packet is null || packet.Length is < 12 or > MaxMessage) throw new InvalidDataException("Недопустимая длина DNS сообщения.");
        var id = U16(packet, 0); var flags = U16(packet, 2);
        if (((flags & 0x8000) != 0) != response || (flags & 0x0040) != 0 || U16(packet, 4) != 1)
            throw new InvalidDataException("Некорректный заголовок DNS.");
        if (!response && ((flags & 0x7a0f) != 0 || U16(packet, 6) != 0 || U16(packet, 8) != 0))
            throw new InvalidDataException("Поддерживается обычный DNS QUERY с одним вопросом.");
        var offset = 12; var name = ReadName(packet, ref offset);
        Ensure(packet, offset, 4);
        var question = new Question(name, U16(packet, offset), U16(packet, offset + 2)); offset += 4;
        if (question.Type == 0 || question.Class == 0) throw new InvalidDataException("Недопустимый DNS вопрос.");
        var records = new List<Record>();
        // A UDP TC response may end inside an RR. Its transaction and complete question must still match.
        if (response && allowTruncated && (flags & 0x0200) != 0) return new(id, flags, question, records);
        var answerCount = U16(packet, 6); var total = answerCount + U16(packet, 8) + U16(packet, 10);
        if (total > 4096) throw new InvalidDataException("Слишком много DNS записей.");
        for (var index = 0; index < total; index++)
        {
            var recordName = Key(ReadName(packet, ref offset)); Ensure(packet, offset, 10);
            var type = U16(packet, offset); var recordClass = U16(packet, offset + 2); var length = U16(packet, offset + 8); offset += 10;
            Ensure(packet, offset, length);
            if (recordClass == 1 && ((type == 1 && length != 4) || (type == 28 && length != 16)))
                throw new InvalidDataException("Некорректная длина IP записи DNS.");
            if (type is 2 or 5 or 12 or 39)
            {
                var nameEnd = offset; _ = ReadName(packet, ref nameEnd);
                if (nameEnd != offset + length) throw new InvalidDataException("Некорректная длина имени в записи DNS.");
            }
            records.Add(new(recordName, type, recordClass, offset, length, index < answerCount)); offset += length;
        }
        if (offset != packet.Length) throw new InvalidDataException("Лишние данные в DNS сообщении.");
        return new(id, flags, question, records);
    }

    private static byte[] ReadName(byte[] packet, ref int offset)
    {
        var position = offset; var returnOffset = -1; var visited = new HashSet<int>(); var name = new List<byte>(64);
        for (var hops = 0; hops < 128; hops++)
        {
            Ensure(packet, position, 1);
            if (!visited.Add(position)) throw new InvalidDataException("Цикл сжатия DNS имени.");
            var length = packet[position];
            if ((length & 0xc0) == 0xc0)
            {
                Ensure(packet, position, 2);
                var pointer = ((length & 63) << 8) | packet[position + 1];
                if (pointer < 12 || pointer >= position) throw new InvalidDataException("Недопустимый указатель DNS имени.");
                if (returnOffset < 0) returnOffset = position + 2;
                position = pointer; continue;
            }
            if ((length & 0xc0) != 0) throw new InvalidDataException("Неизвестный формат метки DNS.");
            position++;
            name.Add(length);
            if (length == 0)
            {
                if (name.Count > 255) throw new InvalidDataException("DNS имя длиннее 255 байт.");
                offset = returnOffset < 0 ? position : returnOffset; return name.ToArray();
            }
            Ensure(packet, position, length);
            if (name.Count + length + 1 > 255) throw new InvalidDataException("DNS имя длиннее 255 байт.");
            for (var i = 0; i < length; i++)
            {
                var b = packet[position++]; name.Add(b is >= 65 and <= 90 ? (byte)(b + 32) : b);
            }
        }
        throw new InvalidDataException("Слишком сложное сжатие DNS имени.");
    }

    private static string Key(byte[] name) => Convert.ToHexString(name);
    private static ushort U16(byte[] packet, int offset) => BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset, 2));
    private static void Ensure(byte[] packet, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > packet.Length - count) throw new InvalidDataException("Оборванное DNS сообщение.");
    }
    private static void Write16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(bytes, value); stream.Write(bytes);
    }
}
