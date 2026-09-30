using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Hlechyky.Turn;

/// <summary>
/// Повідомлення STUN/TURN (RFC 5389, RFC 5766) — рівно стільки, скільки треба ретранслятору Посиденьок: розібрати запит
/// браузера, перевірити підпис (MESSAGE-INTEGRITY) і скласти відповідь із підписом і FINGERPRINT.
/// </summary>
public sealed class StunMessage
{
    public const uint Magic = 0x2112A442;
    public const int HeaderSize = 20;

    // методи
    public const int Binding = 0x001, Allocate = 0x003, Refresh = 0x004, Send = 0x006, Data = 0x007, CreatePermission = 0x008, ChannelBind = 0x009;
    // класи
    public const int Request = 0, Indication = 1, Success = 2, Error = 3;

    // атрибути
    public const ushort AttrMappedAddress = 0x0001, AttrUsername = 0x0006, AttrMessageIntegrity = 0x0008, AttrErrorCode = 0x0009,
        AttrUnknown = 0x000A, AttrChannelNumber = 0x000C, AttrLifetime = 0x000D, AttrXorPeerAddress = 0x0012, AttrData = 0x0013,
        AttrRealm = 0x0014, AttrNonce = 0x0015, AttrXorRelayedAddress = 0x0016, AttrRequestedAddressFamily = 0x0017,
        AttrEvenPort = 0x0018, AttrRequestedTransport = 0x0019, AttrDontFragment = 0x001A, AttrXorMappedAddress = 0x0020,
        AttrReservationToken = 0x0022, AttrSoftware = 0x8022, AttrFingerprint = 0x8028;

    public int Method { get; init; }
    public int Class { get; init; }
    public byte[] Transaction { get; init; } = new byte[12];
    /// <summary>Атрибути в тому порядку, як прийшли (значення без вирівнювання).</summary>
    public List<(ushort Type, byte[] Value)> Attributes { get; } = [];
    /// <summary>Сире повідомлення (для перевірки підпису) і де в ньому починається MESSAGE-INTEGRITY (-1 — нема).</summary>
    public byte[] Raw { get; private init; } = [];
    public int IntegrityOffset { get; private init; } = -1;

    public byte[]? Get(ushort type)
    {
        foreach (var (t, v) in Attributes) if (t == type) return v;
        return null;
    }

    public IEnumerable<byte[]> GetAll(ushort type)
    {
        foreach (var (t, v) in Attributes) if (t == type) yield return v;
    }

    public string? GetString(ushort type) => Get(type) is { } v ? Encoding.UTF8.GetString(v) : null;

    public static int TypeOf(int method, int cls) =>
        (method & 0x000F) | ((cls & 1) << 4) | ((method & 0x0070) << 1) | ((cls & 2) << 7) | ((method & 0x0F80) << 2);

    /// <summary>Схоже на STUN: два старші біти нульові, магічне число на місці, довжина кратна 4 і збігається.</summary>
    public static bool Looks(ReadOnlySpan<byte> b) =>
        b.Length >= HeaderSize && (b[0] & 0xC0) == 0
        && BinaryPrimitives.ReadUInt32BigEndian(b[4..]) == Magic
        && BinaryPrimitives.ReadUInt16BigEndian(b[2..]) is var len && len % 4 == 0 && HeaderSize + len <= b.Length;

