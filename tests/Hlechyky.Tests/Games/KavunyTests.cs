using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Сховище, яке береться пізніше: каса має писати в те саме сховище, що й каркас кімнати (його створює RoomHarness).</summary>
sealed class LateStore(Func<IGameStore> store) : IGameStore
{
    public void SaveState(string key, string json) => store().SaveState(key, json);
    public string? LoadState(string key) => store().LoadState(key);
    public void DeleteState(string key) => store().DeleteState(key);
}

/// <summary>Обв'язка «Кавунів»: свої ставки, каса (розрахунок — одразу), сховище каси = сховище каркаса, соло-кімната.</summary>
public sealed class KavunyKit
{
    public FakeStakes Stakes { get; }
    public KavunyOptions Options { get; } = new();
    public KavunyBook Book { get; }
    public RoomHarness H { get; }
    public string Nick { get; }

    /// <param name="states">Сховище іншого процесу (перезапуск): кладемо в нове перед відкриттям кімнати.</param>
    public KavunyKit(int wallet = 1000, FakeStakes? stakes = null, IReadOnlyDictionary<string, string>? states = null, string nick = "Оля",
        int seed = 3, KavunyOptions? options = null)
    {
        Nick = nick;
        Stakes = stakes ?? new FakeStakes().Set(nick, wallet);
        if (options is not null) Options = options;
        H = null!;
        Book = new KavunyBook(Stakes, new LateStore(() => H.Store), defer: a => a(),
            held: (table, no) => KavunyBook.HeldBy(H.Rooms, table, no), options: new FixedOptions<KavunyOptions>(Options));
        H = new RoomHarness("kavuny", services: RoomHarness.WithService(Book), seed: seed);
        if (states is not null) foreach (var (k, v) in states) H.Store.States[k] = v;
        var reply = H.Solo(nick);
        Assert.True(reply.Ok, reply.Message);
    }

    public Kavuny G => (Kavuny)H.Room.Game;
    public KavunyState S => G.State;
    public KavunyRound R => S.Round ?? throw new InvalidOperationException("раунд не йде");
    public IReadOnlyList<KavunyFruit> Fruits => R.Seq.Fruits;

    /// <summary>Наступний раунд зіграє цей seed (hash — чесний, як показав би стіл).</summary>
    public string Use(string seed)
    {
        S.NextSeed = seed;
        S.NextHash = KavunyCore.Hash(seed);
        return seed;
    }

    /// <summary>Перший seed «kv-0», «kv-1»…, чия послідовність (за стелі <paramref name="cap"/>) задовольняє <paramref name="want"/>.</summary>
    public static string Find(Func<KavunySeq, bool> want, int cap = KavunyCore.DefaultCap)
    {
        for (var i = 0; i < 200_000; i++)
        {
            var s = $"kv-{i}";
            if (want(KavunyCore.Generate(s, cap))) return s;
        }
        throw new InvalidOperationException("такого seed не знайшов");
    }

    public ActResult Start(int amount = 100, double? auto = null, bool autocut = false) =>
        H.Act(0, "start", new { amount, auto, autocut });

    /// <summary>Годинник — рівно на мілісекунду <paramref name="ms"/> від t0 раунду (без тіку).</summary>
    public void At(int ms)
    {
        var target = R.T0.AddMilliseconds(ms);
        var delta = (int)Math.Round((target - H.Clock.UtcNow).TotalMilliseconds);
        if (delta > 0) H.Clock.AdvanceMs(delta);
    }

    public void Cut(params int[] ids) => H.Input(0, "cut", new { ids });

    /// <summary>Тикати, поки раунд не скінчиться.</summary>
    public void ToEnd(int max = 20_000)
    {
        for (var i = 0; i < max && S.Phase == Kavuny.Fly; i++) H.Tick();
        Assert.Equal(Kavuny.Idle, S.Phase);
    }

    public JsonElement View() => H.View(0);
    public List<string> Grants => [.. Stakes.Calls.Where(c => c.StartsWith("grant:", StringComparison.Ordinal))];
    public List<string> Spends => [.. Stakes.Calls.Where(c => c.StartsWith("spend:", StringComparison.Ordinal))];
}

public class KavunyTests
{
    /// <summary>Перші <paramref name="n"/> овочів — цілі (гнилий, якщо є, пізніше).</summary>
    static string Long(int n) => KavunyKit.Find(s => s.Fruits.Take(n).All(f => !f.Rotten) && s.Fruits.Count > n);

