using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «✨ Глек з бажанням» (v13 Г7, ClickerWish.cs): раз на добу чарівний глек замість звичайного (12 с), до двох повторних
/// спроб, лише коли гончар тут; три картки бажань, кожен ефект, подарунок другові (друг отримав, дарувальник +10 хв гри,
/// один раз), протермінування за 24 год, збереження.
/// </summary>
public class ClickerWishTests
{
    sealed class Fixed(double value) : Random
    {
        public override double NextDouble() => value;
        public override int Next(int max) => 0;
    }

    sealed class Tsekh
    {
        public FakeStore Store { get; } = new();
        public FakeClock Clock { get; } = new();
        public ClickerGuildService Svc { get; }
        public Tsekh() => Svc = new ClickerGuildService(Store, Clock);

        public RoomHarness Potter(string nick, double wishRoll = 0)
        {
            var sp = new ServiceCollection().AddSingleton(Svc).AddSingleton(new ClickerRiddleService()).BuildServiceProvider();
            var h = new RoomHarness("clicker", services: sp);
            h.Solo(nick);
            Game(h).WishRng = new Fixed(wishRoll);
            Assert.True(h.Act(0, "look").Ok);
            Publish(h, nick);
            return h;
        }

        public void Publish(RoomHarness h, string nick)
        {
            lock (h.Room.Sync) Store.SaveState("clicker:" + ClickerGuildService.Key(nick), h.Room.Game.Save()!);
        }
    }

    static Clicker Game(RoomHarness h) => (Clicker)h.Room.Game;

