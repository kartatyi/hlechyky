using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «⛵ Хто більше виріс» (v13 Г4, ClickerWeek.cs): тиждень до неділі 23:30 за Києвом (і навколо переходу на літній/зимовий
/// час), ріст зі щоденника кола (total наприкінці ÷ на початку), умова «3 дні гри й total від 1e30», призи 300/150/75 × Boost
/// і «Вітрила тижня» +20/10/5 % до всього на 7 діб (множник в AllMult, гасне сам), нагорода забирається раз.
/// </summary>
public class ClickerWeekGrowTests
{
    internal static DateTimeOffset Local(int y, int m, int d, int h, int min) =>
        new(TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, m, d, h, min, 0, DateTimeKind.Unspecified), Days.Kyiv), TimeSpan.Zero);

    /// <summary>Тиждень 02.11 (пн) — 08.11 (нд) 2026.</summary>
    const string Week = "2026-11-08";
    static readonly DateTimeOffset Wed = Local(2026, 11, 4, 12, 0);
    static readonly DateTimeOffset Settled = Local(2026, 11, 8, 23, 31);

    static DateTimeOffset Ends() => ClickerWeekService.Ends(Week);

    internal sealed class Kit
    {
        public FakeClock Clock { get; } = new();
        public FakeStakes Stakes { get; } = new();
        public FakeSlotsWire Wire { get; } = new();
        public ClickerDiaryService Diary { get; }
        public ClickerWeekService Svc { get; }

        public Kit(DateTimeOffset? at = null, Func<int, int>? earn = null, Db? db = null)
        {
            Clock.UtcNow = at ?? Wed;
            Diary = new ClickerDiaryService(null, () => new ClickerDiaryOptions(), Clock, person: n => !n.StartsWith("бот", StringComparison.Ordinal));
            Svc = Make(db, earn);
        }

        public ClickerWeekService Make(Db? db, Func<int, int>? earn = null) =>
            new(db, Stakes, () => new ClickerWeekOptions(), Clock, Wire, person: n => !n.StartsWith("бот", StringComparison.Ordinal))
            { Diary = Diary, Earn = earn ?? (n => n), Rng = new Random(7), BoardTtl = TimeSpan.Zero };

        /// <summary>Знімок щоденника на день: total на початку/в кінці дня і хвилини гри.</summary>
        public void Day(string nick, string day, double start, double total, int minutes = 10)
        {
            var at = Local(int.Parse(day[..4]), int.Parse(day[5..7]), int.Parse(day[8..10]), 12, 0);
            Diary.Put(nick, day, at, string.Create(CultureInfo.InvariantCulture, $"{{\"total\":{total:R},\"dayStart\":{start:R},\"fired\":{{}}}}"));
            if (minutes > 0) Diary.AddHour(nick, at, minutes * 60, 0, 0, 0);
        }

        /// <summary>Тиждень 02–08.11: Оля ×8, Тарас ×2, Ірина ×1,5 (початок зі вчорашнього total), решта не пройшла умови.</summary>
        public void Season()
        {
            Day("Оля", "2026-11-02", 1e31, 1.5e31);
            Day("Оля", "2026-11-04", 2e31, 4e31);
            Day("Оля", "2026-11-07", 5e31, 8e31);
            Day("Тарас", "2026-11-03", 2e30, 2.5e30);
            Day("Тарас", "2026-11-05", 2.5e30, 3e30);
            Day("Тарас", "2026-11-06", 3e30, 4e30);
            Day("Ірина", "2026-11-01", 1e31, 1e31, minutes: 0);                    // до тижня: звідси початок
            Day("Ірина", "2026-11-03", 0, 1.2e31);
            Day("Ірина", "2026-11-05", 0, 1.3e31);
            Day("Ірина", "2026-11-08", 0, 1.5e31);
            Day("Микола", "2026-11-03", 1e30, 3e30);                                // ×9, але лише два дні гри
            Day("Микола", "2026-11-06", 3e30, 9e30);
            Day("Мала", "2026-11-03", 1e28, 2e28);                                  // total менший за 1e30
            Day("Мала", "2026-11-04", 2e28, 3e28);
            Day("Мала", "2026-11-05", 3e28, 1e29);
            Day("бот Вася", "2026-11-03", 1e31, 9e32);
        }
    }

    // ---------- межі тижня ----------

    [Fact]
    public void A_week_ends_on_sunday_at_half_past_eleven_kyiv_time()
    {
        Assert.Equal(Week, ClickerWeekService.WeekOf(Local(2026, 11, 2, 9, 0)));         // понеділок
        Assert.Equal(Week, ClickerWeekService.WeekOf(Local(2026, 11, 7, 23, 45)));       // субота ввечері
        Assert.Equal(Week, ClickerWeekService.WeekOf(Local(2026, 11, 8, 23, 29)));       // неділя до межі
        Assert.Equal("2026-11-15", ClickerWeekService.WeekOf(Local(2026, 11, 8, 23, 30)));
        Assert.Equal("2026-11-15", ClickerWeekService.WeekOf(Local(2026, 11, 9, 8, 0)));
        Assert.Equal(Local(2026, 11, 8, 23, 30), Ends());
        Assert.Equal("2026-11-02", ClickerWeekService.Monday(Week));
    }

    [Fact]
    public void Summer_and_winter_time_shifts_do_not_move_the_half_past_eleven_boundary()
    {
        // 29.03.2026 о 03:00 годинник іде на літній час (+3), 25.10.2026 о 04:00 — на зимовий (+2).
        var spring = ClickerWeekService.Ends("2026-03-29");
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 20, 30, 0, TimeSpan.Zero), spring);
        Assert.Equal("2026-03-29", ClickerWeekService.WeekOf(spring.AddMinutes(-1)));
        Assert.Equal("2026-04-05", ClickerWeekService.WeekOf(spring));
        var autumn = ClickerWeekService.Ends("2026-10-25");
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 21, 30, 0, TimeSpan.Zero), autumn);
        Assert.Equal("2026-10-25", ClickerWeekService.WeekOf(autumn.AddMinutes(-1)));
        Assert.Equal("2026-11-01", ClickerWeekService.WeekOf(autumn));
        Assert.Equal("2026-03-23", ClickerWeekService.Monday("2026-03-29"));
        Assert.Equal("2026-10-19", ClickerWeekService.Monday("2026-10-25"));
    }

    // ---------- ріст зі щоденника ----------

    [Fact]
    public void Growth_is_the_end_total_over_the_start_and_the_start_falls_back_to_the_last_total_before_the_week()
    {
        var k = new Kit();
        k.Season();
        var board = k.Svc.Board(Wed);
        Assert.Equal(["Оля", "Тарас", "Ірина", "Мала", "Микола"], board.Select(r => r.Nick));
        Assert.Equal(8.0, board[0].X, 6);
        Assert.Equal(2.0, board[1].X, 6);
        Assert.Equal(1.5, board[2].X, 6);                                               // 1,5e31 ÷ 1e31 (total до тижня)
        Assert.Equal([true, true, true, false, false], board.Select(r => r.Ok));
        Assert.Equal(3, board[0].Days);
        Assert.Equal(2, board[4].Days);                                                 // Микола грав лише двічі
        Assert.DoesNotContain(board, r => r.Nick.StartsWith("бот"));
    }

    [Fact]
    public void A_day_counts_as_played_only_from_five_minutes_of_hands()
    {
        var k = new Kit();
        k.Day("Оля", "2026-11-02", 1e31, 2e31, minutes: 5);
        k.Day("Оля", "2026-11-03", 2e31, 3e31, minutes: 4);                             // чотири хвилини — не день
        k.Day("Оля", "2026-11-04", 3e31, 4e31, minutes: 6);
        k.Day("Оля", "2026-11-05", 4e31, 5e31, minutes: 60);
        var row = k.Svc.Board(Wed).Single();
        Assert.Equal(3, row.Days);
        Assert.True(row.Ok);
        Assert.Equal(5.0, row.X, 6);
    }

    [Fact]
    public void The_live_board_goes_to_the_view_with_my_place_and_only_the_top_five()
    {
        var k = new Kit();
        k.Season();
        k.Day("Зоя", "2026-11-03", 1e30, 1.1e30);
        var w = JsonSerializer.SerializeToElement(k.Svc.Wire("Тарас", Wed));
        var grow = w.GetProperty("grow");
        // mine = [місце, ріст, дні, у заліку 1/0]; рядок табло = [нік, ріст, у заліку 1/0]; призи й вітрила — у каталозі.
        Assert.Equal(2, grow.GetProperty("mine")[0].GetInt32());
        Assert.Equal(2.0, grow.GetProperty("mine")[1].GetDouble());
        Assert.Equal(5, grow.GetProperty("board").GetArrayLength());
        Assert.Equal("Тарас", grow.GetProperty("board")[1][0].GetString());
        Assert.False(grow.TryGetProperty("prizes", out _));
        using var cat = JsonDocument.Parse(JsonSerializer.Serialize(k.Svc.CatalogWire()));
        Assert.Equal([300, 150, 75], cat.RootElement.GetProperty("prizes").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal(Ends(), w.GetProperty("ends").GetDateTimeOffset());

        var empty = JsonSerializer.SerializeToElement(new Kit().Svc.Wire("Оля", Wed)).GetProperty("grow");
        Assert.Equal(0, empty.GetProperty("board").GetArrayLength());
        Assert.False(empty.TryGetProperty("mine", out _));
    }

    // ---------- підбиття ----------

    [Fact]
    public void On_sunday_half_past_eleven_the_top_three_get_300_150_75_times_boost_once_and_a_line_goes_to_the_chat()
    {
        var k = new Kit(earn: n => n * 10);
        k.Season();
        k.Svc.Settle(Local(2026, 11, 8, 23, 29));
        Assert.Empty(k.Stakes.Ledger);                                                  // до межі нічого
        k.Svc.Settle(Local(2026, 11, 8, 23, 30));
        Assert.Equal(3, k.Stakes.Ledger.Count);
        k.Svc.Settle(Settled);
        Assert.Equal(3, k.Stakes.Ledger.Count);                                         // удруге не платить
        Assert.Equal([("Оля", 3000), ("Тарас", 1500), ("Ірина", 750)], k.Stakes.Ledger.Select(m => (m.Nick, (int)m.Delta)));
        Assert.Contains(k.Stakes.Reasons, kv => kv.Value == $"clicker-week-grow:{Week}");
        var line = Assert.Single(k.Wire.Chats, c => c.Contains("Хто більше виріс"));
        Assert.Contains("1. Оля ×8,0", line);
        Assert.Contains("2. Тарас ×2,0", line);
        Assert.Contains("3. Ірина ×1,5", line);
        Assert.DoesNotContain("Микола", line);
        Assert.DoesNotContain("Вася", line);
    }

    [Fact]
    public void The_week_pays_only_with_two_people_who_met_the_condition()
    {
        var k = new Kit();
        k.Day("Оля", "2026-11-02", 1e31, 2e31);
        k.Day("Оля", "2026-11-03", 2e31, 3e31);
        k.Day("Оля", "2026-11-04", 3e31, 9e31);
        k.Day("Тарас", "2026-11-03", 1e30, 9e30, minutes: 3);                            // замало хвилин
        k.Svc.Settle(Settled);
        Assert.Empty(k.Stakes.Ledger);
        Assert.Empty(k.Svc.PrizesFor("Оля", Settled));
        Assert.DoesNotContain(k.Wire.Chats, c => c.Contains("Хто більше виріс"));
    }

    [Fact]
    public void A_week_before_the_service_existed_is_not_paid()
    {
        var k = new Kit(at: Local(2026, 11, 10, 12, 0));                                // сервіс застав лише наступний тиждень
        k.Season();
        k.Svc.Settle(Local(2026, 11, 10, 12, 5));
        Assert.Empty(k.Stakes.Ledger);
        Assert.Empty(k.Svc.PrizesFor("Оля", Local(2026, 11, 10, 12, 5)));
    }

    [Fact]
    public void Sails_are_20_10_5_percent_for_seven_days_from_the_settle()
    {
        var k = new Kit();
        k.Season();
        k.Svc.Settle(Settled);
        var sails = k.Svc.PrizesFor("Оля", Settled).Single(p => p.Kind == "sails");
        Assert.Equal($"week:{Week}:grow:1", sails.Key);
        Assert.Equal(20, sails.Pct);
        Assert.Equal(Ends().AddDays(7), sails.Until);
        Assert.Equal(10, k.Svc.PrizesFor("Тарас", Settled).Single().Pct);
        Assert.Equal(5, k.Svc.PrizesFor("Ірина", Settled).Single().Pct);
        Assert.Empty(k.Svc.PrizesFor("Микола", Settled));
        Assert.Empty(k.Svc.PrizesFor("Оля", Ends().AddDays(7).AddMinutes(1)));          // термін вийшов — забирати нічого
    }

    [Fact]
    public void Settled_results_survive_a_restart_through_the_database()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clk-week-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        try
        {
            var db = new Db(Path.Combine(dir, "t.db"));
            var k = new Kit(db: db);
            k.Season();
            k.Svc.Settle(Settled);
            var granted = k.Stakes.Ledger.Count;
            Assert.Equal(3, granted);

            var again = k.Make(db);                                                     // «перезапуск»
            again.Settle(Settled.AddMinutes(1));
            Assert.Equal(granted, k.Stakes.Ledger.Count);
            Assert.Contains(again.PrizesFor("Оля", Settled), p => p.Kind == "sails" && p.Pct == 20);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    // ---------- у грі: вітрила в AllMult ----------

    static (RoomHarness H, Kit K) Wheel(string nick = "Оля")
    {
        var k = new Kit();
        var sp = new ServiceCollection().AddSingleton(k.Svc).BuildServiceProvider();
        var h = new RoomHarness("clicker", services: sp);
        h.Clock.UtcNow = Wed;
        h.Solo(nick);
        return (h, k);
    }

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    static ActResult Spin(RoomHarness h) => h.Act(0, "spin", PotterHands.Human(5));
    static double PerSecond(RoomHarness h) => h.View(0).GetProperty("perSecond").GetDouble();
    static void Rich(RoomHarness h) => Patch(h, s => s["upgrades"] = new JsonObject { ["apprentice"] = 40, ["wheel"] = 30 });

    static void Settle(RoomHarness h, Kit k)
    {
        k.Season();
        h.Clock.UtcNow = Settled;
        k.Svc.Settle(Settled);
    }

    [Theory]
    [InlineData("Оля", 20)]
    [InlineData("Тарас", 10)]
    [InlineData("Ірина", 5)]
    public void The_sails_multiply_everything_by_the_place_percent_and_fade_after_seven_days(string nick, int pct)
    {
        var (h, k) = Wheel(nick);
        Rich(h);
        Spin(h);
        var plain = PerSecond(h);
        Assert.True(plain > 0);

        Settle(h, k);
        var r = Spin(h);
        Assert.Contains($"Вітрила тижня: +{pct} % до всього до неділі", r.Message);
        Assert.Equal(plain * (1 + pct / 100.0), PerSecond(h), plain * 1e-9 + 1e-6);
        // вимпел у виді бафів, поки діє
        var sails = h.View(0).GetProperty("buffs").GetProperty("week").GetProperty("sails");
        Assert.Equal(pct, sails.GetProperty("pct").GetDouble(), 6);

        h.Clock.UtcNow = Ends().AddDays(7).AddMinutes(1);                               // згасло само
        Spin(h);
        Assert.Equal(plain, PerSecond(h), plain * 1e-9 + 1e-6);
        Assert.False(h.View(0).TryGetProperty("buffs", out var b) && b.ValueKind == JsonValueKind.Object
            && b.TryGetProperty("week", out var w) && w.ValueKind == JsonValueKind.Object);
    }

    [Fact]
    public void The_sails_are_collected_once_and_survive_a_save_and_a_load()
    {
        var (h, k) = Wheel("Оля");
        Settle(h, k);
        Assert.Contains("Вітрила тижня", Spin(h).Message);
        Assert.DoesNotContain("Вітрила тижня", Spin(h).Message);                        // удруге — нічого
        Patch(h, _ => { });                                                             // перезапуск, інший пристрій
        Assert.DoesNotContain("Вітрила тижня", Spin(h).Message);
        var save = JsonNode.Parse(h.Room.Game.Save()!)!["feast"]!;
        Assert.Equal(20, save["sails"]!.GetValue<double>());
        Assert.Contains(save["taken"]!.AsArray(), n => n!.GetValue<string>() == $"week:{Week}:grow:1");
        Assert.True(h.View(0).GetProperty("buffs").GetProperty("week").GetProperty("sails").GetProperty("until").GetDateTimeOffset() > Settled);
    }

    [Fact]
    public void A_friends_house_shows_the_pennant_while_the_sails_last()
    {
        var (h, k) = Wheel("Оля");
        Settle(h, k);
        Spin(h);
        var save = h.Room.Game.Save()!;
        var snap = JsonSerializer.SerializeToElement(ClickerGuildService.HouseSnapshot("Оля", save));
        var pennant = snap.GetProperty("pennant");
        Assert.Equal(20, pennant.GetProperty("pct").GetDouble());
        Assert.Equal(Ends().AddDays(7), pennant.GetProperty("until").GetDateTimeOffset());

        var plain = new RoomHarness("clicker");                                         // без вітрил вимпела в знімку нема
        plain.Solo("Тарас");
        Spin(plain);
        var none = JsonSerializer.SerializeToElement(ClickerGuildService.HouseSnapshot("Тарас", plain.Room.Game.Save()!));
        Assert.Equal(JsonValueKind.Null, none.GetProperty("pennant").ValueKind);
    }

    [Fact]
    public void The_week_part_of_the_view_is_short_even_with_a_full_board_and_an_order()
    {
        var (h, k) = Wheel("Оля");
        k.Season();
        k.Svc.Settle(Wed);
        k.Svc.CoopAdd("Оля", 10, Wed);
        var week = JsonSerializer.Serialize(k.Svc.Wire("Оля", Wed));
        Assert.True(week.Length < 1200, $"тиждень у виді — {week.Length} Б");
        Assert.Contains("\"coop\"", week);
        Assert.Equal(JsonValueKind.Object, h.View(0).GetProperty("week").ValueKind);
        Assert.False(h.View(0).TryGetProperty("buffs", out var b) && b.ValueKind == JsonValueKind.Object);   // нічого не діє — бафів нема
    }

    [Fact]
    public void Someone_elses_sails_and_a_potter_without_the_service_get_nothing()
    {
        var (h, k) = Wheel("Богдан");
        Rich(h);
        Spin(h);
        var plain = PerSecond(h);
        Settle(h, k);
        Spin(h);
        Assert.Equal(plain, PerSecond(h), plain * 1e-9 + 1e-6);

        var bare = new RoomHarness("clicker");                                          // сервісу нема — вид без «week»
        bare.Solo("Оля");
        Assert.True(Spin(bare).Ok);
        Assert.False(bare.View(0).TryGetProperty("week", out var w) && w.ValueKind == JsonValueKind.Object);
    }
}
