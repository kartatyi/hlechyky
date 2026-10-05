using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Hlechyky.Turn;

/// <summary>Вбудований ретранслятор (розділ «Turn» в appsettings).</summary>
public sealed class TurnOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>UDP-порт, на який браузери стукають (на MikroTik — dst-nat сюди ж).</summary>
    public int Port { get; set; } = 3478;
    /// <summary>Порти ретрансляції — по одному на кожне з'єднання, що йде через ретранслятор (теж dst-nat на роутері).</summary>
    public int RelayFrom { get; set; } = 49160;
    public int RelayTo { get; set; } = 49359;
    /// <summary>Публічна адреса (домен чи IP), яку бачать друзі. Порожньо — ретранслятор не вмикається.</summary>
    public string PublicHost { get; set; } = "";
    /// <summary>Адреса в домашній мережі для своїх (їм до публічної — лише через «петлю» роутера). Порожньо — сама.</summary>
    public string LanHost { get; set; } = "";
    public string Realm { get; set; } = "hlechyky";
    /// <summary>Скільки з'єднань через ретранслятор тримає одна вкладка (у сітці на десятьох — дев'ять).</summary>
    public int MaxPerUser { get; set; } = 24;
    /// <summary>Лише для тестів: пересилати й на приватні адреси.</summary>
    public bool AllowPrivatePeers { get; set; }
    /// <summary>Лише для тестів: слухати на цій адресі (типово — на всіх).</summary>
    public string Bind { get; set; } = "";
}

/// <summary>
/// Ретранслятор TURN (RFC 5766, лише UDP й IPv4) для Посиденьок: коли двоє за «сірим» NAT провайдера напряму не
/// з'єднуються, голос іде через цей ПК. Живе в процесі сервера — окремих програм і Docker не треба.
/// <list type="bullet">
/// <item>Логіни тимчасові, як у coturn (use-auth-secret): ім'я «строк:позивний», пароль — HMAC-SHA1(секрет, ім'я); секрет
///   лежить у data/turn.key і переживає перезапуски. Роздає їх VoiceChat лише тим, хто зайшов у голос.</item>
/// <item>Пересилає лише туди, куди браузер дав дозвіл (CreatePermission/ChannelBind), і ніколи — у домашню мережу
///   (<see cref="StunMessage.IsPrivate"/>).</item>
/// <item>Пакет на власну публічну адресу й власний порт ретрансляції (друг за NAT ↔ ти через ретранслятор) передається
///   всередині процесу: роутер «петлю» на ці порти не робить, і такий пакет інакше загубився б.</item>
/// <item>Свої (з домашньої мережі) отримують адресу ретранслятора в домашній мережі (<see cref="IceFor"/>).</item>
/// </list>
/// Перезапуск сервера (деплой) рве всі кімнатки ретранслятора; браузери перепідключаються самі за кілька секунд.
/// </summary>
public sealed class TurnServer(IOptionsMonitor<TurnOptions> options, ILogger<TurnServer> log) : BackgroundService
{
    const int DefaultLifetime = 600, MaxLifetime = 3600, PermissionLifetime = 300, ChannelLifetime = 600, NonceSeconds = 3600;
    static readonly byte[] Software = Encoding.UTF8.GetBytes("Hlechyky TURN");

    sealed class Allocation
    {
        public required IPEndPoint Client;
        public required string User;
        public required byte[] Key;
        public required Socket Relay;
        public required int RelayPort;
        public DateTime Expires;
        public readonly Dictionary<IPAddress, DateTime> Perms = [];
        public readonly Dictionary<ushort, (IPEndPoint Peer, DateTime Expires)> Channels = [];
        public readonly Dictionary<IPEndPoint, ushort> ChannelOf = [];
        public byte[] AllocTxn = [];
        public byte[] AllocResponse = [];
        public readonly CancellationTokenSource Stop = new();
    }

    readonly object _lock = new();
    readonly Dictionary<IPEndPoint, Allocation> _byClient = [];
    readonly ConcurrentDictionary<int, Allocation> _byPort = new();
    Socket? _socket;
    byte[] _secret = [];
    IPAddress? _public, _lan;

