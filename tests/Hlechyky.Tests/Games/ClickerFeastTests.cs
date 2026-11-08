using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Бафи-запаси v13 Г (ClickerFeast.cs): «🎉 Свято» (×2 до всього) і «🧺 Щасливий кошик» (глеки з полиці ×3) — банки хвилин зі
/// стелею 180, вмикання витрачає банку одним відрізком, баф іде в реальному часі; призи «Майстра» й «Ловця дня» гончар
/// забирає сам на синхронізації (раз, ключ у збереженні); спійманий руками глек з полиці — очко «Ловця дня».
/// </summary>
public class ClickerFeastTests
{
    sealed class Via(Func<DateTimeOffset> now) : IClock
    {
        public DateTimeOffset UtcNow => now();
    }

    static (RoomHarness H, ClickerDayService Day) Wheel(string nick = "Оля")
    {
        RoomHarness h = null!;
        var day = new ClickerDayService(null, new FakeStakes(), () => new ClickerDayOptions(), new Via(() => h.Clock.UtcNow), person: _ => true);
        var sp = new ServiceCollection().AddSingleton(day).BuildServiceProvider();
        h = new RoomHarness("clicker", services: sp);
        h.Solo(nick);
        return (h, day);
    }

    static Clicker Game(RoomHarness h) => (Clicker)h.Room.Game;

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    static int Bank(RoomHarness h, string kind, int minutes)
    {
        lock (h.Room.Sync) return Game(h).BuffBank(kind, minutes, "За тест");
    }

    static ActResult Buff(RoomHarness h, string kind) => h.Act(0, "buff", new { kind });
    static ActResult Spin(RoomHarness h) => h.Act(0, "spin", PotterHands.Human(5));
    static double PerSecond(RoomHarness h) => h.View(0).GetProperty("perSecond").GetDouble();
    static double FallGain(RoomHarness h) => h.View(0).GetProperty("fall").GetProperty("gain").GetDouble();
    /// <summary>Вид бафів; null (або нема), коли запасів нема й нічого не триває — тоді всі банки нуль.</summary>
    static JsonElement? Buffs(RoomHarness h) =>
        h.View(0).TryGetProperty("buffs", out var b) && b.ValueKind == JsonValueKind.Object ? b : null;
    static int FeastBank(RoomHarness h) => Buffs(h)?.GetProperty("feast").GetProperty("bank").GetInt32() ?? 0;
    static int BasketBank(RoomHarness h) => Buffs(h)?.GetProperty("basket").GetProperty("bank").GetInt32() ?? 0;
    static DateTimeOffset FeastUntil(RoomHarness h) =>
        Buffs(h)?.GetProperty("feast").GetProperty("until") is { ValueKind: JsonValueKind.String } u ? u.GetDateTimeOffset() : default;

    static void Rich(RoomHarness h) => Patch(h, s => s["upgrades"] = new JsonObject { ["apprentice"] = 40, ["wheel"] = 30 });

    // ---------- банки ----------

    [Fact]
    public void Banks_fill_up_to_one_hundred_eighty_minutes_and_say_how_much_fitted()
    {
        var (h, _) = Wheel();
        Assert.Equal(100, Bank(h, "feast", 100));
        Assert.Equal(80, Bank(h, "feast", 100));
        Assert.Equal(0, Bank(h, "feast", 10));
        Assert.Equal(180, FeastBank(h));
        Assert.Equal(0, BasketBank(h));                                // банки окремі
        Assert.Equal(0, Bank(h, "fish", 10));
        Assert.Equal(0, Bank(h, "basket", 0));
        Assert.Equal(0, Bank(h, "basket", -5));
        Assert.Equal(180, Clicker.BuffBankCap);
    }

    [Fact]
    public void Banked_minutes_survive_a_save_and_a_load()
    {
        var (h, _) = Wheel();
        Bank(h, "feast", 45);
        Bank(h, "basket", 30);
        Patch(h, _ => { });                                            // Save → Load
        Assert.Equal(45, FeastBank(h));
        Assert.Equal(30, BasketBank(h));
        Patch(h, s => s["feast"]!["feast"] = 999);                     // правлена база — стеля
        Assert.Equal(180, FeastBank(h));
    }

    // ---------- Свято ----------

