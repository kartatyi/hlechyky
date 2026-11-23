using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Hlechyky.Games;
using Hlechyky.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// 🖥 Екран наживо: справжній Kestrel на випадковому порту і WebSocket <c>/api/screen/ws</c>. Застигла програма (сокет
/// відкритий, а сама нічого не читає) не має лишатися «на зв'язку»: сервер рве її за пропущені пінги, а на закриття
/// після нового ключа чекає недовго. 23.11.2026 саме так екран висів «на зв'язку» навіть після нового ключа, а зміни
/// йшли в порожнечу.
/// </summary>
public class ScreenSocketTests
{
    static async Task<(WebApplication App, Screen Screen, int Port)> Serve(Action<Screen> tune)
    {
        var screen = new Screen(new MemoryGameStore(), new FakeClock(), NullLogger<Screen>.Instance);
        tune(screen);
        var b = WebApplication.CreateBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Services.AddSingleton(screen);
        var app = b.Build();
        Screen.Map(app);
        await app.StartAsync();
        return (app, screen, new Uri(app.Urls.First()).Port);
    }

    static object? Prop(object o, string name) => o.GetType().GetProperty(name)!.GetValue(o);
    static int Streams(Screen s) => (int)Prop(Prop(s.AdminView(false), "device")!, "streams")!;
    static bool Online(Screen s) => (bool)Prop(Prop(s.AdminView(false), "device")!, "online")!;

    static async Task<bool> Until(Func<bool> ok, double seconds)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            if (ok()) return true;
            await Task.Delay(20);
        }
        return ok();
    }

    /// <summary>Під'єднатися й замовкнути: прочитати лише відповідь на рукостискання — як програма, що застигла.</summary>
    static async Task<TcpClient> Frozen(int port, string key)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port);
        var net = tcp.GetStream();
        var hello = $"GET /api/screen/ws?key={Uri.EscapeDataString(key)} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n"
            + "Upgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\n"
            + $"Sec-WebSocket-Key: {Convert.ToBase64String(new byte[16])}\r\n\r\n";
        await net.WriteAsync(Encoding.ASCII.GetBytes(hello));
        var head = new StringBuilder();
        var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n") && await net.ReadAsync(one) == 1) head.Append((char)one[0]);
        Assert.StartsWith("HTTP/1.1 101", head.ToString());
        return tcp;
    }

    [Fact]
    public async Task Frozen_screen_is_dropped_after_missed_pings()
    {
        var (app, s, port) = await Serve(x => { x.PingEvery = TimeSpan.FromMilliseconds(150); x.PongWait = TimeSpan.FromMilliseconds(300); });
        await using var _ = app;
        using var tcp = await Frozen(port, s.Key);
        Assert.True(await Until(() => Streams(s) == 1, 5));
        Assert.True(await Until(() => Streams(s) == 0, 10), "застиглий екран досі тримає з'єднання");
    }

    [Fact]
    public async Task New_key_frees_a_screen_that_never_answers_the_close()
    {
        // пінги — рідко, щоб звільнило саме закриття
        var (app, s, port) = await Serve(x =>
        {
            x.PingEvery = TimeSpan.FromMinutes(5);
            x.PongWait = TimeSpan.FromMinutes(5);
            x.CloseWait = TimeSpan.FromMilliseconds(300);
        });
        await using var _ = app;
        using var tcp = await Frozen(port, s.Key);
        Assert.True(await Until(() => Streams(s) == 1, 5));
        s.NewDeviceKey();
        Assert.True(await Until(() => Streams(s) == 0, 5), "після нового ключа старе з'єднання висить");
        Assert.False(Online(s));
    }

    [Fact]
    public async Task Live_screen_answers_pings_gets_changes_and_reports()
    {
        // дедлайн понгу із запасом: під навантаженням усього набору живий клієнт відповідає не миттєво
        var (app, s, port) = await Serve(x => { x.PingEvery = TimeSpan.FromMilliseconds(100); x.PongWait = TimeSpan.FromSeconds(2); });
        await using var _ = app;
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api/screen/ws?key={Uri.EscapeDataString(s.Key)}"), CancellationToken.None);
        var got = new List<string>();
        var reading = Task.Run(async () =>
        {
            var buf = new byte[4096];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var r = await ws.ReceiveAsync(buf, CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    lock (got) got.Add(Encoding.UTF8.GetString(buf, 0, r.Count));
                }
            }
            catch (WebSocketException) { }
        });
        int Count() { lock (got) return got.Count; }

        Assert.True(await Until(() => Count() == 1, 5));   // поточний стан — одразу
        await Task.Delay(1500);                            // з десяток пінгів — живий відповідає сам
        Assert.Equal(WebSocketState.Open, ws.State);
        Assert.Equal(1, Streams(s));

        s.Apply(new ScreenPatch(Brightness: 7), "a");
        Assert.True(await Until(() => Count() == 2, 5));
        lock (got) Assert.Contains("\"brightness\":7", got[1]);

        await ws.SendAsync(Encoding.UTF8.GetBytes("{\"temperature\": 41.5}"), WebSocketMessageType.Text, true, CancellationToken.None);
        Assert.True(await Until(() => Prop(s.AdminView(false), "temperature") is not null, 5));

        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await reading.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await Until(() => Streams(s) == 0, 5));
    }
}