    [Fact]
    public void Start_takes_the_stake_and_opens_the_book()
    {
        var k = new KavunyKit();
        Assert.Equal(Kavuny.Idle, k.View().GetProperty("phase").GetString());
        var r = k.Start(100);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(Kavuny.Fly, k.S.Phase);
        Assert.Equal(900, k.Stakes.Balance("Оля"));
        var bet = KavunyBook.BetRef(k.R.Table, k.R.No, "Оля");
        Assert.Equal($"spend:Оля:100:{bet}", Assert.Single(k.Spends));
        Assert.Equal("kavuny-bet:kavuny", k.Stakes.Reasons[bet]);
        var rec = Assert.Single(k.Book.Pending());
        Assert.Equal((k.R.Table, k.R.No, 100, (int?)null), (rec.Table, rec.No, rec.Stake, rec.Return));
        Assert.Equal(k.R.Seed, rec.Seed);
        k.H.Tick();
        var v = k.View();
        Assert.Equal("fly", v.GetProperty("phase").GetString());
        Assert.Equal(900, v.GetProperty("wallet").GetInt32());
        Assert.Equal(100, v.GetProperty("round").GetProperty("stake").GetInt32());
        Assert.False(k.Start(100).Ok);   // раунд іде
    }

    [Fact]
    public void Start_is_refused_outside_the_rules_and_nothing_moves()
    {
        var k = new KavunyKit(wallet: 300);
        Assert.False(k.Start(5).Ok);
        Assert.False(k.Start(2001).Ok);
        Assert.False(k.H.Act(0, "start", new { amount = "сто" }).Ok);
        Assert.False(k.H.Act(0, "start", new { amount = 10.5 }).Ok);
        Assert.False(k.Start(100, auto: 1.0).Ok);
        Assert.False(k.Start(100, auto: 101).Ok);   // понад стелю ×100
        Assert.False(k.Start(400).Ok);              // бракує
        Assert.False(k.H.Act(0, "fly").Ok);
        k.Options.Enabled = false;
        var off = k.Start(100);
        Assert.False(off.Ok);
        Assert.Equal(Kavuny.OffText, off.Message);
        Assert.Equal(JsonValueKind.False, k.View().GetProperty("on").ValueKind);
        Assert.Empty(k.Spends);
        Assert.Empty(k.Book.Pending());
        Assert.Equal(Kavuny.Idle, k.S.Phase);
        k.Options.Enabled = true;
        k.Options.MaxBet = 0;
        Assert.True(k.Start(300).Ok);   // без стелі — до всього гаманця
    }

    [Fact]
    public void Hash_shown_before_the_bet_is_the_sha_of_the_seed_revealed_after()
    {
        var k = new KavunyKit();
        var before = k.View().GetProperty("hash").GetString();
        Assert.Equal(64, before!.Length);
        Assert.True(k.Start(100, autocut: true).Ok);
        k.H.Tick();
        Assert.Equal(before, k.View().GetProperty("hash").GetString());
        k.ToEnd();
        var last = k.View().GetProperty("last");
        var seed = last.GetProperty("seed").GetString()!;
        Assert.Equal(before, KavunyCore.Hash(seed));
        Assert.Equal(before, last.GetProperty("hash").GetString());
        Assert.Equal(KavunyCore.Generate(seed, 10_000).Codes, last.GetProperty("codes").GetString());
        // наступний раунд — новий seed, і його hash уже видно
        var next = k.View().GetProperty("hash").GetString();
        Assert.NotEqual(before, next);
        Assert.Equal(next, KavunyCore.Hash(k.S.NextSeed));
    }

    [Fact]
    public void The_view_never_shows_a_fruit_before_it_flies_nor_the_seed()
    {
        var k = new KavunyKit();
        k.Use(Long(30));
        Assert.True(k.Start(100).Ok);
        var seed = k.R.Seed;
        for (var step = 0; step < 200 && k.S.Phase == Kavuny.Fly; step++)
        {
            k.H.Tick();
            var json = k.View().GetRawText();
            Assert.DoesNotContain(seed, json);
            var t = (k.H.Clock.UtcNow - k.R.T0).TotalMilliseconds;
            foreach (var f in k.View().GetProperty("round").GetProperty("fruits").EnumerateArray())
            {
                Assert.True(f.GetProperty("at").GetInt32() <= t);
                Assert.NotEqual("x", f.GetProperty("k").GetString());
            }
            var shown = k.View().GetProperty("round").GetProperty("fruits").GetArrayLength();
            var launched = k.Fruits.Count(f => f.At <= t);
            Assert.True(shown <= launched);
            if (t >= 0 && t < KavunyCore.FlyMs) Assert.Equal(launched, shown);
        }
    }

