using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Hlechyky.Tests.Support;
using Hlechyky.Turn;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Вбудований ретранслятор (Turn/TurnServer.cs) на справжніх UDP-сокетах через 127.0.0.1: так, як його бачить браузер
/// (RFC 5766) — виклик 401, Allocate з підписом, дозволи, Send/Data, канали, відмови.
/// </summary>
[Collection(SerialPerf.Name)]
public sealed class TurnServerTests : IAsyncLifetime
{
    static readonly byte[] Secret = Encoding.UTF8.GetBytes("test-secret-test-secret-test-secret!");
    const string Peer = "aaaaaaaaaaaa";

    readonly CancellationTokenSource _cts = new();
    TurnServer _turn = null!;
    Task _run = Task.CompletedTask;
    readonly List<UdpClient> _sockets = [];

    static int _range = 52000 + Random.Shared.Next(0, 50) * 40;

    public async Task InitializeAsync() => (_turn, _run) = await Start(allowPrivate: true);

    async Task<(TurnServer, Task)> Start(bool allowPrivate)
    {
        var from = Interlocked.Add(ref _range, 40);
        var o = new TurnOptions
        {
            Enabled = true, Port = 0, RelayFrom = from, RelayTo = from + 30, PublicHost = "127.0.0.1", LanHost = "127.0.0.1",
            Bind = "127.0.0.1", AllowPrivatePeers = allowPrivate, MaxPerUser = 3,
        };
        var turn = new TurnServer(new FixedOptions<TurnOptions>(o), NullLogger<TurnServer>.Instance);
        Assert.True(await turn.OpenAsync(Secret, _cts.Token));
        return (turn, turn.RunAsync(_cts.Token));
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        foreach (var s in _sockets) s.Dispose();
        try { await _run; } catch (OperationCanceledException) { }
    }

    UdpClient Socket()
    {
        var s = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        _sockets.Add(s);
        return s;
    }

    IPEndPoint Server(TurnServer? t = null) => new(IPAddress.Loopback, (t ?? _turn).ListenPort);

    static async Task<byte[]?> Receive(UdpClient s, int ms = 2000)
    {
        using var cts = new CancellationTokenSource(ms);
        try { return (await s.ReceiveAsync(cts.Token)).Buffer; }
        catch (OperationCanceledException) { return null; }
    }

    static async Task<StunMessage> Ask(UdpClient s, IPEndPoint to, int method, IEnumerable<(ushort, byte[])> attrs, byte[]? key)
    {
        var txn = RandomNumberGenerator.GetBytes(12);
        await s.SendAsync(StunMessage.Build(method, StunMessage.Request, txn, attrs, key), to);
        var got = await Receive(s) ?? throw new TimeoutException("нема відповіді");
        var m = StunMessage.Parse(got)!;
        Assert.Equal(txn, m.Transaction);
        return m;
    }

    static int ErrorOf(StunMessage m)
    {
        var v = m.Get(StunMessage.AttrErrorCode)!;
        return v[2] * 100 + v[3];
    }

    /// <summary>Allocate, як браузер: без підпису — 401 з REALM і NONCE, тоді з підписом. Повертає ключ і адресу ретрансляції.</summary>
    async Task<(byte[] Key, string User, byte[] Realm, byte[] Nonce, IPEndPoint Relayed)> Allocate(UdpClient c, TurnServer? t = null, string? password = null)
    {
        t ??= _turn;
        var ice = t.IceFor(Peer, lan: false)!;
        var first = await Ask(c, Server(t), StunMessage.Allocate, [(StunMessage.AttrRequestedTransport, [17, 0, 0, 0])], null);
        Assert.Equal(StunMessage.Error, first.Class);
        Assert.Equal(401, ErrorOf(first));
        var realm = first.Get(StunMessage.AttrRealm)!;
        var nonce = first.Get(StunMessage.AttrNonce)!;
        var key = MD5.HashData(Encoding.UTF8.GetBytes($"{ice.Username}:{Encoding.UTF8.GetString(realm)}:{password ?? ice.Credential}"));
        var m = await Ask(c, Server(t), StunMessage.Allocate,
        [
            (StunMessage.AttrRequestedTransport, [17, 0, 0, 0]),
            (StunMessage.AttrUsername, Encoding.UTF8.GetBytes(ice.Username!)),
            (StunMessage.AttrRealm, realm), (StunMessage.AttrNonce, nonce),
        ], key);
        if (m.Class != StunMessage.Success) return (key, ice.Username!, realm, nonce, null!);
        Assert.True(m.CheckIntegrity(key), "відповідь підписана тим самим ключем");
        return (key, ice.Username!, realm, nonce, StunMessage.ReadXorAddress(m.Get(StunMessage.AttrXorRelayedAddress), m.Transaction)!);
    }

