using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Games;

/// <summary>Обв'язка Лелеки: свої ставки, сховище й розсилка для каси (виплати — одразу), кімната через справжній каркас.</summary>
public sealed class LelkaKit
{
    public FakeStakes Stakes { get; } = new();
    public FakeStore Store { get; } = new();
    public FakeOutbox Site { get; } = new();
    public LelkaOptions Options { get; } = new();
    public LelkaBook Book { get; }
    public RoomHarness H { get; }

    public LelkaKit(params (string Nick, int Wallet)[] people) : this(null, null, null, people) { }

    /// <param name="stakes">Що дати касі замість <see cref="Stakes"/> (обгортка-лічильник, справжня економіка).</param>
    /// <param name="defer">Черга каси; типово — одразу.</param>
    public LelkaKit(Func<FakeStakes, IStakes>? stakes, Func<FakeStore, IGameStore>? store, Action<Action>? defer,
        params (string Nick, int Wallet)[] people)
    {
        foreach (var (nick, wallet) in people) Stakes.Set(nick, wallet);
        H = null!;
        Book = new LelkaBook(stakes?.Invoke(Stakes) ?? Stakes, store?.Invoke(Store) ?? Store, defer: defer ?? (a => a()), options: new FixedOptions<LelkaOptions>(Options), outbox: Site,
            held: (table, round) => LelkaBook.HeldBy(H.Rooms, table, round));
        H = new RoomHarness("lelka", services: RoomHarness.WithService(Book), seed: 11);
        foreach (var (nick, _) in people)
        {
            var reply = H.Join(nick);
            Assert.True(reply.Ok, reply.Message);
        }
    }

    public Lelka G => (Lelka)H.Room.Game;
    public LelkaState S => G.State;

    /// <summary>Точка падіння цього й наступних раундів (соті).</summary>
    public void Rig(int cents)
    {
        G.Rig = () => cents;
        if (S.Phase == Lelka.Bets) S.Crash = cents;
    }

    public ActResult Bet(int seat, int amount, double? auto = null) =>
        auto is { } a ? H.Act(seat, "bet", new { amount, auto = a }) : H.Act(seat, "bet", new { amount });

    public void To(string phase, int max = 3000)
    {
        for (var i = 0; i < max && S.Phase != phase; i++) H.Tick();
        Assert.Equal(phase, S.Phase);
    }

    /// <summary>Тикати польотом, поки множник на сервері не стане ≥ <paramref name="cents"/>.</summary>
    public void FlyTo(int cents)
    {
        for (var i = 0; i < 3000 && S.Phase == Lelka.Flight && LelkaCore.CentsAt((H.Clock.UtcNow - S.StartAt!.Value).TotalMilliseconds) < cents; i++)
            H.Tick();
    }

    public JsonElement View(int? seat) => H.View(seat);
    public List<string> Grants => [.. Stakes.Calls.Where(c => c.StartsWith("grant:", StringComparison.Ordinal))];
    public List<string> Achievements(string nick) =>
        [.. H.Awards.Where(a => a.Nick == nick && a.Reason.StartsWith("ach:lelka-", StringComparison.Ordinal)).Select(a => a.Reason)];
}

public class LelkaTests
{
    static LelkaKit Two() => new(("Оля", 1000), ("Петро", 1000));

    [Fact]
    public void Phases_follow_the_schedule()
    {
        var k = Two();
        k.Rig(200);
        var t0 = k.H.Clock.UtcNow;
        Assert.Equal(Lelka.Bets, k.S.Phase);
        Assert.Equal(1, k.S.Round);
        Assert.Equal(t0.AddMilliseconds(Lelka.BetMs), k.S.Until);
        k.H.Tick(79);
        Assert.Equal(Lelka.Bets, k.S.Phase);
        k.H.Tick();
        Assert.Equal(Lelka.Flight, k.S.Phase);
        var start = k.H.Clock.UtcNow;
        k.To(Lelka.Crashed);
        var flew = (k.H.Clock.UtcNow - start).TotalSeconds;
        Assert.InRange(flew, 9.2, 9.4);   // ln 2 / 0,075 ≈ 9,24 с
        k.To(Lelka.Pause);
        k.To(Lelka.Bets);
        Assert.Equal(2, k.S.Round);
        var v = k.View(null);
        Assert.Equal(2.0, v.GetProperty("history")[0].GetProperty("crash").GetDouble());
        Assert.Equal(1, v.GetProperty("history")[0].GetProperty("round").GetInt32());
    }