    public static StunMessage? Parse(ReadOnlySpan<byte> b)
    {
        if (!Looks(b)) return null;
        var type = BinaryPrimitives.ReadUInt16BigEndian(b);
        var len = BinaryPrimitives.ReadUInt16BigEndian(b[2..]);
        var method = (type & 0x000F) | ((type >> 1) & 0x0070) | ((type >> 2) & 0x0F80);
        var cls = ((type >> 4) & 1) | ((type >> 7) & 2);
        var raw = b[..(HeaderSize + len)].ToArray();
        var msg = new StunMessage { Method = method, Class = cls, Transaction = raw[8..20], Raw = raw, IntegrityOffset = FindIntegrity(raw) };
        var pos = HeaderSize;
        while (pos + 4 <= raw.Length)
        {
            var at = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(pos));
            var al = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(pos + 2));
            if (pos + 4 + al > raw.Length) return null;
            msg.Attributes.Add((at, raw[(pos + 4)..(pos + 4 + al)]));
            pos += 4 + ((al + 3) & ~3);
        }
        return msg;
    }

    static int FindIntegrity(byte[] raw)
    {
        var pos = HeaderSize;
        while (pos + 4 <= raw.Length)
        {
            var at = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(pos));
            var al = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(pos + 2));
            if (at == AttrMessageIntegrity) return pos;
            pos += 4 + ((al + 3) & ~3);
        }
        return -1;
    }

    /// <summary>
    /// Підпис MESSAGE-INTEGRITY: HMAC-SHA1 над повідомленням до самого атрибута, коли довжина в заголовку вже рахує й
    /// його (RFC 5389 §15.4). Все, що після (FINGERPRINT), не рахується.
    /// </summary>
    public bool CheckIntegrity(byte[] key)
    {
        if (IntegrityOffset < 0 || IntegrityOffset + 24 > Raw.Length) return false;
        var copy = Raw[..IntegrityOffset];
        BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(2), (ushort)(IntegrityOffset - HeaderSize + 24));
        var mac = HMACSHA1.HashData(key, copy);
        return CryptographicOperations.FixedTimeEquals(mac, Raw.AsSpan(IntegrityOffset + 4, 20));
    }

    // ---------- адреси ----------

    public static IPEndPoint? ReadXorAddress(byte[]? v, byte[] transaction)
    {
        if (v is null || v.Length < 8 || v[1] != 0x01) return null;   // лише IPv4
        var port = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(v.AsSpan(2)) ^ (Magic >> 16));
        var addr = BinaryPrimitives.ReadUInt32BigEndian(v.AsSpan(4)) ^ Magic;
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, addr);
        return new IPEndPoint(new IPAddress(bytes), port);
    }

    public static byte[] XorAddress(IPEndPoint ep)
    {
        var v = new byte[8];
        v[1] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(v.AsSpan(2), (ushort)(ep.Port ^ (int)(Magic >> 16)));
        var addr = ep.Address.MapToIPv4().GetAddressBytes();
        BinaryPrimitives.WriteUInt32BigEndian(v.AsSpan(4), BinaryPrimitives.ReadUInt32BigEndian(addr) ^ Magic);
        return v;
    }

    // ---------- складання ----------

    /// <summary>Скласти повідомлення. key — підписати MESSAGE-INTEGRITY; FINGERPRINT додається завжди.</summary>
    public static byte[] Build(int method, int cls, byte[] transaction, IEnumerable<(ushort Type, byte[] Value)> attrs, byte[]? key)
    {
        var body = new List<byte>(256);
        foreach (var (t, v) in attrs) AppendAttr(body, t, v);
        var msg = new List<byte>(HeaderSize + body.Count + 32);
        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)TypeOf(method, cls));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), Magic);
        transaction.CopyTo(header, 8);
        msg.AddRange(header);
        msg.AddRange(body);
        if (key is not null)
        {
            var arr = msg.ToArray();
            BinaryPrimitives.WriteUInt16BigEndian(arr.AsSpan(2), (ushort)(arr.Length - HeaderSize + 24));
            var mac = HMACSHA1.HashData(key, arr);
            AppendAttr(msg, AttrMessageIntegrity, mac);
        }
        var withFp = msg.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(withFp.AsSpan(2), (ushort)(withFp.Length - HeaderSize + 8));
        var crc = Crc32(withFp) ^ 0x5354554E;
        var fp = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(fp, crc);
        msg.Clear();
        msg.AddRange(withFp);
        AppendAttr(msg, AttrFingerprint, fp);
        var result = msg.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2), (ushort)(result.Length - HeaderSize));
        return result;
    }

    static void AppendAttr(List<byte> to, ushort type, byte[] value)
    {
        Span<byte> h = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(h, type);
        BinaryPrimitives.WriteUInt16BigEndian(h[2..], (ushort)value.Length);
        to.AddRange(h.ToArray());
        to.AddRange(value);
        for (var i = value.Length; i % 4 != 0; i++) to.Add(0);
    }

    public static byte[] ErrorCode(int code, string reason)
    {
        var text = Encoding.UTF8.GetBytes(reason);
        var v = new byte[4 + text.Length];
        v[2] = (byte)(code / 100);
        v[3] = (byte)(code % 100);
        text.CopyTo(v, 4);
        return v;
    }

    public static byte[] U32(uint x)
    {
        var v = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(v, x);
        return v;
    }

    // ---------- CRC32 (FINGERPRINT) ----------

    static readonly uint[] CrcTable = MakeCrcTable();

    static uint[] MakeCrcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFF;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }

    // ---------- ChannelData ----------

    /// <summary>ChannelData: два старші біти 01 — номер каналу 0x4000–0x7FFF, довжина, дані (RFC 5766 §11.4).</summary>
    public static bool IsChannelData(ReadOnlySpan<byte> b) => b.Length >= 4 && (b[0] & 0xC0) == 0x40;

    public static byte[] ChannelData(ushort channel, ReadOnlySpan<byte> data)
    {
        var v = new byte[4 + data.Length];
        BinaryPrimitives.WriteUInt16BigEndian(v, channel);
        BinaryPrimitives.WriteUInt16BigEndian(v.AsSpan(2), (ushort)data.Length);
        data.CopyTo(v.AsSpan(4));
        return v;
    }

    // ---------- адреси, куди не пересилаємо ----------

    /// <summary>
    /// Приватні, петлеві, локальні, CGNAT, групові й службові адреси: ретранслятор стоїть у домашній мережі, і без цієї
    /// заборони будь-хто з логіном міг би слати пакети на роутер чи інші машини вдома.
    /// </summary>
    public static bool IsPrivate(IPAddress a)
    {
        if (a.AddressFamily != AddressFamily.InterNetwork) return true;
        var b = a.GetAddressBytes();
        return b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 192 && b[1] == 0 && b[2] == 0)
            || (b[0] == 198 && (b[1] == 18 || b[1] == 19));
    }
}