    static (ushort, byte[])[] Auth(string user, byte[] realm, byte[] nonce) =>
        [(StunMessage.AttrUsername, Encoding.UTF8.GetBytes(user)), (StunMessage.AttrRealm, realm), (StunMessage.AttrNonce, nonce)];

    static IPEndPoint Local(UdpClient s) => (IPEndPoint)s.Client.LocalEndPoint!;

    [Fact]
    public async Task Binding_tells_the_mapped_address()
    {
        var c = Socket();
        var m = await Ask(c, Server(), StunMessage.Binding, [], null);
        Assert.Equal(StunMessage.Success, m.Class);
        Assert.Equal(Local(c), StunMessage.ReadXorAddress(m.Get(StunMessage.AttrXorMappedAddress), m.Transaction));
    }

    [Fact]
    public async Task Ice_server_points_at_the_listener_with_expiring_login()
    {
        var ice = _turn.IceFor(Peer, lan: true)!;
        Assert.Equal($"turn:127.0.0.1:{_turn.ListenPort}?transport=udp", Assert.Single(ice.Urls));
        var exp = long.Parse(ice.Username!.Split(':')[0]);
        Assert.InRange(exp - DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 11 * 3600, 13 * 3600);
        Assert.EndsWith(":" + Peer, ice.Username);
    }

    [Fact]
    public async Task Allocate_send_data_and_channels()
    {
        var c = Socket();
        var p = Socket();
        var (key, user, realm, nonce, relayed) = await Allocate(c);
        Assert.NotNull(relayed);
        Assert.Equal(1, _turn.Allocations);

        // без дозволу співрозмовник до браузера не достукається
        await p.SendAsync(Encoding.UTF8.GetBytes("рано"), relayed);
        Assert.Null(await Receive(c, 300));

        var perm = await Ask(c, Server(), StunMessage.CreatePermission,
            [(StunMessage.AttrXorPeerAddress, StunMessage.XorAddress(Local(p))), .. Auth(user, realm, nonce)], key);
        Assert.Equal(StunMessage.Success, perm.Class);

        // Send → співрозмовнику, з адреси ретрансляції
        await c.SendAsync(StunMessage.Build(StunMessage.Send, StunMessage.Indication, RandomNumberGenerator.GetBytes(12),
            [(StunMessage.AttrXorPeerAddress, StunMessage.XorAddress(Local(p))), (StunMessage.AttrData, Encoding.UTF8.GetBytes("привіт"))], null), Server());
        var got = await p.ReceiveAsync(new CancellationTokenSource(2000).Token);
        Assert.Equal("привіт", Encoding.UTF8.GetString(got.Buffer));
        Assert.Equal(relayed.Port, got.RemoteEndPoint.Port);

        // співрозмовник → браузеру Data-повідомленням
        await p.SendAsync(Encoding.UTF8.GetBytes("навзаєм"), relayed);
        var data = StunMessage.Parse((await Receive(c))!)!;
        Assert.Equal(StunMessage.Data, data.Method);
        Assert.Equal(StunMessage.Indication, data.Class);
        Assert.Equal(Local(p), StunMessage.ReadXorAddress(data.Get(StunMessage.AttrXorPeerAddress), data.Transaction));
        Assert.Equal("навзаєм", Encoding.UTF8.GetString(data.Get(StunMessage.AttrData)!));

        // канал 0x4001: ChannelData в обидва боки
        var ch = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(ch, 0x4001);
        var bind = await Ask(c, Server(), StunMessage.ChannelBind,
            [(StunMessage.AttrChannelNumber, ch), (StunMessage.AttrXorPeerAddress, StunMessage.XorAddress(Local(p))), .. Auth(user, realm, nonce)], key);
        Assert.Equal(StunMessage.Success, bind.Class);
        await c.SendAsync(StunMessage.ChannelData(0x4001, Encoding.UTF8.GetBytes("каналом")), Server());
        Assert.Equal("каналом", Encoding.UTF8.GetString((await p.ReceiveAsync(new CancellationTokenSource(2000).Token)).Buffer));
        await p.SendAsync(Encoding.UTF8.GetBytes("і назад"), relayed);
        var back = (await Receive(c))!;
        Assert.True(StunMessage.IsChannelData(back));
        Assert.Equal(0x4001, BinaryPrimitives.ReadUInt16BigEndian(back));
        Assert.Equal("і назад", Encoding.UTF8.GetString(back.AsSpan(4, BinaryPrimitives.ReadUInt16BigEndian(back.AsSpan(2)))));

        // Refresh з нулем — кімнатки нема
        var bye = await Ask(c, Server(), StunMessage.Refresh, [(StunMessage.AttrLifetime, StunMessage.U32(0)), .. Auth(user, realm, nonce)], key);
        Assert.Equal(StunMessage.Success, bye.Class);
        Assert.Equal(0, _turn.Allocations);
    }

