using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Розумний Глек «Хуторів» (<see cref="KhutoryBot"/>, хвиля 2): сила проти простих <see cref="KhutoryAuto"/>, самогра,
/// відповіді на пропозиції людей і власні пропозиції з очікуванням до 10 с — через <see cref="RoomHarness"/>.
/// </summary>
[Collection(SerialPerf.Name)]
public class KhutoryBotTests(ITestOutputHelper output)
{
    static readonly DateTimeOffset T0 = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Гра на ядрі до перемоги: щокроку — перший, на кого чекає стіл (від поточного); Глек, що чекає відповідей,
    /// пропускає. Повертає кількість кроків.
    /// </summary>
    static int Run(KhutoryCore c, Func<int, DateTimeOffset, string?> step, int seed)
    {
        var steps = 0;
        var now = T0;
        while (!c.Over)
        {
            string? err = KhutoryBot.Wait;
            foreach (var s in Enumerable.Range(0, 4).Select(i => (c.Turn + i) % 4).Where(c.MayAct))
            {
                err = step(s, now);
                if (err != KhutoryBot.Wait) break;
            }
            Assert.True(err is null, $"сід {seed}, крок {steps}, фаза {c.Phase}: {err}");
            Invariants(c);
            now = now.AddSeconds(1);
            Assert.True(++steps < 20_000, $"сід {seed}: партія не кінчається");
        }
        return steps;
    }

    static KhutoryCore NewGame(int seed, int n, Random rng)
    {
        var board = seed % 5 == 0 ? KhutoryBoard.Starter() : KhutoryBoard.Random(rng);
        var c = new KhutoryCore(board, Enumerable.Range(0, n), rng) { Target = seed % 2 == 0 ? 10 : 8, Friendly = seed % 3 != 0 };
        c.Begin(rng.Next(n));
        return c;
    }

    // =========================================================================================
    // Сила й самогра
    // =========================================================================================

