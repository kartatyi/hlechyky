using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Підставна випадковість автомата: числа з черги (порожня — 0 і 0,999: без Скарбнички).</summary>
public sealed class ScriptRng : ISlotRng
{
    public Queue<int> Ints { get; } = new();
    public Queue<double> Doubles { get; } = new();

    public ScriptRng Stops(params int[] s) { foreach (var x in s) Ints.Enqueue(x); return this; }
    public ScriptRng Card(string c) { Ints.Enqueue(c == "r" ? 0 : 1); return this; }
    public ScriptRng Jackpot() { Doubles.Enqueue(0); return this; }

    public int Next(int max) => Ints.Count > 0 ? Ints.Dequeue() % max : 0;
    public double NextDouble() => Doubles.Count > 0 ? Doubles.Dequeue() : 0.999;
}

public sealed class FakeSlotsWire : ISlotsWire
{
    public List<string> Chats { get; } = [];
    public List<string> Airs { get; } = [];
    public void Chat(string text) => Chats.Add(text);
    public void Air(string kind, string nick, IReadOnlyList<(string Key, object Value)> values) => Airs.Add($"{kind}:{nick}");
}

/// <summary>Обв'язка: свої ставки, сховище й каса автоматів (Балачки — одразу), кімната через справжній каркас.</summary>
public sealed class SlotsKit
{
    public FakeStakes Stakes { get; } = new();
    public FakeStore Store { get; } = new();
    public FakeSlotsWire Wire { get; } = new();
    public SlotsOptions Options { get; } = new();
    public FakeClock Clock { get; } = new();
    public SlotsBank Bank { get; }
    public RoomHarness H { get; }

    public SlotsKit(string nick = "Оля", int wallet = 10_000, int seed = 42, Action<SlotsOptions>? opts = null, string game = "slot-glek")
    {
        opts?.Invoke(Options);
        Stakes.Set(nick, wallet);
        Bank = new SlotsBank(Stakes, Store, () => Options, Clock, Wire, defer: a => a());
        H = new RoomHarness(game, services: RoomHarness.WithService(Bank), seed: seed);
        var r = H.Solo(nick);
        Assert.True(r.Ok, r.Message);
    }

    public SlotGame G => (SlotGame)H.Room.Game;
    public ActResult Spin(int bet = 10, int? seq = null) => seq is { } s ? H.Act(0, "spin", new { bet, seq = s }) : H.Act(0, "spin", new { bet });
    public JsonElement View => H.View(0);

    /// <summary>Зупинки з виграшем рівно <paramref name="units"/> ставок на лінію (чи першим, що задовольняє умову).</summary>
    public static int[] StopsWhere(Func<int[], int, bool> pred)
    {
        for (var a = 0; a < 32; a++)
            for (var b = 0; b < 32; b++)
                for (var c = 0; c < 32; c++)
                {
                    int[] s = [a, b, c];
                    if (pred(s, SlotGlekMath.Units(s))) return s;
                }
        throw new InvalidOperationException("таких зупинок нема");
    }

    public ScriptRng Rig()
    {
        var rng = new ScriptRng();
        G.Rng = rng;
        return rng;
    }
}

public class SlotsTests
{
    static int Sum(FakeStakes s, string suffix) => s.Ledger.Where(m => m.Ref.EndsWith(suffix, StringComparison.Ordinal)).Sum(m => m.Delta);

    [Fact]
    public void Spin_takes_the_bet_and_pays_the_win_exactly_once()
    {
        var k = new SlotsKit(wallet: 100_000);
        long won = 0;
        for (var i = 1; i <= 300; i++)
        {
            var r = k.Spin(20);
            Assert.True(r.Ok, r.Message);
            var last = k.View.GetProperty("last");
            Assert.Equal(i, last.GetProperty("seq").GetInt32());
            won += last.GetProperty("script").GetProperty("win").GetInt32();
            won += last.GetProperty("script").TryGetProperty("jackpot", out var jp) ? jp.GetInt32() : 0;
        }
        Assert.Equal(300, k.Stakes.Ledger.Count(m => m.Ref.EndsWith(":bet", StringComparison.Ordinal)));
        Assert.Equal(-300 * 20, Sum(k.Stakes, ":bet"));
        Assert.Equal(won, Sum(k.Stakes, ":win") + Sum(k.Stakes, ":jp"));
        Assert.Equal(100_000 - 6000 + won, k.Stakes.Balance("Оля"));
        Assert.Equal(k.Stakes.Balance("Оля"), k.View.GetProperty("balance").GetInt32());
        Assert.All(k.Stakes.Reasons.Where(x => x.Key.EndsWith(":bet")), x => Assert.Equal("slot-bet:slot-glek", x.Value));
        Assert.Empty(k.Bank.Pending());
    }

