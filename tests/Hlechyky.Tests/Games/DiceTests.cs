using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Під глеком» (брехливі кості): паспорт, роздача, ставки за таблицею Perudo, «Брешеш!», «Точно!», таймер
/// ходу, паліфіко, кінець, вихід, «Ще раз», приховане, форма виду й кадру (TESTING.md §4, spec dice.md §8).
/// Руки кладемо руками (<see cref="DiceCore.Arrange"/>) уже у фазі ставок — після того, як раунд їх накидав.
/// </summary>
public class DiceTests
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван", "Марта", "Богдан"];

    internal static RoomHarness Table(int players = 2, int seed = 42, object? options = null)
    {
        var h = new RoomHarness("dice", options, seed);
        for (var i = 0; i < players; i++) h.Join(Nicks[i]);
        h.Start();
        return h;
    }

    internal static Dice Game(RoomHarness h) => (Dice)h.Room.Game;
    internal static DiceCore Core(RoomHarness h) => Game(h).Core;
    static string Phase(RoomHarness h) => h.View(null).GetProperty("phase").GetString()!;
    static JsonElement Reveal(RoomHarness h) => h.View(null).GetProperty("reveal");

    /// <summary>Тикати, поки не настане фаза (або поки партія не скінчиться).</summary>
    static void Until(RoomHarness h, string phase, int max = 400)
    {
        for (var i = 0; i < max && h.Room.Status == RoomStatus.Playing && Phase(h) != phase; i++) h.Tick();
        Assert.Equal(phase, Phase(h));
    }

    static void ToBid(RoomHarness h) => Until(h, "bid", 20);

    /// <summary>Стіл у фазі ставок із руками, покладеними руками, і ходом у <paramref name="turn"/>.</summary>
    static RoomHarness Arranged(int[]?[] hands, int turn = 0, int seed = 42, object? options = null)
    {
        var h = Table(hands.Length, seed, options);
        ToBid(h);
        Core(h).Arrange(hands);
        Core(h).SetTurn(turn);
        return h;
    }

    static void Ok(ActResult r) => Assert.True(r.Ok, r.Message);

    static void Refused(RoomHarness h, ActResult r, string text)
    {
        Assert.False(r.Ok);
        Assert.Contains(text, r.Message);
    }

    static int[] Ints(JsonElement e) => [.. e.EnumerateArray().Select(x => x.GetInt32())];

    /// <summary>Усі види разом (шість місць і глядач) — для «стан не змінився» й детермінізму.</summary>
    static string AllViews(RoomHarness h)
    {
        var sb = new StringBuilder();
        for (var s = 0; s < DiceCore.MaxSeats; s++) sb.Append(Views.Text(h.Room.Game.View(s))).Append('\n');
        sb.Append(Views.Text(h.Room.Game.View(null)));
        return sb.ToString();
    }

    static JsonElement Player(RoomHarness h, int seat) =>
        h.View(null).GetProperty("players").EnumerateArray().Single(p => p.GetProperty("seat").GetInt32() == seat);

    static int DiceOf(RoomHarness h, int seat) => Player(h, seat).GetProperty("dice").GetInt32();

    /// <summary>
    /// Бот за столом: у розкритті всі живі тиснуть «Далі», на своєму ході — найнижча законна ставка, а кожен
    /// п'ятий хід (або коли вище нема куди) — «Брешеш!». Той самий бот — у детермінізмі, фазі й перф-тесті.
    /// </summary>
    internal static void BotStep(RoomHarness h, ref int moves)
    {
        var g = Game(h);
        var c = g.Core;
        if (h.Room.Status != RoomStatus.Playing) return;
        if (g.Phase == DicePhase.Reveal)
        {
            for (var s = 0; s < DiceCore.MaxSeats; s++)
                if (c.Alive[s] && !c.Ready[s]) h.Act(s, "ready");
            return;
        }
        if (g.Phase != DicePhase.Bid) return;
        var t = c.Turn;
        var found = Lowest(c, t, out var q, out var f);
        if (c.Bid is { } b && (++moves % 5 == 0 || !found)) h.Act(t, "liar", new { q = b.Q, f = b.F });
        else if (found) h.Act(t, "bid", new { q, f });
    }

    /// <summary>Найнижча законна ставка для місця: найменша кількість, за рівної — найнижча грань (глечики останні).</summary>
    internal static bool Lowest(DiceCore c, int seat, out int q, out int f)
    {
        q = int.MaxValue;
        f = 0;
        foreach (var face in new[] { 2, 3, 4, 5, 6, 1 })
        {
            var min = DiceCore.MinQ(c.Bid, face, c.Palifico, c.Count[seat]);
            if (min > 0 && min <= c.Total && min < q) { q = min; f = face; }
        }
        return f != 0;
    }

    // =========================================================================================
    // Паспорт і опції
    // =========================================================================================

    [Fact]
    public void Dice_is_a_hidden_party_game_for_two_to_six_started_by_host()
    {
        var registry = RoomHarness.NewRegistry();
        var info = registry.Info("dice")!;
        Assert.Equal(GameGroup.Party, info.Group);
        Assert.Equal(2, info.MinPlayers);
        Assert.Equal(6, info.MaxPlayers);
        Assert.Equal(StartMode.ByHost, info.Start);
        Assert.True(info.Hidden);
        Assert.False(info.Rated);
        Assert.Equal(250, info.TickMs);
        Assert.Equal("«Під глеком»", info.Accusative);
        Assert.DoesNotContain("п'ять", info.Hint);   // з опцією «3 — швидка партія» п'яти кісточок нема
        var opts = info.Options!.ToDictionary(o => o.Key, o => o.Default);
        Assert.Equal(new Dictionary<string, string> { ["dice"] = "5", ["turn"] = "30", ["exact"] = "on", ["palifico"] = "on" }, opts);
        var cat = registry.Catalog.Single(g => g.Id == "dice");
        Assert.True(cat.HasCss);
        Assert.Equal("party", cat.Group);
        Assert.Equal("dice", cat.Module);
        Assert.Equal("шостий", registry.Create("dice")!.SeatName(5));
    }

    [Fact]
    public void Options_outside_the_passport_fall_back_to_defaults()
    {
        var h = Table(2, options: new { dice = "7", turn = "5", exact = "maybe", palifico = "ні" });
        var rules = h.View(0).GetProperty("rules");
        Assert.Equal(5, rules.GetProperty("dice").GetInt32());
        Assert.Equal(30_000, rules.GetProperty("turnMs").GetInt32());
        Assert.True(rules.GetProperty("exact").GetBoolean());
        Assert.True(rules.GetProperty("palifico").GetBoolean());
    }

    [Fact]
    public void Three_dice_option_deals_three_and_caps_exact_at_three()
    {
        var h = Table(3, options: new { dice = "3", turn = "60" });
        Assert.All(h.View(null).GetProperty("players").EnumerateArray(), p => Assert.Equal(3, p.GetProperty("dice").GetInt32()));
        Assert.Equal(9, h.View(null).GetProperty("total").GetInt32());
        Assert.Equal(60_000, h.View(null).GetProperty("rules").GetProperty("turnMs").GetInt32());
        ToBid(h);
        Core(h).SetTurn(0);
        Ok(h.Act(0, "bid", new { q = 2, f = 4 }));
        Refused(h, h.Act(2, "exact", new { q = 2, f = 4 }), "повний глек");
        Assert.False(h.View(2).GetProperty("canExact").GetBoolean());
    }

    // =========================================================================================
    // Роздача й раунд
    // =========================================================================================

    [Fact]
    public void Start_gives_every_seated_player_five_dice_and_a_random_starter()
    {
        var starters = new HashSet<int>();
        for (var seed = 1; seed <= 30; seed++)
        {
            var h = Table(4, seed);
            var v = h.View(null);
            Assert.Equal(4, v.GetProperty("players").GetArrayLength());
            Assert.All(v.GetProperty("players").EnumerateArray(), p =>
            {
                Assert.Equal(5, p.GetProperty("dice").GetInt32());
                Assert.True(p.GetProperty("alive").GetBoolean());
            });
            Assert.Equal(20, v.GetProperty("total").GetInt32());
            Assert.Equal(1, v.GetProperty("round").GetInt32());
            var starter = v.GetProperty("starter").GetInt32();
            Assert.InRange(starter, 0, 3);
            starters.Add(starter);
            for (var s = 0; s < 4; s++) Assert.Equal(5, h.View(s).GetProperty("my").GetArrayLength());
        }
        Assert.True(starters.Count >= 3, "стартер має бути випадковим");
    }

    [Fact]
    public void Round_opens_with_a_shake_and_no_action_is_accepted_until_the_bid_phase()
    {
        var h = Table(3);
        Assert.Equal("shake", Phase(h));
        var turn = Core(h).Turn;
        Refused(h, h.Act(turn, "bid", new { q = 1, f = 2 }), "Ще трусимо глеки");
        Refused(h, h.Act(turn, "liar"), "Ще трусимо глеки");
        Refused(h, h.Act((turn + 1) % 3, "exact", new { q = 1, f = 2 }), "Ще трусимо глеки");
        Refused(h, h.Act(turn, "ready"), "Ще трусимо глеки");
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("turn").ValueKind);
        h.Tick(5);
        Assert.Equal("shake", Phase(h));
        h.Tick(1);
        Assert.Equal("bid", Phase(h));
        Assert.Equal(turn, h.View(null).GetProperty("turn").GetInt32());
        Assert.Equal(30_000, h.View(null).GetProperty("phaseMs").GetInt32());
    }

    [Fact]
    public void Hands_are_sorted_and_come_from_the_room_rng()
    {
        var a = Table(4, seed: 7);
        var b = Table(4, seed: 7);
        var c = Table(4, seed: 8);
        var differs = false;
        for (var s = 0; s < 4; s++)
        {
            var ha = Ints(a.View(s).GetProperty("my"));
            Assert.Equal(ha, Ints(b.View(s).GetProperty("my")));
            Assert.Equal(ha.Order().ToArray(), ha);
            Assert.All(ha, v => Assert.InRange(v, 1, 6));
            if (!ha.SequenceEqual(Ints(c.View(s).GetProperty("my")))) differs = true;
        }
        Assert.True(differs, "інший сід — інші руки");
    }

    [Fact]
    public void The_same_seed_and_moves_give_byte_identical_views()
    {
        static string Play(int seed)
        {
            var h = Table(4, seed);
            var moves = 0;
            var log = new StringBuilder();
            for (var i = 0; i < 400 && moves < 20 && h.Room.Status == RoomStatus.Playing; i++)
            {
                BotStep(h, ref moves);
                h.Tick();
                log.Append(AllViews(h));
            }
            Assert.True(moves >= 20);
            return log.ToString();
        }
        Assert.Equal(Play(5), Play(5));
        Assert.NotEqual(Play(5), Play(6));
    }

    // =========================================================================================
    // Ставки
    // =========================================================================================

    [Fact]
    public void Only_the_player_on_turn_may_bid()
    {
        var h = Table(3);
        ToBid(h);
        var other = (Core(h).Turn + 1) % 3;
        var before = AllViews(h);
        Refused(h, h.Act(other, "bid", new { q = 2, f = 3 }), "Зараз не твій хід");
        Refused(h, h.Act(other, "liar"), "Зараз не твій хід");
        Assert.Equal(before, AllViews(h));
    }

    [Fact]
    public void A_bid_must_name_a_face_from_one_to_six()
    {
        var h = Table(2);
        ToBid(h);
        var t = Core(h).Turn;
        var before = AllViews(h);
        Refused(h, h.Act(t, "bid", new { q = 2, f = 0 }), "Грань — від 1 до 6");
        Refused(h, h.Act(t, "bid", new { q = 2, f = 7 }), "Грань — від 1 до 6");
        Assert.Equal(before, AllViews(h));
    }

    [Fact]
    public void A_bid_cannot_exceed_the_dice_on_the_table()
    {
        var h = Table(2);
        ToBid(h);
        var t = Core(h).Turn;
        Refused(h, h.Act(t, "bid", new { q = 11, f = 3 }), "Стільки кісточок на столі нема");
        Refused(h, h.Act(t, "bid", new { q = 0, f = 3 }), "Не зрозумів ставки");
        Ok(h.Act(t, "bid", new { q = 10, f = 3 }));
    }

    [Fact]
    public void A_round_cannot_open_on_jugs()
    {
        var h = Table(2);
        ToBid(h);
        var t = Core(h).Turn;
        Refused(h, h.Act(t, "bid", new { q = 1, f = 1 }), "Раунд не починають з глечиків");
        Core(h).SetPalifico(true);
        Ok(h.Act(t, "bid", new { q = 1, f = 1 }));
    }

    [Fact]
    public void Raising_quantity_or_face_is_higher_anything_else_is_refused()
    {
        var prev = new DiceBid(0, 4, 5);
        Assert.Null(DiceCore.Higher(prev, 4, 6, false, 5, 20));
        Assert.Null(DiceCore.Higher(prev, 5, 2, false, 5, 20));
        Assert.Null(DiceCore.Higher(prev, 2, 1, false, 5, 20));
        Assert.StartsWith("Треба вище", DiceCore.Higher(prev, 4, 5, false, 5, 20));
        Assert.StartsWith("Треба вище", DiceCore.Higher(prev, 4, 4, false, 5, 20));
        Assert.StartsWith("Треба вище", DiceCore.Higher(prev, 3, 6, false, 5, 20));
        Assert.StartsWith("Треба вище", DiceCore.Higher(prev, 1, 1, false, 5, 20));

        // те саме через кімнату: стан не міняється на відмові
        var h = Table(2);
        ToBid(h);
        var t = Core(h).Turn;
        Ok(h.Act(t, "bid", new { q = 4, f = 5 }));
        var n = Core(h).Turn;
        var before = AllViews(h);
        Refused(h, h.Act(n, "bid", new { q = 3, f = 6 }), "Треба вище за 4 × ⚄");
        Assert.Equal(before, AllViews(h));
        Ok(h.Act(n, "bid", new { q = 4, f = 6 }));
    }

    [Fact]
    public void The_refusal_names_exactly_what_would_be_accepted()
    {
        var h = Table(2);
        ToBid(h);
        Ok(h.Act(Core(h).Turn, "bid", new { q = 4, f = 5 }));
        var r = h.Act(Core(h).Turn, "bid", new { q = 4, f = 4 });
        Assert.False(r.Ok);
        Assert.Contains("5 ×", r.Message);
        Assert.Contains("4 × ⚅", r.Message);
        Assert.Contains("2 × глечики", r.Message);
        Assert.Equal("Треба вище за 3 × глечики: 4 × глечики або 7 × будь-що", DiceCore.Hint(new DiceBid(0, 3, 1), false, 5, 20));
        Assert.Equal("Паліфіко: щонайменше 5 × ⚄", DiceCore.Hint(new DiceBid(0, 4, 5), true, 3, 20));
        Assert.Contains("кажи «Брешеш!»", DiceCore.Hint(new DiceBid(0, 10, 1), false, 5, 10));
    }

    [Fact]
    public void Switching_to_jugs_needs_at_least_half_rounded_up()
    {
        Assert.Null(DiceCore.Higher(new DiceBid(0, 5, 3), 3, 1, false, 5, 20));
        Assert.NotNull(DiceCore.Higher(new DiceBid(0, 5, 3), 2, 1, false, 5, 20));
        Assert.Null(DiceCore.Higher(new DiceBid(0, 4, 3), 2, 1, false, 5, 20));
        Assert.NotNull(DiceCore.Higher(new DiceBid(0, 4, 3), 1, 1, false, 5, 20));
        Assert.Null(DiceCore.Higher(new DiceBid(0, 1, 2), 1, 1, false, 5, 20));
    }

    [Fact]
    public void Switching_from_jugs_needs_double_plus_one()
    {
        Assert.Null(DiceCore.Higher(new DiceBid(0, 2, 1), 5, 6, false, 5, 20));
        Assert.Null(DiceCore.Higher(new DiceBid(0, 2, 1), 5, 2, false, 5, 20));
        Assert.NotNull(DiceCore.Higher(new DiceBid(0, 2, 1), 4, 6, false, 5, 20));
        Assert.Null(DiceCore.Higher(new DiceBid(0, 3, 1), 7, 2, false, 5, 20));
        Assert.NotNull(DiceCore.Higher(new DiceBid(0, 3, 1), 6, 6, false, 5, 20));
    }

    [Fact]
    public void Jugs_over_jugs_need_a_bigger_count()
    {
        Assert.Null(DiceCore.Higher(new DiceBid(0, 2, 1), 3, 1, false, 5, 20));
        Assert.NotNull(DiceCore.Higher(new DiceBid(0, 2, 1), 2, 1, false, 5, 20));
        Assert.NotNull(DiceCore.Higher(new DiceBid(0, 2, 1), 1, 1, false, 5, 20));
    }

    [Fact]
    public void A_bid_passes_the_turn_clockwise_over_eliminated_and_empty_seats()
    {
        var h = new RoomHarness("dice", seed: 3);
        foreach (var n in Nicks) h.Join(n);
        h.Leave(Nicks[1]);
        h.Leave(Nicks[3]);
        h.Leave(Nicks[5]);
        h.Start();
        Assert.Equal(3, h.View(null).GetProperty("players").GetArrayLength());
        ToBid(h);
        var c = Core(h);
        c.Arrange([[2, 3, 4, 5, 6], null, [], null, [2, 3, 4, 5, 6]]);
        c.SetTurn(0);
        Ok(h.Act(0, "bid", new { q = 1, f = 2 }));
        Assert.Equal(4, h.View(null).GetProperty("turn").GetInt32());
        Ok(h.Act(4, "bid", new { q = 1, f = 3 }));
        Assert.Equal(0, h.View(null).GetProperty("turn").GetInt32());
        Refused(h, h.Act(2, "bid", new { q = 2, f = 3 }), "Ти без кісточок");
    }

    [Fact]
    public void A_bid_restarts_the_turn_timer()
    {
        var h = Table(2);
        ToBid(h);
        h.Tick(10);
        Ok(h.Act(Core(h).Turn, "bid", new { q = 2, f = 4 }));
        var endsAt = h.View(null).GetProperty("endsAt").GetDateTimeOffset();
        Assert.Equal(h.Clock.UtcNow.AddMilliseconds(30_000), endsAt);
    }

    [Fact]
    public void Malformed_bid_payload_is_refused_and_the_state_is_unchanged()
    {
        var h = Table(2);
        ToBid(h);
        var t = Core(h).Turn;
        var before = AllViews(h);
        Refused(h, h.Act(t, "bid", "4x5"), "Не зрозумів ставки");
        Refused(h, h.Act(t, "bid", new { q = "3", f = 5 }), "Не зрозумів ставки");
        Refused(h, h.Act(t, "bid", new { q = 2.5, f = 3 }), "Не зрозумів ставки");
        Refused(h, h.Act(t, "bid", new { q = 2 }), "Не зрозумів ставки");
        Refused(h, h.Act(t, "bid"), "Не зрозумів ставки");
        Refused(h, h.Act(t, "dance", new { q = 2, f = 3 }), "Тут так не ходять");
        Assert.Equal(before, AllViews(h));
    }

    [Fact]
    public void The_server_reads_exactly_the_payload_the_module_sends()
    {
        // Рівно те, що шле dice.js: act('bid', { q, f }), act('liar', { q, f }), act('exact', { q, f }), act('ready') → null.
        static JsonElement Raw(string json) => JsonDocument.Parse(json).RootElement.Clone();

        var h = Arranged([[2, 3, 4, 5, 6], [2, 3, 4, 5, 6], [2, 3, 4, 5]]);
        Ok(h.Act(0, "bid", Raw("{\"q\":2,\"f\":3}")));
        Ok(h.Act(1, "liar", Raw("{\"q\":2,\"f\":3}")));
        Assert.Equal("reveal", Phase(h));
        Ok(h.Act(0, "ready", null));
        Ok(h.Act(1, "ready", null));
        Ok(h.Act(2, "ready", null));
        h.Tick();
        ToBid(h);

        Core(h).Arrange([[2, 3, 4, 5, 6], [2, 3, 4, 5, 6], [2, 3, 4, 5]]);
        Core(h).SetTurn(0);
        Ok(h.Act(0, "bid", Raw("{\"q\":2,\"f\":3}")));
        Ok(h.Act(1, "liar", null));
        Assert.Equal("liar", Reveal(h).GetProperty("kind").GetString());
        Until(h, "bid");

        Core(h).Arrange([[2, 3, 4, 5, 6], [2, 3, 4, 5, 6], [2, 3, 4, 5]]);
        Core(h).SetTurn(0);
        Ok(h.Act(0, "bid", Raw("{\"q\":2,\"f\":3}")));
        Ok(h.Act(2, "exact", Raw("{\"q\":2,\"f\":3}")));
        Assert.Equal("exact", Reveal(h).GetProperty("kind").GetString());
        Ok(h.Act(1, "react", Raw("{\"e\":1}")));   // act('react', { e })
    }

    // =========================================================================================
    // «Брешеш!»
    // =========================================================================================

    [Fact]
    public void Liar_with_no_bid_on_the_table_is_refused()
    {
        var h = Table(2);
        ToBid(h);
        var before = AllViews(h);
        Refused(h, h.Act(Core(h).Turn, "liar"), "Нема ставки — нема кому не вірити");
        Assert.Equal(before, AllViews(h));
    }

    [Fact]
    public void Liar_on_a_true_bid_costs_the_caller_a_die()
    {
        var h = Arranged([[1, 1, 5], [5, 2, 2]]);
        Ok(h.Act(0, "bid", new { q = 3, f = 5 }));
        Ok(h.Act(1, "liar", new { q = 3, f = 5 }));
        var r = Reveal(h);
        Assert.Equal("liar", r.GetProperty("kind").GetString());
        Assert.Equal(1, r.GetProperty("caller").GetInt32());
        Assert.Equal(1, r.GetProperty("loser").GetInt32());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("gainer").ValueKind);
        Assert.Equal(1, r.GetProperty("next").GetInt32());
        Assert.False(r.GetProperty("out").GetBoolean());
        Assert.Equal(2, DiceOf(h, 1));
        Assert.Equal(3, DiceOf(h, 0));
    }

    [Fact]
    public void Liar_on_a_false_bid_costs_the_bidder_a_die()
    {
        var h = Arranged([[1, 1, 5], [5, 2, 2]]);
        Ok(h.Act(0, "bid", new { q = 5, f = 5 }));
        Ok(h.Act(1, "liar"));
        var r = Reveal(h);
        Assert.Equal(0, r.GetProperty("loser").GetInt32());
        Assert.Equal(0, r.GetProperty("next").GetInt32());
        Assert.Equal(2, DiceOf(h, 0));
    }

    [Fact]
    public void Jugs_count_as_wild_when_counting_a_normal_face()
    {
        var h = Arranged([[1, 1, 5], [5, 2, 2]]);
        Ok(h.Act(0, "bid", new { q = 4, f = 5 }));
        Ok(h.Act(1, "liar"));
        var r = Reveal(h);
        Assert.Equal(4, r.GetProperty("count").GetInt32());
        Assert.Equal(2, r.GetProperty("jokers").GetInt32());
        Assert.Equal(1, r.GetProperty("loser").GetInt32());
    }

    [Fact]
    public void Jugs_are_counted_alone_when_the_bid_is_on_jugs()
    {
        var h = Arranged([[1, 1, 5], [5, 2, 2]]);
        Ok(h.Act(0, "bid", new { q = 2, f = 5 }));
        Ok(h.Act(1, "bid", new { q = 3, f = 1 }));
        Ok(h.Act(0, "liar"));
        var r = Reveal(h);
        Assert.Equal(2, r.GetProperty("count").GetInt32());
        Assert.Equal(0, r.GetProperty("jokers").GetInt32());
        Assert.Equal(1, r.GetProperty("loser").GetInt32());
    }

    [Fact]
    public void Liar_with_a_stale_bid_in_the_payload_is_refused()
    {
        var h = Arranged([[1, 1, 5], [5, 2, 2]]);
        Ok(h.Act(0, "bid", new { q = 4, f = 5 }));
        Ok(h.Act(1, "bid", new { q = 5, f = 5 }));
        var before = AllViews(h);
        Refused(h, h.Act(0, "liar", new { q = 4, f = 5 }), "Ставка вже змінилась");
        Refused(h, h.Act(0, "liar", new { x = 1 }), "Ставка вже змінилась");
        Assert.Equal(before, AllViews(h));
        Ok(h.Act(0, "liar", new { q = 5, f = 5 }));
    }

    // =========================================================================================
    // «Точно!»
    // =========================================================================================

    [Fact]
    public void Exact_may_be_called_out_of_turn_by_anyone_but_the_bidder()
    {
        var h = Arranged([[1, 5, 5, 6], [5, 2, 2], [3, 3, 4, 4]]);
        Ok(h.Act(0, "bid", new { q = 4, f = 5 }));
        Assert.Equal(1, Core(h).Turn);
        Assert.False(h.View(0).GetProperty("canExact").GetBoolean());
        Assert.True(h.View(2).GetProperty("canExact").GetBoolean());
        Refused(h, h.Act(0, "exact", new { q = 4, f = 5 }), "На свою ж ставку");
        Ok(h.Act(2, "exact", new { q = 4, f = 5 }));
        var r = Reveal(h);
        Assert.Equal("exact", r.GetProperty("kind").GetString());
        Assert.Equal(2, r.GetProperty("caller").GetInt32());
    }

    [Fact]
    public void Exact_on_the_spot_returns_a_die_and_asks_for_the_achievement()
    {
        var h = Arranged([[1, 5, 5], [5, 2, 2], [3, 3, 4, 4]]);
        Ok(h.Act(0, "bid", new { q = 4, f = 5 }));
        Ok(h.Act(2, "exact", new { q = 4, f = 5 }));
        var r = Reveal(h);
        Assert.Equal(4, r.GetProperty("count").GetInt32());
        Assert.Equal(2, r.GetProperty("gainer").GetInt32());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("loser").ValueKind);
        Assert.Equal(5, DiceOf(h, 2));
        Assert.Contains(h.Awards, a => a.Nick == "Ганна" && a.Reason == "ach:dice-exact" && a.Shards == 0);
        Assert.Contains("Ганна", r.GetProperty("say").GetString());
    }

    [Fact]
    public void Exact_that_misses_costs_the_caller_a_die()
    {
        var h = Arranged([[1, 5, 5], [5, 2, 2], [3, 3, 4, 4]]);
        Ok(h.Act(0, "bid", new { q = 3, f = 5 }));
        Ok(h.Act(2, "exact", new { q = 3, f = 5 }));
        var r = Reveal(h);
        Assert.Equal(2, r.GetProperty("loser").GetInt32());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("gainer").ValueKind);
        Assert.Equal(3, DiceOf(h, 2));
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:dice-exact");
    }

    [Fact]
    public void A_player_with_a_full_cup_cannot_call_exact()
    {
        var h = Arranged([[1, 5, 5], [5, 2, 2], [2, 3, 3, 4, 4]]);
        Ok(h.Act(0, "bid", new { q = 3, f = 5 }));
        Assert.False(h.View(2).GetProperty("canExact").GetBoolean());
        var before = AllViews(h);
        Refused(h, h.Act(2, "exact", new { q = 3, f = 5 }), "У тебе повний глек — «Точно!» нічого не дасть");
        Assert.Equal(before, AllViews(h));
    }

    [Fact]
    public void Exact_is_refused_when_the_table_plays_without_it()
    {
        var h = Arranged([[1, 5, 5], [5, 2, 2], [3, 3, 4, 4]], options: new { exact = "off" });
        Ok(h.Act(0, "bid", new { q = 4, f = 5 }));
        Assert.False(h.View(2).GetProperty("canExact").GetBoolean());
        Refused(h, h.Act(2, "exact", new { q = 4, f = 5 }), "За цим столом «Точно!» не грають");
        Assert.False(h.View(2).GetProperty("rules").GetProperty("exact").GetBoolean());
    }

    [Fact]
    public void Exact_with_a_stale_or_missing_bid_is_refused()
    {
        var h = Arranged([[1, 5, 5], [5, 2, 2], [3, 3, 4, 4]]);
        Refused(h, h.Act(2, "exact", new { q = 4, f = 5 }), "Ще нема ставки");
        Ok(h.Act(0, "bid", new { q = 4, f = 5 }));
        Ok(h.Act(1, "bid", new { q = 5, f = 5 }));
        var before = AllViews(h);
        Refused(h, h.Act(2, "exact", new { q = 4, f = 5 }), "Ставка вже змінилась");
        Refused(h, h.Act(2, "exact"), "Ставка вже змінилась");
        Refused(h, h.Act(2, "exact", "5x5"), "Ставка вже змінилась");
        Assert.Equal(before, AllViews(h));
        // другий «Точно!» за мить після першого: не встиг — глеки вже підняли, стан не міняється
        Ok(h.Act(0, "exact", new { q = 5, f = 5 }));
        var revealed = AllViews(h);
        Refused(h, h.Act(2, "exact", new { q = 5, f = 5 }), "Не встиг — глеки вже підняли");
        Assert.Equal(revealed, AllViews(h));
    }

    [Fact]
    public void The_exact_caller_starts_the_next_round_win_or_lose()
    {
        foreach (var (q, gains) in new[] { (4, true), (3, false) })
        {
            var h = Arranged([[1, 5, 5], [5, 2, 2], [3, 3, 4, 4]]);
            Ok(h.Act(0, "bid", new { q, f = 5 }));
            Ok(h.Act(2, "exact", new { q, f = 5 }));
            Assert.Equal(gains, Reveal(h).GetProperty("gainer").ValueKind == JsonValueKind.Number);
            Assert.Equal(2, Reveal(h).GetProperty("next").GetInt32());
            Until(h, "shake");
            Assert.Equal(2, h.View(null).GetProperty("starter").GetInt32());
            Until(h, "bid");
            Assert.Equal(2, h.View(null).GetProperty("turn").GetInt32());
        }
    }

    // =========================================================================================
    // Таймер
    // =========================================================================================

    [Fact]
    public void Turn_timeout_without_a_bid_places_the_lowest_bid_and_marks_it_automatic()
    {
        var h = Table(3);
        ToBid(h);
        var t = Core(h).Turn;
        h.Tick(119);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("bid").ValueKind);
        h.Tick(1);
        var bid = h.View(null).GetProperty("bid");
        Assert.Equal(1, bid.GetProperty("q").GetInt32());
        Assert.Equal(2, bid.GetProperty("f").GetInt32());
        Assert.Equal(t, bid.GetProperty("seat").GetInt32());
        Assert.True(bid.GetProperty("auto").GetBoolean());
        Assert.True(h.View(null).GetProperty("history")[0].GetProperty("auto").GetBoolean());
        Assert.Equal(Core(h).NextAlive(t), h.View(null).GetProperty("turn").GetInt32());

        var p = Table(2);
        ToBid(p);
        Core(p).SetPalifico(true);
        p.Tick(120);
        Assert.Equal(1, p.View(null).GetProperty("bid").GetProperty("f").GetInt32());
    }

    [Fact]
    public void Turn_timeout_with_a_bid_on_the_table_counts_as_liar()
    {
        var h = Arranged([[1, 1, 5], [5, 2, 2], [3, 4, 6]]);
        Ok(h.Act(0, "bid", new { q = 3, f = 5 }));
        h.Tick(120);
        var r = Reveal(h);
        Assert.Equal("timeout", r.GetProperty("kind").GetString());
        Assert.Equal(1, r.GetProperty("caller").GetInt32());
        Assert.Equal(1, r.GetProperty("loser").GetInt32());   // ставка чесна — губить той, хто проспав
        Assert.StartsWith("Час вийшов", r.GetProperty("say").GetString());
    }

    [Fact]
    public void Reveal_ends_after_six_seconds_or_when_every_alive_player_is_ready()
    {
        var h = Arranged([[1, 1, 5], [5, 2, 2]]);
        Ok(h.Act(0, "bid", new { q = 3, f = 5 }));
        Ok(h.Act(1, "liar"));
        h.Tick(23);
        Assert.Equal("reveal", Phase(h));
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("turn").ValueKind);
        h.Tick(1);
        Assert.Equal("shake", Phase(h));
        Assert.Equal(2, h.View(null).GetProperty("round").GetInt32());

        var g = Arranged([[1, 1, 5], [5, 2, 2]]);
        Ok(g.Act(0, "bid", new { q = 3, f = 5 }));
        Ok(g.Act(1, "liar"));
        g.Tick();
        Ok(g.Act(0, "ready"));
        g.Tick();
        Assert.Equal("reveal", Phase(g));
        Assert.Equal(new[] { 0 }, Ints(g.View(null).GetProperty("ready")));
        Ok(g.Act(1, "ready"));
        g.Tick();
        Assert.Equal("shake", Phase(g));
    }

    [Fact]
    public void Ready_outside_the_reveal_is_refused_and_twice_is_harmless()
    {
        var h = Arranged([[1, 1, 5], [5, 2, 2], [3, 3]]);
        Refused(h, h.Act(0, "ready"), "Зараз нема чого пропускати");
        Ok(h.Act(0, "bid", new { q = 3, f = 5 }));
        Ok(h.Act(1, "liar"));
        Ok(h.Act(2, "ready"));
        var before = AllViews(h);
        Ok(h.Act(2, "ready"));
        Assert.Equal(before, AllViews(h));
        Refused(h, h.Act(0, "bid", new { q = 3, f = 6 }), "Глеки вже підняли");
    }

    [Fact]
    public void Eliminated_players_are_not_waited_for_in_the_reveal()
    {
        var h = Arranged([[1, 1, 5], [5, 2, 2], []]);
        Ok(h.Act(0, "bid", new { q = 3, f = 5 }));
        Ok(h.Act(1, "liar"));
        Refused(h, h.Act(2, "ready"), "Ти без кісточок");
        Ok(h.Act(0, "ready"));
        Ok(h.Act(1, "ready"));
        h.Tick();
        Assert.Equal("shake", Phase(h));
    }

    // =========================================================================================
    // Паліфіко
    // =========================================================================================

    [Fact]
    public void Palifico_starts_when_the_starter_is_down_to_one_die_and_only_once_per_player()
    {
        var h = Arranged([[2, 3], [4, 4, 4, 6, 6]]);
        Ok(h.Act(0, "bid", new { q = 3, f = 5 }));     // брехня: п'ятірок нема
        Ok(h.Act(1, "liar"));
        Assert.Equal(1, DiceOf(h, 0));
        Until(h, "shake");
        var v = h.View(null);
        Assert.True(v.GetProperty("palifico").GetBoolean());
        Assert.False(v.GetProperty("wild").GetBoolean());
        Assert.Equal(0, v.GetProperty("starter").GetInt32());
        Assert.Contains("Паліфіко!", v.GetProperty("note").GetString());
        Assert.Contains("Оля", v.GetProperty("note").GetString());
        Assert.True(Player(h, 0).GetProperty("palificoUsed").GetBoolean());
        Assert.True(Player(h, 0).GetProperty("wasAtOne").GetBoolean());

        // паліфіко: Оля відкривається, Петро (4 кісточки) лише піднімає ту саму грань, Оля влучає «Точно!» і підростає до двох
        Until(h, "bid");
        Core(h).Arrange([[3], [3, 3, 5, 5]]);
        Ok(h.Act(0, "bid", new { q = 2, f = 3 }));
        Ok(h.Act(1, "bid", new { q = 3, f = 3 }));
        Ok(h.Act(0, "exact", new { q = 3, f = 3 }));
        Assert.Equal(2, DiceOf(h, 0));

        // знов падає до однієї — другого паліфіко нема
        Until(h, "bid");
        Assert.False(h.View(null).GetProperty("palifico").GetBoolean());
        Core(h).Arrange([[2, 2], [4, 4, 4, 4]]);
        Core(h).SetTurn(0);
        Ok(h.Act(0, "bid", new { q = 3, f = 6 }));
        Ok(h.Act(1, "liar"));
        Assert.Equal(1, DiceOf(h, 0));
        Until(h, "shake");
        Assert.Equal(0, h.View(null).GetProperty("starter").GetInt32());
        Assert.False(h.View(null).GetProperty("palifico").GetBoolean());
    }

    [Fact]
    public void In_palifico_jugs_are_plain_and_the_round_may_open_on_them()
    {
        var h = Arranged([[1], [1, 1, 5, 2, 2]]);
        Core(h).SetPalifico(true);
        Assert.False(h.View(null).GetProperty("wild").GetBoolean());
        Ok(h.Act(0, "bid", new { q = 1, f = 1 }));
        Ok(h.Act(1, "bid", new { q = 3, f = 1 }));
        Ok(h.Act(0, "liar"));
        var r = Reveal(h);
        Assert.Equal(3, r.GetProperty("count").GetInt32());
        Assert.Equal(0, r.GetProperty("jokers").GetInt32());
        Assert.Equal(0, r.GetProperty("loser").GetInt32());

        var c = new DiceCore();
        c.Deal([0, 1], 5);
        c.Arrange([[1, 1, 5], [5, 2, 2]]);
        c.SetPalifico(true);
        Assert.Equal(2, c.CountFace(5, out var jokers));
        Assert.Equal(0, jokers);
        c.SetPalifico(false);
        Assert.Equal(4, c.CountFace(5, out jokers));
        Assert.Equal(2, jokers);
    }

    [Fact]
    public void In_palifico_players_with_more_than_one_die_cannot_change_the_face()
    {
        var h = Arranged([[4], [3, 3, 5, 5]]);
        Core(h).SetPalifico(true);
        Ok(h.Act(0, "bid", new { q = 3, f = 3 }));
        var before = AllViews(h);
        Refused(h, h.Act(1, "bid", new { q = 3, f = 4 }), "Паліфіко: грань не міняють");
        Refused(h, h.Act(1, "bid", new { q = 4, f = 4 }), "Паліфіко: грань не міняють");
        Refused(h, h.Act(1, "bid", new { q = 3, f = 3 }), "Паліфіко: щонайменше 4 × ⚂");
        Assert.Equal(before, AllViews(h));
        Ok(h.Act(1, "bid", new { q = 4, f = 3 }));
    }

    [Fact]
    public void In_palifico_a_one_die_player_may_change_the_face()
    {
        var h = Arranged([[4], [3], [2, 2, 6]]);
        Core(h).SetPalifico(true);
        Ok(h.Act(0, "bid", new { q = 2, f = 3 }));
        Ok(h.Act(1, "bid", new { q = 2, f = 4 }));   // одна кісточка — грань міняти можна
        Refused(h, h.Act(2, "bid", new { q = 3, f = 5 }), "Паліфіко: грань не міняють");
        Ok(h.Act(2, "bid", new { q = 3, f = 4 }));
        Assert.Null(DiceCore.Higher(new DiceBid(0, 2, 4), 2, 5, true, 1, 10));
        Assert.NotNull(DiceCore.Higher(new DiceBid(0, 2, 4), 2, 1, true, 1, 10));  // глечик — найнижча грань
        Assert.Null(DiceCore.Higher(new DiceBid(0, 2, 4), 3, 1, true, 1, 10));
    }

    [Fact]
    public void Palifico_is_off_when_the_table_says_so()
    {
        var h = Arranged([[2, 3], [4, 4, 4, 6, 6]], options: new { palifico = "off" });
        Ok(h.Act(0, "bid", new { q = 3, f = 5 }));
        Ok(h.Act(1, "liar"));
        Until(h, "shake");
        Assert.Equal(0, h.View(null).GetProperty("starter").GetInt32());
        Assert.False(h.View(null).GetProperty("palifico").GetBoolean());
        Assert.True(h.View(null).GetProperty("wild").GetBoolean());
        Assert.False(Player(h, 0).GetProperty("palificoUsed").GetBoolean());
    }

    [Fact]
    public void Palifico_applies_to_a_duel_as_well()
    {
        var h = Arranged([[5, 6, 6, 6, 6], [2, 3]], turn: 1);
        Ok(h.Act(1, "bid", new { q = 6, f = 2 }));
        Ok(h.Act(0, "liar"));
        Assert.Equal(1, DiceOf(h, 1));
        Until(h, "bid");
        Assert.True(h.View(null).GetProperty("palifico").GetBoolean());
        Assert.Equal(1, h.View(null).GetProperty("turn").GetInt32());
        Ok(h.Act(1, "bid", new { q = 1, f = 1 }));   // паліфіко: можна відкритись глечиками
    }

    [Fact]
    public void Leaving_mid_round_keeps_the_palifico_of_a_starter_who_stays()
    {
        // Оля падає до однієї кісточки й починає свій раунд паліфіко; посеред нього встає Ганна (перед Олею за колом)
        var h = Arranged([[2, 3], [4, 4, 4, 6, 6], [5, 5]]);
        Ok(h.Act(0, "bid", new { q = 3, f = 6 }));   // брехня: шісток лише дві
        Ok(h.Act(1, "liar"));
        Until(h, "bid");
        Assert.True(h.View(null).GetProperty("palifico").GetBoolean());
        Assert.Equal(0, h.View(null).GetProperty("starter").GetInt32());
        h.Leave("Ганна");
        Assert.Equal("shake", Phase(h));
        Assert.Equal(0, h.View(null).GetProperty("starter").GetInt32());
        Assert.True(h.View(null).GetProperty("palifico").GetBoolean());
        Assert.Contains("Ганна встає з-за столу — перетрушуємо", h.View(null).GetProperty("note").GetString());
    }

    // =========================================================================================
    // Кінець, вихід, «Ще раз»
    // =========================================================================================

    [Fact]
    public void The_last_player_with_dice_wins_after_the_final_reveal()
    {
        var h = Arranged([[6, 6, 6], [2], [3]], turn: 1);
        Ok(h.Act(1, "bid", new { q = 5, f = 4 }));
        Ok(h.Act(2, "liar"));
        var r = Reveal(h);
        Assert.True(r.GetProperty("out").GetBoolean());
        Assert.Equal(2, r.GetProperty("next").GetInt32());
        Assert.Contains("Петро лишається без кісточок", r.GetProperty("say").GetString());
        Until(h, "bid");
        Assert.Equal(2, h.View(null).GetProperty("turn").GetInt32());
        Core(h).Arrange([[6, 6, 6], null, [3]]);
        Ok(h.Act(2, "bid", new { q = 2, f = 5 }));
        Ok(h.Act(0, "liar"));
        Assert.Equal(RoomStatus.Playing, h.Room.Status);   // розкриття ще дограють
        Assert.Equal("reveal", Phase(h));
        h.Tick(24);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal("done", Phase(h));
        var fin = Assert.Single(h.Finished);
        Assert.Equal(new[] { 0 }, fin.Result.Winners);
        Assert.Equal(3, fin.Result.Scores![0]);
        Assert.Equal(0, fin.Result.Scores[1]);
        var log = h.Outbox.OfType<Journal>().Last().Text;
        Assert.Equal("Під глеком: перемога — Оля; вибули по черзі: Петро, Ганна (2 раунди)", log);
        var res = h.View(null).GetProperty("result");
        Assert.Equal(0, res.GetProperty("winner").GetInt32());
        Assert.Equal(new[] { 1, 2 }, Ints(res.GetProperty("places")));
        Assert.Equal(2, res.GetProperty("rounds").GetInt32());
        Assert.False(string.IsNullOrEmpty(res.GetProperty("say").GetString()));
        // Петро загнув 5 четвірок при нулі — найнахабніший блеф партії (Ганнині 2 × ⚄ при нулі скромніші)
        Assert.Equal(new[] { "🤥 Найнахабніший блеф — Петро: 5 × ⚃, а було 0" },
            res.GetProperty("fun").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(JsonValueKind.Object, h.View(null).GetProperty("reveal").ValueKind);   // останнє розкриття лишається на столі
    }

    [Fact]
    public void A_duel_ends_with_a_score_line()
    {
        var h = Arranged([[6, 6], [3]], turn: 1);
        Ok(h.Act(1, "bid", new { q = 3, f = 5 }));
        Ok(h.Act(0, "liar"));
        Ok(h.Act(0, "ready"));
        h.Tick();
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal("Під глеком: Оля 2:0 Петро за 1 раунд", h.Outbox.OfType<Journal>().Last().Text);
        Assert.Equal("Дуель скінчилась: Оля лишає 2 кісточки.", h.View(null).GetProperty("result").GetProperty("say").GetString());
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:dice-comeback");
    }

    [Fact]
    public void The_loser_starts_the_next_round_and_an_eliminated_loser_hands_it_on()
    {
        var h = Arranged([[1, 1, 5], [5, 2, 2], [4, 4, 4]]);
        Ok(h.Act(0, "bid", new { q = 3, f = 5 }));
        Ok(h.Act(1, "liar"));
        Assert.Equal(1, Reveal(h).GetProperty("next").GetInt32());
        Until(h, "shake");
        Assert.Equal(1, h.View(null).GetProperty("starter").GetInt32());

        var g = Arranged([[1, 1, 5], [2], [4, 4, 4]]);
        Core(g).SetTurn(1);
        Ok(g.Act(1, "bid", new { q = 6, f = 6 }));
        Ok(g.Act(2, "liar"));
        Assert.Equal(1, Reveal(g).GetProperty("loser").GetInt32());
        Assert.Equal(2, Reveal(g).GetProperty("next").GetInt32());
    }

    [Fact]
    public void Leaving_mid_round_drops_the_leaver_and_reshakes_the_round_for_the_rest()
    {
        var h = Table(3);
        ToBid(h);
        Core(h).SetTurn(0);
        Ok(h.Act(0, "bid", new { q = 2, f = 3 }));
        var round = h.View(null).GetProperty("round").GetInt32();
        var starter = Core(h).Starter;
        h.Leave("Петро");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        var v = h.View(null);
        Assert.Equal("shake", v.GetProperty("phase").GetString());
        Assert.Equal(0, v.GetProperty("history").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("bid").ValueKind);
        Assert.Contains("Петро встає з-за столу — перетрушуємо", v.GetProperty("note").GetString());
        // починає той самий стартер; якщо пішов саме він — наступний за ним
        Assert.Equal(starter == 1 ? 2 : starter, v.GetProperty("starter").GetInt32());
        Assert.Equal(round, v.GetProperty("round").GetInt32());
        Assert.Equal(10, v.GetProperty("total").GetInt32());
        var p = Player(h, 1);
        Assert.False(p.GetProperty("alive").GetBoolean());
        Assert.True(p.GetProperty("left").GetBoolean());
        Assert.Equal(0, p.GetProperty("dice").GetInt32());
        Assert.Equal("Петро", p.GetProperty("nick").GetString());
    }

    [Fact]
    public void Leaving_during_the_reveal_lets_the_reveal_finish()
    {
        var h = Arranged([[1, 1, 5], [5, 2, 2], [4, 4, 4]]);
        Ok(h.Act(0, "bid", new { q = 3, f = 5 }));
        Ok(h.Act(1, "liar"));
        var dice = Reveal(h).GetProperty("dice").GetRawText();
        h.Leave("Петро");   // він мав починати наступний раунд
        Assert.Equal("reveal", Phase(h));
        Assert.Equal(dice, Reveal(h).GetProperty("dice").GetRawText());
        Assert.Equal(2, Reveal(h).GetProperty("next").GetInt32());
        Until(h, "shake");
        Assert.Equal(2, h.View(null).GetProperty("starter").GetInt32());
        Assert.False(Player(h, 1).GetProperty("alive").GetBoolean());
    }

    [Fact]
    public void When_only_one_player_remains_after_a_leave_the_table_is_won()
    {
        var h = Table(2);
        ToBid(h);
        h.Leave("Петро");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var fin = Assert.Single(h.Finished);
        Assert.Equal(new[] { 0 }, fin.Result.Winners);
        Assert.Equal("Під глеком: Петро встає з-за столу, перемога — Оля", h.Outbox.OfType<Journal>().Last().Text);
        Assert.Equal("done", Phase(h));
        Assert.Equal("За столом лишається тільки Оля — перемога.", h.View(null).GetProperty("result").GetProperty("say").GetString());
        Assert.Equal(0, h.View(null).GetProperty("result").GetProperty("winner").GetInt32());
    }

    [Fact]
    public void Leaving_mid_round_keeps_the_starter_and_the_palifico_wherever_the_leaver_sits()
    {
        // Рецензія: на чотирьох Оля (0) на одній кісточці відкриває свій раунд паліфіко, а встає Петро чи Ганна —
        // не той, хто сидить просто перед нею. Раніше старт діставався сусідові того, хто встав, і Олине
        // паліфіко згорало назавжди (palificoUsed уже true), а «хто програв — той починає» ламалось.
        foreach (var leaver in new[] { "Петро", "Ганна" })
        {
            var h = Arranged([[2, 3], [4, 4, 4, 6, 6], [5, 5, 5], [2, 2, 2]]);
            Ok(h.Act(0, "bid", new { q = 5, f = 6 }));   // брехня: шісток лише дві
            Ok(h.Act(1, "liar"));
            Assert.Equal(1, DiceOf(h, 0));
            Until(h, "bid");
            Assert.True(h.View(null).GetProperty("palifico").GetBoolean());
            Assert.Equal(0, h.View(null).GetProperty("starter").GetInt32());

            h.Leave(leaver);
            var v = h.View(null);
            Assert.Equal("shake", v.GetProperty("phase").GetString());
            Assert.Equal(0, v.GetProperty("starter").GetInt32());
            Assert.True(v.GetProperty("palifico").GetBoolean());
            Assert.StartsWith($"{leaver} встає з-за столу — перетрушуємо. Паліфіко! Оля", v.GetProperty("note").GetString());
            Until(h, "bid");
            Assert.Equal(0, h.View(null).GetProperty("turn").GetInt32());
            Ok(h.Act(0, "bid", new { q = 1, f = 1 }));   // паліфіко живе: глечиками відкриватись можна
        }
    }

    [Fact]
    public void When_the_starter_leaves_mid_round_the_next_seat_opens_the_reshake()
    {
        var h = Arranged([[2, 3, 4], [4, 4, 4], [5, 5, 5], [2, 2, 2]]);
        Ok(h.Act(0, "bid", new { q = 3, f = 6 }));   // шісток нема — Оля губить і починає наступний
        Ok(h.Act(1, "liar"));
        Until(h, "bid");
        Assert.Equal(0, h.View(null).GetProperty("starter").GetInt32());
        h.Leave("Оля");
        Assert.Equal(1, h.View(null).GetProperty("starter").GetInt32());
        Assert.False(h.View(null).GetProperty("palifico").GetBoolean());
    }

    [Fact]
    public void A_win_because_the_rival_left_gives_no_comeback_achievement()
    {
        // «Я спускаюсь до однієї, ти встаєш» — і ачівка за 25? Ні: лише за партію, дограну до розкриття.
        var h = Arranged([[4], [2, 3, 5]]);
        Assert.True(Player(h, 0).GetProperty("wasAtOne").GetBoolean());
        h.Leave("Петро");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal(new[] { 0 }, Assert.Single(h.Finished).Result.Winners);
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:dice-comeback");
    }

    [Fact]
    public void A_table_won_by_a_leave_shows_no_unrevealed_hand_even_after_it_reopens()
    {
        // Партію дограно виходом посеред ставок — руки ніхто не піднімав. Не світимо їх ні глядачеві, ні в
        // перевідкритому лобі тому, хто сів на місце переможця.
        var h = Arranged([[2, 3, 3, 5, 6], [1, 4, 4, 4, 6], []]);
        static void NoHands(RoomHarness h)
        {
            for (var s = 0; s <= DiceCore.MaxSeats; s++)
            {
                int? seat = s == DiceCore.MaxSeats ? null : s;
                var text = Views.Text(h.Room.Game.View(seat));
                Assert.DoesNotContain("[2,3,3,5,6]", text);
                Assert.DoesNotContain("[1,4,4,4,6]", text);
                Assert.Contains("\"reveal\":null", text);
            }
        }
        h.Leave("Петро");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal("done", Phase(h));
        NoHands(h);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("my").ValueKind);

        h.Leave("Оля");                        // переможець теж пішов, Ганна (вибула раніше) лишилась
        Assert.True(h.Join("Марта").Ok);       // стіл перевідкрився, Марта сіла на Олине місце
        Assert.Equal(RoomStatus.Lobby, h.Room.Status);
        Assert.Equal("Марта", h.NickOf(0));
        NoHands(h);
    }

    [Fact]
    public void A_bid_after_the_turn_ran_out_is_refused_and_counts_as_the_timeout()
    {
        // Годинник уже за межею ходу, а тик ще не прийшов (до 250 мс): дуга в людини на нулі — ставку не беремо.
        var h = Table(2);
        ToBid(h);
        var t = Core(h).Turn;
        h.Clock.AdvanceMs(30_000);
        Refused(h, h.Act(t, "bid", new { q = 1, f = 3 }), "Час вийшов — хід пішов далі");
        var bid = h.View(null).GetProperty("bid");
        Assert.True(bid.GetProperty("auto").GetBoolean());   // ⏰ 1 × ⚁ за того, хто не встиг
        Assert.Equal(t, bid.GetProperty("seat").GetInt32());
        Assert.Equal(Core(h).NextAlive(t), h.View(null).GetProperty("turn").GetInt32());

        var u = Core(h).Turn;
        h.Clock.AdvanceMs(30_000);
        Refused(h, h.Act(u, "bid", new { q = 2, f = 3 }), "Час вийшов — глеки вже піднімають");
        Assert.Equal("reveal", Phase(h));
        Assert.Equal("timeout", Reveal(h).GetProperty("kind").GetString());
        Assert.Equal(u, Reveal(h).GetProperty("caller").GetInt32());
    }

    [Fact]
    public void A_player_who_sleeps_through_two_turns_gets_ten_seconds_until_waking_up()
    {
        var h = Arranged([[2, 3, 4], [2, 3, 4]]);
        h.Tick(120);                                  // Оля проспала відкриття — ⏰ 1 × ⚁ за неї
        Assert.Equal(1, h.View(null).GetProperty("turn").GetInt32());
        Ok(h.Act(1, "bid", new { q = 2, f = 3 }));
        Assert.Equal(30_000, h.View(null).GetProperty("phaseMs").GetInt32());   // проспала лише раз — повний час
        Assert.False(Player(h, 0).GetProperty("sleepy").GetBoolean());
        h.Tick(120);                                  // знов мовчить — «Брешеш!» за неї, трійок таки дві
        Assert.Equal("timeout", Reveal(h).GetProperty("kind").GetString());
        Assert.Equal(0, Reveal(h).GetProperty("loser").GetInt32());

        Until(h, "bid");                              // починає вона ж (програла) — і вже сонна
        Assert.Equal(0, h.View(null).GetProperty("turn").GetInt32());
        Assert.Equal(10_000, h.View(null).GetProperty("phaseMs").GetInt32());
        Assert.True(Player(h, 0).GetProperty("sleepy").GetBoolean());
        Assert.False(Player(h, 1).GetProperty("sleepy").GetBoolean());

        Ok(h.Act(0, "bid", new { q = 1, f = 4 }));    // прокинулась
        Assert.False(Player(h, 0).GetProperty("sleepy").GetBoolean());
        Ok(h.Act(1, "bid", new { q = 2, f = 4 }));
        Assert.Equal(0, h.View(null).GetProperty("turn").GetInt32());
        Assert.Equal(30_000, h.View(null).GetProperty("phaseMs").GetInt32());
    }

    [Fact]
    public void The_summary_names_the_boldest_bluff_and_the_sniper()
    {
        var h = Arranged([[6, 6, 6], [2, 2], [3, 3]], turn: 1, options: new { palifico = "off" });
        Ok(h.Act(1, "bid", new { q = 5, f = 4 }));   // четвірок нема зовсім — загин на п'ять
        Ok(h.Act(2, "liar"));
        Until(h, "bid");
        Core(h).Arrange([[6, 6, 6], [2], [3, 3]]);
        Core(h).SetTurn(1);
        Ok(h.Act(1, "bid", new { q = 3, f = 6 }));
        Ok(h.Act(2, "exact", new { q = 3, f = 6 }));  // рівно три шістки — Ганна влучила
        h.Leave("Петро");
        h.Leave("Ганна");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var fun = h.View(null).GetProperty("result").GetProperty("fun").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal(new[]
        {
            "🤥 Найнахабніший блеф — Петро: 5 × ⚃, а було 0",
            "🎯 Снайпер — Ганна: 1 влучне «Точно!»",
        }, fun);
    }

    [Fact]
    public void Reactions_reach_everyone_but_change_nothing_in_the_game()
    {
        var h = Arranged([[1, 5, 5], [5, 2, 2], []]);   // Ганна вже без кісточок — але реагувати може
        Ok(h.Act(0, "bid", new { q = 2, f = 5 }));
        h.Tick();
        var before = StripReacts(AllViews(h));
        var sent = h.Outbox.Count(o => o is RoomViews);
        Ok(h.Act(2, "react", new { e = 1 }));
        Ok(h.Act(1, "react", new { e = 0 }));
        Assert.Equal(before, StripReacts(AllViews(h)));   // ставка, хід, таймер — усе як було
        Assert.Equal(new[] { 0, 0, 1, 0, 0, 0 }, Ints(h.View(null).GetProperty("react")));
        Assert.Equal(new[] { 0, 1, 1, 0, 0, 0 }, Ints(h.View(null).GetProperty("reactN")));
        h.Tick();
        Assert.Equal(sent + 1, h.Outbox.Count(o => o is RoomViews));   // бульбашку везе найближчий тик

        Refused(h, h.Act(1, "react", new { e = 2 }), "Не так часто");
        h.Clock.AdvanceMs(Dice.ReactGapMs);
        Ok(h.Act(1, "react", new { e = 2 }));
        Assert.Equal(2, Ints(h.View(null).GetProperty("reactN"))[1]);
        Assert.Equal(2, Ints(h.View(null).GetProperty("react"))[1]);
        h.Clock.AdvanceMs(Dice.ReactGapMs);
        foreach (var bad in new object?[] { null, "x", new { e = 3 }, new { e = -1 }, new { e = "1" }, new { q = 1 } })
            Refused(h, h.Act(0, "react", bad), "Не зрозумів реакції");
    }

    [Fact]
    public void An_eliminated_player_leaving_changes_nothing()
    {
        var h = Arranged([[1, 1, 5], [5, 2, 2], []]);
        Ok(h.Act(0, "bid", new { q = 2, f = 5 }));
        static string Strip(string s) => Regex.Replace(s, "\"left\":(true|false)", "");
        var before = Strip(AllViews(h));
        h.Leave("Ганна");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal("bid", Phase(h));
        Assert.True(Player(h, 2).GetProperty("left").GetBoolean());
        // місце звільнилось, тож вид самого місця 2 тепер — вид глядача; решта — байт у байт
        for (var s = 0; s < 2; s++) Assert.Equal(Strip(before.Split('\n')[s]), Strip(Views.Text(h.Room.Game.View(s))));
    }

    [Fact]
    public void Rematch_gives_fresh_cups_rotated_seats_and_a_new_first_round()
    {
        var h = Arranged([[6, 6], [3]], turn: 1);
        Ok(h.Act(1, "bid", new { q = 3, f = 5 }));
        Ok(h.Act(0, "liar"));
        h.Tick(24);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal("Оля", h.NickOf(0));

        Assert.True(h.Rematch().Ok);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal("Петро", h.NickOf(0));
        Assert.Equal("Оля", h.NickOf(1));
        var v = h.View(null);
        Assert.Equal("shake", v.GetProperty("phase").GetString());
        Assert.Equal(1, v.GetProperty("round").GetInt32());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("result").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("reveal").ValueKind);
        Assert.All(v.GetProperty("players").EnumerateArray(), p =>
        {
            Assert.Equal(5, p.GetProperty("dice").GetInt32());
            Assert.False(p.GetProperty("palificoUsed").GetBoolean());
            Assert.False(p.GetProperty("wasAtOne").GetBoolean());
            Assert.False(p.GetProperty("left").GetBoolean());
        });
        Assert.Equal("Петро", Player(h, 0).GetProperty("nick").GetString());
        Assert.Equal(10, v.GetProperty("total").GetInt32());
    }

    [Fact]
    public void Comeback_from_one_die_to_victory_asks_for_the_achievement()
    {
        var h = Arranged([[4], [2]], turn: 1);
        Ok(h.Act(1, "bid", new { q = 2, f = 6 }));
        Ok(h.Act(0, "liar"));
        h.Tick(24);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var got = h.Awards.Where(a => a.Reason == "ach:dice-comeback").ToList();
        Assert.Single(got);
        Assert.Equal("Оля", got[0].Nick);
    }

    [Fact]
    public void A_table_nobody_touches_still_plays_itself_to_the_end()
    {
        // Заснули всі: перший хід — найнижча ставка за гравця, далі «Брешеш!» за наступного. Стіл не стоїть.
        var h = Table(3, seed: 11, options: new { dice = "3" });
        for (var i = 0; i < 20_000 && h.Room.Status == RoomStatus.Playing; i++) h.Tick();
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var w = h.View(null).GetProperty("result").GetProperty("winner").GetInt32();
        Assert.True(DiceOf(h, w) > 0);
        // хтось таки проспав найбільше — підсумок каже, хто
        var fun = h.View(null).GetProperty("result").GetProperty("fun").EnumerateArray().Select(x => x.GetString()!).ToList();
        Assert.Contains(fun, l => l.StartsWith("😴 Соня — ") && l.EndsWith(" проспано"));
    }

    [Fact]
    public void Bots_finish_every_table_from_two_to_six_without_breaking_the_rules()
    {
        for (var players = 2; players <= 6; players++)
            for (var seed = 1; seed <= 6; seed++)
            {
                var h = Table(players, seed * 13 + players);
                var moves = 0;
                var lastTotal = int.MaxValue;
                for (var i = 0; i < 20_000 && h.Room.Status == RoomStatus.Playing; i++)
                {
                    BotStep(h, ref moves);
                    h.Tick();
                    var v = h.View(null);
                    var total = v.GetProperty("total").GetInt32();
                    var sum = v.GetProperty("players").EnumerateArray().Where(p => p.GetProperty("alive").GetBoolean())
                        .Sum(p => p.GetProperty("dice").GetInt32());
                    Assert.Equal(sum, total);
                    Assert.All(v.GetProperty("players").EnumerateArray(), p => Assert.InRange(p.GetProperty("dice").GetInt32(), 0, 5));
                    if (v.GetProperty("phase").GetString() == "bid") lastTotal = total;
                }
                Assert.Equal(RoomStatus.Finished, h.Room.Status);
                Assert.Equal(players - 1, h.View(null).GetProperty("result").GetProperty("places").GetArrayLength());
                Assert.True(lastTotal < int.MaxValue);
            }
    }

    // =========================================================================================
    // Приховане, вид, кадр
    // =========================================================================================

    [Fact]
    public void Nobody_sees_another_players_dice_before_the_reveal()
    {
        var h = Table(4, seed: 9);
        ToBid(h);
        var c = Core(h);
        c.Arrange([[1, 2, 2, 5, 6], [3, 3, 4, 4, 6], [1, 1, 2, 3, 5], [2, 4, 5, 6, 6]]);
        c.SetTurn(0);
        static string Hand(int[] a) => "[" + string.Join(",", a) + "]";
        for (var step = 0; step < 3; step++)
        {
            for (var s = 0; s < 4; s++)
            {
                var text = Views.Text(h.Room.Game.View(s));
                for (var o = 0; o < 4; o++)
                {
                    if (o == s) Assert.Contains("\"my\":" + Hand(c.HandOf(o)), text);
                    else Assert.DoesNotContain(Hand(c.HandOf(o)), text);
                }
                Assert.Contains("\"reveal\":null", text);
            }
            var spectator = Views.Text(h.Room.Game.View(null));
            for (var o = 0; o < 4; o++) Assert.DoesNotContain(Hand(c.HandOf(o)), spectator);
            Assert.Contains("\"my\":null", spectator);
            var frame = Views.Text(h.Room.Game.Frame());
            for (var o = 0; o < 4; o++) Assert.DoesNotContain(Hand(c.HandOf(o)), frame);
            BotBid(h);
        }
    }

    /// <summary>Вид без лічильників реакцій — для «граней нема» і «стан не змінився».</summary>
    static string StripReacts(string s) => Regex.Replace(s, @"""react(N)?"":\[[^\]]*\]", "");

    static void BotBid(RoomHarness h)
    {
        var c = Core(h);
        Assert.True(Lowest(c, c.Turn, out var q, out var f));
        Ok(h.Act(c.Turn, "bid", new { q, f }));
    }

    [Fact]
    public void The_spectator_view_has_no_dice_at_all_and_no_exact()
    {
        var h = Arranged([[1, 5, 5], [5, 2, 2], [3, 3, 4, 4]]);
        Ok(h.Act(0, "bid", new { q = 4, f = 5 }));
        var v = h.View(null);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("my").ValueKind);
        Assert.False(v.GetProperty("canExact").GetBoolean());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("reveal").ValueKind);
        // ні єдиного масиву граней у тексті: лише ready і history (порожні/об'єкти)
        // (лічильники реакцій — теж масиви чисел, але граней у них нема)
        var text = StripReacts(Views.Text(h.Room.Game.View(null)));
        Assert.DoesNotMatch(new Regex(@"\[\d+(,\d+)+\]"), text);
    }

    [Fact]
    public void The_reveal_shows_every_hand_and_the_count()
    {
        var h = Table(3);
        ToBid(h);
        Core(h).Arrange([[1, 5, 5], [5, 2, 2], [3, 3, 4, 4]]);
        Core(h).SetTurn(0);
        Ok(h.Act(0, "bid", new { q = 4, f = 5 }));
        Ok(h.Act(1, "liar"));
        foreach (int? s in new int?[] { 0, 1, 2, null })
        {
            var r = h.View(s).GetProperty("reveal");
            var dice = r.GetProperty("dice");
            Assert.Equal(6, dice.GetArrayLength());
            Assert.Equal(new[] { 1, 5, 5 }, Ints(dice[0]));
            Assert.Equal(new[] { 2, 2, 5 }, Ints(dice[1]));
            Assert.Equal(new[] { 3, 3, 4, 4 }, Ints(dice[2]));
            Assert.Equal(0, dice[3].GetArrayLength());
            Assert.Equal(4, r.GetProperty("count").GetInt32());
            Assert.Equal(1, r.GetProperty("jokers").GetInt32());
            Assert.Equal(4, r.GetProperty("bid").GetProperty("q").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(r.GetProperty("say").GetString()));
        }
        // у розкритті «мої» — ті, що були під глеком, навіть якщо одна вже впала
        Assert.Equal(new[] { 2, 2, 5 }, Ints(h.View(1).GetProperty("my")));
    }

    [Fact]
    public void The_view_has_the_shape_the_client_expects()
    {
        var h = Table(3);
        var shake = h.View(0);
        foreach (var key in new[] { "turn", "phase", "round", "endsAt", "phaseMs", "rules", "palifico", "wild", "starter", "total",
                     "players", "my", "bid", "history", "canExact", "ready", "note", "react", "reactN", "reveal", "result" })
            Assert.True(Views.Has(shake, key), $"нема поля {key}");
        Assert.Equal(JsonValueKind.Null, shake.GetProperty("turn").ValueKind);
        Assert.Equal(1500, shake.GetProperty("phaseMs").GetInt32());
        Assert.False(string.IsNullOrEmpty(shake.GetProperty("note").GetString()));
        Assert.Equal(6, shake.GetProperty("react").GetArrayLength());
        Assert.Equal(6, shake.GetProperty("reactN").GetArrayLength());
        foreach (var key in new[] { "seat", "nick", "dice", "alive", "left", "palificoUsed", "wasAtOne", "sleepy" })
            Assert.True(Views.Has(shake.GetProperty("players")[0], key), $"нема players[].{key}");
        foreach (var key in new[] { "dice", "turnMs", "exact", "palifico" })
            Assert.True(Views.Has(shake.GetProperty("rules"), key));

        ToBid(h);
        var bid = h.View(0);
        Assert.Equal(JsonValueKind.Number, bid.GetProperty("turn").ValueKind);
        Assert.Equal(JsonValueKind.Null, bid.GetProperty("note").ValueKind);
        Core(h).SetTurn(0);
        Ok(h.Act(0, "bid", new { q = 2, f = 4 }));
        foreach (var key in new[] { "seat", "q", "f", "auto" }) Assert.True(Views.Has(h.View(0).GetProperty("bid"), key));
        Ok(h.Act(1, "liar"));
        var rev = h.View(0);
        Assert.Equal("reveal", rev.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, rev.GetProperty("turn").ValueKind);
        Assert.Equal(6000, rev.GetProperty("phaseMs").GetInt32());
        foreach (var key in new[] { "kind", "caller", "bid", "count", "jokers", "dice", "loser", "gainer", "out", "next", "say" })
            Assert.True(Views.Has(rev.GetProperty("reveal"), key), $"нема reveal.{key}");
        Assert.Equal(6, rev.GetProperty("reveal").GetProperty("dice").GetArrayLength());
    }

    [Fact]
    public void The_frame_is_public_and_tiny()
    {
        var h = Table(6);
        ToBid(h);
        var text = Views.Text(h.Room.Game.Frame());
        Assert.True(Encoding.UTF8.GetByteCount(text) <= 150, $"кадр {text.Length} Б: {text}");
        var f = Views.Json(h.Room.Game.Frame());
        Assert.Equal("bid", f.GetProperty("ph").GetString());
        Assert.Equal(Core(h).Turn, f.GetProperty("turn").GetInt32());
        Assert.Equal(JsonValueKind.String, f.GetProperty("endsAt").ValueKind);
        Assert.Equal(new[] { 5, 5, 5, 5, 5, 5 }, Ints(f.GetProperty("n")));
        Assert.Equal(4, f.EnumerateObject().Count());
    }

    [Fact]
    public void A_quiet_tick_sends_nothing_and_a_move_makes_the_next_tick_carry_views_without_a_frame()
    {
        var h = Table(2);
        ToBid(h);
        h.Tick();
        int Frames() => h.Outbox.Count(o => o is RoomFrame);
        int ViewsSent() => h.Outbox.Count(o => o is RoomViews);
        var v0 = ViewsSent();
        Assert.Equal(0, Frames());   // кадр модуль не читає — тик його й не шле (усе це є у виді)
        h.Tick(3);
        Assert.Equal(v0, ViewsSent());
        Ok(h.Act(Core(h).Turn, "bid", new { q = 1, f = 3 }));
        Assert.Equal(v0, ViewsSent());   // з Act реалтайм-кімната видів не шле — їх везе тик
        h.Tick();
        Assert.Equal(v0 + 1, ViewsSent());
        h.Tick(3);
        Assert.Equal(v0 + 1, ViewsSent());
        Assert.Equal(0, Frames());
    }

    [Fact]
    public void A_six_player_view_stays_under_two_kilobytes()
    {
        var h = Table(6, seed: 4);
        ToBid(h);
        for (var i = 0; i < 12; i++) BotBid(h);
        Assert.Equal(12, h.View(null).GetProperty("history").GetArrayLength());
        Ok(h.Act(Core(h).Turn, "liar"));
        for (var s = 0; s <= 6; s++)
        {
            int? seat = s == 6 ? null : s;
            var bytes = Encoding.UTF8.GetByteCount(Views.Text(h.Room.Game.View(seat)));
            Assert.True(bytes <= 2048, $"вид місця {seat?.ToString() ?? "глядача"} — {bytes} Б");
        }
    }

    // =========================================================================================
    // Тексти й таблиця правил, яку дублює клієнт
    // =========================================================================================

    [Fact]
    public void Glek_counts_faces_and_rounds_in_proper_ukrainian()
    {
        Assert.Equal("1 глечик", DiceSay.Faces(1, 1));
        Assert.Equal("3 п'ятірки", DiceSay.Faces(3, 5));
        Assert.Equal("5 шісток", DiceSay.Faces(5, 6));
        Assert.Equal("11 двійок", DiceSay.Faces(11, 2));
        Assert.Equal("21 трійка", DiceSay.Faces(21, 3));
        Assert.Equal("жодної четвірки", DiceSay.Faces(0, 4));
        Assert.Equal("жодного глечика", DiceSay.Faces(0, 1));
        Assert.Equal("1 раунд", DiceSay.Rounds(1));
        Assert.Equal("3 раунди", DiceSay.Rounds(3));
        Assert.Equal("7 раундів", DiceSay.Rounds(7));
        Assert.Equal("12 раундів", DiceSay.Rounds(12));
        Assert.Equal("22 раунди", DiceSay.Rounds(22));
        Assert.Equal("1 кісточку", DiceSay.DiceLeft(1));
    }

    /// <summary>Усі фрази банку для такого розкриття (сід перебирає вибір фрази).</summary>
    static HashSet<string> AllLines(string kind, int caller, DiceBid bid, int count, int? loser, int? gainer, bool @out, Func<int, string> nick)
    {
        var lines = new HashSet<string>();
        for (var seed = 0; seed < 64; seed++)
        {
            var o = new DiceOutcome(kind, caller, bid, count, 0, new int[DiceCore.MaxSeats][], loser, gainer, @out, 0);
            lines.Add(DiceSay.Reveal(new Random(seed), o, nick));
        }
        return lines;
    }

    [Fact]
    public void Glek_never_says_only_none_when_the_face_is_missing()
    {
        // Рецензія: «На столі лише жодної шістки» — у паліфіко й без глечиків нуль потрібної грані звичайна річ.
        static string Nick(int s) => s == 0 ? "Оля" : "Петро";
        var bid = new DiceBid(1, 2, 6);
        var liar = AllLines("liar", 0, bid, 0, 1, null, false, Nick);
        var sleep = AllLines("timeout", 0, bid, 0, 1, null, false, Nick);
        Assert.All(liar.Concat(sleep), l => Assert.DoesNotContain("лише жодн", l));
        Assert.Contains("Розкусили! На столі жодної шістки. Петро платить кісточкою.", liar);
        Assert.Contains(sleep, l => l.EndsWith("А мовчання врятувало: на столі жодної шістки. Петро платить кісточкою."));
        // а коли щось таки є — «лише» на місці
        Assert.Contains("Розкусили! На столі лише 1 шістка. Петро платить кісточкою.", AllLines("liar", 0, bid, 1, 1, null, false, Nick));
    }

    [Fact]
    public void A_guest_nick_opening_a_sentence_gets_a_capital_letter()
    {
        // «гість Ярина загинає…» — гостьові ніки з малої, а речення Глека — з великої.
        static string Nick(int s) => s == 0 ? "гість ярина" : "гість петро";
        var all = new List<string>();
        all.AddRange(AllLines("liar", 0, new DiceBid(1, 2, 6), 5, 0, null, false, Nick));   // правда — губить той, хто не повірив
        all.AddRange(AllLines("liar", 0, new DiceBid(1, 2, 6), 0, 1, null, true, Nick));    // брехня — автор, і вибуває
        all.AddRange(AllLines("exact", 0, new DiceBid(1, 2, 6), 2, null, 0, false, Nick));
        all.AddRange(AllLines("exact", 0, new DiceBid(1, 2, 6), 3, 0, null, false, Nick));
        all.AddRange(AllLines("timeout", 0, new DiceBid(1, 2, 6), 0, 1, null, true, Nick));
        for (var seed = 0; seed < 16; seed++) all.Add(DiceSay.Victory(new Random(seed), "гість ярина", 7, 2, duel: false));
        var sentenceStart = new Regex(@"(^|[.!?]\s)\p{Ll}");
        Assert.All(all, l => Assert.DoesNotMatch(sentenceStart, l));
        Assert.Contains(all, l => l.Contains("Гість петро лишається без кісточок"));
        Assert.Contains(all, l => l.Contains("мовчить"));   // «Час вийшов: гість ярина мовчить» — після двокрапки мала ок
        Assert.Equal("Гість ярина", DiceSay.Cap("гість ярина"));
        Assert.Equal("Оля", DiceSay.Cap("Оля"));
        Assert.Equal("", DiceSay.Cap(""));
    }

    /// <summary>
    /// Клієнт дублює таблицю <see cref="DiceCore.MinQ"/> (підказки конструктора, погашені грані). Обидва боки звіряються
    /// з тим самим знімком <c>DiceRuleTable.json</c>: тут — C#, у браузері — <c>dice.js</c> (qa-скрипт живої перевірки).
    /// Перезаписати знімок: <c>DICE_WRITE_TABLE=1 dotnet test --filter DiceTests</c>.
    /// </summary>
    [Fact]
    public void The_rule_table_the_client_duplicates_matches_the_server()
    {
        var table = RuleTable();
        var path = Paths.Resolve("tests/Hlechyky.Tests/Games/DiceRuleTable.json");
        if (Environment.GetEnvironmentVariable("DICE_WRITE_TABLE") == "1")
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                note = "MinQ(prev, f, palifico, bidderDice): індекс ((p*6 + f-1)*2 + pal)*2 + one; p = 0 без ставки, інакше 1 + (Q-1)*6 + (F-1), Q ≤ 30; one — у того, хто ставить, одна кісточка; 0 — грань не можна",
                maxQ = 30,
                table,
            }));
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(table, Ints(doc.RootElement.GetProperty("table")));
    }

    /// <summary>
    /// Той самий знімок — проти <c>minQ</c> з <c>web/games/dice.js</c>: функцію читаємо з файлу модуля й проганяємо
    /// крихітним інтерпретатором (<see cref="DiceJsRule"/>) через усі 4344 випадки. Розбіжність JS тепер ловить
    /// сам <c>dotnet test</c>, а не лише ручний qa-скрипт.
    /// </summary>
    [Fact]
    public void The_client_minQ_in_dice_js_gives_the_same_table()
    {
        var js = DiceJsRule.Load(File.ReadAllText(Paths.Resolve("web/games/dice.js")), "minQ");
        var table = RuleTable();
        var i = 0;
        for (var p = 0; p <= 180; p++)
        {
            var prev = p == 0 ? null : new Dictionary<string, object?> { ["q"] = (double)((p - 1) / 6 + 1), ["f"] = (double)((p - 1) % 6 + 1) };
            for (var f = 1; f <= 6; f++)
                for (var pal = 0; pal < 2; pal++)
                    for (var one = 0; one < 2; one++, i++)
                    {
                        var got = js.Call(prev, (double)f, pal == 1, one == 1 ? 1.0 : 2.0);
                        Assert.True(table[i] == got,
                            $"dice.js minQ({(prev is null ? "null" : $"{prev["q"]}×{prev["f"]}")}, {f}, pal={pal == 1}, myDice={(one == 1 ? 1 : 2)}) = {got}, а сервер каже {table[i]}");
                    }
        }
        Assert.Equal(table.Length, i);
    }

    internal static int[] RuleTable()
    {
        var list = new List<int>(181 * 6 * 4);
        for (var p = 0; p <= 180; p++)
        {
            DiceBid? prev = p == 0 ? null : new DiceBid(0, (p - 1) / 6 + 1, (p - 1) % 6 + 1);
            for (var f = 1; f <= 6; f++)
                for (var pal = 0; pal < 2; pal++)
                    for (var one = 0; one < 2; one++)
                        list.Add(DiceCore.MinQ(prev, f, pal == 1, one == 1 ? 1 : 2));
        }
        return [.. list];
    }
}

/// <summary>Швидкодія «Під глеком»: окремою колекцією, бо стінний годинник у паралельному прогоні бреше.</summary>
[Collection(SerialPerf.Name)]
public class DicePerfTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Perf")]
    public void Three_thousand_ticks_of_a_six_player_table_take_under_a_second()
    {
        var h = DiceTests.Table(6, seed: 21);
        var moves = 0;
        var viewBytes = 0L;
        var views = 0;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 3000; i++)
        {
            var sent = h.Outbox.Count;
            DiceTests.BotStep(h, ref moves);
            h.Tick();
            if (h.Room.Status == RoomStatus.Finished) h.Rematch();
            // Тик розіслав види — будуємо їх для всіх шести, як це зробив би Broadcaster.
            for (var k = sent; k < h.Outbox.Count; k++)
                if (h.Outbox[k] is RoomViews)
                {
                    for (var s = 0; s < 6; s++) { viewBytes += Views.Text(h.Room.Game.View(s)).Length; views++; }
                    break;
                }
        }
        sw.Stop();
        Assert.True(moves > 300, $"бот зробив лише {moves} ходів");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"3000 тиків столу на шістьох зайняли {sw.Elapsed}");
        output.WriteLine($"3000 тиків (з ходами й видами на шістьох): {sw.Elapsed.TotalMilliseconds:F1} мс, " +
                         $"{sw.Elapsed.TotalMilliseconds / 3000:F4} мс на тик; ходів {moves}; середній вид {(views > 0 ? viewBytes / views : 0)} Б");

        // Голий тик у фазі ставок, коли нічого не міняється (99 % тиків): три порівняння.
        var g = DiceTests.Table(6, seed: 22);
        for (var i = 0; i < 6; i++) g.Tick();
        var game = DiceTests.Game(g);
        Assert.Equal(DicePhase.Bid, game.Phase);
        var quiet = Stopwatch.StartNew();
        const int N = 200_000;
        lock (g.Room.Sync)
            for (var i = 0; i < N; i++)
                if (game.Tick().View) throw new InvalidOperationException("тихий тик щось розіслав");
        quiet.Stop();
        output.WriteLine($"тихий Tick(): {quiet.Elapsed.TotalMilliseconds * 1_000_000 / N:F0} нс");
        var frame = Views.Text(game.Frame());
        output.WriteLine($"кадр: {Encoding.UTF8.GetByteCount(frame)} Б — {frame}");
        Assert.True(quiet.Elapsed.TotalMilliseconds / N < 0.25);
    }
}

/// <summary>
/// Крихітний інтерпретатор рівно того JS, яким написано правило <c>minQ</c> у <c>dice.js</c>: <c>if (…) return …;</c>,
/// блоки, тернарний оператор, <c>&amp;&amp; || ! === !== &lt; &gt; &lt;= &gt;= + - * / %</c>, поле <c>prev.q</c> і
/// <c>Math.floor</c>. Числа — double, як у JS. Щось інше в правилі — тест упаде з назвою незнайомого, і тоді
/// або навчити інтерпретатор, або звірити руками <c>docs/games/dev/dice-check.py --what rules</c>.
/// Виконання — рекурсивний спуск просто по токенах; гілки, які не беруться, проходимо «холосто» (run = false).
/// </summary>
internal sealed class DiceJsRule
{
    readonly string[] _params;
    readonly List<string> _tok;
    Dictionary<string, object?> _env = [];
    int _p;

    DiceJsRule(string[] ps, List<string> tok) { _params = ps; _tok = tok; }

    public static DiceJsRule Load(string source, string name)
    {
        var m = Regex.Match(source, @"function\s+" + name + @"\s*\(([^)]*)\)\s*\{");
        if (!m.Success) throw new InvalidOperationException($"у модулі нема function {name}(…)");
        var ps = m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        // тіло — до парної дужки
        var start = m.Index + m.Length;
        var depth = 1;
        var k = start;
        for (; k < source.Length && depth > 0; k++) depth += source[k] == '{' ? 1 : source[k] == '}' ? -1 : 0;
        return new DiceJsRule(ps, Lex("{" + source[start..k]));
    }

    static List<string> Lex(string s)
    {
        var list = new List<string>();
        var i = 0;
        string[] ops = ["===", "!==", ">=", "<=", "&&", "||", "=>"];
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*') { i = s.IndexOf("*/", i + 2, StringComparison.Ordinal) + 2; continue; }
            if (char.IsDigit(c))
            {
                var j = i;
                while (j < s.Length && (char.IsDigit(s[j]) || s[j] == '.')) j++;
                list.Add(s[i..j]);
                i = j;
                continue;
            }
            if (char.IsLetter(c) || c is '_' or '$')
            {
                var j = i;
                while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] is '_' or '$')) j++;
                list.Add(s[i..j]);
                i = j;
                continue;
            }
            var op = ops.FirstOrDefault(o => string.CompareOrdinal(s, i, o, 0, o.Length) == 0);
            if (op is not null) { list.Add(op); i += op.Length; continue; }
            list.Add(c.ToString());
            i++;
        }
        return list;
    }

    sealed class Ret(object? value) : Exception { public object? Value { get; } = value; }

    public int Call(params object?[] args)
    {
        _env = new Dictionary<string, object?>();
        for (var i = 0; i < _params.Length; i++) _env[_params[i]] = i < args.Length ? args[i] : null;
        _p = 0;
        try { Block(true); }
        catch (Ret r) { return (int)Num(r.Value); }
        throw new InvalidOperationException("minQ нічого не повернула");
    }

    string Peek => _p < _tok.Count ? _tok[_p] : "";

    void Eat(string t)
    {
        if (Peek != t) throw new InvalidOperationException($"чекали «{t}», а там «{Peek}» (токен {_p})");
        _p++;
    }

    void Block(bool run)
    {
        Eat("{");
        while (Peek != "}") Statement(run);
        Eat("}");
    }

    void Statement(bool run)
    {
        switch (Peek)
        {
            case "{":
                Block(run);
                return;
            case "if":
            {
                _p++;
                Eat("(");
                var cond = Expr(run);
                Eat(")");
                var yes = run && Truthy(cond);
                Statement(yes);
                if (Peek == "else")
                {
                    _p++;
                    Statement(run && !yes);
                }
                return;
            }
            case "return":
            {
                _p++;
                var v = Expr(run);
                if (Peek == ";") _p++;
                if (run) throw new Ret(v);
                return;
            }
            default:
                throw new InvalidOperationException($"незнайомий оператор «{Peek}» у правилі");
        }
    }

    object? Expr(bool run)
    {
        var cond = Or(run);
        if (Peek != "?") return cond;
        _p++;
        var take = run && Truthy(cond);
        var a = Expr(take);
        Eat(":");
        var b = Expr(run && !take);
        return take ? a : b;
    }

    object? Or(bool run)
    {
        var l = And(run);
        while (Peek == "||")
        {
            _p++;
            var done = run && Truthy(l);
            var r = And(run && !done);
            if (run && !done) l = r;
        }
        return l;
    }

    object? And(bool run)
    {
        var l = Eq(run);
        while (Peek == "&&")
        {
            _p++;
            var go = run && Truthy(l);
            var r = Eq(go);
            if (go) l = r;
        }
        return l;
    }

    object? Eq(bool run)
    {
        var l = Rel(run);
        while (Peek is "===" or "!==")
        {
            var op = _tok[_p++];
            var r = Rel(run);
            if (run) l = (op == "===") == Equals(l, r);
        }
        return l;
    }

    object? Rel(bool run)
    {
        var l = Add(run);
        while (Peek is "<" or ">" or "<=" or ">=")
        {
            var op = _tok[_p++];
            var r = Add(run);
            if (!run) continue;
            double a = Num(l), b = Num(r);
            l = op switch { "<" => a < b, ">" => a > b, "<=" => a <= b, _ => a >= b };
        }
        return l;
    }

    object? Add(bool run)
    {
        var l = Mul(run);
        while (Peek is "+" or "-")
        {
            var op = _tok[_p++];
            var r = Mul(run);
            if (run) l = op == "+" ? Num(l) + Num(r) : Num(l) - Num(r);
        }
        return l;
    }

    object? Mul(bool run)
    {
        var l = Unary(run);
        while (Peek is "*" or "/" or "%")
        {
            var op = _tok[_p++];
            var r = Unary(run);
            if (run) l = op switch { "*" => Num(l) * Num(r), "/" => Num(l) / Num(r), _ => Num(l) % Num(r) };
        }
        return l;
    }

    object? Unary(bool run)
    {
        if (Peek == "!")
        {
            _p++;
            var v = Unary(run);
            return run ? !Truthy(v) : null;
        }
        if (Peek == "-")
        {
            _p++;
            var v = Unary(run);
            return run ? -Num(v) : null;
        }
        return Postfix(run);
    }

    object? Postfix(bool run)
    {
        var t = _tok[_p++];
        if (t == "(")
        {
            var v = Expr(run);
            Eat(")");
            return v;
        }
        if (double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n)) return n;
        if (t == "Math")
        {
            Eat(".");
            var fn = _tok[_p++];
            Eat("(");
            var a = Expr(run);
            Eat(")");
            if (!run) return null;
            return fn switch
            {
                "floor" => Math.Floor(Num(a)),
                "ceil" => Math.Ceiling(Num(a)),
                _ => throw new InvalidOperationException($"незнайома Math.{fn}"),
            };
        }
        if (t is "true" or "false") return t == "true";
        if (t == "null") return null;
        if (!_env.TryGetValue(t, out var val)) throw new InvalidOperationException($"незнайоме ім'я «{t}» у правилі");
        while (Peek == ".")
        {
            _p++;
            var field = _tok[_p++];
            if (!run) continue;
            val = val is Dictionary<string, object?> o && o.TryGetValue(field, out var fv)
                ? fv
                : throw new InvalidOperationException($"поле {field} у {t}, якого нема");
        }
        return run ? val : null;
    }

    static bool Truthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        double d => d != 0 && !double.IsNaN(d),
        _ => true,
    };

    static double Num(object? v) => v switch
    {
        double d => d,
        bool b => b ? 1 : 0,
        null => 0,
        _ => double.NaN,
    };
}