    [Fact]
    public void Turning_on_the_feast_spends_the_whole_bank_and_doubles_everything_until_it_runs_out()
    {
        var (h, _) = Wheel();
        Rich(h);
        var plain = PerSecond(h);
        Assert.True(plain > 0);
        Bank(h, "feast", 60);
        Assert.Equal(plain, PerSecond(h));                             // у запасі — ще не діє

        var start = h.Clock.UtcNow;
        var r = Buff(h, "feast");
        Assert.True(r.Ok, r.Message);
        Assert.Contains("60 хв", r.Message);
        Assert.Equal(0, FeastBank(h));
        Assert.Equal(start + TimeSpan.FromMinutes(60), FeastUntil(h));
        Assert.Equal(2.0, PerSecond(h) / plain, 6);

        h.Clock.Advance(TimeSpan.FromMinutes(59));                     // іде в реальному часі, без жодної дії
        Assert.Equal(2.0, PerSecond(h) / plain, 6);
        h.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1.0, PerSecond(h) / plain, 6);
        Assert.Equal(default, FeastUntil(h));
    }

    [Fact]
    public void The_feast_keeps_running_across_a_reload_and_does_not_touch_the_basket()
    {
        var (h, _) = Wheel();
        Rich(h);
        var plain = PerSecond(h);
        var gain = FallGain(h);
        Bank(h, "feast", 30);
        Assert.True(Buff(h, "feast").Ok);
        Patch(h, _ => { });
        Assert.Equal(2.0, PerSecond(h) / plain, 6);
        Assert.InRange(FallGain(h) / gain, 1.9, 2.1);                  // глек з полиці росте лише через пасив (×2), не ×3 кошика
        Assert.Equal(0, BasketBank(h));
    }

    [Fact]
    public void An_empty_bank_or_an_unknown_buff_cannot_be_turned_on()
    {
        var (h, _) = Wheel();
        Assert.False(Buff(h, "feast").Ok);
        Assert.False(Buff(h, "basket").Ok);
        Assert.False(Buff(h, "fish").Ok);
        Assert.False(h.Act(0, "buff", new { }).Ok);
    }

    [Fact]
    public void Turning_on_a_running_buff_adds_the_new_minutes_to_its_end_instead_of_losing_them()
    {
        var (h, _) = Wheel();
        var start = h.Clock.UtcNow;
        Bank(h, "feast", 60);
        Assert.True(Buff(h, "feast").Ok);
        h.Clock.Advance(TimeSpan.FromMinutes(10));
        Bank(h, "feast", 30);
        Assert.True(Buff(h, "feast").Ok);
        Assert.Equal(start + TimeSpan.FromMinutes(90), FeastUntil(h));
        Assert.Equal(0, FeastBank(h));
    }

    // ---------- Щасливий кошик ----------

    [Fact]
    public void The_basket_triples_shelf_jugs_only_and_only_while_it_lasts()
    {
        var (h, _) = Wheel();
        Rich(h);
        var plain = PerSecond(h);
        var gain = FallGain(h);
        Assert.True(gain > 100);                                       // дно не псує відношення
        Bank(h, "basket", 20);
        Assert.Equal(gain, FallGain(h));

        Assert.True(Buff(h, "basket").Ok);
        Assert.InRange(FallGain(h) / gain, 2.99, 3.01);
        Assert.Equal(plain, PerSecond(h));                             // решта доходу не змінилась
        Assert.Equal(0, BasketBank(h));

        h.Clock.Advance(TimeSpan.FromMinutes(21));
        Assert.Equal(gain, FallGain(h), 6);
    }

    // ---------- призи дня ----------

    static void Settle(RoomHarness h, ClickerDayService day, Action<DateTimeOffset> play)
    {
        play(h.Clock.UtcNow);
        h.Clock.UtcNow = ClickerDayService.Ends(ClickerDayService.DayOf(h.Clock.UtcNow)).AddMinutes(5);
        Assert.Equal(1, day.Settle(h.Clock.UtcNow));
    }

    [Fact]
    public void The_potter_collects_day_prizes_himself_once_and_gets_a_toast()
    {
        var (h, day) = Wheel("Оля");
        Settle(h, day, now =>
        {
            day.Add("Оля", 40, now);
            day.Add("Тарас", 35, now);
            day.AddCatch("Оля", 30, now);
            day.AddCatch("Тарас", 25, now);
        });
        var r = Spin(h);
        Assert.True(r.Ok, r.Message);
        Assert.Contains("🎉 За «Майстра дня»: +60 хв Свята в запасі", r.Message);
        Assert.Contains("🧺 За «Ловця дня»: +60 хв Кошика в запасі", r.Message);
        Assert.Equal(60, FeastBank(h));
        Assert.Equal(60, BasketBank(h));

        Assert.DoesNotContain("в запасі", Spin(h).Message);            // удруге — нічого
        Patch(h, _ => { });                                            // перезапуск, інший пристрій
        Spin(h);
        Assert.Equal(60, FeastBank(h));
        Assert.Equal(60, BasketBank(h));
    }

    [Fact]
    public void The_second_place_gets_thirty_minutes_and_a_full_bank_takes_only_what_fits()
    {
        var (h, day) = Wheel("Тарас");
        Settle(h, day, now =>
        {
            day.Add("Оля", 40, now);
            day.Add("Тарас", 35, now);
        });
        Bank(h, "feast", 170);
        var r = Spin(h);
        Assert.Contains("+10 хв Свята в запасі (запас повний)", r.Message);
        Assert.Equal(180, FeastBank(h));

        // і не повертається, коли банку витратили: приз уже забраний
        Assert.True(Buff(h, "feast").Ok);
        Spin(h);
        Assert.Equal(0, FeastBank(h));
    }

    [Fact]
    public void Someone_elses_prizes_and_a_day_without_winners_bring_nothing()
    {
        var (h, day) = Wheel("Богдан");
        Settle(h, day, now =>
        {
            day.Add("Оля", 40, now);
            day.Add("Тарас", 35, now);
        });
        Spin(h);
        Assert.Equal(0, FeastBank(h));
        Assert.Equal(0, BasketBank(h));
    }

    [Fact]
    public void Old_prize_keys_are_dropped_from_the_save_but_fresh_ones_stay()
    {
        var (h, day) = Wheel("Оля");
        Settle(h, day, now =>
        {
            day.Add("Оля", 40, now);
            day.Add("Тарас", 35, now);
        });
        Patch(h, s => s["feast"] = new JsonObject
        {
            ["taken"] = new JsonArray("day:2026-07-01:master:1", "day:2026-09-09:catcher:2", "чужий-ключ"),
        });
        Spin(h);
        var taken = JsonNode.Parse(h.Room.Game.Save()!)!["feast"]!["taken"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        Assert.DoesNotContain("day:2026-07-01:master:1", taken);
        Assert.Contains("day:2026-09-09:catcher:2", taken);
        Assert.Contains("day:2026-09-10:master:1", taken);             // щойно забраний
        Assert.Contains("чужий-ключ", taken);                          // невідомого формату не чіпаємо
    }

    // ---------- очки «Ловця дня» ----------

    [Fact]
    public void A_shelf_jug_caught_by_hand_is_a_point_for_the_catcher_but_a_golden_one_is_not()
    {
        var (h, day) = Wheel("Оля");
        string Today() => ClickerDayService.DayOf(h.Clock.UtcNow);
        Patch(h, s => s["fall"] = new JsonObject
        {
            ["at"] = h.Clock.UtcNow.ToString("O"), ["until"] = (h.Clock.UtcNow + Clicker.FallShown).ToString("O"), ["x"] = 40,
        });
        Assert.True(h.Act(0, "grab").Ok);
        Assert.Equal(1, day.BoardCatch(Today()).Single().Points);

        Patch(h, s => s["golden"] = new JsonObject
        {
            ["at"] = h.Clock.UtcNow.ToString("O"), ["until"] = (h.Clock.UtcNow + Clicker.GoldenShown).ToString("O"),
            ["kind"] = (int)Clicker.GoldenKind.Merchant, ["x"] = 10, ["y"] = 10,
        });
        Assert.True(h.Act(0, "catch").Ok);
        Assert.Equal(1, day.BoardCatch(Today()).Single().Points);
        Assert.Empty(day.Board(Today()));                              // дошка майстра — окрема
    }
}