    [Fact]
    public void Illegal_bets_change_nothing()
    {
        var k = new SlotsKit(wallet: 30);
        var before = k.View.ToString();
        Assert.False(k.Spin(15).Ok);                       // нема в наборі
        Assert.False(k.H.Act(0, "spin", new { }).Ok);      // без ставки
        Assert.False(k.Spin(50).Ok);                       // більше за гаманець
        Assert.False(k.H.Act(0, "dance").Ok);
        Assert.Empty(k.Stakes.Ledger);
        Assert.Equal(before, k.View.ToString());
    }

    [Fact]
    public void MaxBet_and_custom_bets_limit_the_set()
    {
        var k = new SlotsKit(opts: o => { o.Bets = [5, 10, 1000]; o.MaxBet = 100; });
        Assert.Equal([5, 10], k.View.GetProperty("bets").EnumerateArray().Select(x => x.GetInt32()));
        Assert.False(k.Spin(1000).Ok);
        Assert.True(k.Spin(5).Ok);
    }

    [Fact]
    public void Disabled_machines_refuse_with_a_break()
    {
        var k = new SlotsKit();
        k.Options.Enabled = false;
        var r = k.Spin();
        Assert.False(r.Ok);
        Assert.Equal(SlotGame.OffText, r.Message);
        Assert.False(k.View.GetProperty("on").GetBoolean());
        Assert.Empty(k.Stakes.Ledger);
    }

    [Fact]
    public void Repeated_request_with_a_stale_seq_does_not_spin_again()
    {
        var k = new SlotsKit();
        Assert.True(k.Spin(10, seq: 0).Ok);
        Assert.True(k.Spin(10, seq: 0).Ok);   // той самий запит удруге — уже пішов
        Assert.Equal(1, k.View.GetProperty("last").GetProperty("seq").GetInt32());
        Assert.Single(k.Stakes.Ledger, m => m.Ref.EndsWith(":bet"));
        Assert.True(k.Spin(10, seq: 1).Ok);
        Assert.Equal(2, k.View.GetProperty("last").GetProperty("seq").GetInt32());
    }

    [Fact]
    public void Exception_after_the_charge_keeps_the_win_by_the_ledger()
    {
        // Рецензія m9: Take на виняток давав false, гра робила Forget — а списання таки пройшло, і виграш губився.
        var k = new SlotsKit();
        var win = SlotsKit.StopsWhere((_, u) => u == 10);
        var rng = k.Rig();
        rng.Stops(win);
        k.Stakes.ThrowAfterSpend = true;
        Assert.True(k.Spin(10).Ok, k.H.Reply.Message);        // звірились із леджером — оберт пройшов
        Assert.Equal(10_000 - 10 + 20, k.Stakes.Balance("Оля"));
        Assert.Empty(k.Bank.Pending());

        rng.Stops(win);
        k.Stakes.ThrowBeforeSpend = true;
        Assert.False(k.Spin(10).Ok);                          // не списалось — і не грали
        Assert.Equal(10_000 + 10, k.Stakes.Balance("Оля"));
        Assert.Empty(k.Bank.Pending());
        Assert.True(k.Spin(10).Ok);                           // наступний оберт — як завжди
    }