    [Fact]
    public async Task Two_relays_talk_to_each_other()
    {
        var a = Socket();
        var b = Socket();
        var ra = await Allocate(a);
        var rb = await Allocate(b);
        foreach (var (c, me, other) in new[] { (a, ra, rb.Relayed), (b, rb, ra.Relayed) })
        {
            var perm = await Ask(c, Server(), StunMessage.CreatePermission,
                [(StunMessage.AttrXorPeerAddress, StunMessage.XorAddress(other)), .. Auth(me.User, me.Realm, me.Nonce)], me.Key);
            Assert.Equal(StunMessage.Success, perm.Class);
        }
        await a.SendAsync(StunMessage.Build(StunMessage.Send, StunMessage.Indication, RandomNumberGenerator.GetBytes(12),
            [(StunMessage.AttrXorPeerAddress, StunMessage.XorAddress(rb.Relayed)), (StunMessage.AttrData, [1, 2, 3])], null), Server());
        var data = StunMessage.Parse((await Receive(b))!)!;
        Assert.Equal(ra.Relayed, StunMessage.ReadXorAddress(data.Get(StunMessage.AttrXorPeerAddress), data.Transaction));
        Assert.Equal(new byte[] { 1, 2, 3 }, data.Get(StunMessage.AttrData));
    }

    [Fact]
    public async Task Wrong_password_is_refused()
    {
        var c = Socket();
        var (_, _, _, _, relayed) = await Allocate(c, password: "не той");
        Assert.Null(relayed);
        Assert.Equal(0, _turn.Allocations);
    }

    [Fact]
    public async Task Quota_per_user()
    {
        for (var i = 0; i < 3; i++) Assert.NotNull((await Allocate(Socket())).Relayed);
        var (_, _, _, _, relayed) = await Allocate(Socket());
        Assert.Null(relayed);
    }

    [Fact]
    public async Task Home_network_is_off_limits()
    {
        var (turn, run) = await Start(allowPrivate: false);
        try
        {
            var c = Socket();
            var r = await Allocate(c, turn);
            var perm = await Ask(c, Server(turn), StunMessage.CreatePermission,
                [(StunMessage.AttrXorPeerAddress, StunMessage.XorAddress(new IPEndPoint(IPAddress.Parse("192.168.88.1"), 80))), .. Auth(r.User, r.Realm, r.Nonce)], r.Key);
            Assert.Equal(403, ErrorOf(perm));
            // а власна публічна адреса (кімнатка іншого браузера) — можна
            var own = await Ask(c, Server(turn), StunMessage.CreatePermission,
                [(StunMessage.AttrXorPeerAddress, StunMessage.XorAddress(r.Relayed)), .. Auth(r.User, r.Realm, r.Nonce)], r.Key);
            Assert.Equal(StunMessage.Success, own.Class);
        }
        finally { await Task.WhenAny(run, Task.Delay(10)); }
    }

    [Fact]
    public void Private_addresses()
    {
        foreach (var a in new[] { "10.1.2.3", "192.168.88.254", "172.20.0.1", "127.0.0.1", "169.254.1.1", "100.64.0.1", "224.0.0.1", "0.0.0.0" })
            Assert.True(StunMessage.IsPrivate(IPAddress.Parse(a)), a);
        foreach (var a in new[] { "134.249.147.16", "8.8.8.8", "172.32.0.1", "100.128.0.1" })
            Assert.False(StunMessage.IsPrivate(IPAddress.Parse(a)), a);
    }

    [Fact]
    public void Fingerprint_and_integrity_round_trip()
    {
        var key = MD5.HashData(Encoding.UTF8.GetBytes("u:r:p"));
        var raw = StunMessage.Build(StunMessage.Binding, StunMessage.Request, new byte[12], [(StunMessage.AttrSoftware, Encoding.UTF8.GetBytes("x"))], key);
        var m = StunMessage.Parse(raw)!;
        Assert.True(m.CheckIntegrity(key));
        Assert.False(m.CheckIntegrity(MD5.HashData(Encoding.UTF8.GetBytes("u:r:q"))));
        var fp = m.Get(StunMessage.AttrFingerprint)!;
        var crc = StunMessage.Crc32(raw.AsSpan(0, raw.Length - 8)) ^ 0x5354554E;
        Assert.Equal(crc, BinaryPrimitives.ReadUInt32BigEndian(fp));
    }
}