    [Fact]
    public void A_fruit_is_cut_only_while_it_is_in_the_air_by_the_server_schedule()
    {
        var k = new KavunyKit();
        k.Use(Long(6));
        Assert.True(k.Start(100).Ok);
        var f = k.Fruits;
        // ще не вилетів — ні
        k.At(f[0].At - 1);
        k.Cut(1);
        Assert.Empty(k.R.Cut);
        // вилетів — так, і приріст додано
        k.At(f[0].At);
        k.Cut(1);
        Assert.Equal([1], k.R.Cut);
        Assert.Equal(100 + f[0].Inc, k.R.M);
        // двічі той самий — ні
        k.Cut(1);
        Assert.Equal(100 + f[0].Inc, k.R.M);
        // майбутній, неіснуючий, кривий — ні
        k.Cut(5, 0, -3, 99_999);
        k.H.Input(0, "cut", new { ids = new object[] { "2", 1.5 } });
        Assert.Equal([1], k.R.Cut);
        // останню мить вікна (політ + запас) — так; мить по тому — ні
        k.At(f[1].At + KavunyCore.FlyMs + KavunyCore.LagMs);
        if (k.S.Phase == Kavuny.Fly && f[1].At + KavunyCore.FlyMs + KavunyCore.LagMs < k.R.Seq.EndAt)
        {
            k.Cut(2);
            Assert.Contains(2, k.R.Cut);
            k.H.Clock.AdvanceMs(1);
            var m = k.R.M;
            k.Cut(3);
            if (f[2].At + KavunyCore.FlyMs + KavunyCore.LagMs < (k.H.Clock.UtcNow - k.R.T0).TotalMilliseconds)
                Assert.Equal(m, k.R.M);
        }
    }

    [Fact]
    public void Late_cut_one_millisecond_after_the_window_is_ignored()
    {
        var k = new KavunyKit();
        k.Use(Long(8));
        Assert.True(k.Start(100).Ok);
        var f = k.Fruits[0];
        k.At(f.At + KavunyCore.FlyMs + KavunyCore.LagMs + 1);
        Assert.Equal(Kavuny.Fly, k.S.Phase);
        k.Cut(1);
        Assert.Empty(k.R.Cut);
        Assert.Equal(100, k.R.M);
    }

    [Fact]
    public void Rotten_pumpkin_ends_the_round_at_launch_and_cannot_be_cut_or_outrun()
    {
        var seed = KavunyKit.Find(s => s.Rotten && s.Fruits.Count == 4);
        var k = new KavunyKit();
        k.Use(seed);
        Assert.True(k.Start(100).Ok);
        var f = k.Fruits;
        k.At(f[0].At + 10);
        k.Cut(1);
        k.At(f[2].At + 10);
        k.Cut(2, 3);
        Assert.Equal(3, k.R.Cut.Count);
        k.At(f[3].At - 1);
        Assert.Equal(Kavuny.Fly, k.S.Phase);
        // «Забрати» отримано в ту саму мілісекунду, коли вилетів гнилий, — пізно
        k.At(f[3].At);
        var late = k.H.Act(0, "cash");
        Assert.False(late.Ok);
        Assert.Equal(Kavuny.Idle, k.S.Phase);
        Assert.Equal(Kavuny.Rotten, k.S.Last!.Why);
        Assert.Equal(0, k.S.Last.Win);
        Assert.Empty(k.Grants);
        Assert.Empty(k.Book.Pending());
        Assert.Equal(900, k.Stakes.Balance("Оля"));
        k.Cut(4);
        Assert.Equal(Kavuny.Idle, k.S.Phase);
        var last = k.View().GetProperty("last");
        Assert.Equal("rot", last.GetProperty("why").GetString());
        Assert.Equal(4, last.GetProperty("rot").GetProperty("id").GetInt32());
        Assert.Equal("rot", k.View().GetProperty("history")[0].GetProperty("why").GetString());
    }