    [Fact]
    public void Gamble_win_doubles_and_lose_burns()
    {
        var k = new SlotsKit();
        var win = SlotsKit.StopsWhere((_, u) => u == 10);    // три вишні на одній лінії: 10 × ставка/5
        var rng = k.Rig();
        rng.Stops(win).Card("r");
        Assert.True(k.Spin(10).Ok);
        var g = k.View.GetProperty("gamble");
        Assert.True(g.GetProperty("open").GetBoolean());
        Assert.Equal(20, g.GetProperty("amount").GetInt32());
        var wallet = k.Stakes.Balance("Оля");

        Assert.True(k.H.Act(0, "gamble", new { pick = "r" }).Ok);
        g = k.View.GetProperty("gamble");
        Assert.True(g.GetProperty("ok").GetBoolean());
        Assert.Equal(40, g.GetProperty("amount").GetInt32());
        Assert.Equal(1, g.GetProperty("steps").GetInt32());
        Assert.Equal(wallet + 20, k.Stakes.Balance("Оля"));

        rng.Card("b");
        Assert.True(k.H.Act(0, "gamble", new { pick = "r" }).Ok);
        g = k.View.GetProperty("gamble");
        Assert.False(g.GetProperty("ok").GetBoolean());
        Assert.False(g.GetProperty("open").GetBoolean());
        Assert.Equal(0, g.GetProperty("amount").GetInt32());
        Assert.Equal(wallet + 20 - 40, k.Stakes.Balance("Оля"));
        Assert.False(k.H.Act(0, "gamble", new { pick = "r" }).Ok);   // згоріло — Ворожка закрита
        Assert.Empty(k.Bank.Pending());
    }

    [Fact]
    public void Gamble_stops_after_five_and_gives_the_achievement()
    {
        var k = new SlotsKit(wallet: 1000);
        var rng = k.Rig();
        rng.Stops(SlotsKit.StopsWhere((_, u) => u == 10));
        Assert.True(k.Spin(10).Ok);
        for (var i = 0; i < 5; i++)
        {
            rng.Card("b");
            Assert.True(k.H.Act(0, "gamble", new { pick = "b" }).Ok);
        }
        var g = k.View.GetProperty("gamble");
        Assert.Equal(5, g.GetProperty("steps").GetInt32());
        Assert.Equal(20 * 32, g.GetProperty("amount").GetInt32());
        Assert.False(g.GetProperty("open").GetBoolean());
        Assert.False(k.H.Act(0, "gamble", new { pick = "b" }).Ok);
        Assert.Equal(1000 - 10 + 20 * 32, k.Stakes.Balance("Оля"));
        Assert.Contains(k.H.Awards, a => a.Reason == "ach:slot-vorozhka");
    }

    [Fact]
    public void Gamble_is_closed_by_collect_and_by_a_new_spin_and_needs_a_pick()
    {
        var k = new SlotsKit();
        var rng = k.Rig();
        var win = SlotsKit.StopsWhere((_, u) => u == 10);
        var lose = SlotsKit.StopsWhere((_, u) => u == 0);
        rng.Stops(win);
        Assert.True(k.Spin(10).Ok);
        Assert.False(k.H.Act(0, "gamble", new { pick = "x" }).Ok);
        Assert.True(k.H.Act(0, "collect").Ok);
        Assert.Equal(JsonValueKind.Null, k.View.GetProperty("gamble").ValueKind);
        Assert.False(k.H.Act(0, "gamble", new { pick = "r" }).Ok);

        rng.Stops(win);
        Assert.True(k.Spin(10).Ok);
        rng.Stops(lose);
        Assert.True(k.Spin(10).Ok);
        Assert.Equal(JsonValueKind.Null, k.View.GetProperty("gamble").ValueKind);
        Assert.False(k.H.Act(0, "gamble", new { pick = "r" }).Ok);
    }

