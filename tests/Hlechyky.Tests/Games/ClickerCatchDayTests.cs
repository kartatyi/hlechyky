using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Ловець дня» (v13 Г2) та призи до кола «Майстра дня» (Г1), ClickerDay.cs: друга дошка в тому самому сервісі (очки — глеки
/// з полиці, спіймані руками), межа 23:30, MinPlayers 2 / MinPoints 20, призи 100/50/25 × Boost і хвилини «Щасливого кошика»
/// 60/30/15; «Майстер» додає «Свято» 60/30/15. Призи до кола сервіс лише записує — гравець забирає сам.
/// </summary>
public class ClickerCatchDayTests
{
    static DateTimeOffset Local(int y, int m, int d, int h, int min) =>
        new(TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, m, d, h, min, 0, DateTimeKind.Unspecified), Days.Kyiv), TimeSpan.Zero);

    static ClickerDayService Svc(FakeStakes stakes, FakeClock clock, ClickerDayOptions? o = null, ISlotsWire? wire = null, Func<int, int>? earn = null, Db? db = null) =>
        new(db, stakes, () => o ?? new ClickerDayOptions(), clock, wire, person: nick => !nick.StartsWith("гість", StringComparison.Ordinal))
        { Earn = earn ?? (n => n) };

    static void WithDb(Action<Db> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "clk-catch-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        try { body(new Db(Path.Combine(dir, "t.db"))); }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    static readonly DateTimeOffset Noon = Local(2026, 11, 8, 12, 0);
    static readonly DateTimeOffset Settled = Local(2026, 11, 8, 23, 31);
    const string Day = "2026-11-08";

    [Fact]
    public void The_catcher_board_counts_only_people_and_ranks_by_points_then_who_got_there_first()
    {
        var svc = Svc(new FakeStakes(), new FakeClock { UtcNow = Noon });
        svc.AddCatch("Оля", 40, Noon);
        svc.AddCatch("Тарас", 25, Noon);
        svc.AddCatch("Ірина", 25, Noon.AddMinutes(5));   // рівні з Тарасом — хто раніше
        svc.AddCatch("гість Оля", 500, Noon);            // гості — не в табло
        svc.AddCatch("Оля", 0, Noon);                    // нуль очок нічого не дає
        Assert.Equal(["Оля", "Тарас", "Ірина"], svc.BoardCatch(Day).Select(r => r.Nick));
        Assert.Equal(40, svc.BoardCatch(Day)[0].Points);
        Assert.Empty(svc.Board(Day));                    // дошка майстра окрема
    }

    [Fact]
    public void At_half_past_eleven_the_top_three_catchers_get_100_50_25_times_boost_once()
    {
        var stakes = new FakeStakes();
        var clock = new FakeClock { UtcNow = Noon };
        var svc = Svc(stakes, clock, earn: n => n * 10);
        svc.AddCatch("Оля", 90, Noon);
        svc.AddCatch("Тарас", 60, Noon);
        svc.AddCatch("Ірина", 30, Noon);
        svc.AddCatch("Микола", 20, Noon);                // четвертий — без призу

        Assert.Equal(0, svc.Settle(Local(2026, 11, 8, 23, 29)));
        Assert.Equal(1, svc.Settle(Local(2026, 11, 8, 23, 30)));
        Assert.Equal(0, svc.Settle(Local(2026, 11, 8, 23, 45)));
        Assert.Equal([("Оля", 1000), ("Тарас", 500), ("Ірина", 250)], stakes.Ledger.Select(m => (m.Nick, (int)m.Delta)));
        Assert.Contains(stakes.Reasons, kv => kv.Value == $"clicker-catch:{Day}");
        // Після 23:30 очки йдуть уже в завтрашній день.
        svc.AddCatch("Оля", 3, Local(2026, 11, 8, 23, 40));
        Assert.Equal(3, svc.BoardCatch("2026-11-09").Single().Points);
    }

    [Fact]
    public void A_catcher_day_pays_only_with_two_people_over_twenty_points()
    {
        var stakes = new FakeStakes();
        var svc = Svc(stakes, new FakeClock { UtcNow = Noon });
        svc.AddCatch("Оля", 300, Noon);
        svc.AddCatch("Тарас", 19, Noon);                 // нижче порогу 20
        Assert.Equal(1, svc.Settle(Settled));
        Assert.Empty(stakes.Ledger);
        Assert.Empty(svc.PrizesFor("Оля", Settled));

        var stakes2 = new FakeStakes();
        var svc2 = Svc(stakes2, new FakeClock { UtcNow = Noon });
        svc2.AddCatch("Оля", 300, Noon);
        svc2.AddCatch("Тарас", 20, Noon);                // рівно поріг — досить
        svc2.Settle(Settled);
        Assert.Equal(2, stakes2.Ledger.Count);
    }

    [Fact]
    public void Both_boards_settle_on_their_own_and_do_not_mix_keys()
    {
        var stakes = new FakeStakes();
        var wire = new FakeSlotsWire();
        var svc = Svc(stakes, new FakeClock { UtcNow = Noon }, wire: wire);
        svc.Add("Smaug", 100, Noon);
        svc.Add("Назар", 50, Noon);
        svc.AddCatch("Оля", 70, Noon);
        svc.AddCatch("Smaug", 30, Noon);
        Assert.Equal(1, svc.Settle(Settled));            // один день — одне підбиття на обидві дошки
        Assert.Equal(4, stakes.Ledger.Count);
        Assert.Equal([("Smaug", 200), ("Назар", 100), ("Оля", 100), ("Smaug", 50)], stakes.Ledger.Select(m => (m.Nick, (int)m.Delta)));
        Assert.Contains(stakes.Reasons, kv => kv.Value == $"clicker-day:{Day}");
        Assert.Contains(stakes.Reasons, kv => kv.Value == $"clicker-catch:{Day}");
        Assert.Contains(wire.Chats, c => c.StartsWith("🏆 Майстер дня"));
        Assert.Contains(wire.Chats, c => c.StartsWith("🧺 Ловець дня") && c.Contains("1. Оля 70") && c.Contains("2. Smaug 30"));
        Assert.Equal(1, svc.WinsOf("Smaug"));
        Assert.Equal(0, svc.WinsOf("Оля"));              // перемоги в «Майстрі» — окремо (ачівки лише за нього)
    }

    [Fact]
    public void Places_bring_feast_minutes_to_masters_and_basket_minutes_to_catchers()
    {
        var svc = Svc(new FakeStakes(), new FakeClock { UtcNow = Noon });
        svc.Add("Smaug", 100, Noon);
        svc.Add("Назар", 50, Noon);
        svc.Add("Владік", 40, Noon);
        svc.AddCatch("Оля", 70, Noon);
        svc.AddCatch("Smaug", 30, Noon);
        svc.Settle(Settled);
        svc.Settle(Settled.AddMinutes(1));               // повторне підбиття призів не множить

        var smaug = svc.PrizesFor("Smaug", Settled);
        Assert.Equal(["day:2026-11-08:catcher:2", "day:2026-11-08:master:1"], smaug.Select(p => p.Key));
        Assert.Equal(("basket", 30), (smaug[0].Kind, smaug[0].Minutes));
        Assert.Equal(("feast", 60), (smaug[1].Kind, smaug[1].Minutes));
        Assert.Equal("За «Майстра дня»", smaug[1].Why);
        var nazar = svc.PrizesFor("назар", Settled).Single();                   // ключ ніка без регістру
        Assert.Equal(("day:2026-11-08:master:2", "feast", 30), (nazar.Key, nazar.Kind, nazar.Minutes));
        Assert.Equal(15, svc.PrizesFor("Владік", Settled).Single().Minutes);
        var ola = svc.PrizesFor("Оля", Settled).Single();
        Assert.Equal(("day:2026-11-08:catcher:1", "basket", 60, "За «Ловця дня»"), (ola.Key, ola.Kind, ola.Minutes, ola.Why));
        Assert.Empty(svc.PrizesFor("Тарас", Settled));
    }

    [Fact]
    public void Unclaimed_prizes_are_forgotten_after_fourteen_days()
    {
        var svc = Svc(new FakeStakes(), new FakeClock { UtcNow = Noon });
        svc.Add("Smaug", 100, Noon);
        svc.Add("Назар", 50, Noon);
        svc.Settle(Settled);
        Assert.Single(svc.PrizesFor("Smaug", Settled.AddDays(10)));
        Assert.Empty(svc.PrizesFor("Smaug", Settled.AddDays(20)));
    }

    [Fact]
    public void Prize_minutes_come_from_the_options_and_zero_means_none()
    {
        var o = new ClickerDayOptions { FeastMinutes = [45, 0], MinPlayers = 1 };
        var svc = Svc(new FakeStakes(), new FakeClock { UtcNow = Noon }, o);
        svc.Add("Smaug", 100, Noon);
        svc.Add("Назар", 50, Noon);
        svc.Settle(Settled);
        Assert.Equal(45, svc.PrizesFor("Smaug", Settled).Single().Minutes);
        Assert.Empty(svc.PrizesFor("Назар", Settled));
    }

    [Fact]
    public void The_catcher_can_be_switched_off_without_touching_the_master()
    {
        var o = new ClickerDayOptions { CatchEnabled = false };
        var svc = Svc(new FakeStakes(), new FakeClock { UtcNow = Noon }, o);
        svc.AddCatch("Оля", 50, Noon);
        svc.Add("Оля", 50, Noon);
        Assert.Empty(svc.BoardCatch(Day));
        Assert.Single(svc.Board(Day));
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(svc.Wire("Оля", Noon)));
        Assert.False(doc.RootElement.TryGetProperty("catcher", out _));                  // Ловця нема — і ключа нема
        using var cat0 = JsonDocument.Parse(JsonSerializer.Serialize(svc.CatalogWire()));
        Assert.Equal(JsonValueKind.Null, cat0.RootElement.GetProperty("catcher").ValueKind);
    }

    [Fact]
    public void The_view_carries_the_catcher_board_next_to_the_master_board()
    {
        var svc = Svc(new FakeStakes(), new FakeClock { UtcNow = Noon }, earn: n => n * 10);
        svc.Add("Оля", 12, Noon);
        svc.AddCatch("Оля", 44, Noon);
        svc.AddCatch("Тарас", 50, Noon);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(svc.Wire("Оля", Noon)));
        var w = doc.RootElement;
        // Вид стислий: mine = [місце, очки], рядок табло = [нік, очки]; призи й пороги — у каталозі.
        Assert.Equal(12, w.GetProperty("mine")[1].GetInt32());
        Assert.False(w.TryGetProperty("feast", out _));
        Assert.False(w.TryGetProperty("prizes", out _));
        var c = w.GetProperty("catcher");
        Assert.Equal(2, c.GetProperty("mine")[0].GetInt32());
        Assert.Equal(44, c.GetProperty("mine")[1].GetInt32());
        Assert.Equal("Тарас", c.GetProperty("board")[0][0].GetString());
        Assert.Equal(50, c.GetProperty("board")[0][1].GetInt32());
        Assert.Equal("Оля", c.GetProperty("board")[1][0].GetString());
        Assert.Equal(2, c.GetProperty("players").GetInt32());
        using var cat = JsonDocument.Parse(JsonSerializer.Serialize(svc.CatalogWire()));
        var k = cat.RootElement;
        Assert.Equal(new[] { 2000, 1000, 500 }, k.GetProperty("prizes").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal([60, 30, 15], k.GetProperty("feast").EnumerateArray().Select(x => x.GetInt32()));
        var kc = k.GetProperty("catcher");
        Assert.Equal([1000, 500, 250], kc.GetProperty("prizes").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal([60, 30, 15], kc.GetProperty("minutes").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal(20, kc.GetProperty("minPoints").GetInt32());
        Assert.Equal(2, kc.GetProperty("minPlayers").GetInt32());
    }

    [Fact]
    public void The_database_keeps_the_catcher_board_paid_days_and_prizes_across_a_restart()
    {
        WithDb(db =>
        {
            var stakes = new FakeStakes();
            var clock = new FakeClock { UtcNow = Noon };
            var one = Svc(stakes, clock, db: db);
            one.AddCatch("Оля", 70, Noon);
            one.AddCatch("Тарас", 40, Noon);
            one.Add("Smaug", 10, Noon);
            one.FlushNow();

            var two = Svc(stakes, clock, db: db);                                   // «перезапуск»: очки вціліли
            Assert.Equal(["Оля", "Тарас"], two.BoardCatch(Day).Select(r => r.Nick));
            Assert.Equal(1, two.Settle(Settled));
            Assert.Equal(2, stakes.Ledger.Count);

            var three = Svc(stakes, clock, db: db);                                 // оплачене не платиться вдруге, приз до кола — є
            Assert.Equal(0, three.Settle(Settled.AddMinutes(1)));
            Assert.Equal(2, stakes.Ledger.Count);
            Assert.Equal("day:2026-11-08:catcher:1", three.PrizesFor("Оля", Settled).Single().Key);
            three.AddCatch("Оля", 5, Noon);                                         // у вже оплачений день очки не йдуть
            Assert.Empty(three.BoardCatch(Day));
        });
    }

    [Fact]
    public void The_catcher_day_cap_stops_counting_points_above_it_so_a_script_cannot_win_all_day()
    {
        var svc = Svc(new FakeStakes(), new FakeClock { UtcNow = Noon });
        svc.AddCatch("Оля", 400, Noon);
        svc.AddCatch("Оля", 400, Noon.AddMinutes(1));
        svc.AddCatch("Оля", 50, Noon.AddMinutes(2));
        Assert.Equal(500, svc.BoardCatch(Day).Single().Points);                          // типова стеля CatchMaxPoints

        var small = Svc(new FakeStakes(), new FakeClock { UtcNow = Noon }, new ClickerDayOptions { CatchMaxPoints = 100 });
        small.AddCatch("Тарас", 60, Noon);
        small.AddCatch("Тарас", 60, Noon);
        Assert.Equal(100, small.BoardCatch(Day).Single().Points);
        var free = Svc(new FakeStakes(), new FakeClock { UtcNow = Noon }, new ClickerDayOptions { CatchMaxPoints = 0 });
        free.AddCatch("Іван", 900, Noon);
        free.AddCatch("Іван", 900, Noon);
        Assert.Equal(1800, free.BoardCatch(Day).Single().Points);                        // 0 — без стелі
    }
}