    [Fact]
    public void Bet_and_cancel_only_while_bets_are_open()
    {
        var k = Two();
        Assert.True(k.Bet(0, 100, 2.5).Ok);
        var me = k.View(0).GetProperty("mine");
        Assert.Equal(100, me.GetProperty("amount").GetInt32());
        Assert.Equal(2.5, me.GetProperty("auto").GetDouble());
        Assert.Equal(900, k.View(0).GetProperty("wallet").GetInt32());
        Assert.True(k.Bet(0, 50).Ok);   // переставив — замість, а не додатково
        Assert.Single(k.S.Bets);
        Assert.Equal(950, k.View(0).GetProperty("wallet").GetInt32());
        Assert.True(k.H.Act(0, "cancel").Ok);
        Assert.Empty(k.S.Bets);
        Assert.Equal(JsonValueKind.Null, k.View(0).GetProperty("mine").ValueKind);
        Assert.False(k.H.Act(0, "cancel").Ok);

        // поза лімітами, понад гаманець, кривий автозабір — відмова, стан той самий
        Assert.False(k.Bet(1, 5).Ok);
        Assert.False(k.Bet(1, 2001).Ok);
        Assert.False(k.Bet(1, 1, 2).Ok);
        Assert.False(k.Bet(1, 100, 1.0).Ok);
        Assert.False(k.Bet(1, 100, 1001).Ok);
        Assert.False(k.H.Act(1, "bet", new { amount = "сто" }).Ok);
        Assert.False(k.H.Act(1, "fly").Ok);
        Assert.Empty(k.S.Bets);
        k.Stakes.Set("Петро", 30);
        Assert.False(k.Bet(1, 50).Ok);

        Assert.True(k.Bet(1, 20).Ok);
        k.Rig(500);
        k.To(Lelka.Flight);
        Assert.False(k.Bet(0, 100).Ok);
        Assert.False(k.H.Act(1, "cancel").Ok);
        Assert.Single(k.S.Bets);
        Assert.Contains($"spend:Петро:20:lelka-bet:{k.H.RoomId}:{k.S.Epoch}:1:петро", k.Stakes.Calls);
        Assert.Equal(10, k.Stakes.Balance("Петро"));
    }

    [Fact]
    public void Limits_come_from_config_live()
    {
        var k = Two();
        k.Options.MinBet = 1;
        k.Options.MaxBet = 0;
        Assert.True(k.Bet(0, 1000).Ok);
        var lim = k.View(0).GetProperty("limits");
        Assert.Equal(1, lim.GetProperty("min").GetInt32());
        Assert.Equal(0, lim.GetProperty("max").GetInt32());
        k.Options.Enabled = false;
        Assert.False(k.Bet(1, 100).Ok);
        Assert.False(k.View(1).GetProperty("on").GetBoolean());
        // вимкнено — нового столу не поставити
        var other = new RoomHarness("lelka", services: RoomHarness.WithService(k.Book));
        Assert.False(other.Join("Іра").Ok);
    }

    [Fact]
    public void Cash_pays_the_multiplier_at_the_moment()
    {
        var k = Two();
        k.Rig(500);
        Assert.True(k.Bet(0, 100).Ok);
        Assert.True(k.Bet(1, 100).Ok);
        Assert.False(k.H.Act(0, "cash").Ok);   // ще на землі
        k.To(Lelka.Flight);
        k.FlyTo(200);
        var r = k.H.Act(0, "cash");
        Assert.True(r.Ok, r.Message);
        var bet = k.S.Bets.Single(b => b.Nick == "Оля");
        Assert.InRange(bet.Out!.Value, 200, 201);
        Assert.Equal(bet.Out!.Value, bet.Win);   // 100 × ×2,00 = 200
        Assert.Equal(900 + bet.Win, k.Stakes.Balance("Оля"));
        Assert.False(k.H.Act(0, "cash").Ok);   // двічі не забереш
        Assert.Equal(bet.Win, k.View(0).GetProperty("mine").GetProperty("win").GetInt32());
        k.To(Lelka.Crashed);
        Assert.False(k.H.Act(1, "cash").Ok);
        Assert.Equal(900 + bet.Win, k.Stakes.Balance("Оля"));   // розрахунок не доплатив удруге
        Assert.Equal(900, k.Stakes.Balance("Петро"));
        Assert.Single(k.Grants);
        Assert.Empty(k.Book.Pending());
    }