    [Fact]
    public void Script_matches_the_mock_shape()
    {
        var k = new SlotsKit();
        var rng = k.Rig();
        var stops = SlotsKit.StopsWhere((s, u) => u > 0 && SlotGlekMath.At(s, 0, 1) == "seven" && SlotGlekMath.At(s, 1, 1) == "seven");
        rng.Stops(stops);
        Assert.True(k.Spin(50).Ok);
        var v = k.View;
        foreach (var key in new[] { "on", "bet", "bets", "maxBet", "balance", "jackpot", "last", "gamble", "best", "spins", "table" })
            Assert.True(v.TryGetProperty(key, out _), key);
        var s = v.GetProperty("last").GetProperty("script");
        Assert.Equal(50, s.GetProperty("bet").GetInt32());
        Assert.Equal(JsonValueKind.Object, s.GetProperty("state").ValueKind);
        Assert.Equal(JsonValueKind.False, s.GetProperty("glek3").ValueKind);
        var steps = s.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal("spin", steps[0].GetProperty("t").GetString());
        Assert.Equal(stops, steps[0].GetProperty("stops").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal([2], steps[0].GetProperty("tease").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal("win", steps[1].GetProperty("t").GetString());
        var item = steps[1].GetProperty("items")[0];
        foreach (var key in new[] { "line", "cells", "sym", "amount" }) Assert.True(item.TryGetProperty(key, out _), key);
        Assert.Equal(s.GetProperty("win").GetInt32(), steps[1].GetProperty("amount").GetInt32());
        Assert.Equal(3, v.GetProperty("table").GetProperty("reels").GetArrayLength());
    }

    [Fact]
    public void Same_seed_same_spins()
    {
        static string Run()
        {
            var k = new SlotsKit(seed: 7, wallet: 100_000);
            var all = new List<string>();
            for (var i = 0; i < 40; i++) { k.Spin(10); all.Add(k.View.GetProperty("last").GetProperty("script").ToString()); }
            return string.Join("\n", all);
        }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void Save_and_load_keep_the_machine_but_not_the_epoch()
    {
        var k = new SlotsKit(wallet: 100_000);
        for (var i = 0; i < 5; i++) k.Spin(10);
        var json = k.G.Save()!;
        var epoch = k.G.State.Epoch;
        var other = new SlotGlek();
        other.Load(json);
        Assert.Equal(5, other.State.Seq);
        Assert.Equal(SlotGame.FieldOnly(k.G.State.Last)!.ToJsonString(), other.State.Last!.ToJsonString());   // лише поле
        Assert.Equal(k.G.State.Spins, other.State.Spins);
        Assert.NotEqual(epoch, other.State.Epoch);
        Assert.Equal(json, other.Save());
    }

    [Fact]
    public void Save_keeps_only_the_field_of_the_last_spin_and_the_gamble()
    {
        // Рецензія m8: у збереження йшов цілий сценарій (у кластері до 45 КБ) — клієнтові ж після перезавантаження
        // потрібне лише поле, щоб поставити барабани; старий оберт він не програє, а Ворожка відновлюється з gamble.
        var k = new SlotsKit();
        var rng = k.Rig();
        rng.Stops(SlotsKit.StopsWhere((_, u) => u == 10));
        Assert.True(k.Spin(10).Ok);
        Assert.True(k.View.GetProperty("last").GetProperty("script").GetProperty("steps").GetArrayLength() > 1);
        var json = k.G.Save()!;
        var other = new SlotGlek();
        other.Load(json);
        var steps = other.State.Last!["steps"]!.AsArray();
        Assert.Single(steps);
        Assert.Equal("spin", steps[0]!["t"]!.GetValue<string>());
        Assert.Equal(k.G.State.Last!["win"]!.ToJsonString(), other.State.Last!["win"]!.ToJsonString());
        Assert.Equal(1, other.State.Seq);
        Assert.True(other.State.Gamble!.Open);
        Assert.Equal(20, other.State.Gamble.Amount);
        Assert.True(k.View.GetProperty("last").GetProperty("script").GetProperty("steps").GetArrayLength() > 1);   // у пам'яті — цілий
    }

    [Fact]
    public void Big_win_scores_awards_feeds_and_chats()
    {
        var k = new SlotsKit(wallet: 1000);
        var rng = k.Rig();
        rng.Stops(SlotsKit.StopsWhere((_, u) => u >= 600));   // три Глеки на лінії — ≥ 120×
        Assert.True(k.Spin(10).Ok);
        var mult = k.View.GetProperty("last").GetProperty("script").GetProperty("win").GetInt32() / 10.0;
        Assert.True(mult >= 120);
        Assert.True(k.View.GetProperty("last").GetProperty("script").GetProperty("glek3").GetBoolean());
        Assert.Contains(k.H.Scores, s => s.Score == mult);
        Assert.Contains(k.H.Awards, a => a.Reason == "ach:slot-big");
        Assert.Contains(k.H.Awards, a => a.Reason == "ach:slot-epic");
        Assert.Single(k.Bank.Week());
        Assert.Single(k.Wire.Chats);
        Assert.Empty(k.Wire.Airs);
    }

    [Fact]
    public void Jackpot_is_paid_reset_and_announced()
    {
        var k = new SlotsKit(wallet: 10_000, opts: o => o.JackpotMustHit = 0);
        for (var i = 0; i < 10; i++) k.Spin(100);
        Assert.Equal(10_000 + 10, k.Bank.Pot);   // по 1 % з десяти ставок по 100 (без межі — увесь внесок у суму)
        var rng = k.Rig();
        rng.Stops(SlotsKit.StopsWhere((_, u) => u == 0)).Jackpot();
        var before = k.Stakes.Balance("Оля");
        Assert.True(k.Spin(100).Ok);
        var s = k.View.GetProperty("last").GetProperty("script");
        Assert.Equal(10_011, s.GetProperty("jackpot").GetInt32());
        var last = s.GetProperty("steps").EnumerateArray().Last();
        Assert.Equal("jackpot", last.GetProperty("t").GetString());
        Assert.Equal(before - 100 + 10_011, k.Stakes.Balance("Оля"));
        Assert.Equal("slot-jackpot:slot-glek", k.Stakes.Reasons.Single(x => x.Key.EndsWith(":jp")).Value);
        Assert.Equal(10_000, k.Bank.Pot);
        Assert.Contains(k.H.Awards, a => a.Reason == "ach:slot-jackpot");
        Assert.Single(k.Wire.Chats);
        Assert.Single(k.Wire.Airs);
        Assert.True(k.Bank.Week()[0].Jackpot > 0);
        // сума пережила «перезапуск»
        k.Bank.FlushNow();
        var again = new SlotsBank(k.Stakes, k.Store, () => k.Options, k.Clock);
        Assert.Equal(10_000, again.Pot);
    }

    [Fact]
    public void Recovery_pays_a_spin_cut_between_bet_and_win_once()
    {
        var stakes = new FakeStakes().Set("Оля", 100);
        var store = new FakeStore();
        var clock = new FakeClock();
        var opts = new SlotsOptions();
        var bank = new SlotsBank(stakes, store, () => opts, clock, defer: a => a());
        var paid = new SlotPending("slot:r1-e:1", "Оля", "slot-glek", 10, 40, 0, clock.UtcNow);
        var cut = new SlotPending("slot:r1-e:2", "Оля", "slot-glek", 10, 80, 0, clock.UtcNow);
        Assert.True(bank.Open(paid));
        Assert.True(bank.Take("Оля", 10, "slot-bet:slot-glek", paid.Ref + ":bet"));
        Assert.True(bank.Open(cut));   // «упало» до списання
        // процес упав: нова каса з того ж сховища
        clock.Advance(1);
        var after = new SlotsBank(stakes, store, () => opts, clock, defer: a => a());
        Assert.Equal(2, after.Pending().Count);
        Assert.Equal(2, after.Recover(clock.UtcNow));
        Assert.Equal(100 - 10 + 40, stakes.Balance("Оля"));
        Assert.Empty(after.Pending());
        // ще раз той самий запис (скажімо, гра встигла заплатити сама) — ключі не дадуть двічі
        Assert.True(after.Open(paid));
        after.Recover(clock.UtcNow.AddSeconds(1));
        Assert.Equal(130, stakes.Balance("Оля"));
        var third = new SlotsBank(stakes, store, () => opts, clock);
        Assert.Empty(third.Pending());
    }

    [Fact]
    public void Feed_view_lists_week_wins_with_jackpots_first()
    {
        var clock = new FakeClock();
        var bank = new SlotsBank(new FakeStakes(), new FakeStore(), () => new SlotsOptions(), clock, defer: a => a());
        bank.Brag("Стара", "slot-glek", "Однорукий Глек", 10, 900, 0);
        clock.Advance(TimeSpan.FromDays(8));
        bank.Brag("Оля", "slot-glek", "Однорукий Глек", 10, 300, 0);
        bank.Brag("Петро", "slot-glek", "Однорукий Глек", 10, 0, 12_000);
        bank.Brag("Дрібно", "slot-glek", "Однорукий Глек", 10, 150, 0);   // 15× — не занос
        var view = JsonSerializer.SerializeToElement(bank.FeedView(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(view.GetProperty("on").GetBoolean());
        Assert.Equal(10_000, view.GetProperty("jackpot").GetInt32());
        var wins = view.GetProperty("wins").EnumerateArray().Select(w => w.GetProperty("nick").GetString()).ToList();
        Assert.Equal(["Петро", "Оля"], wins);
    }
}