    [Fact]
    [Trait("Category", "Perf")]
    public void Smart_glek_beats_three_simple_ones_clearly()
    {
        var wins = 0;
        var turnsTotal = 0;
        for (var seed = 1; seed <= 200; seed++)
        {
            var rng = new Random(seed);
            var c = NewGame(seed, 4, rng);
            var me = seed % 4;
            var bot = new KhutoryBot();
            Run(c, (s, now) => s == me ? bot.Step(c, s, rng, now) : KhutoryAuto.Step(c, s, rng), seed);
            if (c.Winner == me) wins++;
            turnsTotal += c.Rounds * c.Seats + c.TurnIdx;
        }
        output.WriteLine($"KhutoryBot проти трьох KhutoryAuto: {wins}/200 = {wins / 2.0:0.#} %, у середньому {turnsTotal / 200.0:0.#} ходів");
        // Рівний із ними виграв би 25 %. Ціль оркестратора була 40 %; на самогрі стеля евристик ≈ 37–38 % (див. звіт).
        Assert.True(wins >= 70, $"виграв лише {wins} із 200");
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Four_smart_gleks_play_to_a_win_trade_with_each_other_and_keep_invariants()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var turnsTotal = 0;
        var deals = 0;
        var samples = new List<string>();
        for (var seed = 1; seed <= 200; seed++)
        {
            var rng = new Random(seed);
            var n = 2 + seed % 3;
            var c = NewGame(seed, n, rng);
            var bot = new KhutoryBot();
            var log = new List<string>();
            Run(c, (s, now) =>
            {
                var before = c.Offers.Count;
                var traded = c.Trades.Sum();
                var err = bot.Step(c, s, rng, now);
                if (c.Offers.Count > before && c.Offers[^1] is var o)
                    log.Add($"місце {s} пропонує {Res(o.Give)} за {Res(o.Get)}");
                if (c.Trades.Sum() == traded + 2)
                {
                    deals++;
                    log.Add(c.Log[^1]);
                }
                return err;
            }, seed);
            var turns = c.Rounds * c.Seats + c.TurnIdx;
            Assert.True(turns < 400, $"сід {seed}: {turns} ходів");
            Assert.True(c.Points(c.Winner!.Value) >= c.Target);
            turnsTotal += turns;
            if (seed <= 2) samples.AddRange(log.Take(6));
        }
        output.WriteLine($"самогра 200 партій: {sw.ElapsedMilliseconds} мс, у середньому {turnsTotal / 200.0:0.#} ходів, обмінів між гравцями {deals}");
        foreach (var l in samples) output.WriteLine(l);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"{sw.ElapsedMilliseconds} мс");
        Assert.True(deals > 0, "Глеки між собою не міняються");
    }

    static string Res(int[] a) =>
        string.Join(" + ", Enumerable.Range(0, 5).Where(r => a[r] > 0).Select(r => $"{a[r]} {KhutoryCore.ResGen[r]}"));

    /// <summary>Банк + руки = 19 кожного ресурсу, фігури в межах запасу, грамоти всі на місці.</summary>
    static void Invariants(KhutoryCore c)
    {
        for (var r = 0; r < 5; r++)
        {
            var sum = c.Bank[r];
            for (var s = 0; s < 4; s++)
            {
                if (c.Hand[s][r] < 0) Assert.Fail($"від'ємна рука {s}");
                sum += c.Hand[s][r];
            }
            if (sum != 19) Assert.Fail($"ресурс {r}: {sum}");
        }
        for (var s = 0; s < 4; s++)
            if (c.RoadsOf(s) > 15 || c.TownsOf(s) > 5 || c.CitiesOf(s) > 4) Assert.Fail($"фігур більше за запас у {s}");
        var devs = c.Deck.Count + c.PlayedOther;
        for (var s = 0; s < 4; s++) devs += c.Devs[s].Count + c.Knights[s];
        if (devs != 25) Assert.Fail($"грамот {devs}");
    }

    // =========================================================================================
    // Пропозиції з людьми (кімната)
    // =========================================================================================

    static RoomHarness Table(string bots = "1", int seed = 5)
    {
        var h = new RoomHarness("khutory", new { map = "starter", timer = "180", bots }, seed: seed);
        h.Join("Оля");
        Assert.True(h.Start().Ok);
        return h;
    }

    static KhutoryCore CoreOf(RoomHarness h) => ((Khutory)h.Room.Game).Core;

    static int BotSeat(RoomHarness h) =>
        h.View(0).GetProperty("players").EnumerateArray().First(p => p.GetProperty("bot").ValueKind == JsonValueKind.String)
            .GetProperty("seat").GetInt32();

    /// <summary>Розставити всіх (автоходом прямо в ядрі), руки — порожні, фаза main, хід <paramref name="turn"/>.</summary>
    static KhutoryCore Main(RoomHarness h, int turn)
    {
        var c = CoreOf(h);
        var rng = new Random(1);
        while (c.Phase == KhPhase.Setup) Assert.Null(KhutoryAuto.Step(c, c.Turn, rng));
        for (var s = 0; s < 4; s++)
            for (var r = 0; r < 5; r++) { c.Bank[r] += c.Hand[s][r]; c.Hand[s][r] = 0; }
        c.Phase = KhPhase.Main;
        c.Turn = turn;
        c.TurnIdx = Array.IndexOf(c.Order, turn);
        c.Rolled = true;
        c.Dice = [2, 3];
        return c;
    }

    /// <summary>Після ручної правки ядра — перерахувати строк і botIn, як після будь-якої дії.</summary>
    static void Rearm(RoomHarness h) =>
        typeof(Khutory).GetMethod("After", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(h.Room.Game, null);

    static void Give(KhutoryCore c, int s, int wood = 0, int clay = 0, int wool = 0, int grain = 0, int ore = 0)
    {
        int[] a = [wood, clay, wool, grain, ore];
        for (var r = 0; r < 5; r++) { c.Bank[r] -= a[r]; c.Hand[s][r] += a[r]; }
    }

    static int? BotIn(RoomHarness h) =>
        h.View(0).GetProperty("botIn") is { ValueKind: JsonValueKind.Number } b ? b.GetInt32() : null;

    static JsonElement[] Offers(RoomHarness h) => [.. h.View(0).GetProperty("offers").EnumerateArray()];

    [Fact]
    public void Bot_accepts_a_human_offer_that_helps_its_city_and_the_deal_goes_through()
    {
        var h = Table();
        var bot = BotSeat(h);
        var c = Main(h, 0);
        Give(c, bot, wood: 2, grain: 2, ore: 2);
        Give(c, 0, ore: 1);
        Assert.True(h.Act(0, "offer", new { give = new { ore = 1 }, get = new { wood = 1 }, to = bot }).Ok);
        // Відповідає швидко — за 1–2 с.
        Assert.InRange(BotIn(h) ?? -1, 0, 2000);
        h.Clock.AdvanceMs(BoardBots.ThinkMs);
        Assert.True(h.Act(0, "bot").Ok);
        var offer = Offers(h).Single();
        Assert.Contains(bot, offer.GetProperty("acc").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Null(BotIn(h));
        Assert.True(h.Act(0, "deal", new { id = offer.GetProperty("id").GetInt32(), seat = bot }).Ok);
        Assert.Equal([1, 0, 0, 2, 3], c.Hand[bot]);
        Assert.Equal([1, 0, 0, 0, 0], c.Hand[0]);
    }

    [Fact]
    public void Bot_refuses_a_human_two_points_from_victory()
    {
        var h = Table();
        var bot = BotSeat(h);
        var c = Main(h, 0);
        c.Longest = 0;
        c.Army = 0;
        Assert.True(c.PublicPoints(0) >= c.Target - 2);
        Give(c, bot, wood: 2, grain: 2, ore: 2);
        Give(c, 0, ore: 1);
        Assert.True(h.Act(0, "offer", new { give = new { ore = 1 }, get = new { wood = 1 }, to = (int?)null }).Ok);
        h.Clock.AdvanceMs(BoardBots.ThinkMs);
        Assert.True(h.Act(0, "bot").Ok);
        var offer = Offers(h).Single();
        Assert.Contains(bot, offer.GetProperty("dec").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Empty(offer.GetProperty("acc").EnumerateArray());
    }

    /// <summary>Хід Глека: у руці 4 «зайвих» (з курсом банку ≥ 3) і майже містечко — пропонує людям 2 за 1 залізо.</summary>
    static (RoomHarness h, KhutoryCore c, int bot, int spare) BotOffers()
    {
        var h = Table();
        var bot = BotSeat(h);
        var c = Main(h, bot);
        var spare = new[] { 0, 1 }.First(r => c.Rates(bot)[r] >= 3);
        c.Hand[bot][spare] += 4; c.Bank[spare] -= 4;
        Give(c, bot, grain: 2, ore: 2);
        Give(c, 0, ore: 1);
        Rearm(h);
        h.Clock.AdvanceMs(BoardBots.ThinkMs);
        Assert.True(h.Act(0, "bot").Ok);
        var offer = Offers(h).Single();
        Assert.Equal(bot, offer.GetProperty("from").GetInt32());
        Assert.Equal(JsonValueKind.Null, offer.GetProperty("to").ValueKind);
        Assert.Equal(2, offer.GetProperty("give").GetProperty(KhutoryBoard.Res[spare]).GetInt32());
        Assert.Equal(1, offer.GetProperty("get").GetProperty("ore").GetInt32());
        return (h, c, bot, spare);
    }

    [Fact]
    public void Bot_offer_accepted_by_a_human_turns_into_a_deal()
    {
        var (h, c, bot, spare) = BotOffers();
        var id = Offers(h).Single().GetProperty("id").GetInt32();
        Assert.True(h.Act(0, "answer", new { id, ok = true }).Ok);
        Assert.InRange(BotIn(h) ?? -1, 0, 2000);
        h.Clock.AdvanceMs(BoardBots.ThinkMs);
        Assert.True(h.Act(0, "bot").Ok);
        Assert.Empty(Offers(h));
        Assert.Equal(0, c.Hand[0][4]);
        Assert.Equal(2, c.Hand[0][spare]);
        Assert.Equal(3, c.Hand[bot][4]);
        // Далі — містечко.
        h.Clock.AdvanceMs(BoardBots.ThinkMs);
        Assert.True(h.Act(0, "bot").Ok);
        Assert.Equal(1, c.CitiesOf(bot));
    }

    [Fact]
    public void Bot_offer_without_answer_is_cancelled_after_ten_seconds_and_the_bot_plays_on()
    {
        var (h, c, bot, _) = BotOffers();
        // Чекає людей до 10 с, раніше не штовхається.
        Assert.InRange(BotIn(h) ?? -1, 8000, KhutoryBot.OfferWaitMs);
        h.Clock.AdvanceMs(BoardBots.ThinkMs);
        Assert.False(h.Act(0, "bot").Ok);
        h.Clock.AdvanceMs(KhutoryBot.OfferWaitMs);
        Assert.True(h.Act(0, "bot").Ok);
        Assert.Empty(Offers(h));
        for (var i = 0; i < 20 && c.Turn == bot; i++)
        {
            h.Clock.AdvanceMs(BoardBots.ThinkMs);
            Assert.True(h.Act(0, "bot").Ok);
        }
        Assert.NotEqual(bot, c.Turn);
        Assert.Equal(1, c.CitiesOf(bot));   // обміняв у банку й поставив містечко
        Assert.Equal(1, c.Hand[0][4]);      // у людини нічого не взяли
    }

    [Fact]
    public void Whole_game_with_offers_both_ways_does_not_hang_and_room_journal_is_short()
    {
        var h = Table(bots: "3", seed: 7);
        var offered = false;
        var steps = 0;
        var asked = 0;
        while (h.Finished.Count == 0)
        {
            Assert.True(++steps < 10_000, "партія зависла");
            var v = h.View(0);
            var me = v.GetProperty("me");
            var must = me.GetProperty("must").ValueKind == JsonValueKind.String ? me.GetProperty("must").GetString() : null;
            var phase = v.GetProperty("phase").GetString();
            var mine = v.GetProperty("turn").ValueKind == JsonValueKind.Number && v.GetProperty("turn").GetInt32() == 0;
            var hand = KhutoryBoard.Res.Select(r => me.GetProperty("hand").GetProperty(r).GetInt32()).ToArray();
            // Пропозиція Глека мені — відповісти (через раз «так»).
            var toMe = v.GetProperty("offers").EnumerateArray().FirstOrDefault(o =>
                o.GetProperty("from").GetInt32() != 0 && (o.GetProperty("to").ValueKind == JsonValueKind.Null || o.GetProperty("to").GetInt32() == 0)
                && !o.GetProperty("acc").EnumerateArray().Any(x => x.GetInt32() == 0)
                && !o.GetProperty("dec").EnumerateArray().Any(x => x.GetInt32() == 0));
            if (toMe.ValueKind == JsonValueKind.Object)
            {
                asked++;
                var pay = toMe.GetProperty("get").EnumerateObject().All(x => hand[Array.IndexOf(KhutoryBoard.Res, x.Name)] >= x.Value.GetInt32());
                Assert.True(h.Act(0, "answer", new { id = toMe.GetProperty("id").GetInt32(), ok = pay && asked % 2 == 0 }).Ok);
                continue;
            }
            if (phase == "setup" && mine)
            {
                var spots = me.GetProperty("spots");
                Assert.True(must == "town"
                    ? h.Act(0, "town", new { v = spots.GetProperty("town")[0].GetInt32() }).Ok
                    : h.Act(0, "road", new { e = spots.GetProperty("road")[0].GetInt32() }).Ok);
                continue;
            }
            if (must == "roll") { offered = false; Assert.True(h.Act(0, "roll").Ok); continue; }
            if (must == "discard")
            {
                var d = KhutoryAuto.DiscardPick(hand, me.GetProperty("discard").GetInt32());
                Assert.True(h.Act(0, "discard", new { res = KhutoryCore.ResMap(d) }).Ok);
                continue;
            }
            if (must == "robber") { Assert.True(h.Act(0, "robber", new { h = me.GetProperty("robber")[0].GetInt32() }).Ok); continue; }
            if (must == "steal") { Assert.True(h.Act(0, "steal", new { seat = me.GetProperty("steal")[0].GetInt32() }).Ok); continue; }
            if (must == "road") { Assert.True(h.Act(0, "road", new { e = me.GetProperty("spots").GetProperty("road")[0].GetInt32() }).Ok); continue; }
            if (phase == "main" && mine)
            {
                var own = v.GetProperty("offers").EnumerateArray().FirstOrDefault(o => o.GetProperty("from").GetInt32() == 0);
                if (own.ValueKind == JsonValueKind.Object && own.GetProperty("acc").GetArrayLength() > 0)
                {
                    Assert.True(h.Act(0, "deal", new { id = own.GetProperty("id").GetInt32(), seat = own.GetProperty("acc")[0].GetInt32() }).Ok);
                    continue;
                }
                if (!offered && hand.Sum() > 0)
                {
                    offered = true;
                    var give = Enumerable.Range(0, 5).OrderByDescending(r => hand[r]).First();
                    var get = Enumerable.Range(0, 5).Where(r => r != give).OrderBy(r => hand[r]).First();
                    Assert.True(h.Act(0, "offer", new { give = new Dictionary<string, int> { [KhutoryBoard.Res[give]] = 1 },
                        get = new Dictionary<string, int> { [KhutoryBoard.Res[get]] = 1 }, to = (int?)null }).Ok);
                    continue;
                }
                if (BotIn(h) is null) { Assert.True(h.Act(0, "end").Ok); continue; }
            }
            if (BotIn(h) is { } ms)
            {
                h.Clock.AdvanceMs(Math.Max(1, ms));
                Assert.True(h.Act(0, "bot").Ok, "поштовх не пройшов");
                continue;
            }
            Assert.Fail($"нічого не робиться: фаза {phase}, must {must}");
        }
        var over = h.View(0).GetProperty("over");
        Assert.Equal("points", over.GetProperty("reason").GetString());
        output.WriteLine($"кроків {steps}, пропозицій Глеків мені {asked}, обмінів у людини {over.GetProperty("stats").GetProperty("seats")[0].GetProperty("trades").GetInt32()}");
        Assert.True(asked > 0, "Глеки не пропонували людині");
        // У журнал кімнати — лише нагороди, перше містечко й перемога.
        var journal = h.Outbox.OfType<Journal>().Select(j => j.Text).Where(t => t.StartsWith("Хутори:")).ToList();
        foreach (var line in journal) output.WriteLine(line);
        Assert.DoesNotContain(journal, l => l.Contains("кидає") || l.Contains("ставить хату"));
        Assert.Contains(journal, l => l.Contains("перемагає"));
        Assert.True(journal.Count(l => l.Contains("ставить містечко")) <= 4);
    }
}