    static RoomHarness Wheel(string nick = "Оля", double roll = 0)
    {
        var sp = new ServiceCollection().AddSingleton(new ClickerRiddleService()).BuildServiceProvider();
        var h = new RoomHarness("clicker", services: sp);
        h.Solo(nick);
        Game(h).WishRng = new Fixed(roll);
        return h;
    }

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var rng = Game(h).WishRng;
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
            Game(h).WishRng = rng;
        }
    }

    static JsonNode Save(RoomHarness h)
    {
        lock (h.Room.Sync) return JsonNode.Parse(h.Room.Game.Save()!)!;
    }

    static string Today(RoomHarness h) => ClickerDayService.DayOf(h.Clock.UtcNow);
    static ActResult Spin(RoomHarness h) => h.Act(0, "spin", PotterHands.Human(3));
    static ActResult Wish(RoomHarness h, string pick, string? gift = null) =>
        h.Act(0, "wish", gift is null ? new { pick } : new { pick, gift });
    static JsonElement Fall(RoomHarness h) => h.View(0).GetProperty("fall");
    static bool Magic(RoomHarness h) => WishView(h) is { } w && w.GetProperty("magic").GetBoolean();
    /// <summary>Годинник — на початок глека з полиці, що чекає (як клієнт, що дочекався польоту).</summary>
    static void ToFall(RoomHarness h) => h.Clock.UtcNow = Fall(h).GetProperty("at").GetDateTimeOffset();
    static double Pots(RoomHarness h) => h.View(0).GetProperty("pots").GetDouble();
    static JsonElement? WishView(RoomHarness h) =>
        h.View(0).TryGetProperty("chores", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 3 && c[3].ValueKind == JsonValueKind.Object ? c[3] : null;
    static List<string> Cards(RoomHarness h) =>
        WishView(h) is { } w && w.TryGetProperty("offer", out var o) && o.ValueKind == JsonValueKind.Array
            ? o.EnumerateArray().Select(c => c.GetProperty("k").GetString()!).ToList() : [];
    static int Bank(RoomHarness h, string kind) =>
        h.View(0).TryGetProperty("buffs", out var b) && b.ValueKind == JsonValueKind.Object ? b.GetProperty(kind).GetProperty("bank").GetInt32() : 0;

    static void FallNow(RoomHarness h) => Patch(h, s =>
        s["fall"] = new JsonObject { ["at"] = h.Clock.UtcNow.ToString("O"), ["until"] = (h.Clock.UtcNow + Clicker.FallShown).ToString("O"), ["x"] = 40 });

    /// <summary>Розклад чарівного глека на цю мить: сьогодні, спроб було <paramref name="tries"/>, час настав.</summary>
    static void MagicDueNow(RoomHarness h, int tries = 0) => Patch(h, s =>
        s["wish"] = new JsonObject { ["day"] = Today(h), ["tries"] = tries, ["at"] = h.Clock.UtcNow.ToString("O"), ["done"] = false });

    /// <summary>Пропозиція з цих карток чекає вибору (24 год).</summary>
    static void Offer(RoomHarness h, params string[] cards) => Patch(h, s =>
        s["wish"] = new JsonObject
        {
            ["day"] = Today(h), ["tries"] = 1, ["done"] = true, ["cards"] = new JsonArray(cards.Select(x => (JsonNode)x).ToArray()),
            ["until"] = h.Clock.UtcNow.AddHours(24).ToString("O"),
        });

    /// <summary>Усе для всіх бажань: Пристань, зала скарбів, палій.</summary>
    static void Rich(RoomHarness h) => Patch(h, s =>
    {
        s["pots"] = 1e12;
        s["total"] = 1e40;
        s["styles"] = new JsonArray("kosiv");
        var built = Clicker.Buildings.Select(b => b.Key).TakeWhile(k => k != "pier").Append("pier");
        s["toloka"] = new JsonObject { ["built"] = new JsonArray(built.Select(x => (JsonNode)x).ToArray()) };
        s["album"] = new JsonObject { ["cells"] = new JsonObject { ["pot"] = new JsonArray("") } };
        s["craft"] = new JsonObject { ["firedBy"] = new JsonObject { ["pot"] = 50 } };
        s["guild"] = new JsonObject { ["rank"] = (int)Clicker.GuildJourneyman };
    });

    // ---------- розклад ----------

    [Theory]
    [InlineData(0.0, 2)]
    [InlineData(1.0, 40)]
    public void The_first_hand_action_of_the_day_plans_the_magic_jug_two_to_forty_minutes_ahead(double roll, int minutes)
    {
        var h = Wheel(roll: roll);
        Assert.True(Spin(h).Ok);
        var w = Save(h)["wish"]!;
        var at = w["at"]!.GetValue<DateTimeOffset>();
        Assert.Equal(h.Clock.UtcNow.AddMinutes(minutes), at);
        Assert.Equal(Today(h), w["day"]!.GetValue<string>());
        Assert.False(Magic(h));                                         // до часу — звичайні глеки
    }

    [Fact]
    public void A_quiet_look_does_not_plan_it_and_the_next_day_plans_it_again()
    {
        var h = Wheel();
        Assert.True(h.Act(0, "look").Ok);
        Assert.Null(Save(h)["wish"]);
        Spin(h);
        var first = Save(h)["wish"]!["at"]!.GetValue<DateTimeOffset>();
        h.Clock.Advance(TimeSpan.FromHours(24));
        Spin(h);
        Assert.True(Save(h)["wish"]!["at"]!.GetValue<DateTimeOffset>() >= first.AddHours(24));
        Assert.Equal(Today(h), Save(h)["wish"]!["day"]!.GetValue<string>());
    }

    [Fact]
    public void When_the_time_comes_a_magic_jug_falls_instead_of_the_plain_one_and_hangs_twelve_seconds()
    {
        var h = Wheel();
        Spin(h);
        MagicDueNow(h);
        FallNow(h);
        Assert.True(h.Act(0, "grab").Ok);                               // звичайний ловимо — наступним буде чарівний
        Assert.True(Magic(h));
        var f = Fall(h);
        Assert.Equal(12, (f.GetProperty("until").GetDateTimeOffset() - f.GetProperty("at").GetDateTimeOffset()).TotalSeconds);
    }

    [Fact]
    public void Before_its_time_or_when_the_potter_is_away_the_jug_stays_plain()
    {
        var h = Wheel();
        Spin(h);
        Patch(h, s => s["wish"] = new JsonObject { ["day"] = Today(h), ["tries"] = 0, ["at"] = h.Clock.UtcNow.AddHours(1).ToString("O") });
        FallNow(h);
        Assert.True(h.Act(0, "grab").Ok);
        Assert.False(Magic(h));                                         // рано

        MagicDueNow(h);
        h.Clock.Advance(TimeSpan.FromMinutes(10));                      // гончар пішов від кола — руки не свіжі
        Assert.True(h.Act(0, "look").Ok);
        Assert.False(Magic(h));
        FallNow(h);
        Assert.True(h.Act(0, "grab").Ok);                               // повернувся — і чарівний уже на полиці
        Assert.True(Magic(h));
    }

    // ---------- спроби ----------

    [Fact]
    public void A_missed_magic_jug_costs_a_try_and_comes_again_in_ten_to_twenty_minutes_up_to_three_tries()
    {
        var h = Wheel(roll: 0);
        Spin(h);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            MagicDueNow(h, attempt - 1);
            FallNow(h);
            Assert.True(h.Act(0, "grab").Ok);
            Assert.True(Magic(h), $"спроба {attempt}");
            var at = Fall(h).GetProperty("at").GetDateTimeOffset();
            h.Clock.UtcNow = at + TimeSpan.FromSeconds(1);
            Assert.True(Spin(h).Ok);                                    // гончар тут, але глека не ловить
            h.Clock.UtcNow = Fall(h).GetProperty("until").GetDateTimeOffset() + TimeSpan.FromSeconds(3);
            Spin(h);
            var w = Save(h)["wish"]!;
            Assert.Equal(attempt, w["tries"]!.GetValue<int>());
            if (attempt < 3) Assert.Equal(h.Clock.UtcNow.AddMinutes(10), w["at"]!.GetValue<DateTimeOffset>());   // Fixed(0) — рівно десять
            else Assert.Equal(default, w["at"]!.GetValue<DateTimeOffset>());   // спроби скінчились
        }
        FallNow(h);
        Spin(h);
        Assert.False(Magic(h));
    }

    [Fact]
    public void A_magic_jug_that_fell_without_the_potter_does_not_eat_a_try()
    {
        var h = Wheel();
        Spin(h);
        MagicDueNow(h);
        FallNow(h);
        Assert.True(h.Act(0, "grab").Ok);
        Assert.True(Magic(h));
        h.Clock.Advance(TimeSpan.FromMinutes(15));                      // гончар пішов, глек упав без нього
        Assert.True(Spin(h).Ok);
        Assert.Equal(0, Save(h)["wish"]!["tries"]!.GetValue<int>());
        Assert.False(Magic(h));                                         // зараз руки ще не свіжі за розкладом — чарівний чекає
    }

    // ---------- спіймав ----------

    [Fact]
    public void Catching_the_magic_jug_pays_as_usual_and_offers_three_different_available_wishes_once_a_day()
    {
        var h = Wheel();
        Spin(h);
        MagicDueNow(h);
        FallNow(h);
        Assert.True(h.Act(0, "grab").Ok);
        Assert.True(Magic(h));
        ToFall(h);
        var before = Pots(h);
        var r = h.Act(0, "grab");
        Assert.True(r.Ok, r.Message);
        Assert.True(Pots(h) > before);                                  // звичайний виграш
        Assert.Contains("Чарівний глек", r.Message);
        var cards = Cards(h);
        Assert.Equal(3, cards.Count);
        Assert.Equal(3, cards.Distinct().Count());
        Assert.All(cards, c => Assert.Contains(c, new[] { "feast", "basket", "cat", "money" }));   // у голого гончаря — лише те, що можна
        Assert.False(Magic(h));
        Assert.Equal(default, Save(h)["wish"]!["at"]!.GetValue<DateTimeOffset>());   // раз на добу: другого чарівного нема
        FallNow(h);
        Assert.True(h.Act(0, "grab").Ok);
        Assert.False(Magic(h));
    }

    [Fact]
    public void The_cards_with_everything_open_are_deterministic_for_the_day_and_can_include_the_rare_ones()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 30; i++)
        {
            var h = Wheel("гончар" + i);
            Rich(h);
            Spin(h);
            MagicDueNow(h);
            FallNow(h);
            h.Act(0, "grab");
            ToFall(h);
            h.Act(0, "grab");
            var cards = Cards(h);
            Assert.Equal(3, cards.Distinct().Count());
            foreach (var c in cards) seen.Add(c);
        }
        Assert.Contains("glory", seen);
        Assert.Contains("ship", seen);
        Assert.Contains("stoker", seen);
        Assert.Contains("shard", seen);
    }

    // ---------- вибір: кожен ефект ----------

    [Fact]
    public void Feast_and_basket_wishes_put_thirty_minutes_into_the_bank()
    {
        var h = Wheel();
        Offer(h, "feast", "basket", "money");
        var r = Wish(h, "feast");
        Assert.True(r.Ok, r.Message);
        Assert.Equal(30, Bank(h, "feast"));
        Assert.Empty(Cards(h));                                         // вибір зроблено — пропозиції нема
        Assert.False(Wish(h, "basket").Ok);
        Offer(h, "basket", "cat", "money");
        Assert.True(Wish(h, "basket").Ok);
        Assert.Equal(30, Bank(h, "basket"));
    }

    [Fact]
    public void The_glory_wish_adds_three_hundred_and_money_adds_play_minutes()
    {
        var h = Wheel();
        Rich(h);
        Spin(h);
        var slava = h.View(0).GetProperty("trade").GetProperty("slava").GetInt64();
        Offer(h, "glory", "money", "cat");
        Assert.True(Wish(h, "glory").Ok);
        Assert.Equal(slava + 300, h.View(0).GetProperty("trade").GetProperty("slava").GetInt64());
        Offer(h, "glory", "money", "cat");
        var pots = Pots(h);
        Assert.True(Wish(h, "money").Ok);
        Assert.True(Pots(h) > pots);
    }

    [Fact]
    public void The_cat_wish_wakes_a_sleeping_cat_and_keeps_it_awake_until_six_in_the_morning()
    {
        var h = Wheel();
        Assert.True(h.Act(0, "nap", new { on = true }).Ok);
        Assert.Equal("on", Fall(h).GetProperty("nap").GetString());
        Offer(h, "cat", "feast", "money");
        Assert.True(Wish(h, "cat").Ok);
        Assert.Equal(JsonValueKind.Null, Fall(h).GetProperty("nap").ValueKind);
        var until = WishView(h)!.Value.GetProperty("cat").GetDateTimeOffset();
        // 10.09 15:00 за Києвом → наступні 06:00 — 11.09, а це 03:00 UTC.
        Assert.Equal(new DateTimeOffset(2026, 9, 11, 3, 0, 0, TimeSpan.Zero), until);
        var nap = h.Act(0, "nap", new { on = true });
        Assert.False(nap.Ok);                                           // молока не хоче
        h.Clock.UtcNow = until + TimeSpan.FromMinutes(1);
        Assert.True(h.Act(0, "nap", new { on = true }).Ok);             // зранку — знову можна
    }

    [Fact]
    public void The_ship_wish_brings_a_ship_at_once_only_with_a_pier_and_free_room()
    {
        var h = Wheel();
        Rich(h);
        Spin(h);
        var ships = Ships(h);
        Offer(h, "ship", "money", "cat");
        Assert.True(Wish(h, "ship").Ok);
        Assert.Equal(ships + 1, Ships(h));

        var bare = Wheel("Без");
        Offer(bare, "ship", "money", "cat");
        Assert.False(Wish(bare, "ship").Ok);                            // нема Пристані — нікуди прикласти
    }

    static int Ships(RoomHarness h) => h.View(0).GetProperty("trade").GetProperty("ships").GetArrayLength();

    [Fact]
    public void The_shard_wish_adds_a_fragment_to_the_treasure_hall()
    {
        var h = Wheel();
        Rich(h);
        Spin(h);
        Offer(h, "shard", "money", "cat");
        Assert.True(Wish(h, "shard").Ok);
        var shards = h.View(0).GetProperty("album").GetProperty("hall").GetProperty("shards").EnumerateArray().Sum(x => x.GetInt32());
        Assert.Equal(1, shards);
    }

    [Fact]
    public void The_stoker_wish_makes_the_helper_batch_ring_like_a_hand_fired_one_for_two_hours()
    {
        var plain = RingingFromHelper(wish: false);
        var wished = RingingFromHelper(wish: true);
        Assert.True(plain <= 1, $"без бажання дзвінких мало бути майже нуль: {plain}");
        Assert.True(wished >= 3, $"з бажанням дзвінких мало: {wished}");
    }

    static int RingingFromHelper(bool wish)
    {
        var h = Wheel();
        Rich(h);
        Patch(h, s =>
        {
            s["upgrades"] = new JsonObject { ["kiln"] = 180 };
            var rack = new JsonArray();
            for (var i = 0; i < 24; i++) rack.Add(new JsonObject { ["ware"] = "pot", ["clay"] = "", ["dryAt"] = h.Clock.UtcNow.AddMinutes(-1).ToString("O") });
            s["craft"]!["rack"] = rack;
        });
        if (wish)
        {
            Offer(h, "stoker", "money", "cat");
            Assert.True(Wish(h, "stoker").Ok);
            Assert.Equal(h.Clock.UtcNow.AddHours(2), WishView(h)!.Value.GetProperty("stoker").GetDateTimeOffset());
        }
        Assert.True(h.Act(0, "kiln", new { op = "light", helper = true }).Ok);
        h.Clock.Advance(40);
        Spin(h);
        var last = h.View(0).GetProperty("kiln").GetProperty("last");
        return last.GetProperty("items").EnumerateArray().Count(i => i[1].GetInt32() >= 3);
    }

    // ---------- термін і збереження ----------

    [Fact]
    public void An_unmade_choice_waits_twenty_four_hours_and_survives_a_save_and_a_load()
    {
        var h = Wheel();
        Offer(h, "feast", "basket", "money");
        Patch(h, _ => { });                                             // Save → Load
        Assert.Equal(["feast", "basket", "money"], Cards(h));
        h.Clock.Advance(TimeSpan.FromHours(23));
        Assert.Equal(3, Cards(h).Count);
        h.Clock.Advance(TimeSpan.FromHours(2));
        Assert.Empty(Cards(h));
        var late = Wish(h, "feast");
        Assert.False(late.Ok);
        Assert.Equal(0, Bank(h, "feast"));
    }

    [Fact]
    public void A_card_that_is_not_on_offer_is_refused()
    {
        var h = Wheel();
        Assert.False(Wish(h, "feast").Ok);                              // нічого не чекає
        Offer(h, "feast", "basket", "money");
        Assert.False(Wish(h, "cat").Ok);
        Assert.Equal(0, Bank(h, "feast"));
        Assert.Equal(3, Cards(h).Count);                                // помилка пропозицію не з'їла
    }

    [Fact]
    public void The_star_wish_with_no_pick_still_works_as_before()
    {
        var h = Wheel();
        var r = h.Act(0, "wish");
        Assert.False(r.Ok);
        Assert.Equal("Зірка вже згасла", r.Message);
    }

    // ---------- подарунок другові ----------

    [Fact]
    public void A_gifted_wish_reaches_the_friend_once_and_pays_the_giver_ten_play_minutes()
    {
        var g = new Tsekh();
        var ola = g.Potter("Оля");
        var petro = g.Potter("Петро");
        Offer(ola, "feast", "basket", "money");
        var before = Pots(ola);
        var r = Wish(ola, "feast", "Петро");
        Assert.True(r.Ok, r.Message);
        Assert.Empty(Cards(ola));
        Assert.Equal(0, Bank(ola, "feast"));                            // собі не взяла
        Assert.Equal(0, Bank(petro, "feast"));                          // друг ще не заходив

        var got = Spin(petro);
        Assert.True(got.Ok, got.Message);
        Assert.Contains("подарував(-ла) тобі бажання", got.Message);
        Assert.Contains("Оля", got.Message);
        Assert.Equal(30, Bank(petro, "feast"));
        Spin(petro);
        Assert.Equal(30, Bank(petro, "feast"));                         // удруге не прийшло

        var thanks = Spin(ola);
        Assert.True(thanks.Ok, thanks.Message);
        Assert.Contains("прийняв(-ла) твоє бажання", thanks.Message);
        Assert.True(Pots(ola) > before);
        Assert.DoesNotContain("прийняв(-ла)", Spin(ola).Message);
    }

    [Fact]
    public void A_wish_the_friend_cannot_use_turns_into_a_bag_of_pots_and_giving_to_nobody_fails()
    {
        var g = new Tsekh();
        var ola = g.Potter("Оля");
        var petro = g.Potter("Петро");
        Offer(ola, "ship", "money", "cat");
        Assert.False(Wish(ola, "ship", "Оля").Ok);                      // собі — не дарунок
        Assert.False(Wish(ola, "ship", "Незнайко").Ok);                 // ще не сідав за коло
        Assert.Equal(3, Cards(ola).Count);                              // відмова пропозицію не з'їла
        Assert.True(Wish(ola, "ship", "Петро").Ok);
        var before = Pots(petro);
        var got = Spin(petro);
        Assert.Contains("мішок глеків", got.Message);
        Assert.True(Pots(petro) > before);
    }

    [Fact]
    public void A_full_mailbox_refuses_the_gift_and_the_wish_stays_with_the_giver()
    {
        var g = new Tsekh();
        var ola = g.Potter("Оля");
        g.Potter("Петро");
        for (var i = 0; i < ClickerGuildService.MailMax; i++)
            Assert.Null(g.Svc.SendWish("k" + i, "Друг" + i, "Петро", "feast", g.Clock.UtcNow));
        Offer(ola, "feast", "basket", "money");
        var r = Wish(ola, "feast", "Петро");
        Assert.False(r.Ok);
        Assert.Equal("Скринька Петро повна — спробуй пізніше", r.Message);
        Assert.Equal(3, Cards(ola).Count);                              // бажання не з'їдено
    }
}
