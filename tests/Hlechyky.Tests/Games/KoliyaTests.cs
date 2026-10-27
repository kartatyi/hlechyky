using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Колія»: сервер-секретар (<see cref="Koliya"/>) — правил нема, лише тики команд, журнал, водяний знак, звірка хешу.
/// </summary>
public class KoliyaTests
{
    static RoomHarness Table(params string[] nicks)
    {
        var h = new RoomHarness("koliya", seed: 7);
        foreach (var n in nicks.Length > 0 ? nicks : ["Оля"]) h.Join(n);
        Assert.True(h.Act(0, "go").Ok);   // K62: зібрались — рушаємо
        return h;
    }

    static Koliya Game(RoomHarness h) => (Koliya)h.Room.Game;

    static JsonElement Log(RoomHarness h, int from = 0)
    {
        lock (h.Room.Sync) return Views.Json(Game(h).Log(from));
    }

    static JsonElement LastFrame(RoomHarness h) => Views.Json(h.Outbox.OfType<RoomFrame>().Last().Frame);

    /// <summary>Записи журналу як [seq, t, p, k, a, b, c, d, e, i].</summary>
    static int[][] Entries(JsonElement j)
    {
        var c = j.GetProperty("c").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        return [.. c.Chunk(Koliya.Stride)];
    }

    [Fact]
    public void Стіл_стартує_одразу_й_не_в_каталозі()
    {
        var h = Table();
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        var v = h.View(0);
        Assert.Equal(1, v.GetProperty("e").GetInt32());
        Assert.Equal(0, v.GetProperty("p").GetInt32());
        Assert.Equal(1, v.GetProperty("n0").GetInt32());
        Assert.Equal(-1, h.View(null).GetProperty("p").GetInt32());
        Assert.True(RoomHarness.NewRegistry().Catalog.Single(g => g.Id == "koliya").Unlisted);
    }

    [Fact]
    public void Команда_лягає_на_бажаний_тик_а_запізніла_за_водяний_знак()
    {
        var h = Table();
        h.Tick(20);                                   // T = 20, W = 20 − 6 − 1 = 13
        h.Input(0, "in", new { mx = 100, my = 0, t = 22, i = 1 });
        h.Input(0, "in", new { mx = 0, my = 50, t = 3, i = 2 });   // у закритий тик — зсув на W+1, але не раніше попередньої
        h.Input(0, "use", new { x = 1500, y = 2500, fx = 1, fy = 0, h = 1, t = 500, i = 3 });   // надто наперед — зріз
        var e = Entries(Log(h));
        Assert.Equal(3, e.Length);
        Assert.Equal([0, 22, 0, Koliya.In, 100, 0, 0, 0, 0, 1], e[0]);
        Assert.Equal(22, e[1][1]);
        Assert.Equal(2, e[1][9]);
        Assert.Equal(20 + Koliya.MaxAhead, e[2][1]);
        Assert.Equal([1500, 2500, 1, 0, 1], e[2][4..9]);
    }

    [Fact]
    public void Без_тику_команда_йде_на_зараз_і_ніколи_в_закрите()
    {
        var h = Table("Оля", "Петро");
        h.Tick(30);
        h.Input(1, "in", new { mx = -100, my = 0, i = 1 });
        var w = Log(h).GetProperty("w").GetInt32();
        var t = Entries(Log(h)).Last()[1];
        Assert.True(t > w);
        Assert.Equal(30, t);
    }

    [Fact]
    public void Погана_команда_не_міняє_журнал()
    {
        var h = Table();
        var n = Log(h).GetProperty("n").GetInt32();
        Assert.False(h.Act(0, "in", new { mx = "вліво" }).Ok);
        Assert.False(h.Act(0, "fly", new { }).Ok);
        Assert.False(h.Act(0, "restart", new { seed = -5 }).Ok);
        Assert.False(h.Act(0, "restart", new { seed = 1, @params = new[] { 1, 2 } }).Ok);
        Assert.Equal(n, Log(h).GetProperty("n").GetInt32());
        Assert.Equal(1, h.View(0).GetProperty("e").GetInt32());
    }