    [Fact]
    public void Cash_needs_a_cut_and_pays_stake_times_the_sum()
    {
        var k = new KavunyKit();
        k.Use(Long(5));
        Assert.True(k.Start(150).Ok);
        var f = k.Fruits;
        k.At(f[0].At + 50);
        var none = k.H.Act(0, "cash");
        Assert.False(none.Ok);
        Assert.Equal(Kavuny.Fly, k.S.Phase);
        k.Cut(1);
        k.At(f[2].At + 50);
        k.Cut(2, 3);
        var m = 100 + f[0].Inc + f[1].Inc + f[2].Inc;
        Assert.Equal(m, k.R.M);
        var table = k.R.Table;
        var no = k.R.No;
        var r = k.H.Act(0, "cash");
        Assert.True(r.Ok, r.Message);
        var win = 150 * m / 100;
        Assert.Contains($"+{win} 🏺", r.Message);
        Assert.Equal($"grant:Оля:{win}:{KavunyBook.WinRef(table, no, "Оля")}", Assert.Single(k.Grants));
        Assert.Equal("kavuny-win:kavuny", k.Stakes.Reasons[KavunyBook.WinRef(table, no, "Оля")]);
        Assert.Equal(1000 - 150 + win, k.Stakes.Balance("Оля"));
        Assert.Empty(k.Book.Pending());
        Assert.Equal(Kavuny.Cash, k.S.Last!.Why);
        Assert.Equal(m, k.S.Last.M);
        Assert.False(k.H.Act(0, "cash").Ok);
        Assert.Equal(win - 150, k.H.Scores.Last().Score);
    }

    [Fact]
    public void Auto_cash_fires_on_the_cut_that_reaches_the_target_and_pays_the_actual_sum()
    {
        var k = new KavunyKit();
        k.Use(Long(12));
        Assert.True(k.Start(100, auto: 1.1).Ok);
        var f = k.Fruits;
        var m = 100;
        for (var i = 0; i < 12 && k.S.Phase == Kavuny.Fly; i++)
        {
            k.At(f[i].At + 20);
            k.Cut(i + 1);
            m += f[i].Inc;
            if (m >= 110) break;
        }
        Assert.Equal(Kavuny.Idle, k.S.Phase);
        Assert.Equal(Kavuny.Cash, k.S.Last!.Why);
        Assert.Equal(m, k.S.Last.M);
        Assert.Equal(100 * m / 100, k.S.Last.Win);
    }

    [Fact]
    public void Lowering_auto_below_the_current_sum_cashes_at_once()
    {
        var k = new KavunyKit();
        k.Use(Long(4));
        Assert.True(k.Start(100, auto: 50).Ok);
        k.At(k.Fruits[0].At + 10);
        k.Cut(1);
        Assert.True(k.H.Act(0, "auto", new { x = 1.01 }).Ok);
        Assert.Equal(Kavuny.Cash, k.S.Last!.Why);
        Assert.False(k.H.Act(0, "auto", new { x = 2 }).Ok);   // раунд уже не йде
    }

    /// <summary>Те, що зробив би «ідеальний різник» на автозаборі <paramref name="target"/>: (множник, гнилий раніше?).</summary>
    static (int M, bool Rot) Perfect(KavunySeq seq, int target, int cap)
    {
        var p = 100;
        foreach (var f in seq.Fruits)
        {
            if (f.Rotten) return (0, true);
            p += f.Inc;
            if (p >= target || p >= cap) return (Math.Min(p, cap), false);
        }
        return (Math.Min(p, cap), false);
    }

    [Fact]
    public void Autocut_plays_exactly_like_the_perfect_cutter()
    {
        for (var i = 0; i < 25; i++)
        {
            var seed = $"auto-{i}";
            var seq = KavunyCore.Generate(seed, 10_000);
            var k = new KavunyKit();
            k.Use(seed);
            Assert.True(k.Start(100, auto: 1.5, autocut: true).Ok);
            k.ToEnd();
            var (m, rot) = Perfect(seq, 150, 10_000);
            Assert.Equal(rot ? Kavuny.Rotten : Kavuny.Cash, k.S.Last!.Why);
            Assert.Equal(rot ? 0 : 100 * m / 100, k.S.Last.Win);
        }
    }