    [Fact]
    public void Auto_cash_fires_at_its_own_multiplier_and_on_the_crash_point()
    {
        var k = new LelkaKit(("Оля", 1000), ("Петро", 1000), ("Іра", 1000));
        k.Rig(300);
        Assert.True(k.Bet(0, 100, 1.5).Ok);
        Assert.True(k.Bet(1, 100, 3).Ok);   // рівно на точці падіння — встигає
        Assert.True(k.Bet(2, 100, 3.01).Ok);
        k.To(Lelka.Crashed);
        Assert.Equal(150, k.S.Bets.Single(b => b.Nick == "Оля").Win);
        Assert.Equal(150, k.S.Bets.Single(b => b.Nick == "Оля").Out);
        Assert.Equal(300, k.S.Bets.Single(b => b.Nick == "Петро").Win);
        Assert.Equal(0, k.S.Bets.Single(b => b.Nick == "Іра").Win);
        Assert.Equal(1050, k.Stakes.Balance("Оля"));
        Assert.Equal(1200, k.Stakes.Balance("Петро"));
        Assert.Equal(900, k.Stakes.Balance("Іра"));
    }

    [Fact]
    public void Auto_can_change_mid_flight_and_below_current_cashes_now()
    {
        var k = Two();
        k.Rig(1000);
        Assert.True(k.Bet(0, 100, 50).Ok);
        Assert.False(k.H.Act(1, "auto", new { x = 2 }).Ok);   // без ставки
        k.To(Lelka.Flight);
        k.FlyTo(150);
        Assert.True(k.H.Act(0, "auto", new { x = 1.2 }).Ok);
        var b = k.S.Bets.Single();
        Assert.InRange(b.Out!.Value, 150, 151);
        Assert.False(k.H.Act(0, "auto", new { x = 3 }).Ok);
    }

    [Fact]
    public void Instant_crash_takes_everything()
    {
        var k = Two();
        k.Rig(100);
        Assert.True(k.Bet(0, 100, 1.01).Ok);
        Assert.True(k.Bet(1, 200).Ok);
        k.H.Tick(80);
        Assert.Equal(Lelka.Crashed, k.S.Phase);   // упала в ту ж мить, що злетіла
        Assert.False(k.H.Act(1, "cash").Ok);
        Assert.Equal(900, k.Stakes.Balance("Оля"));
        Assert.Equal(800, k.Stakes.Balance("Петро"));
        Assert.Empty(k.Grants);
        Assert.Equal(1.0, k.View(null).GetProperty("crash").GetDouble());
    }

