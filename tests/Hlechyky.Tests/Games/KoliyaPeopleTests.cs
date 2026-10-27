using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Колія» K28 — люди за столом: ніки над гравцями, реакції-емодзі, пінг і «відпав», глядачі. Нічого з цього не йде
/// в журнал правил: світ (і хеш) від них не міняється.
/// </summary>
public class KoliyaPeopleTests
{
    static RoomHarness Table(params string[] nicks)
    {
        var h = new RoomHarness("koliya", seed: 7);
        foreach (var n in nicks) h.Join(n);
        return h;
    }

    static Koliya Game(RoomHarness h) => (Koliya)h.Room.Game;

    static JsonElement Log(RoomHarness h)
    {
        lock (h.Room.Sync) return Views.Json(Game(h).Log(0));
    }

    static JsonElement LastFrame(RoomHarness h) => Views.Json(h.Outbox.OfType<RoomFrame>().Last().Frame);

    static string?[] Names(JsonElement j) =>
        [.. j.GetProperty("nm").EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Null ? null : x.GetString())];

    [Fact]
    public void Ніки_гравців_у_виді_й_журналі_другий_за_ПК_з_двійкою()
    {
        var h = Table("Оля", "Петро");
        Assert.Equal(["Оля", "Петро"], Names(h.View(0)));
        Assert.True(h.Act(0, "join2").Ok);
        Assert.Equal(["Оля", "Петро", "Оля ²"], Names(h.View(null)));
        Assert.Equal(["Оля", "Петро", "Оля ²"], Names(Log(h)));
        h.Leave("Петро");
        Assert.Equal(["Оля", null, "Оля ²"], Names(h.View(0)));
    }

    static int[] R(JsonElement frame) => [.. frame.GetProperty("r").EnumerateArray().Select(x => x.GetInt32())];

    [Fact]
    public void Реакція_йде_в_кадрах_повз_журнал_не_частіше_раз_на_секунду()
    {
        var h = Table("Оля", "Петро");
        h.Tick(5);
        var n = Log(h).GetProperty("n").GetInt32();
        h.Input(1, "react", new { e = 2, i = 7 });
        h.Input(1, "react", new { e = 0, i = 8 });          // зарано — мовчки викинуто
        h.Tick(1);
        Assert.Equal([1, 1, 2, 7], R(LastFrame(h)));
        Assert.Equal(n, Log(h).GetProperty("n").GetInt32());   // журнал правил (і світ, і хеш) не зачеплено
        h.Tick(10);                                          // 0,5 с — ще їде в кадрах
        Assert.Equal([1, 1, 2, 7], R(LastFrame(h)));
        h.Tick(10);                                          // 1 с від першої — можна знову; перша ще в кадрі
        h.Input(1, "react", new { e = 3, i = 9 });
        h.Input(0, "react", new { e = 1, i = 1 });           // інший гравець — свій ліміт
        h.Tick(1);
        Assert.Equal([1, 1, 2, 7, 2, 1, 3, 9, 3, 0, 1, 1], R(LastFrame(h)));
        h.Tick(30);                                          // > 1,5 с — зникли з кадрів
        Assert.Empty(R(LastFrame(h)));
    }

    static int[] Ints(JsonElement frame, string name) => [.. frame.GetProperty(name).EnumerateArray().Select(x => x.GetInt32())];

    [Fact]
    public void Пінг_луна_по_місцю_а_мовчазний_гравець_відпадає()
    {
        var h = Table("Оля", "Петро");
        Assert.True(h.Act(1, "join2").Ok);                   // Петро ² — p 2, те саме місце 1
        h.Tick(2);
        h.Input(0, "ping", new { i = 5, r = 118 });
        h.Tick(1);
        var f = LastFrame(h);
        Assert.Equal([5, 50, 0, 0, 0, 0, 0, 0], Ints(f, "pa"));   // пролежав до кадру 50 мс
        Assert.Equal([118, 0, 0], Ints(f, "pg"));
        h.Tick(40);                                           // 2 с: Оля мовчить, Петро мовчить
        h.Input(0, "ping", new { i = 6, r = 120 });
        h.Tick(30);                                           // ще 1,5 с — Петро > 3 с без нічого
        f = LastFrame(h);
        Assert.Equal([120, -1, -1], Ints(f, "pg"));          // відпав — і другий за його ПК
        h.Input(1, "in", new { mx = 1, i = 1 });              // будь-яка дія — знову живий
        h.Tick(1);
        Assert.Equal([120, 0, 0], Ints(LastFrame(h), "pg"));
    }

    [Fact]
    public void Глядачі_рахуються_поки_озиваються()
    {
        var h = Table("Оля", "Петро", "Іра", "Тарас");
        h.Join("Гість");                                      // п'ятий — місць нема, дивиться
        Assert.Null(h.Room.SeatOf("Гість"));
        int Watch(string id) { lock (h.Room.Sync) return Game(h).Watch(id); }
        Assert.Equal(1, Watch("a1"));
        Assert.Equal(1, Watch("a1"));                          // та сама вкладка — той самий глядач
        Assert.Equal(2, Watch("b2"));
        h.Tick(1);
        Assert.Equal(2, LastFrame(h).GetProperty("sp").GetInt32());
        h.Tick(60);                                           // 3 с — a1 озвався, b2 ні
        Watch("a1");
        h.Tick(50);                                           // b2 мовчить > 5 с
        Assert.Equal(1, LastFrame(h).GetProperty("sp").GetInt32());
        for (var i = 0; i < Koliya.WatchKeep + 10; i++) Watch("x" + i);   // пам'ять не росте без меж
        Assert.Equal(Koliya.WatchKeep, Watch("y"));
        Assert.Equal(4, Ints(LastFrame(h), "pg").Length);     // глядачі — не гравці
    }

    [Fact]
    public void Погана_реакція_відмовляє()
    {
        var h = Table("Оля");
        Assert.False(h.Act(0, "react", new { e = 4 }).Ok);
        Assert.False(h.Act(0, "react", new { e = -1 }).Ok);
        Assert.False(h.Act(0, "react", new { e = 1, q = 1 }).Ok);   // другого за ПК нема
        Assert.True(h.Act(0, "react", new { e = 1 }).Ok);
        Assert.False(h.Act(0, "react", new { e = 1 }).Ok);           // зарано
    }
}
