using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Games;

/// <summary>Турнір тижня в слотах (пакет 5): тиждень, спроба на день, фішки без гаманця, табло, призи раз.</summary>
public class SlotTourneyTests
{
    // середа 21.10.2026, 13:00 за Києвом (UTC+3); тиждень 19.10 — «Однорукий Глек», кінець — нд 25.10 21:00 (UTC+2 після зміни часу)
    static readonly DateTimeOffset Wed = new(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Close = new(2026, 10, 25, 19, 0, 0, TimeSpan.Zero);
    const string Week = "2026-10-19";

    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    static JsonElement J(object? o) => JsonSerializer.SerializeToElement(o, Web);

    /// <summary>Математика-підставка: кожен оберт виграє <see cref="Win"/> фішок.</summary>
    sealed class FixedMath(int win) : ISlotMath
    {
        public int Win { get; set; } = win;
        public int Cap => 1000;
        public bool Gamble => false;
        public SlotOutcome Spin(int bet, ISlotRng rng, JsonObject state)
        {
            var n = (state["n"]?.GetValue<int>() ?? 0) + 1;
            return new SlotOutcome(new JsonObject { ["steps"] = new JsonArray(), ["win"] = Win }, Win, [], new JsonObject { ["n"] = n });
        }
        public JsonObject Table() => new();
    }

    static bool Person(string nick) => !nick.StartsWith("гість", StringComparison.Ordinal) && !nick.StartsWith("🤖", StringComparison.Ordinal);

    static SlotTourney Svc(SlotTourneyOptions? o = null, FakeStakes? stakes = null, FakeSlotsWire? wire = null, Db? db = null)
    {
        var opts = o ?? new SlotTourneyOptions();
        return new SlotTourney(db, stakes, () => opts, new FakeClock { UtcNow = Wed }, wire, person: Person);
    }

    /// <summary>Зіграти спробу до кінця з виграшем <paramref name="win"/> на кожен оберт.</summary>
    static TourneySpin Play(SlotTourney t, string nick, DateTimeOffset at, int win, string game = "slot-glek")
    {
        var m = new FixedMath(win);
        var rng = new SeededSlotRng(new Random(1));
        TourneySpin? last = null;
        for (var i = 0; i < 1000; i++)
        {
            var err = t.Spin(nick, game, m, rng, at.AddSeconds(i), out var r);
            Assert.Null(err);
            last = r!;
            if (r!.Done) break;
        }
        return last!;
    }

    [Fact]
    public void Week_is_monday_to_sunday_21_kyiv_and_machine_rotates()
    {
        Assert.Equal(new DateOnly(2026, 10, 19), SlotWeek.Monday(Wed));
        // неділя 18.10 23:30 за Києвом — ще попередній тиждень; понеділок 00:30 — уже новий
        Assert.Equal(new DateOnly(2026, 10, 12), SlotWeek.Monday(new DateTimeOffset(2026, 10, 18, 20, 30, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 10, 19), SlotWeek.Monday(new DateTimeOffset(2026, 10, 18, 21, 30, 0, TimeSpan.Zero)));
        Assert.Equal(Close, SlotWeek.Close(new DateOnly(2026, 10, 19)));
        Assert.Equal(new DateTimeOffset(2026, 10, 18, 18, 0, 0, TimeSpan.Zero), SlotWeek.Close("2026-10-12"));   // ще літній час
        Assert.Equal("slot-glek", SlotWeek.Game(new DateOnly(2026, 10, 19)));
        Assert.Equal("slot-cascade", SlotWeek.Game(new DateOnly(2026, 10, 26)));
        Assert.Equal("slot-slidy", SlotWeek.Game(new DateOnly(2026, 10, 12)));
        Assert.Equal("slot-glek", SlotWeek.Game(new DateOnly(2026, 11, 23)));   // через п'ять тижнів — знову
        Assert.Equal(5, Enumerable.Range(0, 5).Select(i => SlotWeek.Game(new DateOnly(2026, 10, 19).AddDays(7 * i))).Distinct().Count());
    }

    [Fact]
    public void One_attempt_a_day_fifty_spins()
    {
        var t = Svc();
        var r = Play(t, "Оля", Wed, 20);   // виграш = ставка: фішки стоять, кінець — за обертами
        Assert.True(r.Done);
        Assert.Equal(50, r.Spin);
        Assert.Equal(1000, r.Chips);
        Assert.Equal("Сьогоднішню спробу вже зіграно — завтра нова", t.Spin("Оля", "slot-glek", new FixedMath(0), new SeededSlotRng(new Random(1)), Wed.AddHours(1), out _));
        // завтра — нова спроба
        Assert.Null(t.Spin("Оля", "slot-glek", new FixedMath(0), new SeededSlotRng(new Random(1)), Wed.AddDays(1), out var next));
        Assert.Equal(1, next!.Spin);
        Assert.Equal(980, next.Chips);
    }

    [Fact]
    public void Attempt_ends_when_chips_below_bet()
    {
        var t = Svc(new SlotTourneyOptions { Chips = 100 });
        var r = Play(t, "Оля", Wed, 0);
        Assert.True(r.Done);
        Assert.Equal(5, r.Spin);
        Assert.Equal(0, r.Chips);
    }

    [Fact]
    public void Wrong_machine_closed_week_and_switch_refuse()
    {
        var t = Svc();
        var m = new FixedMath(0);
        var rng = new SeededSlotRng(new Random(1));
        Assert.Equal("Турнір тижня — у «Однорукий Глек»", t.Spin("Оля", "slot-hold", m, rng, Wed, out _));
        Assert.Equal("Турнір тижня вже підбито — новий з понеділка", t.Spin("Оля", "slot-glek", m, rng, Close, out _));
        Assert.Null(t.Spin("Оля", "slot-glek", m, rng, Close.AddSeconds(-1), out _));
        var off = Svc(new SlotTourneyOptions { Enabled = false });
        Assert.NotNull(off.Spin("Оля", "slot-glek", m, rng, Wed, out _));
        Assert.Null(off.Wire("slot-glek", "Оля", Wed));
    }

    [Fact]
    public void Best_attempt_counts_ties_go_to_earlier_guests_and_bots_out()
    {
        var t = Svc();
        Play(t, "Оля", Wed, 30);                  // 1000 + 50×10 = 1500
        Play(t, "Оля", Wed.AddDays(1), 25);       // 1250 — гірша, у залік лишається 1500
        Play(t, "Петро", Wed.AddHours(1), 30);    // 1500, але пізніше
        Play(t, "гість Іван", Wed, 100);          // гість — поза табло
        Play(t, "🤖 Бот", Wed, 100);              // бот — теж
        Assert.Null(t.Spin("Іра", "slot-glek", new FixedMath(500), new SeededSlotRng(new Random(1)), Wed, out _));   // незакінчена — ще не в заліку

        var board = t.Board(Week);
        Assert.Equal(["Оля", "Петро"], board.Select(r => r.Nick));
        Assert.Equal(1500, board[0].Best);
        Assert.Equal(2, board[0].Tries);
        Assert.Equal(1, board[1].Tries);

        var w = J(t.Wire("slot-glek", "Оля", Wed.AddDays(1).AddHours(1)));
        Assert.True(w.GetProperty("here").GetBoolean());
        Assert.Equal("Однорукий Глек", w.GetProperty("title").GetString());
        Assert.Equal(1, w.GetProperty("mine").GetProperty("place").GetInt32());
        Assert.True(w.GetProperty("my").GetProperty("done").GetBoolean());
        Assert.Equal(2, w.GetProperty("board").GetArrayLength());
        Assert.True(w.GetProperty("board")[0].GetProperty("me").GetBoolean());
        var guest = J(t.Wire("slot-hold", "гість Іван", Wed));
        Assert.False(guest.GetProperty("person").GetBoolean());
        Assert.False(guest.GetProperty("here").GetBoolean());
        Assert.Equal(JsonValueKind.Null, guest.GetProperty("mine").ValueKind);
    }

    [Fact]
    public void Prizes_once_with_own_reason_and_chat_line()
    {
        var stakes = new FakeStakes();
        var wire = new FakeSlotsWire();
        var t = Svc(stakes: stakes, wire: wire);
        Play(t, "Оля", Wed, 40);      // 2000
        Play(t, "Петро", Wed, 30);    // 1500
        Play(t, "Іра", Wed, 25);      // 1250
        Play(t, "Тарас", Wed, 0);     // 0
        Play(t, "гість Іван", Wed, 100);

        Assert.Equal(0, t.Settle(Close.AddSeconds(-1)));   // ще не неділя 21:00
        Assert.Empty(stakes.Calls);

        Assert.Equal(1, t.Settle(Close));
        Assert.Equal(["grant:Оля:1000:slot-prize:2026-10-19:1", "grant:Петро:500:slot-prize:2026-10-19:2", "grant:Іра:250:slot-prize:2026-10-19:3"], stakes.Calls);
        Assert.All(stakes.Reasons.Values, r => Assert.Equal("slot-prize:2026-10-19", r));
        Assert.Null(Hlechyky.Games.Economy.AzartSaldo.KindOf("slot-prize:2026-10-19"));   // приз — не сальдо азарту
        Assert.Equal(["🏆 Турнір тижня (Однорукий Глек): 1. Оля 2 000 · 2. Петро 1 500 · 3. Іра 1 250 — призи від Дядька Глека"], wire.Chats);
        Assert.True(t.Paid(Week));

        // повтор (таймер, перезапуск) — нічого вдруге
        Assert.Equal(0, t.Settle(Close.AddMinutes(5)));
        Assert.Equal(3, stakes.Calls.Count);
        Assert.Single(wire.Chats);
    }

    [Fact]
    public void Fewer_than_two_people_no_prizes()
    {
        var stakes = new FakeStakes();
        var wire = new FakeSlotsWire();
        var t = Svc(stakes: stakes, wire: wire);
        Play(t, "Оля", Wed, 40);
        Play(t, "гість Іван", Wed, 40);
        Assert.Equal(1, t.Settle(Close.AddDays(3)));   // сервер лежав у неділю — підбиває пізніше
        Assert.Empty(stakes.Calls);
        Assert.Empty(wire.Chats);
        Assert.True(t.Paid(Week));
    }

    [Fact]
    public void Unfinished_attempt_counts_at_close()
    {
        var stakes = new FakeStakes();
        var t = Svc(stakes: stakes);
        Play(t, "Оля", Wed, 30);   // 1500
        var m = new FixedMath(2000);
        Assert.Null(t.Spin("Петро", "slot-glek", m, new SeededSlotRng(new Random(1)), Close.AddMinutes(-1), out var r));   // 2980, не дограв
        Assert.False(r!.Done);
        t.Settle(Close);
        Assert.Equal("grant:Петро:1000:slot-prize:2026-10-19:1", stakes.Calls[0]);
    }

    [Fact]
    public void Wallet_untouched_and_view_shows_chips_through_room()
    {
        var stakes = new FakeStakes().Set("Оля", 5000);
        var bank = new SlotsBank(stakes, new FakeStore(), () => new SlotsOptions(), new FakeClock(), new FakeSlotsWire(), defer: a => a());
        var tour = new SlotTourney(null, stakes, () => new SlotTourneyOptions(), new FakeClock { UtcNow = Wed }, person: Person);
        var sp = new ServiceCollection().AddSingleton(bank).AddSingleton(tour).BuildServiceProvider();
        var h = new RoomHarness("slot-glek", services: sp, seed: 7);
        h.Clock.UtcNow = Wed;
        Assert.True(h.Solo("Оля").Ok);
        var pot = bank.Pot;

        var spins = 0;
        while (true)
        {
            var v0 = h.View(0);
            var seq = v0.GetProperty("last").ValueKind == JsonValueKind.Null ? 0 : v0.GetProperty("last").GetProperty("seq").GetInt32();
            var res = h.Act(0, "tspin", new { seq });
            Assert.True(res.Ok, res.Message);
            spins++;
            var v = h.View(0);
            var tv = v.GetProperty("last").GetProperty("script").GetProperty("tourney");
            Assert.Equal(spins, tv.GetProperty("spin").GetInt32());
            Assert.Equal(tv.GetProperty("chips").GetInt32(), v.GetProperty("tourney").GetProperty("my").GetProperty("chips").GetInt32());
            if (tv.GetProperty("done").GetBoolean()) break;
            Assert.True(spins < 50);
        }
        Assert.True(spins <= 50);
        Assert.False(h.Act(0, "tspin", new { }).Ok);   // спробу на сьогодні зіграно
        Assert.Equal(5000, stakes.Balance("Оля"));
        Assert.Empty(stakes.Calls);
        Assert.Equal(pot, bank.Pot);
        Assert.Equal(0, h.View(0).GetProperty("spins").GetInt32());   // лічильник справжніх обертів не рухався
        Assert.Empty(h.Scores);

        // інший автомат: турнір не тут
        var h2 = new RoomHarness("slot-hold", services: sp, seed: 7);
        h2.Clock.UtcNow = Wed;
        Assert.True(h2.Solo("Оля").Ok);
        Assert.False(h2.View(0).GetProperty("tourney").GetProperty("here").GetBoolean());
        var no = h2.Act(0, "tspin", new { });
        Assert.False(no.Ok);
        Assert.Contains("Однорукий Глек", no.Message);
    }

    [Fact]
    public void Unfinished_attempt_survives_restart()
    {
        using var tmp = new TempDb();
        var t = Svc(db: tmp.Db);
        var m = new FixedMath(0);
        for (var i = 0; i < 7; i++) Assert.Null(t.Spin("Оля", "slot-glek", m, new SeededSlotRng(new Random(1)), Wed.AddSeconds(i), out _));
        t.FlushNow();

        var again = Svc(db: tmp.Db);
        Assert.Null(again.Spin("Оля", "slot-glek", m, new SeededSlotRng(new Random(1)), Wed.AddMinutes(1), out var r));
        Assert.Equal(8, r!.Spin);
        Assert.Equal(1000 - 8 * 20, r.Chips);
    }

    [Fact]
    public void Paid_week_remembered_after_restart()
    {
        using var tmp = new TempDb();
        var stakes = new FakeStakes();
        var t = Svc(stakes: stakes, db: tmp.Db);
        Play(t, "Оля", Wed, 30);
        Play(t, "Петро", Wed, 20);
        t.FlushNow();
        Assert.Equal(1, t.Settle(Close));
        var again = Svc(stakes: stakes, db: tmp.Db);
        Assert.True(again.Paid(Week));
        Assert.Equal(0, again.Settle(Close.AddHours(1)));
        Assert.Equal(2, stakes.Calls.Count);
    }
}
