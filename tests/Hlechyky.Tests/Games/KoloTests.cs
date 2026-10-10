using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Обв'язка колеса: свої ставки й сховище для каси (черга — одразу), кімната через справжній каркас.</summary>
public sealed class KoloKit
{
    public FakeStakes Stakes { get; } = new();
    public FakeStore Store { get; } = new();
    public KoloOptions Options { get; } = new();
    public KoloBook Book { get; }
    public RoomHarness H { get; }

    public KoloKit(params (string Nick, int Wallet)[] people) : this(null, null, people) { }

    public KoloKit(Func<FakeStore, IGameStore>? store, Action<Action>? defer, params (string Nick, int Wallet)[] people)
    {
        foreach (var (nick, wallet) in people) Stakes.Set(nick, wallet);
        H = null!;
        Book = new KoloBook(Stakes, store?.Invoke(Store) ?? Store, defer: defer ?? (a => a()), options: new FixedOptions<KoloOptions>(Options),
            held: (table, round) => KoloBook.HeldBy(H.Rooms, table, round));
        H = new RoomHarness("kolo", services: RoomHarness.WithService(Book), seed: 11);
        foreach (var (nick, _) in people)
        {
            var reply = H.Join(nick);
            Assert.True(reply.Ok, reply.Message);
        }
    }

    public Kolo G => (Kolo)H.Room.Game;
    public KoloState S => G.State;

    /// <summary>Множник цього й наступних раундів (перший сегмент із ним).</summary>
    public void Rig(int x)
    {
        var seg = KoloCore.FirstSeg(x);
        G.Rig = () => seg;
        if (S.Phase == Kolo.Bets) S.Seg = seg;
    }

    public ActResult Bet(int seat, int pick, int amount) => H.Act(seat, "bet", new { pick, amount });

    public void To(string phase, int max = 2000)
    {
        for (var i = 0; i < max && S.Phase != phase; i++) H.Tick();
        Assert.Equal(phase, S.Phase);
    }

    public JsonElement View(int? seat) => H.View(seat);
    public List<string> Grants => [.. Stakes.Calls.Where(c => c.StartsWith("grant:", StringComparison.Ordinal))];
    public List<string> Spends => [.. Stakes.Calls.Where(c => c.StartsWith("spend:", StringComparison.Ordinal))];
    public List<string> Achievements(string nick) =>
        [.. H.Awards.Where(a => a.Nick == nick && a.Reason.StartsWith("ach:kolo-", StringComparison.Ordinal)).Select(a => a.Reason)];
}

/// <summary>Сховище, що кидає на запис (каса не може записати раунд).</summary>
sealed class KoloBrokenStore(FakeStore inner) : IGameStore
{
    public bool Broken { get; set; } = true;
    public void SaveState(string key, string json)
    {
        if (Broken) throw new IOException("диск зайнятий");
        inner.SaveState(key, json);
    }
    public string? LoadState(string key) => inner.LoadState(key);
    public void DeleteState(string key) => inner.DeleteState(key);
}

public class KoloTests
{
    static KoloKit Two() => new(("Оля", 1000), ("Петро", 1000));

    // ---------- колесо й математика ----------

    [Fact]
    public void Layout_has_exact_counts_and_no_equal_neighbours()
    {
        var w = KoloCore.Wheel;
        Assert.Equal(125, w.Length);
        Assert.Equal(KoloCore.Crack, w[0]);
        Assert.Equal(60, w.Count(x => x == 2));
        Assert.Equal(40, w.Count(x => x == 3));
        Assert.Equal(20, w.Count(x => x == 6));
        Assert.Equal(4, w.Count(x => x == 30));
        Assert.Equal(1, w.Count(x => x == 0));
        for (var i = 0; i < w.Length; i++) Assert.NotEqual(w[i], w[(i + 1) % w.Length]);
        // глеки по колу майже через чверть
        var g = Enumerable.Range(0, w.Length).Where(i => w[i] == 30).ToList();
        for (var i = 0; i < g.Count; i++) Assert.InRange((g[(i + 1) % g.Count] - g[i] + 125) % 125, 25, 37);
        Assert.Equal([2, 3, 6, 30], KoloCore.Picks);
    }

    [Fact]
    public void Seed_to_segment_formula_is_fixed()
    {
        string Seed(string head) => head + new string('0', 64 - head.Length);
        Assert.Equal(0, KoloCore.Seg(Seed("0000000000000")));
        Assert.Equal(1, KoloCore.Seg(Seed("0000000000001")));
        Assert.Equal(124, KoloCore.Seg(Seed("000000000007c")));             // 124
        Assert.Equal(0, KoloCore.Seg(Seed("000000000007d")));               // 125 mod 125
        Assert.Equal((int)(0xfffffffffffffL % 125), KoloCore.Seg(Seed("fffffffffffff")));
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", KoloCore.Hash("abc"));
        var a = KoloCore.NewSeed(new Random(5));
        Assert.Equal(a, KoloCore.NewSeed(new Random(5)));
        Assert.Equal(64, a.Length);
        Assert.Throws<ArgumentException>(() => KoloCore.Seg("abc"));
    }