    [Fact]
    public void A_sparse_tick_ends_the_round_exactly_like_a_dense_one()
    {
        var seed = KavunyKit.Find(s => s.Rotten && s.Fruits.Count > 15);
        var dense = new KavunyKit();
        dense.Use(seed);
        Assert.True(dense.Start(100, auto: 1.4, autocut: true).Ok);
        dense.ToEnd();

        var sparse = new KavunyKit();
        sparse.Use(seed);
        Assert.True(sparse.Start(100, auto: 1.4, autocut: true).Ok);
        sparse.H.Clock.AdvanceMs(10 * 60_000);   // одна дія через 10 хв
        sparse.H.Act(0, "cash");
        Assert.Equal(Kavuny.Idle, sparse.S.Phase);
        Assert.Equal(dense.S.Last!.Why, sparse.S.Last!.Why);
        Assert.Equal(dense.S.Last.Win, sparse.S.Last.Win);
        Assert.Equal(dense.S.Last.M, sparse.S.Last.M);
        Assert.Equal(dense.S.Last.EndAt, sparse.S.Last.EndAt);
    }

    [Fact]
    public void Switching_autocut_on_mid_round_cuts_what_is_in_the_air_but_not_what_fell()
    {
        var k = new KavunyKit();
        k.Use(Long(14));
        Assert.True(k.Start(100).Ok);
        var f = k.Fruits;
        // овоч 1 давно впав, овоч 6 ще летить
        var t = f[5].At + 300;
        k.At(t);
        Assert.True(k.H.Act(0, "autocut", new { on = true }).Ok);
        Assert.DoesNotContain(1, k.R.Cut);
        Assert.Contains(6, k.R.Cut);
        foreach (var id in k.R.Cut) Assert.True(f[id - 1].At <= t && t <= f[id - 1].At + KavunyCore.FlyMs + KavunyCore.LagMs);
        // далі — кожен новий
        k.At(f[9].At + KavunyCore.AutoCutMs);
        k.H.Tick();
        if (k.S.Phase == Kavuny.Fly) Assert.Contains(10, k.R.Cut);
    }

    [Fact]
    public void Empty_cart_at_the_cap_pays_by_itself()
    {
        var seed = KavunyKit.Find(s => !s.Rotten, cap: 200);
        var k = new KavunyKit();
        k.Options.MaxX = 2;
        k.Use(seed);
        Assert.True(k.Start(100, autocut: true).Ok);
        Assert.Equal(200, k.R.Cap);
        k.ToEnd();
        Assert.Equal(Kavuny.Empty, k.S.Last!.Why);
        Assert.Equal(200, k.S.Last.M);   // що понад стелю — не платиться
        Assert.Equal(200, k.S.Last.Win);
        Assert.Equal(1100, k.Stakes.Balance("Оля"));
        Assert.Equal(2.0, k.View().GetProperty("last").GetProperty("cap").GetDouble());
    }

    [Fact]
    public void Switching_the_game_off_mid_round_lets_the_round_finish()
    {
        var k = new KavunyKit();
        k.Use(Long(4));
        Assert.True(k.Start(100).Ok);
        k.Options.Enabled = false;
        k.At(k.Fruits[0].At + 10);
        k.Cut(1);
        Assert.True(k.H.Act(0, "cash").Ok);
        Assert.Single(k.Grants);
        Assert.False(k.Start(100).Ok);
    }

    [Fact]
    public void Reload_with_a_live_round_refunds_the_stake_exactly_once()
    {
        var a = new KavunyKit();
        a.Use(Long(6));
        Assert.True(a.Start(100).Ok);
        a.At(a.Fruits[0].At + 10);
        a.Cut(1);
        var table = a.R.Table;
        var no = a.R.No;
        var oldNext = a.S.NextSeed;
        // «процес упав»: у сховищі — стан після старту (вводи не пишуться) і запис каси
        var b = new KavunyKit(stakes: a.Stakes, states: a.H.Store.States);
        Assert.Equal(Kavuny.Idle, b.S.Phase);
        Assert.Equal(Kavuny.Void, b.S.Last!.Why);
        Assert.Contains("повернуто", b.S.Note);
        Assert.Equal($"grant:Оля:100:{KavunyBook.BackRef(table, no, "Оля")}", Assert.Single(b.Grants));
        Assert.Equal("kavuny-back:kavuny", b.Stakes.Reasons[KavunyBook.BackRef(table, no, "Оля")]);
        Assert.Equal(1000, b.Stakes.Balance("Оля"));
        Assert.Empty(b.Book.Pending());
        Assert.NotEqual(oldNext, b.S.NextSeed);   // seed, що міг засвітитись, не грається
        Assert.NotEqual(a.S.Epoch, b.S.Epoch);
        // ні каса, ні ще одне відновлення вдруге не платять
        b.Book.Recover(DateTimeOffset.MaxValue);
        var c = new KavunyKit(stakes: a.Stakes, states: a.H.Store.States);
        Assert.Single(c.Grants);
        Assert.Equal(1000, c.Stakes.Balance("Оля"));
        Assert.Equal(1000, c.View().GetProperty("wallet").GetInt32());
    }