    [Fact]
    public void Frame_hides_the_crash_point_until_the_crash()
    {
        var k = Two();
        Assert.True(k.Bet(0, 100).Ok);
        var crash = k.S.Crash;
        var seed = k.S.Seed;
        void Hidden()
        {
            foreach (var v in new[] { Views.Json(k.G.Frame()), k.View(0), k.View(null) })
            {
                Assert.Equal(JsonValueKind.Null, v.GetProperty("crash").ValueKind);
                Assert.Equal(JsonValueKind.Null, v.GetProperty("seed").ValueKind);
                Assert.DoesNotContain(seed, v.GetRawText());
                Assert.Equal(k.S.Hash, v.GetProperty("hash").GetString());
            }
        }
        Hidden();
        k.H.Tick(80);
        if (k.S.Phase == Lelka.Flight)
        {
            Hidden();
            k.H.Tick(5);
            if (k.S.Phase == Lelka.Flight)
            {
                var f = Views.Json(k.G.Frame());
                Assert.True(f.GetProperty("m").GetDouble() < crash / 100.0);
                Assert.Equal(LelkaCore.K, f.GetProperty("k").GetDouble());
                Assert.Equal(JsonValueKind.String, f.GetProperty("startAt").ValueKind);
            }
        }
        k.To(Lelka.Crashed);
        var after = Views.Json(k.G.Frame());
        Assert.Equal(seed, after.GetProperty("seed").GetString());
        Assert.Equal(crash / 100.0, after.GetProperty("crash").GetDouble());
        // перевірка, яку може зробити будь-хто: sha256(seed) = hash, формула seed → crash
        Assert.Equal(after.GetProperty("hash").GetString(), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed))));
        Assert.Equal(crash, LelkaCore.CrashCents(seed));
        var bet = after.GetProperty("bets")[0];
        Assert.Equal("Оля", bet.GetProperty("nick").GetString());
        Assert.Equal(100, bet.GetProperty("amount").GetInt32());
        foreach (var name in new[] { "auto", "out", "win" }) Assert.True(bet.TryGetProperty(name, out _), name);
    }

    [Fact]
    public void Seed_to_crash_formula_is_fixed()
    {
        string Seed(string head) => head + new string('0', 64 - head.Length);
        Assert.Equal(100, LelkaCore.CrashCents(Seed("0000000000000")));        // n = 0 → 96 → ×1,00
        Assert.Equal(192, LelkaCore.CrashCents(Seed("8000000000000")));        // n = 2^51 → 192
        Assert.Equal(100, LelkaCore.CrashCents(Seed("0a3d70a3d70a3")));        // трохи менше 0,04·2^52 → ще ×1,00
        Assert.Equal(100, LelkaCore.CrashCents(Seed("0a3d70a3d70a4")));        // 0,04·2^52 → рівно 100
        Assert.Equal(480, LelkaCore.CrashCents(Seed("ccccccccccccd")));        // n ≈ 0,8·2^52 → 96/0,2 = 480
        Assert.Equal(100_000, LelkaCore.CrashCents(Seed("fffffffffffff")));    // стеля ×1000
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", LelkaCore.Hash("abc"));
        var a = LelkaCore.NewSeed(new Random(5));
        Assert.Equal(a, LelkaCore.NewSeed(new Random(5)));
        Assert.Equal(64, a.Length);
        Assert.Equal(LelkaCore.CrashCents(a), LelkaCore.CrashCents(a));
    }

    [Theory]
    [InlineData(1.01)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    [InlineData(5.0)]
    [InlineData(10.0)]
    [InlineData(100.0)]
    public void Rtp_is_96_percent_for_any_auto_cash(double x)
    {
        const int n = 100_000;
        var rng = new Random(2026);
        var auto = LelkaCore.Cents(x)!.Value;
        long paid = 0, instant = 0;
        for (var i = 0; i < n; i++)
        {
            var c = LelkaCore.CrashCents(LelkaCore.NewSeed(rng));
            if (c == 100) instant++;
            if (auto <= c) paid += LelkaCore.Win(100, auto);
        }
        var rtp = paid / (100.0 * n);
        var p = 0.96 / x;
        var sigma = x * Math.Sqrt(p * (1 - p) / n);
        Assert.InRange(rtp, 0.96 - 4 * sigma - 0.001, 0.96 + 4 * sigma + 0.001);
        // ×1,00: 4 % «одразу» + ще ≈0,95 % між 1,00 і 1,01 (сітка сотих) = 1 − 0,96/1,01 ≈ 4,95 %
        Assert.InRange(instant / (double)n, 0.0495 - 0.003, 0.0495 + 0.003);
    }

    [Fact]
    public void Settling_twice_pays_once()
    {
        var k = Two();
        k.Rig(400);
        Assert.True(k.Bet(0, 100, 2).Ok);
        Assert.True(k.Bet(1, 100).Ok);
        k.To(Lelka.Flight);
        var pending = k.S.Pending!;
        k.To(Lelka.Crashed);
        Assert.Equal(1100, k.Stakes.Balance("Оля"));
        var final = pending with { Pays = [new LelkaPay("Оля", 100, 200, 200), new LelkaPay("Петро", 100, null, 0)] };
        k.Book.Settle(final);
        k.Book.Open(final);
        Assert.Equal(1, k.Book.Recover(DateTimeOffset.MaxValue));
        Assert.Equal(1100, k.Stakes.Balance("Оля"));
        Assert.Equal(900, k.Stakes.Balance("Петро"));
        Assert.Single(k.Grants);
    }

    [Fact]
    public void Resume_mid_flight_keeps_the_flight_and_settles_once()
    {
        var k = Two();
        k.Rig(300);
        Assert.True(k.Bet(0, 100, 2).Ok);
        Assert.True(k.Bet(1, 100).Ok);
        k.To(Lelka.Flight);
        k.FlyTo(150);
        Assert.True(k.H.Act(1, "cash").Ok);
        var json = k.G.Save()!;
        var pause = TimeSpan.FromSeconds(30);
        k.H.Clock.Advance(pause);
        lock (k.H.Room.Sync)
        {
            k.G.Start();
            k.G.Load(json);
            k.G.Resumed(pause);
        }
        Assert.Equal(Lelka.Flight, k.S.Phase);
        Assert.InRange(LelkaCore.CentsAt((k.H.Clock.UtcNow - k.S.StartAt!.Value).TotalMilliseconds), 150, 152);   // не перескочила за паузу
        var book2 = new LelkaBook(k.Stakes, k.Store, k.H.Clock, defer: a => a(), held: (t, r) => LelkaBook.HeldBy(k.H.Rooms, t, r));
        Assert.Equal(0, book2.Recover(DateTimeOffset.MaxValue));   // стіл живий — каса не чіпає
        k.To(Lelka.Crashed);
        Assert.Equal(1100, k.Stakes.Balance("Оля"));
        Assert.InRange(k.Stakes.Balance("Петро"), 1050, 1052);
        Assert.Equal(2, k.Grants.Count);
        Assert.Empty(book2.Pending());
    }

    [Fact]
    public void Orphan_round_is_settled_by_the_book_fairly()
    {
        var k = new LelkaKit(("Оля", 1000), ("Петро", 1000), ("Іра", 1000), ("Ліна", 1000));
        k.Rig(250);
        Assert.True(k.Bet(0, 100, 2).Ok);     // автозабір ≤ точки — платить ×2
        Assert.True(k.Bet(1, 100, 3).Ok);     // автозабір вище — повертаємо ставку (польоту не бачили до кінця)
        Assert.True(k.Bet(2, 100).Ok);        // забрав на ×1,2 до падіння процесу
        Assert.True(k.Bet(3, 100).Ok);        // без автозабору — ставку назад
        k.To(Lelka.Flight);
        k.FlyTo(120);
        Assert.True(k.H.Act(2, "cash").Ok);
        // процес упав: столу нема, каса на старті розраховує сама
        var book2 = new LelkaBook(k.Stakes, k.Store, k.H.Clock, defer: a => a());
        Assert.Equal(1, book2.Recover(DateTimeOffset.MaxValue));
        Assert.Equal(1100, k.Stakes.Balance("Оля"));
        Assert.Equal(1000, k.Stakes.Balance("Петро"));
        Assert.InRange(k.Stakes.Balance("Іра"), 1020, 1021);
        Assert.Equal(1000, k.Stakes.Balance("Ліна"));
        Assert.Empty(book2.Pending());
        Assert.Equal(0, book2.Recover(DateTimeOffset.MaxValue));
        Assert.Equal((0, false), LelkaBook.Due(new LelkaPay("x", 100, null, null), 100));   // одразу ×1,00 — програш
    }

    [Fact]
    public void Load_of_a_round_the_book_already_settled_lands_quietly()
    {
        var k = Two();
        k.Rig(900);
        Assert.True(k.Bet(0, 100).Ok);
        k.To(Lelka.Flight);
        var json = k.G.Save()!;
        new LelkaBook(k.Stakes, k.Store, k.H.Clock, defer: a => a()).Recover(DateTimeOffset.MaxValue);   // сирота: ставку назад
        Assert.Equal(1000, k.Stakes.Balance("Оля"));
        lock (k.H.Room.Sync)
        {
            k.G.Start();
            k.G.Load(json);
            k.G.Resumed(TimeSpan.FromMinutes(5));
        }
        Assert.Equal(Lelka.Crashed, k.S.Phase);
        k.To(Lelka.Bets);
        Assert.Equal(1000, k.Stakes.Balance("Оля"));
        Assert.Empty(k.Achievements("Оля"));
    }

    [Fact]
    public void Achievements_journal_table_and_site_chat()
    {
        var k = Two();
        k.Rig(5_005);
        Assert.True(k.Bet(0, 100, 50).Ok);    // ×50 за 0,05 до падіння: обидві ачівки
        Assert.True(k.Bet(1, 500).Ok);        // пролетів
        k.To(Lelka.Crashed);
        Assert.Equal(["ach:lelka-10", "ach:lelka-brave"], k.Achievements("Оля").Order().ToList());
        Assert.Empty(k.Achievements("Петро"));
        Assert.Contains(k.H.Outbox.OfType<TableSaid>(), t => t.Line.Text.Contains("Оля"));
        Assert.Contains(k.H.Outbox.OfType<Journal>(), j => j.Text.Contains("Оля") && j.Text.Contains("×50,00"));
        Assert.Single(k.Site.Of<DjSays>());
        Assert.Contains("×50,05", k.Site.Of<DjSays>()[0].Text);
    }

    [Fact]
    public void Shared_table_one_per_site()
    {
        var k = new LelkaKit(("Оля", 1000));
        var again = k.H.Rooms.Create("Петро", "lelka", null);
        Assert.True(again.Reply.Ok, again.Reply.Message);
        Assert.Equal(k.H.RoomId, again.Reply.RoomId);
        Assert.Equal("Петро", k.H.Room.Seats[1]);
        var mine = k.H.Rooms.Create("Оля", "lelka", null);
        Assert.True(mine.Reply.Ok);
        Assert.Equal(k.H.RoomId, mine.Reply.RoomId);
        // повний стіл — новий; хто вже за повним, той і далі «за цим столом»
        for (var i = 2; i < Lelka.MaxSeats; i++) Assert.True(k.H.Rooms.Create($"гравець {i}", "lelka", null).Reply.Ok);
        var extra = k.H.Rooms.Create("Зайвий", "lelka", null);
        Assert.True(extra.Reply.Ok, extra.Reply.Message);
        Assert.NotEqual(k.H.RoomId, extra.Reply.RoomId);
        Assert.Equal(k.H.RoomId, k.H.Rooms.Create("гравець 5", "lelka", null).Reply.RoomId);
        Assert.True(new Registry().Catalog.Single(g => g.Id == "lelka").Shared);
        Assert.False(new Registry().Catalog.Single(g => g.Id == "roulette").Shared);
    }

    [Fact]
    public void Shared_table_dead_after_restart_lets_people_go()
    {
        // перезапуск без чистого знімка: партію перервано, стіл стоїть Finished, а люди ще «за ним»
        var k = new LelkaKit(("Оля", 1000), ("Петро", 1000));
        var dead = k.H.RoomId;
        var snap = k.H.Rooms.Capture(clean: false);
        var rooms = new Rooms(k.H.Registry, k.H.Clock, new GameEvents(), k.H.Stakes, new FakeStore(), RoomHarness.WithService(k.Book)) { SeedOverride = 5 };
        Assert.Equal(1, rooms.Restore(snap).Interrupted);
        Assert.Equal(RoomStatus.Finished, rooms.Find(dead)!.Status);

        // «Сісти до Лелеки» — не «Ти вже за цим столом» за мертвим, а новий живий стіл; з мертвого відпустило
        var olya = rooms.Create("Оля", "lelka", null);
        Assert.True(olya.Reply.Ok, olya.Reply.Message);
        Assert.NotEqual(dead, olya.Reply.RoomId);
        Assert.Equal(RoomStatus.Playing, rooms.Find(olya.Reply.RoomId!)!.Status);
        Assert.Null(rooms.Find(dead)!.SeatOf("Оля"));
        // другий — за той самий живий, а мертвий, спорожнівши, зникає
        var petro = rooms.Create("Петро", "lelka", null);
        Assert.True(petro.Reply.Ok, petro.Reply.Message);
        Assert.Equal(olya.Reply.RoomId, petro.Reply.RoomId);
        Assert.Null(rooms.Find(dead));
        Assert.Equal("Ти вже за цим столом", rooms.Create("Оля", "lelka", null).Reply.Message);
    }

    [Fact]
    public void Leaving_keeps_the_bet_flying()
    {
        var k = Two();
        k.Rig(300);
        Assert.True(k.Bet(1, 100, 2).Ok);
        k.H.Leave("Петро");
        k.To(Lelka.Crashed);
        Assert.Equal(1100, k.Stakes.Balance("Петро"));
    }

    [Fact]
    public void Empty_rounds_keep_flying_and_history_caps_at_20()
    {
        var k = new LelkaKit(("Оля", 1000));
        k.Rig(110);
        for (var i = 0; i < 22; i++)
        {
            k.To(Lelka.Crashed);
            k.To(Lelka.Bets);
        }
        Assert.Equal(20, k.View(0).GetProperty("history").GetArrayLength());
        Assert.Equal(23, k.S.Round);
    }

}

[Collection(SerialPerf.Name)]
public class LelkaPerfTests
{
    [Fact]
    [Trait("Category", "Perf")]
    public void Thousand_ticks_are_fast()
    {
        var k = new LelkaKit(("Оля", 1000), ("Петро", 1000), ("Іра", 1000));
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            if (k.S.Phase == Lelka.Bets && k.S.Bets.Count == 0)
                for (var s = 0; s < 3; s++) k.Bet(s, 10, s == 0 ? 1.5 : null);
            k.H.Tick();
            if (k.S.Phase == Lelka.Flight && i % 7 == 0) k.H.Act(1, "cash");
            k.View(0);
        }
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"1000 тиків — {sw.Elapsed}");
    }
}