    [Fact]
    public void Return_pays_only_the_pick_that_landed()
    {
        Assert.Equal(200, KoloCore.Return(2, 100, 2));
        Assert.Equal(0, KoloCore.Return(2, 100, 3));
        Assert.Equal(3000, KoloCore.Return(30, 100, 30));
        Assert.Equal(0, KoloCore.Return(30, 100, KoloCore.Crack));
        Assert.Equal(600, KoloCore.Return([(2, 100), (6, 100), (30, 10)], 6));
        Assert.Equal(0, KoloCore.Return([(2, 100), (3, 100), (6, 100), (30, 100)], KoloCore.Crack));
        Assert.Equal((long)KoloCore.MaxPerPick * 30, KoloCore.Return(30, KoloCore.MaxPerPick, 30));
    }

    // ---------- розклад ----------

    [Fact]
    public void Phases_follow_the_schedule()
    {
        var k = Two();
        k.Rig(3);
        var t0 = k.H.Clock.UtcNow;
        Assert.Equal(Kolo.Bets, k.S.Phase);
        Assert.Equal(1, k.S.Round);
        Assert.Equal(t0.AddMilliseconds(Kolo.BetMs), k.S.Until);
        var v = k.View(0);
        Assert.Equal(10_000, v.GetProperty("phaseMs").GetInt32());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("spin").ValueKind);
        k.H.Tick(39);
        Assert.Equal(Kolo.Bets, k.S.Phase);
        k.H.Tick();
        Assert.Equal(Kolo.Spin, k.S.Phase);
        Assert.Equal(5_000, k.View(0).GetProperty("spin").GetProperty("leftMs").GetInt32());
        k.H.Tick(19);
        Assert.Equal(Kolo.Spin, k.S.Phase);
        k.H.Tick();
        Assert.Equal(Kolo.Result, k.S.Phase);
        k.H.Tick(11);
        Assert.Equal(Kolo.Result, k.S.Phase);
        k.H.Tick();
        Assert.Equal(Kolo.Bets, k.S.Phase);
        Assert.Equal(2, k.S.Round);
        var h = k.View(null).GetProperty("history")[0];
        Assert.Equal(3, h.GetProperty("x").GetInt32());
        Assert.Equal(1, h.GetProperty("round").GetInt32());
    }

    [Fact]
    public void Empty_rounds_keep_spinning_sigh_once_and_history_caps_at_20()
    {
        var k = new KoloKit(("Оля", 1000));
        k.Rig(2);
        k.To(Kolo.Spin);
        Assert.Equal("sigh", k.S.Glek.Mood);
        for (var i = 0; i < 22; i++)
        {
            k.To(Kolo.Result);
            k.To(Kolo.Bets);
            k.To(Kolo.Spin);
            Assert.NotEqual("sigh", k.S.Glek.Mood);
        }
        Assert.Equal(20, k.View(0).GetProperty("history").GetArrayLength());
        Assert.Empty(k.Stakes.Calls);
        Assert.Empty(k.Book.Pending());
    }

    [Fact]
    public void Hurry_cue_comes_once_in_the_last_3_seconds_when_someone_bet()
    {
        var k = Two();
        Assert.True(k.Bet(0, 2, 10).Ok);
        k.H.Tick(27);
        Assert.Equal("idle", k.S.Glek.Mood);
        k.H.Tick(2);
        Assert.Equal("hurry", k.S.Glek.Mood);
        var seq = k.S.Glek.Seq;
        k.H.Tick(5);
        Assert.Equal(seq, k.S.Glek.Seq);
    }

    // ---------- ставки ----------

    [Fact]
    public void Bets_on_several_picks_add_up_and_show_in_the_view()
    {
        var k = Two();
        Assert.True(k.Bet(0, 2, 100).Ok);
        Assert.True(k.Bet(0, 30, 10).Ok);
        Assert.True(k.Bet(0, 2, 50).Ok);
        Assert.True(k.Bet(1, 2, 20).Ok);
        var v = k.View(0);
        var me = v.GetProperty("me");
        Assert.Equal(840, me.GetProperty("free").GetInt32());
        Assert.Equal(160, me.GetProperty("onTable").GetInt32());
        Assert.True(me.GetProperty("canClear").GetBoolean());
        var olya = v.GetProperty("players").EnumerateArray().Single(p => p.GetProperty("nick").GetString() == "Оля");
        Assert.True(olya.GetProperty("mine").GetBoolean());
        Assert.Equal(160, olya.GetProperty("total").GetInt32());
        var bets = olya.GetProperty("bets").EnumerateArray().Select(b => (b.GetProperty("pick").GetInt32(), b.GetProperty("amount").GetInt32())).ToList();
        Assert.Equal([(2, 150), (30, 10)], bets);
        var totals = v.GetProperty("totals").EnumerateArray().ToDictionary(t => t.GetProperty("pick").GetInt32());
        Assert.Equal(170, totals[2].GetProperty("amount").GetInt32());
        Assert.Equal(2, totals[2].GetProperty("people").GetInt32());
        Assert.Equal(10, totals[30].GetProperty("amount").GetInt32());
        Assert.Equal(0, totals[3].GetProperty("amount").GetInt32());
        Assert.Equal(180, v.GetProperty("onTable").GetInt32());
        // глядач бачить усі ставки, але без «me»
        var w = k.View(null);
        Assert.Equal(JsonValueKind.Null, w.GetProperty("me").ValueKind);
        Assert.All(w.GetProperty("players").EnumerateArray(), p => Assert.False(p.GetProperty("mine").GetBoolean()));
    }

    [Fact]
    public void Bet_errors_have_exact_texts_and_change_nothing()
    {
        var k = Two();
        Assert.True(k.Bet(0, 2, 100).Ok);
        var before = k.View(0).GetRawText();
        void No(ActResult r, string text)
        {
            Assert.False(r.Ok);
            Assert.Equal(text, r.Message);
            Assert.Equal(before, k.View(0).GetRawText());
        }
        No(k.H.Act(0, "spin"), "Тут так не ходять");
        No(k.H.Act(0, "bet", "так"), "Не зрозумів ставки");
        No(k.Bet(0, 5, 100), "Такого множника на колесі нема");
        No(k.H.Act(0, "bet", new { pick = 2 }), "Ставка — ціле число черепків");
        No(k.H.Act(0, "bet", new { pick = 2, amount = "сто" }), "Ставка — ціле число черепків");
        No(k.Bet(0, 2, 0), "Ставка — ціле число черепків");
        No(k.Bet(0, 2, 5), "Найменша ставка — 10 🏺");
        No(k.Bet(0, 2, 1901), "На один множник — щонайбільше 2000 🏺");
        No(k.Bet(0, 3, 901), "Бракує черепків: вільних 900");
        No(k.H.Act(1, "clear"), "Нема чого знімати");
        No(k.H.Act(1, "repeat"), "Минулого разу ставок не було — нема чого повторювати");
        k.To(Kolo.Spin);
        before = k.View(0).GetRawText();
        No(k.Bet(0, 2, 10), "Ставки зроблено — чекай наступного кола");
        No(k.H.Act(0, "clear"), "Ставки зроблено — чекай наступного кола");
        k.To(Kolo.Result);
        before = k.View(0).GetRawText();
        No(k.Bet(0, 2, 10), "Глек рахує — ставки за мить");
    }

    [Fact]
    public void A_bet_after_the_deadline_before_the_tick_is_refused()
    {
        var k = Two();
        k.H.Clock.Advance(TimeSpan.FromMilliseconds(Kolo.BetMs));
        var r = k.Bet(0, 2, 10);
        Assert.False(r.Ok);
        Assert.Equal("Ставки зроблено — чекай наступного кола", r.Message);
    }

    [Fact]
    public void Huge_bets_are_refused_before_the_payout_could_overflow()
    {
        var k = new KoloKit(("Оля", int.MaxValue));
        k.Options.MaxBet = 0;
        var r = k.Bet(0, 2, KoloCore.MaxPerPick + 1);
        Assert.False(r.Ok);
        Assert.Equal("Завелика ставка — каса стільки за раз не виплатить", r.Message);
        Assert.True(k.Bet(0, 30, KoloCore.MaxPerPick).Ok);
        Assert.False(k.Bet(0, 30, 10).Ok);
    }

    [Fact]
    public void Clear_and_repeat()
    {
        var k = Two();
        k.Rig(6);
        Assert.True(k.Bet(0, 6, 100).Ok);
        Assert.True(k.Bet(0, 30, 20).Ok);
        Assert.True(k.Bet(1, 6, 50).Ok);
        Assert.True(k.H.Act(0, "clear").Ok);
        Assert.Equal(1000, k.View(0).GetProperty("me").GetProperty("free").GetInt32());
        Assert.Single(k.S.Bets);   // Петрові — не чіпає
        Assert.True(k.Bet(0, 6, 100).Ok);
        Assert.True(k.Bet(0, 30, 20).Ok);
        k.To(Kolo.Result);
        Assert.Equal(1000 - 120 + 600, k.Stakes.Balance("Оля"));
        k.To(Kolo.Bets);
        var me = k.View(0).GetProperty("me");
        Assert.True(me.GetProperty("canRepeat").GetBoolean());
        Assert.Equal(120, me.GetProperty("repeatCost").GetInt32());
        Assert.True(k.H.Act(0, "repeat").Ok);
        Assert.True(k.H.Act(0, "repeat").Ok);   // поверх поточних
        Assert.Equal(200, k.S.Bets.Single(b => b.Nick == "Оля").On(6));
        Assert.Equal(40, k.S.Bets.Single(b => b.Nick == "Оля").On(30));
        k.Stakes.Set("Петро", 40);
        var r = k.H.Act(1, "repeat");
        Assert.False(r.Ok);
        Assert.Equal("Бракує черепків на повтор: треба 50, вільних 40", r.Message);
    }

    // ---------- гроші ----------

    [Fact]
    public void Close_debits_once_per_player_with_the_round_ref()
    {
        var k = Two();
        k.Rig(2);
        Assert.True(k.Bet(0, 2, 100).Ok);
        Assert.True(k.Bet(0, 3, 50).Ok);
        Assert.True(k.Bet(1, 30, 10).Ok);
        Assert.Empty(k.Stakes.Calls);   // поки йдуть ставки, гаманець не чіпаємо
        k.To(Kolo.Spin);
        var room = k.H.RoomId;
        Assert.Equal([$"spend:Оля:150:kolo-bet:{room}:{k.S.Epoch}:1:оля", $"spend:Петро:10:kolo-bet:{room}:{k.S.Epoch}:1:петро"], k.Spends);
        Assert.Equal("kolo-bet:kolo", k.Stakes.Reasons[$"kolo-bet:{room}:{k.S.Epoch}:1:оля"]);
        Assert.Empty(k.Grants);   // виграш — коли колесо стало, не раніше
        var pending = Assert.Single(k.Book.Pending());
        Assert.Equal(2, pending.X);
        Assert.Equal(200, pending.Pays.Single(p => p.Nick == "Оля").Return);
        Assert.Equal(0, pending.Pays.Single(p => p.Nick == "Петро").Return);
        k.To(Kolo.Result);
        Assert.Equal([$"grant:Оля:200:kolo-win:{room}:{k.S.Epoch}:1:оля"], k.Grants);
        Assert.Equal("kolo-win:kolo", k.Stakes.Reasons[$"kolo-win:{room}:{k.S.Epoch}:1:оля"]);
        Assert.Equal(1050, k.Stakes.Balance("Оля"));
        Assert.Equal(990, k.Stakes.Balance("Петро"));
        Assert.Empty(k.Book.Pending());
        var last = k.View(0).GetProperty("last");
        Assert.Equal(2, last.GetProperty("x").GetInt32());
        Assert.Equal(160, last.GetProperty("staked").GetInt32());
        Assert.Equal(200, last.GetProperty("paid").GetInt32());
        var top = last.GetProperty("results")[0];
        Assert.Equal("Оля", top.GetProperty("nick").GetString());
        Assert.Equal(50, top.GetProperty("net").GetInt32());
    }

    [Fact]
    public void Thirty_pays_thirty_times_and_crack_takes_everything()
    {
        var k = Two();
        k.Rig(30);
        Assert.True(k.Bet(0, 30, 20).Ok);
        Assert.True(k.Bet(0, 2, 100).Ok);
        k.To(Kolo.Result);
        Assert.Equal(1000 - 120 + 600, k.Stakes.Balance("Оля"));
        Assert.Equal("dance", k.S.Glek.Mood);
        Assert.Equal("Оля", k.View(0).GetProperty("last").GetProperty("big").GetString());

        k.Rig(KoloCore.Crack);
        k.To(Kolo.Bets);
        k.Rig(KoloCore.Crack);
        foreach (var p in KoloCore.Picks) Assert.True(k.Bet(1, p, 50).Ok);
        k.To(Kolo.Result);
        Assert.Equal(800, k.Stakes.Balance("Петро"));
        Assert.Equal("laugh", k.S.Glek.Mood);
        Assert.Equal(0, k.View(1).GetProperty("spin").GetProperty("x").GetInt32());
        Assert.Single(k.Grants);
    }

    [Fact]
    public void Debit_failure_at_close_drops_that_players_bets_with_a_note()
    {
        var k = Two();
        k.Rig(2);
        Assert.True(k.Bet(0, 2, 500).Ok);
        Assert.True(k.Bet(1, 2, 100).Ok);
        k.Stakes.Set("Оля", 100);   // гаманець спорожнів деінде (Лавка, слоти)
        k.To(Kolo.Spin);
        Assert.Single(k.S.Bets);
        Assert.Equal("Черепків не стало — твої ставки (500) знято", k.View(0).GetProperty("me").GetProperty("note").GetString());
        k.To(Kolo.Result);
        Assert.Equal(100, k.Stakes.Balance("Оля"));
        Assert.Equal(1100, k.Stakes.Balance("Петро"));
        Assert.Single(k.Grants);
    }

    [Fact]
    public void Store_failure_spins_without_money()
    {
        KoloBrokenStore? broken = null;
        var k = new KoloKit(s => broken = new KoloBrokenStore(s), null, ("Оля", 1000));
        k.Rig(2);
        Assert.True(k.Bet(0, 2, 100).Ok);
        k.To(Kolo.Spin);
        Assert.Empty(k.Stakes.Calls);
        Assert.Empty(k.S.Bets);
        Assert.Equal("Каса заїла — ставки не взято, черепки цілі", k.View(0).GetProperty("me").GetProperty("note").GetString());
        Assert.Equal(KoloLines.BookStuck, k.S.Glek.Say);
        k.To(Kolo.Result);
        Assert.Equal(1000, k.Stakes.Balance("Оля"));
        broken!.Broken = false;
        k.To(Kolo.Bets);
        Assert.True(k.Bet(0, 2, 100).Ok);
        k.To(Kolo.Result);
        Assert.Equal(1100, k.Stakes.Balance("Оля"));
    }

    [Fact]
    public void Book_queue_answers_later_and_the_tick_does_not_wait()
    {
        var queue = new List<Action>();
        var k = new KoloKit(null, a => queue.Add(a), ("Оля", 1000));
        k.Rig(3);
        Assert.True(k.Bet(0, 3, 100).Ok);
        k.To(Kolo.Spin);
        Assert.Empty(k.Stakes.Calls);   // тік не ходив у базу
        foreach (var a in queue.ToList()) a();
        queue.Clear();
        Assert.Single(k.Spends);
        k.To(Kolo.Result);
        Assert.Empty(k.Grants);
        foreach (var a in queue.ToList()) a();
        Assert.Equal(1200, k.Stakes.Balance("Оля"));
    }

    [Fact]
    public void Settling_twice_pays_once()
    {
        var k = Two();
        k.Rig(6);
        Assert.True(k.Bet(0, 6, 100).Ok);
        Assert.True(k.Bet(1, 3, 100).Ok);
        k.To(Kolo.Spin);
        var pending = k.S.Pending!;
        k.To(Kolo.Result);
        Assert.Equal(1500, k.Stakes.Balance("Оля"));
        k.Book.Settle(pending);
        k.Book.Open(pending);
        Assert.Equal(1, k.Book.Recover(DateTimeOffset.MaxValue));
        Assert.Equal(1500, k.Stakes.Balance("Оля"));
        Assert.Equal(900, k.Stakes.Balance("Петро"));
        Assert.Single(k.Grants);
    }

    [Fact]
    public void Leaving_keeps_the_bets_and_they_settle()
    {
        var k = Two();
        k.Rig(3);
        Assert.True(k.Bet(1, 3, 100).Ok);
        k.H.Leave("Петро");
        var gone = k.View(0).GetProperty("players").EnumerateArray().Single(p => p.GetProperty("nick").GetString() == "Петро");
        Assert.False(gone.GetProperty("here").GetBoolean());
        Assert.Equal(JsonValueKind.Null, gone.GetProperty("seat").ValueKind);
        k.To(Kolo.Result);
        Assert.Equal(1200, k.Stakes.Balance("Петро"));
        Assert.Empty(k.Achievements("Петро"));
        k.To(Kolo.Bets);
        Assert.DoesNotContain(k.View(0).GetProperty("players").EnumerateArray(), p => p.GetProperty("nick").GetString() == "Петро");
    }

    // ---------- чесно наперед ----------

    [Fact]
    public void Hash_comes_before_the_spin_and_seed_after_it_and_both_check_out()
    {
        var k = Two();
        Assert.True(k.Bet(0, 2, 100).Ok);
        var seed = k.S.Seed;
        var seg = k.S.Seg;
        Assert.Equal(KoloCore.Seg(seed), seg);   // без Rig — сегмент саме з seed
        foreach (var v in new[] { k.View(0), k.View(null) })
        {
            Assert.Equal(JsonValueKind.Null, v.GetProperty("seed").ValueKind);
            Assert.Equal(JsonValueKind.Null, v.GetProperty("spin").ValueKind);
            Assert.DoesNotContain(seed, v.GetRawText());
            Assert.Equal(k.S.Hash, v.GetProperty("hash").GetString());
        }
        var hash = k.S.Hash;
        k.To(Kolo.Spin);
        var s = k.View(null);
        Assert.Equal(JsonValueKind.Null, s.GetProperty("seed").ValueKind);   // seed — коли колесо стало
        Assert.Equal(seg, s.GetProperty("spin").GetProperty("seg").GetInt32());
        k.To(Kolo.Result);
        var r = k.View(null);
        Assert.Equal(seed, r.GetProperty("seed").GetString());
        Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed))));
        Assert.Equal(KoloCore.Wheel[KoloCore.Seg(seed)], r.GetProperty("spin").GetProperty("x").GetInt32());
        var wheel = r.GetProperty("wheel").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal(KoloCore.Wheel, wheel);
        // і в наступному раунді: last тримає відбиток і seed минулого — «ⓘ» перевіряє й після F5
        k.To(Kolo.Bets);
        var last = k.View(0).GetProperty("last");
        Assert.Equal(hash, last.GetProperty("hash").GetString());
        Assert.Equal(seed, last.GetProperty("seed").GetString());
        Assert.Equal(seg, last.GetProperty("seg").GetInt32());
        Assert.NotEqual(hash, k.View(0).GetProperty("hash").GetString());
    }

    [Fact]
    public void Draws_through_the_seed_are_uniform()
    {
        var hits = new int[KoloCore.Size];
        var rng = new Random(9);
        for (var i = 0; i < 125_000; i++) hits[KoloCore.Seg(KoloCore.NewSeed(rng))]++;
        Assert.All(hits, h => Assert.InRange(h, 800, 1200));
    }

    // ---------- конфіг ----------

    [Fact]
    public void Disabled_live_refuses_bets_finishes_the_round_and_stands_still()
    {
        var k = Two();
        k.Rig(2);
        Assert.True(k.Bet(0, 2, 100).Ok);
        k.Options.Enabled = false;
        var r = k.Bet(1, 2, 100);
        Assert.False(r.Ok);
        Assert.Equal(Kolo.OffText, r.Message);
        Assert.False(k.View(1).GetProperty("on").GetBoolean());
        Assert.True(k.H.Act(0, "clear").Ok);   // зняти своє — можна
        Assert.True(k.Bet(1, 2, 0).Message == Kolo.OffText);
        k.Options.Enabled = true;
        Assert.True(k.Bet(0, 2, 100).Ok);
        k.Options.Enabled = false;
        k.To(Kolo.Result);   // раунд, що йшов, докрутився й заплатив
        Assert.Equal(1100, k.Stakes.Balance("Оля"));
        k.To(Kolo.Off);
        Assert.Null(k.S.Until);
        Assert.Equal("doze", k.S.Glek.Mood);
        k.H.Tick(100);
        Assert.Equal(Kolo.Off, k.S.Phase);
        // вимкнено — нового столу не поставити
        var other = new RoomHarness("kolo", services: RoomHarness.WithService(k.Book));
        Assert.False(other.Join("Іра").Ok);
        k.Options.Enabled = true;
        k.H.Tick();
        Assert.Equal(Kolo.Bets, k.S.Phase);
    }

    [Fact]
    public void Limits_come_from_config_live()
    {
        var k = Two();
        k.Options.MinBet = 1;
        k.Options.MaxBet = 0;
        Assert.True(k.Bet(0, 2, 1).Ok);
        Assert.True(k.Bet(0, 2, 999).Ok);
        var lim = k.View(0).GetProperty("limits");
        Assert.Equal(1, lim.GetProperty("min").GetInt32());
        Assert.Equal(0, lim.GetProperty("max").GetInt32());
        k.Options.MaxBet = 50;
        Assert.Equal("На один множник — щонайбільше 50 🏺", k.Bet(1, 3, 51).Message);
    }

    // ---------- перезапуск ----------

    [Fact]
    public void Resume_mid_bets_keeps_bets_and_shifts_the_deadline()
    {
        var k = Two();
        Assert.True(k.Bet(0, 3, 100).Ok);
        k.H.Tick(10);
        var until = k.S.Until!.Value;
        var json = k.G.Save()!;
        var epoch = k.S.Epoch;
        var pause = TimeSpan.FromSeconds(30);
        k.H.Clock.Advance(pause);
        lock (k.H.Room.Sync)
        {
            k.G.Start();
            k.G.Load(json);
            k.G.Resumed(pause);
        }
        Assert.Equal(Kolo.Bets, k.S.Phase);
        Assert.Equal(until + pause, k.S.Until);
        Assert.Equal(100, k.S.Bets.Single().On(3));
        Assert.NotEqual(epoch, k.S.Epoch);   // новий epoch — ключі леджера не повторяться
        Assert.Equal(900, k.View(0).GetProperty("me").GetProperty("free").GetInt32());
    }

    [Fact]
    public void Resume_mid_spin_settles_once()
    {
        var k = Two();
        k.Rig(30);
        Assert.True(k.Bet(0, 30, 10).Ok);
        k.To(Kolo.Spin);
        var json = k.G.Save()!;
        var pause = TimeSpan.FromSeconds(20);
        k.H.Clock.Advance(pause);
        lock (k.H.Room.Sync)
        {
            k.G.Start();
            k.G.Load(json);
            k.G.Resumed(pause);
        }
        Assert.Equal(Kolo.Spin, k.S.Phase);
        var book2 = new KoloBook(k.Stakes, k.Store, k.H.Clock, defer: a => a(), held: (t, r) => KoloBook.HeldBy(k.H.Rooms, t, r));
        Assert.Equal(0, book2.Recover(DateTimeOffset.MaxValue));   // стіл живий — каса не чіпає
        Assert.Empty(k.Grants);
        k.To(Kolo.Result);
        Assert.Equal(1290, k.Stakes.Balance("Оля"));
        Assert.Single(k.Grants);
        Assert.Equal(["ach:kolo-30"], k.Achievements("Оля"));
        Assert.Empty(book2.Pending());
    }

    [Fact]
    public void Orphan_round_is_settled_by_the_drawn_segment()
    {
        var k = new KoloKit(("Оля", 1000), ("Петро", 1000), ("Іра", 1000));
        k.Rig(6);
        Assert.True(k.Bet(0, 6, 100).Ok);    // виграє ×6
        Assert.True(k.Bet(1, 2, 100).Ok);    // програє — ставку не повертаємо
        Assert.True(k.Bet(2, 6, 50).Ok);
        Assert.True(k.Bet(2, 30, 50).Ok);    // половина виграє
        k.To(Kolo.Spin);
        // процес упав: столу нема, каса на старті розраховує сама за вже вирішеним сегментом
        var book2 = new KoloBook(k.Stakes, k.Store, k.H.Clock, defer: a => a());
        Assert.Equal(1, book2.Recover(DateTimeOffset.MaxValue));
        Assert.Equal(1500, k.Stakes.Balance("Оля"));
        Assert.Equal(900, k.Stakes.Balance("Петро"));
        Assert.Equal(1200, k.Stakes.Balance("Іра"));
        Assert.Empty(book2.Pending());
        Assert.Equal(0, book2.Recover(DateTimeOffset.MaxValue));
    }

    [Fact]
    public void Recover_skips_players_who_never_paid()
    {
        var k = Two();
        var round = new KoloRound("стіл:abcd0123", 7, "kolo", KoloCore.FirstSeg(2), 2, k.H.Clock.UtcNow,
            [new KoloPay("Оля", 100, 200), new KoloPay("Петро", 100, 200)]);
        Assert.True(k.Book.Open(round));
        Assert.True(k.Book.Take("Оля", 100, KoloBook.BetRef(round.Table, round.Round, "Оля")));
        Assert.Equal(1, k.Book.Recover(DateTimeOffset.MaxValue));
        Assert.Equal(1100, k.Stakes.Balance("Оля"));
        Assert.Equal(1000, k.Stakes.Balance("Петро"));   // упало між Open і Take — не платив, не отримує
    }

    [Fact]
    public void Periodic_sweep_takes_only_old_records_and_start_recovery_only_the_old_process()
    {
        var k = Two();
        var book = new KoloBook(k.Stakes, k.Store, k.H.Clock, defer: a => a());
        var fresh = new KoloRound("x:1", 1, "kolo", 1, 3, k.H.Clock.UtcNow, [new KoloPay("Оля", 10, 30)]);
        Assert.True(book.Open(fresh));
        Assert.Equal(0, book.RecoverAtStart());   // відкрито вже цим процесом
        Assert.Equal(0, book.Sweep());
        k.H.Clock.Advance(KoloBook.OrphanAge + TimeSpan.FromSeconds(1));
        Assert.Equal(1, book.Sweep());
        Assert.Empty(book.Pending());
    }

    [Fact]
    public void Load_of_a_round_the_book_already_settled_lands_quietly()
    {
        var k = Two();
        k.Rig(30);
        Assert.True(k.Bet(0, 30, 10).Ok);
        k.To(Kolo.Spin);
        var json = k.G.Save()!;
        new KoloBook(k.Stakes, k.Store, k.H.Clock, defer: a => a()).Recover(DateTimeOffset.MaxValue);   // сирота: виграш уже пішов
        Assert.Equal(1290, k.Stakes.Balance("Оля"));
        lock (k.H.Room.Sync)
        {
            k.G.Start();
            k.G.Load(json);
            k.G.Resumed(TimeSpan.FromMinutes(5));
        }
        Assert.Equal(Kolo.Result, k.S.Phase);
        k.To(Kolo.Bets);
        Assert.Equal(1290, k.Stakes.Balance("Оля"));
        Assert.Single(k.Grants);
        Assert.Empty(k.Achievements("Оля"));
        Assert.DoesNotContain(k.H.Outbox.OfType<Journal>(), j => j.Text.StartsWith("🏺 Гончарне колесо", StringComparison.Ordinal));
    }

    [Fact]
    public void Shared_table_is_resumable_and_one_per_site()
    {
        var k = new KoloKit(("Оля", 1000));
        Assert.True(k.G.Resumable);
        var again = k.H.Rooms.Create("Петро", "kolo", null);
        Assert.True(again.Reply.Ok, again.Reply.Message);
        Assert.Equal(k.H.RoomId, again.Reply.RoomId);
        Assert.True(new Registry().Catalog.Single(g => g.Id == "kolo").Shared);
    }

    // ---------- Глек, ачівки, таблиця, Журнал ----------

    [Fact]
    public void Achievements_score_journal_and_table_chat()
    {
        var k = Two();
        k.Rig(30);
        Assert.True(k.Bet(0, 30, 100).Ok);   // +2900: ачівка, таблиця, Журнал, балачка
        Assert.True(k.Bet(1, 2, 100).Ok);
        k.To(Kolo.Result);
        Assert.Equal(["ach:kolo-30"], k.Achievements("Оля"));
        Assert.Empty(k.Achievements("Петро"));
        Assert.Contains(k.H.Scores, s => s.Nick == "Оля" && s.Score == 2900);
        Assert.DoesNotContain(k.H.Scores, s => s.Nick == "Петро");
        Assert.Contains(k.H.Outbox.OfType<TableSaid>(), t => t.Line.Text.Contains("Оля") && t.Line.Text.Contains("3000"));
        var j = Assert.Single(k.H.Outbox.OfType<Journal>(), l => l.Text.StartsWith("🏺 Гончарне колесо", StringComparison.Ordinal));
        Assert.Contains("Оля виграє 2900", j.Text);

        k.To(Kolo.Bets);
        k.Rig(KoloCore.Crack);
        Assert.True(k.Bet(0, 2, 100).Ok);
        Assert.True(k.Bet(1, 3, 100).Ok);
        k.To(Kolo.Result);
        Assert.Contains("ach:kolo-crack", k.Achievements("Петро"));
        Assert.Contains("ach:kolo-crack", k.Achievements("Оля"));
        Assert.Equal(2, k.H.Outbox.OfType<TableSaid>().Count());
        Assert.Single(k.H.Outbox.OfType<Journal>(), j => j.Text.StartsWith("🏺 Гончарне колесо", StringComparison.Ordinal));   // не частіше раз на 10 хв
        Assert.Contains(AchievementCatalog.All, a => a.Key == "kolo-30");
        Assert.Contains(AchievementCatalog.All, a => a.Key == "kolo-crack");
    }

    [Fact]
    public void Glek_moods_follow_the_result()
    {
        var k = Two();
        k.Rig(2);
        Assert.True(k.Bet(0, 2, 100).Ok);
        k.To(Kolo.Spin);
        Assert.Equal("call", k.S.Glek.Mood);
        var seq = k.S.Glek.Seq;
        k.To(Kolo.Result);
        Assert.Equal("clap", k.S.Glek.Mood);
        Assert.StartsWith("Миска ×2!", k.S.Glek.Say);
        Assert.True(k.S.Glek.Seq > seq);
        k.To(Kolo.Bets);
        k.Rig(3);
        Assert.True(k.Bet(0, 2, 100).Ok);
        k.To(Kolo.Result);
        Assert.Equal("rake", k.S.Glek.Mood);
        Assert.StartsWith("Горщик ×3!", k.S.Glek.Say);
    }

    [Fact]
    public void Late_join_can_bet_in_the_next_window()
    {
        var k = new KoloKit(("Оля", 1000));
        k.Stakes.Set("Петро", 500);
        k.To(Kolo.Spin);
        Assert.True(k.H.Join("Петро").Ok);
        Assert.False(k.Bet(1, 2, 10).Ok);
        k.To(Kolo.Bets);
        Assert.True(k.Bet(1, 2, 10).Ok);
        Assert.Equal(490, k.View(1).GetProperty("me").GetProperty("free").GetInt32());
    }

    [Fact]
    public void View_shape_on_the_wire()
    {
        var k = Two();
        Assert.True(k.Bet(0, 2, 10).Ok);
        var v = Views.Json(k.G.View(0));
        foreach (var f in new[] { "phase", "round", "until", "leftMs", "phaseMs", "hash", "seed", "wheel", "picks", "spin", "history",
                     "players", "totals", "onTable", "last", "glek", "me", "limits", "on" })
            Assert.True(v.TryGetProperty(f, out _), f);
        foreach (var f in new[] { "free", "onTable", "canClear", "canRepeat", "repeatCost", "note" })
            Assert.True(v.GetProperty("me").TryGetProperty(f, out _), f);
        foreach (var f in new[] { "mood", "say", "seq" }) Assert.True(v.GetProperty("glek").TryGetProperty(f, out _), f);
        var p = v.GetProperty("players")[0];
        foreach (var f in new[] { "nick", "seat", "color", "here", "mine", "total", "bets" }) Assert.True(p.TryGetProperty(f, out _), f);
        Assert.Equal(125, v.GetProperty("wheel").GetArrayLength());
        Assert.True(v.GetRawText().Length < 8_000);
    }

    [Fact]
    public void Deterministic_with_the_same_seed()
    {
        string Run()
        {
            var k = Two();
            Assert.True(k.Bet(0, 2, 10).Ok);
            Assert.True(k.Bet(1, 30, 10).Ok);
            k.To(Kolo.Result);
            var v = k.View(null);
            return v.GetProperty("spin").GetRawText() + v.GetProperty("seed").GetString();
        }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void Client_module_is_registered_new_and_in_the_azart_theme()
    {
        var js = File.ReadAllText(Paths.Resolve("web/games/kolo.js"));
        Assert.Matches(@"added:\s*'2026-10-10'", js);
        Assert.Contains("id: 'kolo'", js);
        var core = File.ReadAllText(Paths.Resolve("web/games/core.js"));
        Assert.Matches(@"id: 'roulette'[^\n]*'kolo'", core);
    }
}

[Collection(SerialPerf.Name)]
public class KoloPerfTests
{
    [Fact]
    [Trait("Category", "Perf")]
    public void Thousand_ticks_with_twelve_players_are_fast()
    {
        var people = Enumerable.Range(0, Kolo.MaxSeats).Select(i => ($"гравець {i}", 100_000)).ToArray();
        var k = new KoloKit(people);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            if (k.S.Phase == Kolo.Bets && k.S.Bets.Count == 0)
                for (var s = 0; s < Kolo.MaxSeats; s++) k.Bet(s, KoloCore.Picks[s % 4], 10);
            k.H.Tick();
            if (i % 5 == 0) k.View(i % Kolo.MaxSeats);
        }
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"1000 тиків — {sw.Elapsed}");
    }
}