    [Fact]
    public void Кадр_везе_свіжі_записи_а_старі_відходять()
    {
        var h = Table();
        h.Tick(10);
        h.Input(0, "in", new { mx = 100, my = 0, t = 10, i = 1 });
        h.Tick(1);
        var f = LastFrame(h);
        Assert.Equal(1, f.GetProperty("n").GetInt32());
        Assert.Equal(0, f.GetProperty("f").GetInt32());
        Assert.Single(Entries(f));
        Assert.Equal(11, f.GetProperty("t").GetInt32());
        Assert.Equal(11 - Koliya.Grace - 1, f.GetProperty("w").GetInt32());
        h.Tick(Koliya.Grace + Koliya.KeepTicks + 2);
        f = LastFrame(h);
        Assert.Equal(1, f.GetProperty("f").GetInt32());   // запис уже лише в журналі
        Assert.Empty(Entries(f));
        Assert.Single(Entries(Log(h)));
        Assert.Empty(Entries(Log(h, 1)));
    }

    [Fact]
    public void Хто_сів_і_встав_посеред_партії_join_і_leave_а_слот_вертається()
    {
        var h = Table("Оля");
        h.Tick(5);
        h.Join("Петро");
        Assert.Equal(1, h.View(1).GetProperty("p").GetInt32());
        h.Leave("Петро");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);   // не техпоразка
        h.Join("Іра");
        var e = Entries(Log(h));
        Assert.Equal([Koliya.Join, Koliya.Leave, Koliya.Join], e.Select(x => x[3]).ToArray());
        Assert.All(e, x => Assert.Equal(1, x[2]));          // rules.js join займає перший вільний слот
        var seat = h.Room.SeatOf("Іра")!.Value;
        Assert.Equal(1, h.View(seat).GetProperty("p").GetInt32());
    }

    [Fact]
    public void K24_join_не_раніше_за_leave_що_ліг_наперед()
    {
        var h = Table("Оля", "Петро");
        h.Tick(100);
        h.Input(1, "in", new { mx = 100, my = 0, t = 130, i = 1 });   // годинник Петра біжить наперед
        h.Leave("Петро");                                             // leave — не раніше його команди (130)
        h.Join("Іра");                                                // Іра на той самий слот rules
        var e = Entries(Log(h));
        var leave = e.Single(x => x[3] == Koliya.Leave);
        var join = e.Last(x => x[3] == Koliya.Join);
        Assert.Equal(leave[2], join[2]);
        Assert.True(join[1] >= leave[1], $"join на тику {join[1]} раніше за leave на {leave[1]} — rules дасть інший слот");
    }

    [Fact]
    public void K24_ввід_старої_епохи_мовчки_викидаємо()
    {
        var h = Table("Оля", "Петро");
        h.Tick(30);
        Assert.True(h.Act(1, "restart", new { seed = 5u, @params = new { } }).Ok);
        Assert.True(h.Act(1, "go").Ok);   // K62
        h.Input(0, "use", new { x = 1500, y = 2500, fx = 1, fy = 0, t = 3000, i = 7, e = 1 });   // летів до перезапуску
        h.Input(0, "hash", new { t = 0, h = 1L, e = 1 });
        h.Input(0, "in", new { mx = 100, my = 0, i = 8, e = 2 });                                 // нова епоха — лягає
        h.Input(1, "in", new { mx = -100, my = 0, i = 9 });                                       // без e — як було
        var e = Entries(Log(h));
        Assert.Equal([Koliya.In, Koliya.In], e.Select(x => x[3]).ToArray());
        Assert.Equal([8, 9], e.Select(x => x[9]).ToArray());
    }

    [Fact]
    public void Перезапуск_нова_епоха_сід_і_параметри_журнал_з_нуля()
    {
        var h = Table("Оля", "Петро");
        h.Tick(30);
        h.Input(0, "in", new { mx = 100, my = 0, i = 1 });
        Assert.True(h.Act(1, "restart", new { seed = 4000000000u, @params = new { speedStart = 0.5, overheat = "stop" } }).Ok);
        var v = h.View(1);
        Assert.Equal(2, v.GetProperty("e").GetInt32());
        Assert.Equal(4000000000u, v.GetProperty("seed").GetUInt32());
        Assert.Equal("stop", v.GetProperty("prm").GetProperty("overheat").GetString());
        Assert.Equal(2, v.GetProperty("n0").GetInt32());
        var log = Log(h);
        Assert.Equal(0, log.GetProperty("n").GetInt32());
        Assert.Equal(0, log.GetProperty("t").GetInt32());
        h.Tick(3);
        Assert.Equal(2, LastFrame(h).GetProperty("e").GetInt32());
    }

    [Fact]
    public void Різні_хеші_на_один_тик_розбіжність_у_кадрі()
    {
        var h = Table("Оля", "Петро");
        h.Tick(120);
        h.Input(0, "hash", new { t = 100, h = 123456789L });
        h.Input(1, "hash", new { t = 100, h = 123456789L });
        h.Tick(1);
        Assert.Equal(-1, LastFrame(h).GetProperty("d").GetInt32());
        h.Input(1, "hash", new { t = 100, h = 42L });
        h.Input(0, "hash", new { t = 5000, h = 1L });       // незакритий тик — не звіряємо
        h.Tick(1);
        Assert.Equal(100, LastFrame(h).GetProperty("d").GetInt32());
        Assert.Equal(100, h.View(0).GetProperty("d").GetInt32());
    }

    [Fact]
    public void Однаковий_потік_однаковий_журнал()
    {
        static string Run()
        {
            var h = Table("Оля", "Петро");
            for (var i = 0; i < 50; i++)
            {
                h.Tick(1);
                h.Input(i % 2, "in", new { mx = i * 3 - 70, my = 10, t = i + 2, i = i + 1 });
            }
            return Log(h).GetProperty("c").GetRawText() + h.View(0).GetProperty("seed").GetRawText();
        }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void Двоє_за_одним_ПК_другий_гравець_місця()
    {
        var h = Table("Оля", "Петро");
        h.Tick(10);
        Assert.False(h.Act(0, "in", new { mx = 1, q = 1 }).Ok);        // другого ще нема
        Assert.True(h.Act(0, "join2").Ok);
        Assert.True(h.Act(0, "join2").Ok);                              // повтор — той самий
        var v = h.View(0);
        Assert.Equal(2, v.GetProperty("p2").GetInt32());
        Assert.Equal(-1, h.View(1).GetProperty("p2").GetInt32());
        h.Input(0, "in", new { mx = 100, my = 0, q = 1, i = 1 });
        h.Input(0, "in", new { mx = -100, my = 0, i = 2 });
        var e = Entries(Log(h)).Skip(1).ToArray();                     // перший запис — join Петра
        Assert.Equal([Koliya.Join, Koliya.In, Koliya.In], e.Select(x => x[3]).ToArray());
        Assert.Equal([2, 2, 0], e.Select(x => x[2]).ToArray());
        // перезапуск зберігає другого: спершу по гравцю на місце, тоді другі
        Assert.True(h.Act(1, "restart", new { seed = 5 }).Ok);
        v = h.View(0);
        Assert.Equal(3, v.GetProperty("n0").GetInt32());
        Assert.Equal(0, v.GetProperty("p").GetInt32());
        Assert.Equal(2, v.GetProperty("p2").GetInt32());
        Assert.Equal(1, h.View(1).GetProperty("p").GetInt32());
        // встала — обидва її гравці виходять
        h.Leave("Оля");
        e = Entries(Log(h));
        Assert.Equal([2, 0], e.Where(x => x[3] == Koliya.Leave).Select(x => x[2]).ToArray());
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void Кидок_з_позою_і_гудок_K18_у_журналі()
    {
        var h = Table();
        h.Tick(20);
        h.Input(0, "throw", new { x = 1500, y = 2500, fx = 0, fy = -1, t = 21, i = 1 });
        h.Input(0, "horn", new { t = 22, i = 2 });
        Assert.False(h.Act(0, "throw", new { x = "сюди" }).Ok);
        Assert.False(h.Act(0, "horn", new { q = 1 }).Ok);              // другого за ПК нема
        var e = Entries(Log(h));
        Assert.Equal(2, e.Length);
        Assert.Equal([0, 21, 0, Koliya.Throw, 1500, 2500, 0, -1, 0, 1], e[0]);
        Assert.Equal([1, 22, 0, Koliya.Horn, 0, 0, 0, 0, 0, 2], e[1]);
        h.Tick(1);
        Assert.Equal(2, Entries(LastFrame(h)).Length);                   // і в кадрі
    }

    [Fact]
    public void Двоє_за_ПК_не_більше_чотирьох_гравців_rules()
    {
        var h = Table("Оля", "Петро");
        Assert.True(h.Act(0, "join2").Ok);
        Assert.True(h.Act(1, "join2").Ok);                              // 4 гравці rules
        h.Join("Іра");
        var ira = h.Room.SeatOf("Іра")!.Value;
        Assert.Equal(-1, h.View(ira).GetProperty("p").GetInt32());     // дивиться
        Assert.False(h.Act(ira, "in", new { mx = 1 }).Ok);
        Assert.True(h.Act(1, "leave2").Ok);
        Assert.False(h.Act(1, "in", new { mx = 1, q = 1 }).Ok);
        // перезапуск: спершу кожне місце (Іра теж), другий Олин — у залишок
        Assert.True(h.Act(0, "restart", new { seed = 9 }).Ok);
        Assert.Equal(3, h.View(0).GetProperty("p2").GetInt32());
        Assert.Equal(2, h.View(ira).GetProperty("p").GetInt32());
        Assert.Equal(4, h.View(0).GetProperty("n0").GetInt32());
    }

    [Fact]
    public void K62_новий_стіл_і_перезапуск_чекають_на_go()
    {
        var h = new RoomHarness("koliya", seed: 7);
        h.Join("Оля");
        h.Tick(200);                                                    // 10 с — годинник стоїть
        var f = LastFrame(h);
        Assert.Equal(1, f.GetProperty("wait").GetInt32());
        Assert.Equal(0, f.GetProperty("t").GetInt32());
        Assert.Equal(-1, f.GetProperty("w").GetInt32());
        Assert.Equal(1, h.View(0).GetProperty("wait").GetInt32());
        Assert.Equal(1, Log(h).GetProperty("wait").GetInt32());
        // у зборі рух/дії мовчки геть; join/leave — на тик 0
        h.Input(0, "in", new { mx = 100, my = 0, t = 5, i = 1 });
        h.Input(0, "use", new { x = 1500, y = 2500, fx = 1, fy = 0, t = 5, i = 2 });
        h.Join("Петро");
        Assert.Equal(1, h.View(1).GetProperty("p").GetInt32());
        var e = Entries(Log(h));
        Assert.Equal([[Koliya.Join, 0]], e.Select(x => new[] { x[3], x[1] }).ToArray());
        Assert.True(h.Act(1, "react", new { em = 1, i = 3 }).Ok);       // реакції — можна
        // «▶ Почали» від будь-кого за столом — годинник рушає з цієї миті
        Assert.True(h.Act(1, "go").Ok);
        Assert.False(Game(h).Waiting);
        h.Tick(20);
        f = LastFrame(h);
        Assert.Equal(0, f.GetProperty("wait").GetInt32());
        Assert.Equal(20, f.GetProperty("t").GetInt32());
        Assert.Equal(20 - Koliya.Grace - 1, f.GetProperty("w").GetInt32());
        Assert.True(h.Act(0, "go").Ok);                                 // повтор — нічого
        Assert.Equal(20, Log(h).GetProperty("t").GetInt32());
        h.Input(0, "in", new { mx = 100, my = 0, t = 21, i = 4 });
        Assert.Equal(21, Entries(Log(h)).Last()[1]);
        // перезапуск — знову збір
        Assert.True(h.Act(0, "restart", new { seed = 5 }).Ok);
        Assert.True(Game(h).Waiting);
        h.Tick(50);
        Assert.Equal(0, LastFrame(h).GetProperty("t").GetInt32());
        Assert.True(h.Act(0, "go", new { e = 1 }).Ok);                  // «go» старої епохи — геть
        Assert.True(Game(h).Waiting);
        Assert.True(h.Act(0, "go", new { e = 2 }).Ok);
        h.Tick(3);
        Assert.Equal(3, LastFrame(h).GetProperty("t").GetInt32());
    }

    static string? Put(RoomHarness h, int t, long hash, byte[]? b = null, int e = 1)
    {
        lock (h.Room.Sync) return Game(h).PutSnap(e, t, hash, true, b ?? [1, 2, 3]);
    }

    static JsonElement SnapLog(RoomHarness h)
    {
        lock (h.Room.Sync) return Views.Json(Game(h).Log(0, snap: true));
    }

    [Fact]
    public void K44_знімок_підтверджують_хеші_двох_місць_і_журнал_іде_з_його_тику()
    {
        var h = Table("Оля", "Петро");
        h.Tick(30);
        h.Input(0, "in", new { mx = 100, my = 0, t = 30, i = 1 });     // до знімка
        h.Input(1, "in", new { mx = 0, my = 100, t = 60, i = 1 });     // просився наперед — після знімка за тиком, але раніше в журналі
        h.Tick(100);                                                    // T = 130, W = 123
        h.Input(0, "in", new { mx = -100, my = 0, i = 2 });            // після знімка
        Assert.Null(Put(h, 50, 777));
        h.Input(0, "hash", new { t = 50, h = 777L });
        Assert.Equal(-1, Game(h).SnapTick);                              // одне місце з двох — ще ні
        Assert.Equal(JsonValueKind.Null, SnapLog(h).GetProperty("s").ValueKind);
        h.Input(1, "hash", new { t = 50, h = 777L });
        Assert.Equal(50, Game(h).SnapTick);
        var j = SnapLog(h);
        var s = j.GetProperty("s");
        Assert.Equal(50, s.GetProperty("t").GetInt32());
        Assert.Equal(777u, s.GetProperty("h").GetUInt32());
        Assert.Equal(1, s.GetProperty("z").GetInt32());
        Assert.Equal([1, 2, 3], s.GetProperty("b").GetBytesFromBase64());
        var f = j.GetProperty("f").GetInt32();
        var es = Entries(j);
        Assert.True(f > 0);
        Assert.All(es, x => Assert.True(x[1] >= 50));                    // усе до тику знімка — уже в ньому
        Assert.Equal(f, es[0][0]);
        Assert.Equal(j.GetProperty("n").GetInt32() - f, es.Length);
        // без snap=1 (старий браузер) — журнал з нуля, без знімка
        Assert.Equal(0, Log(h).GetProperty("f").GetInt32());
        Assert.Equal(JsonValueKind.Null, Log(h).GetProperty("s").ValueKind);
    }

    [Fact]
    public void K44_знімок_не_той_не_приймається_а_один_гравець_підтверджує_сам()
    {
        var h = Table();
        h.Tick(300);                                                    // W = 293
        Assert.NotNull(Put(h, 290, 5, e: 9));                          // стара епоха
        Assert.NotNull(Put(h, 290, 5, new byte[Koliya.MaxSnap + 1])); // завеликий
        Assert.NotNull(Put(h, 290, 5, []));                            // порожній
        Assert.NotNull(Put(h, 500, 5));                                // тик ще не закрито
        h.Input(0, "hash", new { t = 200, h = 11L });
        Assert.NotNull(Put(h, 200, 12));                               // хеш не той, що в звірці
        Assert.Null(Put(h, 200, 11));
        Assert.Equal(200, Game(h).SnapTick);                             // один за столом — досить одного
        Assert.Null(Put(h, 100, 11));                                  // старший за підтверджений — мовчки ні
        Assert.Equal(200, Game(h).SnapTick);
        // знімок, що чекає, а звірка прийшла іншою — викидаємо
        Assert.Null(Put(h, 290, 33));
        h.Input(0, "hash", new { t = 290, h = 34L });
        h.Input(0, "hash", new { t = 290, h = 33L });
        Assert.Equal(200, Game(h).SnapTick);
        // перезапуск — знімки старої епохи геть
        Assert.True(h.Act(0, "restart", new { seed = 5 }).Ok);
        Assert.Equal(-1, Game(h).SnapTick);
    }
}

/// <summary>Швидкодія «Колії» — окремою колекцією: у паралельному прогоні стінний годинник бреше.</summary>
[Collection(SerialPerf.Name)]
public class KoliyaPerfTests
{
    [Fact]
    [Trait("Category", "Perf")]
    public void Тисяча_тиків_швидко()
    {
        var h = new RoomHarness("koliya", seed: 7);
        foreach (var n in new[] { "Оля", "Петро", "Іра", "Тарас" }) h.Join(n);
        h.Act(0, "go");   // K62
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            h.Tick(1);
            h.Input(i % 4, "in", new { mx = 100, my = 0, i = i + 1 });
        }
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"1000 тиків — {sw.ElapsedMilliseconds} мс");
        // стіл стартує з першим (Immediate), троє решти — join посеред партії
        lock (h.Room.Sync) Assert.Equal(1003, Views.Json(((Koliya)h.Room.Game).Log(0)).GetProperty("n").GetInt32());
    }

}