    [Fact]
    public void A_round_that_ended_on_a_tick_is_saved_as_ended()
    {
        var seed = KavunyKit.Find(s => s.Rotten && s.Fruits.Count == 3);
        var a = new KavunyKit();
        a.Use(seed);
        Assert.True(a.Start(100).Ok);
        a.ToEnd();
        Assert.Equal(Kavuny.Rotten, a.S.Last!.Why);
        var b = new KavunyKit(stakes: a.Stakes, states: a.H.Store.States);
        Assert.Equal(Kavuny.Idle, b.S.Phase);
        Assert.Equal(Kavuny.Rotten, b.S.Last!.Why);
        Assert.Null(b.S.Note);
        Assert.Empty(b.Grants);
        Assert.Equal(900, b.Stakes.Balance("Оля"));
    }

    [Fact]
    public void Reload_after_the_book_already_paid_only_closes_the_round()
    {
        var a = new KavunyKit();
        a.Use(Long(4));
        Assert.True(a.Start(100).Ok);
        var states = new Dictionary<string, string>(a.H.Store.States);   // стан «раунд іде»
        a.At(a.Fruits[0].At + 10);
        a.Cut(1);
        Assert.True(a.H.Act(0, "cash").Ok);   // заплачено, запис геть
        var paid = a.Stakes.Balance("Оля");
        states.Remove(KavunyBook.StoreKey);
        var b = new KavunyKit(stakes: a.Stakes, states: states);
        Assert.Equal(Kavuny.Void, b.S.Last!.Why);
        Assert.Contains("уже розраховано", b.S.Note);
        Assert.Equal(paid, b.Stakes.Balance("Оля"));
        Assert.Single(b.Grants);
    }

    [Fact]
    public void Leaving_mid_round_and_coming_back_refunds_the_stake()
    {
        var k = new KavunyKit();
        k.Use(Long(4));
        Assert.True(k.Start(100).Ok);
        k.H.Leave("Оля");
        Assert.True(k.H.Solo("Оля").Ok);
        Assert.Equal(Kavuny.Idle, k.S.Phase);
        Assert.Equal(Kavuny.Void, k.S.Last!.Why);
        Assert.Equal(1000, k.Stakes.Balance("Оля"));
        Assert.Single(k.Grants);
    }

