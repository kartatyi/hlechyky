using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Перебудова економіки 15.10.2026: Economy:Boost множить усе зароблене (партії, щоденки, очки, нагороди ігор, ачівки,
/// слухання, обмін у колі разом зі стелею), а <see cref="Regift"/> раз доплачує за вже здобуті ачівки й дарує рівну суму.
/// </summary>
public class EconomyBoostTests
{
    static readonly GameInfo Ttt = EconomyRig.Info("ttt", "Хрестики-нолики", "хрестики-нолики");

    static EconomyRig Rig(int boost = 10, bool people = false)
    {
        var rig = new EconomyRig(people);
        rig.Options.Boost = boost;
        return rig;
    }

    [Fact]
    public void Earn_multiplies_only_positive_amounts_and_never_shrinks()
    {
        Assert.Equal(50, new EconomyOptions { Boost = 10 }.Earn(5));
        Assert.Equal(0, new EconomyOptions { Boost = 10 }.Earn(0));
        Assert.Equal(5, new EconomyOptions { Boost = 0 }.Earn(5));
        Assert.Equal(5, new EconomyOptions { Boost = -3 }.Earn(5));
        Assert.Equal(int.MaxValue, new EconomyOptions { Boost = 10 }.Earn(int.MaxValue / 2));
        // типове в коді — старі суми: прод бере 10 з appsettings.json
        Assert.Equal(1, new EconomyOptions().Boost);
        Assert.Equal("", new EconomyOptions().RegiftId);
    }

    [Fact]
    public void A_finished_game_pays_ten_times_more()
    {
        using var rig = Rig();
        rig.Events.Raise(rig.Finished("r1", Ttt, ["Оля", "Петро"], [0]));
        rig.Events.Raise(rig.Finished("r2", Ttt, ["Яся", "Сем"], [], draw: true));

        Assert.Equal(50, rig.Paid("Оля", "win:ttt"));
        Assert.Equal(10, rig.Paid("Петро", "play:ttt"));
        Assert.Equal(20, rig.Paid("Яся", "draw:ttt"));
    }

    [Fact]
    public void The_daily_cap_on_games_still_counts_games_not_shards()
    {
        using var rig = Rig();
        for (var i = 0; i < 11; i++) rig.Events.Raise(rig.Finished($"r{i}", Ttt, ["Оля", "Петро"], [0]));
        Assert.Equal(10 * 50, rig.Paid("Оля", "win:ttt"));
    }

    [Fact]
    public void Daily_puzzles_and_points_are_multiplied()
    {
        using var rig = Rig();
        rig.Events.Raise(new AwardEvent("wordle", "room1", "Оля", 0, "daily:wordle"));
        rig.Events.Raise(new AwardEvent("mines", "room2", "Оля", 7, "daily:mines"));
        rig.Events.Raise(new AwardEvent("skilky", "room3", "Оля", 3, "points:1"));

        Assert.Equal(50, rig.Paid("Оля", "daily:wordle"));
        Assert.Equal(70, rig.Paid("Оля", "daily:mines"));
        Assert.Equal(30, rig.Paid("Оля", "points:skilky"));
    }

    [Fact]
    public void Game_awards_are_multiplied_and_so_is_their_daily_cap()
    {
        using var rig = Rig();
        // AwardDailyCap = 30 → 300 на день: шість нагород по 50 проходять, сьома — ні
        for (var i = 0; i < 7; i++) rig.Events.Raise(new AwardEvent("pictionary", $"room{i}", "Оля", 5, "🎨 Митець партії в піктіонарі"));
        Assert.Equal(300, rig.Paid("Оля", "award:"));
    }

    [Fact]
    public void The_clicker_amount_is_taken_as_is_and_only_the_cap_is_multiplied()
    {
        using var rig = Rig();
        // коло множить саме (воно ж показує курс): 30 — це вже 3 сотні × 10
        rig.Events.Raise(new AwardEvent("clicker", "c1", "Оля", 30, "clicker"));
        Assert.Equal(30, rig.Paid("Оля", "clicker"));
        // стеля 20 × 10 = 200: ще 170 влазить, наступні 10 — ні
        rig.Events.Raise(new AwardEvent("clicker", "c1", "Оля", 170, "clicker"));
        rig.Events.Raise(new AwardEvent("clicker", "c1", "Оля", 10, "clicker"));
        Assert.Equal(200, rig.Paid("Оля", "clicker"));

        // клейма піднімають стелю: «clicker:340» пускає до 340, вище за 40 × 10 = 400 — не пускає
        rig.Events.Raise(new AwardEvent("clicker", "c2", "Петро", 340, "clicker:340"));
        rig.Events.Raise(new AwardEvent("clicker", "c2", "Петро", 10, "clicker:340"));
        Assert.Equal(340, rig.Paid("Петро", "clicker"));
        rig.Events.Raise(new AwardEvent("clicker", "c3", "Яся", 400, "clicker:9999"));
        rig.Events.Raise(new AwardEvent("clicker", "c3", "Яся", 10, "clicker:9999"));
        Assert.Equal(400, rig.Paid("Яся", "clicker"));
    }

    [Fact]
    public void An_achievement_pays_and_shows_ten_times_its_catalog_price()
    {
        using var rig = Rig();
        rig.Events.Raise(rig.Finished("r1", Ttt, ["Оля", "Петро"], [0]));

        Assert.Equal(50, rig.Paid("Оля", "ach:first-game"));     // «Перший крок» — 5 у каталозі
        Assert.Equal(100, rig.Paid("Оля", "ach:first-win"));     // «Перша перемога» — 10
        var shown = rig.Outbox.Of<AchievementUnlocked>().First(a => a.Nick == "Оля" && a.Key == "first-win");
        Assert.Equal(100, shown.Reward);
        Assert.Contains(rig.Outbox.Of<Journal>(), j => j.Text.Contains("«Перша перемога» (+100 черепків)"));

        var profile = Views.Json(rig.Boards.Profile("Оля"));
        var ach = profile.GetProperty("achievements").EnumerateArray().First(a => a.GetProperty("key").GetString() == "first-win");
        Assert.Equal(100, ach.GetProperty("reward").GetInt32());
    }

    [Fact]
    public void Listening_pays_ten_per_ten_minutes_and_the_cap_still_counts_times()
    {
        using var rig = Rig();
        rig.Presence.Set("c-оля", "Оля");
        for (var m = 0; m < 10; m++) rig.Ticker.Minute();
        Assert.Equal(10, rig.Paid("Оля", "listen"));

        for (var m = 0; m < 200; m++) rig.Ticker.Minute();
        Assert.Equal(12 * 10, rig.Paid("Оля", "listen"));
    }

    // ---------- подарунок-перерахунок ----------

    static Regift Regift(EconomyRig rig) =>
        new(rig.Store, rig.Economy, new FixedOptions<EconomyOptions>(rig.Options), NullLogger<Regift>.Instance);

    [Fact]
    public void Regift_tops_up_old_achievements_and_gives_every_account_the_same_gift_once()
    {
        using var rig = Rig(boost: 1, people: true);
        // до перебудови: Оля здобула «Перший крок» (5) і «Першу перемогу» (10) за старими цінами
        rig.Events.Raise(rig.Finished("r1", Ttt, ["Оля", "Петро"], [0]));
        Assert.Equal(15, rig.Paid("Оля", "ach:first-game") + rig.Paid("Оля", "ach:first-win"));
        rig.Events.Raise(rig.Finished("r2", Ttt, ["гість Вася", "Яся"], [0]));   // гість — без акаунта

        rig.Options.Boost = 10;
        rig.Options.RegiftId = "x10";
        rig.Options.RegiftShards = 5000;
        var lines = Regift(rig).Run();

        // Оля: (5 + 10) × 10 − 15 = 135 за ачівки + 5000; Петро: «Перший крок» 50 − 5 = 45 + 5000
        Assert.Equal(135, rig.Paid("Оля", "regift-ach:x10"));
        Assert.Equal(5000, rig.Paid("Оля", "regift:x10"));
        Assert.Equal(45, rig.Paid("Петро", "regift-ach:x10"));
        Assert.Equal(5000, rig.Paid("Петро", "regift:x10"));
        // акаунт без ачівок — лише подарунок; гість — нічого
        Assert.Equal(0, rig.Paid("Сем", "regift-ach:"));
        Assert.Equal(5000, rig.Paid("Сем", "regift:x10"));
        Assert.Equal(0, rig.Paid("гість Вася", "regift"));
        Assert.Equal(EconomyRig.Cast.Length, lines.Count);
        Assert.DoesNotContain(rig.Outbox.Of<Journal>(), j => j.Text.Contains("Глек"));   // у Балачки — ні слова

        // другий старт із тим самим ярликом нічого не подвоює
        rig.Outbox.Clear();
        Assert.Empty(Regift(rig).Run());
        Assert.Empty(rig.Outbox.Of<WalletChanged>());
    }

    [Fact]
    public void Achievements_won_after_the_boost_are_not_paid_twice_and_a_new_label_pays_only_the_difference()
    {
        using var rig = Rig(boost: 10, people: true);
        rig.Events.Raise(rig.Finished("r1", Ttt, ["Оля", "Петро"], [0]));   // уже за ×10: 50 + 100 і «Сотня» 100
        var paid = rig.Paid("Оля", "ach:");
        var catalog = rig.Achievements.Of("Оля").Sum(a => a.Info.Reward);
        Assert.Equal(catalog * 10, paid);
        rig.Options.RegiftId = "x10";
        rig.Options.RegiftShards = 0;
        Regift(rig).Run();
        Assert.Equal(0, rig.Paid("Оля", "regift-ach:"));
        Assert.Equal(0, rig.Paid("Оля", "regift:"));

        // колись ×20 — новий ярлик доплачує лише різницю: ціни × 20 − уже заплачене × 10
        rig.Options.Boost = 20;
        rig.Options.RegiftId = "x20";
        Regift(rig).Run();
        Assert.Equal(catalog * 20 - paid, rig.Paid("Оля", "regift-ach:x20"));

        // а знижений множник нічого не забирає
        rig.Options.Boost = 2;
        rig.Options.RegiftId = "low";
        Regift(rig).Run();
        Assert.Equal(0, rig.Paid("Оля", "regift-ach:low"));
    }

    [Fact]
    public void Regift_skips_bots_and_does_nothing_without_a_label()
    {
        using var rig = Rig(people: true);
        // аі-агент, якого сервер запам'ятав ботом ще до того, як нік зайняли акаунтом
        Assert.True(Bots.Mark(rig.Db, "Агент Сміт", "mcp", rig.Clock.UtcNow));
        rig.Db.Accounts("Агент Сміт");
        rig.Options.RegiftShards = 5000;

        rig.Options.RegiftId = "  ";
        Assert.Empty(Regift(rig).Run());

        rig.Options.RegiftId = "x10";
        Regift(rig).Run();
        Assert.Equal(0, rig.Paid("Агент Сміт", "regift"));
        Assert.Equal(5000, rig.Paid("Оля", "regift:x10"));
    }

    [Fact]
    public void The_wallet_names_the_regift_in_human_words()
    {
        using var rig = Rig();
        Assert.Equal("подарунок від Дядька Глека", rig.Economy.Reason("regift:x10"));
        Assert.Equal("Дядько Глек перерахував ачівки за новим курсом", rig.Economy.Reason("regift-ach:x10"));
    }

    // ---------- гончарне коло ----------

    static RoomHarness Wheel(EconomyOptions o)
    {
        var h = new RoomHarness("clicker", services: RoomHarness.WithService<IOptionsMonitor<EconomyOptions>>(new FixedOptions<EconomyOptions>(o)));
        h.Solo("Оля");
        var room = h.Room;
        lock (room.Sync)
        {
            var node = JsonNode.Parse(room.Game.Save()!)!;
            node["pots"] = 500;
            node["total"] = 500;
            room.Game.Load(node.ToJsonString());
        }
        return h;
    }

    [Fact]
    public void The_clicker_trades_a_hundred_pots_for_ten_shards_and_says_so()
    {
        var h = Wheel(new EconomyOptions { Boost = 10, ClickerDailyCap = 3 });
        var v = h.View(0);
        Assert.Equal(10, v.GetProperty("per").GetInt32());
        Assert.Equal(3, v.GetProperty("cap").GetInt32());            // у сотнях глеків
        Assert.Equal(3, v.GetProperty("canSellToday").GetInt32());

        Assert.Equal("Сьогодні лишилось 30 — більше не візьму", h.Act(0, "sell", new { pots = 400 }).Message);
        Assert.Equal("Міняю сотнями: 100 глеків — 10 черепків", h.Act(0, "sell", new { pots = 150 }).Message);
        var ok = h.Act(0, "sell", new { pots = 200 });
        Assert.True(ok.Ok);
        Assert.Equal("Обміняв 200 глеків на 20 черепків", ok.Message);
        var award = Assert.Single(h.Awards);
        Assert.Equal(20, award.Shards);
        Assert.Equal("clicker", award.Reason);
        Assert.Equal(2, h.View(0).GetProperty("soldToday").GetInt32());
    }

    [Fact]
    public void Without_a_boost_the_clicker_is_exactly_as_before()
    {
        var h = Wheel(new EconomyOptions { ClickerDailyCap = 3 });
        Assert.Equal(1, h.View(0).GetProperty("per").GetInt32());
        Assert.Equal("Міняю сотнями: 100 глеків — один черепок", h.Act(0, "sell", new { pots = 150 }).Message);
        Assert.Equal("Обміняв 300 глеків на 3 черепки", h.Act(0, "sell", new { pots = 300 }).Message);
        Assert.Equal(3, Assert.Single(h.Awards).Shards);
    }
}