    /// <summary>Ретранслятор слухає й знає свою публічну адресу — можна роздавати логіни.</summary>
    public bool Available => _socket is not null && _public is not null;
    public int Allocations { get { lock (_lock) return _byClient.Count; } }
    public int ListenPort => (_socket?.LocalEndPoint as IPEndPoint)?.Port ?? 0;

    // ---------- логіни ----------

    /// <summary>Сервер ICE для цього браузера: свій (з домашньої мережі) — на адресу в мережі, решта — на публічну.</summary>
    public IceServer? IceFor(string peer, bool lan, int ttlHours = 12)
    {
        if (!Available) return null;
        var host = lan && _lan is not null ? _lan : _public!;
        var user = $"{DateTimeOffset.UtcNow.AddHours(Math.Max(1, ttlHours)).ToUnixTimeSeconds()}:{peer}";
        return new IceServer([$"turn:{host}:{ListenPort}?transport=udp"], user, Password(user));
    }

    string Password(string user) => Convert.ToBase64String(HMACSHA1.HashData(_secret, Encoding.UTF8.GetBytes(user)));

    byte[] KeyFor(string user) =>
        MD5.HashData(Encoding.UTF8.GetBytes($"{user}:{options.CurrentValue.Realm}:{Password(user)}"));

    // ---------- запуск ----------