    [Fact]
    public void Book_recovers_orphans_by_the_record_and_skips_live_rounds()
    {
        var stakes = new FakeStakes().Set("Оля", 1000).Set("Петро", 1000);
        var store = new FakeStore();
        var book = new KavunyBook(stakes, store, defer: a => a());
        var at = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        Assert.Null(book.Start(new KavunyRec("r1:e", 1, "Оля", 100, "s", at, null)));
        Assert.Null(book.Start(new KavunyRec("r2:e", 1, "Петро", 200, "s", at, null)));
        Assert.NotNull(book.Start(new KavunyRec("r3:e", 1, "Петро", 5000, "s", at, null)));   // бракує — запису нема
        Assert.Equal(2, book.Pending().Count);
        // Петро забрав, але розрахунок не дійшов до виплати: підсумок у записі
        var all = JsonSerializer.Deserialize<Dictionary<string, KavunyRec>>(store.LoadState(KavunyBook.StoreKey)!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        all["r2:e:1"] = all["r2:e:1"] with { Return = 350 };
        store.SaveState(KavunyBook.StoreKey, JsonSerializer.Serialize(all, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(0, book.Recover(at));   // ще не старі
        Assert.Equal(2, book.Recover(at.AddMinutes(1)));
        Assert.Contains($"grant:Оля:100:{KavunyBook.BackRef("r1:e", 1, "Оля")}", stakes.Calls);
        Assert.Contains($"grant:Петро:350:{KavunyBook.WinRef("r2:e", 1, "Петро")}", stakes.Calls);
        Assert.Equal(1000, stakes.Balance("Оля"));
        Assert.Equal(1150, stakes.Balance("Петро"));
        Assert.Empty(book.Pending());
        Assert.Equal(0, book.Recover(at.AddDays(1)));

        // живий раунд каса не чіпає
        var k = new KavunyKit();
        Assert.True(k.Start(100).Ok);
        Assert.Equal(0, k.Book.Recover(DateTimeOffset.MaxValue));
        Assert.Single(k.Book.Pending());
        Assert.Equal(Kavuny.Fly, k.S.Phase);
    }

    [Fact]
    public void Ten_x_cash_and_glek_watermelon_bring_achievements_score_and_a_journal_line()
    {
        var seed = KavunyKit.Find(s =>
        {
            var p = 100;
            var glek = false;
            foreach (var f in s.Fruits)
            {
                if (f.Rotten) return false;
                glek |= f.Code == 'K';
                p += f.Inc;
                if (p >= 1000) return glek;
            }
            return false;
        });
        var k = new KavunyKit(wallet: 5000);
        k.Use(seed);
        Assert.True(k.Start(100, auto: 10, autocut: true).Ok);
        k.ToEnd();
        Assert.Equal(Kavuny.Cash, k.S.Last!.Why);
        Assert.True(k.S.Last.M >= 1000);
        var achs = k.H.Awards.Where(a => a.Reason.StartsWith("ach:kavuny-", StringComparison.Ordinal)).Select(a => a.Reason).ToList();
        Assert.Contains("ach:kavuny-10", achs);
        Assert.Contains("ach:kavuny-glek", achs);
        Assert.Equal(k.S.Last.Win - 100, k.H.Scores.Last().Score);
        Assert.Contains(k.H.Outbox.OfType<Journal>(), j => j.Text.Contains("Кавуни на ярмарку") && j.Text.Contains("Оля"));
        Assert.Equal(KavunyCore.X(k.S.Last.M), k.View().GetProperty("best").GetProperty("x").GetDouble());
    }

    [Fact]
    public void The_view_has_the_wire_shape_the_client_reads()
    {
        var k = new KavunyKit();
        k.Use(Long(6));
        var idle = k.View();
        foreach (var p in new[] { "phase", "now", "on", "hash", "limits", "cap", "wallet", "round", "last", "history", "best", "k" })
            Assert.True(idle.TryGetProperty(p, out _), p);
        Assert.Equal(100.0, idle.GetProperty("cap").GetDouble());
        Assert.Equal(10, idle.GetProperty("limits").GetProperty("min").GetInt32());
        Assert.Equal(KavunyCore.FlyMs, idle.GetProperty("k").GetProperty("fly").GetInt32());
        // так шле модуль: { amount, auto: null, autocut: false }
        Assert.True(k.H.Act(0, "start", new { amount = 100, auto = (double?)null, autocut = false }).Ok);
        k.At(k.Fruits[1].At + 10);
        k.Cut(1, 2);
        k.H.Tick();
        var round = k.View().GetProperty("round");
        foreach (var p in new[] { "no", "stake", "auto", "autocut", "t0", "m", "win", "cap", "cuts", "tier", "strip", "fruits" })
            Assert.True(round.TryGetProperty(p, out _), p);
        var fr = round.GetProperty("fruits")[0];
        foreach (var p in new[] { "id", "k", "inc", "at", "x0", "x1", "h", "spin", "cut" })
            Assert.True(fr.TryGetProperty(p, out _), p);
        Assert.Equal(2, round.GetProperty("strip").GetArrayLength());
        Assert.Equal(KavunyCore.X(k.R.M), round.GetProperty("m").GetDouble());
    }

    [Fact]
    public void Same_seed_and_inputs_give_the_same_state()
    {
        string Play()
        {
            var k = new KavunyKit(seed: 5);
            k.Use("same-seed");
            Assert.True(k.Start(100, auto: 3, autocut: true).Ok);
            k.ToEnd();
            return JsonSerializer.Serialize(k.S.Last) + JsonSerializer.Serialize(k.S.History);
        }
        Assert.Equal(Play(), Play());
    }
}