    /// <summary>Запустити на цьому секреті (тести; сервер бере data/turn.key).</summary>
    public async Task<bool> OpenAsync(byte[] secret, CancellationToken ct)
    {
        _secret = secret;
        var o = options.CurrentValue;
        if (!o.Enabled || string.IsNullOrWhiteSpace(o.PublicHost)) return false;
        _public = await ResolveAsync(o.PublicHost, ct);
        if (_public is null) { log.LogWarning("TURN: не знайшов публічної адреси {Host} — ретранслятор вимкнено", o.PublicHost); return false; }
        _lan = string.IsNullOrWhiteSpace(o.LanHost) ? LocalLan() : await ResolveAsync(o.LanHost, ct);
        try
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            NoConnReset(s);
            s.Bind(new IPEndPoint(string.IsNullOrEmpty(o.Bind) ? IPAddress.Any : IPAddress.Parse(o.Bind), o.Port));
            _socket = s;
        }
        catch (SocketException ex)
        {
            // Порт зайнятий (інший сервер уже тримає ретранслятор — dev-копія поруч із продом) — тоді без нього.
            log.LogWarning("TURN: порт {Port} зайнятий ({Error}) — ретранслятор вимкнено", o.Port, ex.SocketErrorCode);
            return false;
        }
        log.LogInformation("TURN: слухаю udp {Port}, публічна {Public}, у мережі {Lan}, порти {From}–{To}",
            ListenPort, _public, _lan, o.RelayFrom, o.RelayTo);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!await OpenAsync(LoadSecret(), ct)) return;
        await RunAsync(ct);
    }

    /// <summary>Цикл прийому й прибирання. Тести кличуть після <see cref="OpenAsync"/>.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var sweep = new PeriodicTimer(TimeSpan.FromSeconds(15));
        var sweeping = Task.Run(async () => { while (await sweep.WaitForNextTickAsync(ct)) Sweep(); }, ct);
        var buf = new byte[65536];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try { r = await _socket!.ReceiveFromAsync(buf, SocketFlags.None, any, ct); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { break; }
            try { OnClient((IPEndPoint)r.RemoteEndPoint, buf.AsSpan(0, r.ReceivedBytes)); }
            catch (Exception ex) { log.LogDebug(ex, "TURN: пакет не розібрався"); }
        }
        lock (_lock)
        {
            foreach (var a in _byClient.Values.ToList()) Drop(a);
        }
        _socket?.Dispose();
        try { await sweeping; } catch (OperationCanceledException) { }
    }

    byte[] LoadSecret()
    {
        var path = Paths.Resolve("data/turn.key");
        try
        {
            if (File.Exists(path) && File.ReadAllBytes(path) is { Length: >= 32 } k) return k;
            var fresh = RandomNumberGenerator.GetBytes(32);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, fresh);
            return fresh;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "TURN: data/turn.key не читається — ключ лише до перезапуску");
            return RandomNumberGenerator.GetBytes(32);
        }
    }

    static async Task<IPAddress?> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var ip)) return ip;
        try { return (await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct)).FirstOrDefault(); }
        catch (Exception) { return null; }
    }

    /// <summary>Своя адреса в домашній мережі: IPv4 робочого інтерфейсу з типовим шлюзом.</summary>
    static IPAddress? LocalLan()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var p = ni.GetIPProperties();
                if (!p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))) continue;
                var a = p.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
                if (a is not null && StunMessage.IsPrivate(a)) return a;
            }
        }
        catch (Exception) { /* без неї свої підуть на публічну */ }
        return null;
    }

    /// <summary>Windows: ICMP «порт недосяжний» інакше рве наступний ReceiveFrom винятком ConnectionReset.</summary>
    static void NoConnReset(Socket s)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { s.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null); } catch (Exception) { /* не страшно */ }
    }

    // ---------- від браузера ----------

    void OnClient(IPEndPoint from, ReadOnlySpan<byte> b)
    {
        if (StunMessage.IsChannelData(b))
        {
            var ch = BinaryPrimitives.ReadUInt16BigEndian(b);
            var len = BinaryPrimitives.ReadUInt16BigEndian(b[2..]);
            if (4 + len > b.Length) return;
            Allocation? a;
            IPEndPoint? peer = null;
            lock (_lock)
            {
                if (!_byClient.TryGetValue(from, out a)) return;
                if (a.Channels.TryGetValue(ch, out var bound) && bound.Expires > DateTime.UtcNow) peer = bound.Peer;
            }
            if (peer is not null) ToPeer(a, peer, b.Slice(4, len));
            return;
        }
        if (StunMessage.Parse(b) is not { } m) return;
        if (m.Class == StunMessage.Indication)
        {
            if (m.Method == StunMessage.Send) OnSend(from, m);
            return;
        }
        if (m.Class != StunMessage.Request) return;
        var reply = m.Method switch
        {
            StunMessage.Binding => Reply(m, StunMessage.Success, [(StunMessage.AttrXorMappedAddress, StunMessage.XorAddress(from))], null),
            StunMessage.Allocate => OnAllocate(from, m),
            StunMessage.Refresh => OnRefresh(from, m),
            StunMessage.CreatePermission => OnPermission(from, m),
            StunMessage.ChannelBind => OnChannelBind(from, m),
            _ => Fail(m, 400, "Bad Request", null),
        };
        if (reply is not null) SendTo(_socket!, reply, from);
    }

    static byte[] Reply(StunMessage m, int cls, IEnumerable<(ushort, byte[])> attrs, byte[]? key) =>
        StunMessage.Build(m.Method, cls, m.Transaction, attrs.Append((StunMessage.AttrSoftware, Software)), key);

    static byte[] Fail(StunMessage m, int code, string reason, byte[]? key, params (ushort, byte[])[] more) =>
        Reply(m, StunMessage.Error, new (ushort, byte[])[] { (StunMessage.AttrErrorCode, StunMessage.ErrorCode(code, reason)) }.Concat(more), key);

    // ---------- автентифікація ----------

    string MakeNonce()
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString("x");
        var mac = Convert.ToHexString(HMACSHA1.HashData(_secret, Encoding.UTF8.GetBytes("nonce:" + ts)))[..16];
        return ts + "-" + mac;
    }

    bool NonceFresh(string? nonce)
    {
        if (nonce is null || nonce.Split('-') is not [var ts, var mac]) return false;
        var want = Convert.ToHexString(HMACSHA1.HashData(_secret, Encoding.UTF8.GetBytes("nonce:" + ts)))[..16];
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(want), Encoding.ASCII.GetBytes(mac))) return false;
        return long.TryParse(ts, System.Globalization.NumberStyles.HexNumber, null, out var t)
            && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - t < NonceSeconds;
    }

    /// <summary>
    /// Перевірити підпис запиту. Повертає (ім'я, ключ) або готову відмову: 401 з REALM і NONCE (перший запит браузера
    /// завжди без підпису), 438 — застарілий NONCE.
    /// </summary>
    (string User, byte[] Key)? Authenticate(StunMessage m, out byte[]? refusal)
    {
        refusal = null;
        var realm = Encoding.UTF8.GetBytes(options.CurrentValue.Realm);
        var challenge = new (ushort, byte[])[] { (StunMessage.AttrRealm, realm), (StunMessage.AttrNonce, Encoding.UTF8.GetBytes(MakeNonce())) };
        var user = m.GetString(StunMessage.AttrUsername);
        if (user is null || m.IntegrityOffset < 0) { refusal = Fail(m, 401, "Unauthorized", null, challenge); return null; }
        if (!NonceFresh(m.GetString(StunMessage.AttrNonce))) { refusal = Fail(m, 438, "Stale Nonce", null, challenge); return null; }
        // ім'я «строк:позивний» — строк ще не минув
        if (user.Split(':', 2) is not [var exp, _] || !long.TryParse(exp, out var until) || until < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        { refusal = Fail(m, 401, "Unauthorized", null, challenge); return null; }
        var key = KeyFor(user);
        if (!m.CheckIntegrity(key)) { refusal = Fail(m, 401, "Unauthorized", null, challenge); return null; }
        return (user, key);
    }

    // ---------- Allocate ----------

    byte[]? OnAllocate(IPEndPoint from, StunMessage m)
    {
        if (Authenticate(m, out var refusal) is not { } auth) return refusal;
        var (user, key) = auth;
        lock (_lock)
        {
            if (_byClient.TryGetValue(from, out var have))
            {
                // Повтор того самого запиту (відповідь загубилась) — та сама відповідь; інший запит — кімнатка вже є.
                return have.AllocTxn.AsSpan().SequenceEqual(m.Transaction) ? have.AllocResponse : Fail(m, 437, "Allocation Mismatch", key);
            }
            if (m.Get(StunMessage.AttrRequestedTransport) is not { Length: >= 1 } tr || tr[0] != 17) return Fail(m, 442, "Unsupported Transport Protocol", key);
            if (m.Get(StunMessage.AttrRequestedAddressFamily) is { Length: >= 1 } fam && fam[0] != 0x01) return Fail(m, 440, "Address Family not Supported", key);
            if (m.Get(StunMessage.AttrEvenPort) is not null || m.Get(StunMessage.AttrReservationToken) is not null) return Fail(m, 508, "Insufficient Capacity", key);
            var owner = user.Split(':', 2)[1];
            if (_byClient.Values.Count(a => a.User.EndsWith(":" + owner, StringComparison.Ordinal)) >= options.CurrentValue.MaxPerUser)
                return Fail(m, 486, "Allocation Quota Reached", key);
            if (OpenRelay() is not { } relay) return Fail(m, 508, "Insufficient Capacity", key);
            var lifetime = Lifetime(m, DefaultLifetime);
            var a = new Allocation
            {
                Client = from, User = user, Key = key, Relay = relay.Socket, RelayPort = relay.Port,
                Expires = DateTime.UtcNow.AddSeconds(lifetime), AllocTxn = m.Transaction,
            };
            a.AllocResponse = Reply(m, StunMessage.Success,
            [
                (StunMessage.AttrXorRelayedAddress, StunMessage.XorAddress(new IPEndPoint(_public!, relay.Port))),
                (StunMessage.AttrLifetime, StunMessage.U32((uint)lifetime)),
                (StunMessage.AttrXorMappedAddress, StunMessage.XorAddress(from)),
            ], key);
            _byClient[from] = a;
            _byPort[relay.Port] = a;
            _ = Task.Run(() => RelayLoop(a));
            return a.AllocResponse;
        }
    }

    static int Lifetime(StunMessage m, int fallback) =>
        m.Get(StunMessage.AttrLifetime) is { Length: 4 } v ? (int)Math.Min(MaxLifetime, BinaryPrimitives.ReadUInt32BigEndian(v)) : fallback;

    (Socket Socket, int Port)? OpenRelay()
    {
        var o = options.CurrentValue;
        var ports = Enumerable.Range(o.RelayFrom, Math.Max(0, o.RelayTo - o.RelayFrom + 1)).Where(p => !_byPort.ContainsKey(p)).ToList();
        // навмання — щоб порт щойно закритої кімнатки не дістався одразу наступному
        foreach (var port in ports.OrderBy(_ => Random.Shared.Next()))
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                NoConnReset(s);
                s.Bind(new IPEndPoint(string.IsNullOrEmpty(o.Bind) ? IPAddress.Any : IPAddress.Parse(o.Bind), port));
                return (s, port);
            }
            catch (SocketException) { s.Dispose(); }
        }
        return null;
    }

    // ---------- Refresh, CreatePermission, ChannelBind ----------

    /// <summary>Кімнатка цього браузера й перевірка, що запит від того самого імені. null + refusal — відмова.</summary>
    Allocation? Owned(IPEndPoint from, StunMessage m, out byte[]? refusal)
    {
        refusal = null;
        if (Authenticate(m, out refusal) is not { } auth) return null;
        lock (_lock)
        {
            if (!_byClient.TryGetValue(from, out var a) || a.User != auth.User) { refusal = Fail(m, 437, "Allocation Mismatch", auth.Key); return null; }
            return a;
        }
    }

    byte[]? OnRefresh(IPEndPoint from, StunMessage m)
    {
        if (Owned(from, m, out var refusal) is not { } a) return refusal;
        var lifetime = Lifetime(m, DefaultLifetime);
        lock (_lock)
        {
            if (lifetime == 0) Drop(a);
            else a.Expires = DateTime.UtcNow.AddSeconds(lifetime);
        }
        return Reply(m, StunMessage.Success, [(StunMessage.AttrLifetime, StunMessage.U32((uint)lifetime))], a.Key);
    }

    bool PeerAllowed(IPAddress ip) =>
        options.CurrentValue.AllowPrivatePeers || ip.Equals(_public) || !StunMessage.IsPrivate(ip);

    byte[]? OnPermission(IPEndPoint from, StunMessage m)
    {
        if (Owned(from, m, out var refusal) is not { } a) return refusal;
        var peers = m.GetAll(StunMessage.AttrXorPeerAddress).Select(v => StunMessage.ReadXorAddress(v, m.Transaction)).ToList();
        if (peers.Count == 0 || peers.Any(p => p is null)) return Fail(m, 400, "Bad Request", a.Key);
        if (peers.Any(p => !PeerAllowed(p!.Address))) return Fail(m, 403, "Forbidden", a.Key);
        lock (_lock) foreach (var p in peers) a.Perms[p!.Address] = DateTime.UtcNow.AddSeconds(PermissionLifetime);
        return Reply(m, StunMessage.Success, [], a.Key);
    }

    byte[]? OnChannelBind(IPEndPoint from, StunMessage m)
    {
        if (Owned(from, m, out var refusal) is not { } a) return refusal;
        var peer = StunMessage.ReadXorAddress(m.Get(StunMessage.AttrXorPeerAddress), m.Transaction);
        if (m.Get(StunMessage.AttrChannelNumber) is not { Length: 4 } chv || peer is null) return Fail(m, 400, "Bad Request", a.Key);
        var ch = BinaryPrimitives.ReadUInt16BigEndian(chv);
        if (ch is < 0x4000 or > 0x7FFF) return Fail(m, 400, "Bad Request", a.Key);
        if (!PeerAllowed(peer.Address)) return Fail(m, 403, "Forbidden", a.Key);
        lock (_lock)
        {
            // канал уже за іншим співрозмовником чи співрозмовник уже на іншому каналі — так не можна (RFC 5766 §11.2)
            if (a.Channels.TryGetValue(ch, out var bound) && !bound.Peer.Equals(peer)) return Fail(m, 400, "Bad Request", a.Key);
            if (a.ChannelOf.TryGetValue(peer, out var other) && other != ch) return Fail(m, 400, "Bad Request", a.Key);
            a.Channels[ch] = (peer, DateTime.UtcNow.AddSeconds(ChannelLifetime));
            a.ChannelOf[peer] = ch;
            a.Perms[peer.Address] = DateTime.UtcNow.AddSeconds(PermissionLifetime);
        }
        return Reply(m, StunMessage.Success, [], a.Key);
    }

    void OnSend(IPEndPoint from, StunMessage m)
    {
        Allocation? a;
        lock (_lock) if (!_byClient.TryGetValue(from, out a)) return;
        var peer = StunMessage.ReadXorAddress(m.Get(StunMessage.AttrXorPeerAddress), m.Transaction);
        if (peer is null || m.Get(StunMessage.AttrData) is not { } data) return;
        ToPeer(a, peer, data);
    }

    // ---------- пересилання ----------

    /// <summary>Від браузера до співрозмовника — лише з дозволом; на власну кімнатку — всередині процесу.</summary>
    void ToPeer(Allocation a, IPEndPoint peer, ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            if (!a.Perms.TryGetValue(peer.Address, out var until) || until < DateTime.UtcNow) return;
        }
        if (peer.Address.Equals(_public) && _byPort.TryGetValue(peer.Port, out var local))
        {
            FromPeer(local, new IPEndPoint(_public!, a.RelayPort), data);
            return;
        }
        if (!PeerAllowed(peer.Address)) return;
        SendTo(a.Relay, data, peer);
    }

    async Task RelayLoop(Allocation a)
    {
        var buf = new byte[65536];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!a.Stop.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try { r = await a.Relay.ReceiveFromAsync(buf, SocketFlags.None, any, a.Stop.Token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            FromPeer(a, (IPEndPoint)r.RemoteEndPoint, buf.AsSpan(0, r.ReceivedBytes));
        }
    }

    /// <summary>Від співрозмовника до браузера: каналом, якщо прив'язаний, інакше Data-повідомленням. Лише з дозволом.</summary>
    void FromPeer(Allocation a, IPEndPoint peer, ReadOnlySpan<byte> data)
    {
        ushort ch = 0;
        lock (_lock)
        {
            if (!a.Perms.TryGetValue(peer.Address, out var until) || until < DateTime.UtcNow) return;
            if (a.ChannelOf.TryGetValue(peer, out var c) && a.Channels.TryGetValue(c, out var b) && b.Expires > DateTime.UtcNow) ch = c;
        }
        var packet = ch != 0
            ? StunMessage.ChannelData(ch, data)
            : StunMessage.Build(StunMessage.Data, StunMessage.Indication, RandomNumberGenerator.GetBytes(12),
                [(StunMessage.AttrXorPeerAddress, StunMessage.XorAddress(peer)), (StunMessage.AttrData, data.ToArray())], null);
        SendTo(_socket!, packet, a.Client);
    }

    static void SendTo(Socket s, ReadOnlySpan<byte> data, IPEndPoint to)
    {
        try { s.SendTo(data, SocketFlags.None, to); }
        catch (SocketException) { /* недосяжний — його справа */ }
        catch (ObjectDisposedException) { }
    }

    // ---------- прибирання ----------

    void Sweep()
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            foreach (var a in _byClient.Values.ToList())
            {
                if (a.Expires < now) { Drop(a); continue; }
                foreach (var p in a.Perms.Where(p => p.Value < now).Select(p => p.Key).ToList()) a.Perms.Remove(p);
                foreach (var (ch, b) in a.Channels.Where(c => c.Value.Expires < now).ToList())
                {
                    a.Channels.Remove(ch);
                    a.ChannelOf.Remove(b.Peer);
                }
            }
        }
    }

    /// <summary>Закрити кімнатку. Під _lock.</summary>
    void Drop(Allocation a)
    {
        _byClient.Remove(a.Client);
        _byPort.TryRemove(a.RelayPort, out _);
        a.Stop.Cancel();
        a.Relay.Dispose();
    }
}
