using System.Net;
using System.Text;
using System.Text.Json;
using Hlechyky.Bets;
using Hlechyky.Games.Economy;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// «🎲 Ставки» (Bets/): ядро грошей (BetBook), події адміна, Polymarket на підмінному HttpMessageHandler (без мережі),
/// пропозиції й «Глек у мінусі». Справжня економіка на тимчасовій базі (EconomyRig), розсилка — заглушка.
/// </summary>
public sealed class BetsTests : IDisposable
{
    sealed class FakeWire : IBetsWire
    {
        public List<(string Nick, string Text, bool Toast)> Mine { get; } = [];
        public int EventsChanged { get; private set; }
        public List<(int Pending, string? Toast)> Suggest { get; } = [];
        void IBetsWire.Mine(string nick, string text, bool toast) => Mine.Add((nick, text, toast));
        public void Events() => EventsChanged++;
        public void Suggestions(int pending, string? toast) => Suggest.Add((pending, toast));
    }

    /// <summary>Підмінний Polymarket: відповідь за шляхом запиту; <see cref="Fail"/> — мережа лежить.</summary>
    sealed class FakePm : HttpMessageHandler
    {
        public Dictionary<string, string> Bodies { get; } = new(StringComparer.Ordinal);
        public List<string> Urls { get; } = [];
        public Exception? Fail { get; set; }
        public bool Hang { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.PathAndQuery;
            Urls.Add(url);
            if (Hang) await Task.Delay(Timeout.Infinite, ct);
            if (Fail is not null) throw Fail;
            var key = Bodies.Keys.FirstOrDefault(k => url.StartsWith(k, StringComparison.Ordinal));
            return key is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"type":"not found error","error":"slug not found"}""") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Bodies[key], Encoding.UTF8, "application/json") };
        }
    }

    readonly EconomyRig _eco = new();
    readonly FakeWire _wire = new();
    readonly FakePm _http = new();
    readonly BetsOptions _opts = new() { Polymarket = new PolymarketOptions { BaseUrl = "https://pm.test", CacheSeconds = 90, TimeoutSeconds = 1 } };
    readonly BetsStore _store;
    readonly BetBook _book;
    readonly Polymarket _pm;
    readonly BetEvents _bets;

    static readonly BetActor Vlad = new("Влад", true, false);
    static readonly BetActor Olia = new("Оля", true, false);
    static readonly BetActor Petro = new("Петро", true, false);
    static readonly BetActor Guest = new("гість Вася", false, false);
    static readonly BetActor Admin = new("гість Адмін", false, true);

    public BetsTests()
    {
        var o = new FixedOptions<BetsOptions>(_opts);
        _store = new BetsStore(_eco.Db);
        _book = new BetBook(_store, _eco.Economy, _eco.Clock, o, NullLogger<BetBook>.Instance);
        _pm = new Polymarket(o, _eco.Clock, NullLogger<Polymarket>.Instance, _http);
        _bets = new BetEvents(_store, _book, _eco.Economy, _pm, _wire, _eco.Clock, o, NullLogger<BetEvents>.Instance);
        foreach (var n in new[] { "Влад", "Оля", "Петро" })
        {
            _eco.Economy.Grant(n, 1000, "award", "seed:" + n);
            // поверх стартової тисячі одразу лягають ачівки («Сотня» тощо) — рахуємо від рівно 1000
            _extra[n] = _eco.Economy.Balance(n) - 1000;
        }
    }

    readonly Dictionary<string, int> _extra = [];

    public void Dispose()
    {
        _http.Dispose();
        _eco.Dispose();
    }

    int Balance(string nick) => _eco.Economy.Balance(nick) - _extra[nick];

    static JsonElement Data(BetReply r)
    {
        Assert.True(r.Ok, r.Message);
        return Views.Json(r.Data);
    }

    /// <summary>Відкрита подія «Реал — Барса» з двома варіантами: o1 ×2, o2 ×1,5.</summary>
    long OpenEvent(string title = "Реал — Барса", double a = 2, double b = 1.5, DateTimeOffset? closes = null)
    {
        var d = Data(_bets.Create(Admin, new BetEvents.EventRequest(title, "", null, closes,
            [new(null, "Реал", a, null), new(null, "Барса", b, null)], "open", null)));
        return d.GetProperty("id").GetInt64();
    }

    BetReply Bet(BetActor who, long id, string option, int stake, double? odds = null, string? key = null) =>
        _bets.Bet(who, id, new BetEvents.BetRequest(option, stake, odds, key));

    // =============================================================================================
    // Кефи
    // =============================================================================================

    [Fact]
    public void Odds_come_from_probability_with_the_margin_rounded_and_within_bounds()
    {
        Assert.Equal(1.84, BetMath.Odds(0.5, _opts));          // (1 − 0,08) / 0,5
        Assert.Equal(9.2, BetMath.Odds(0.1, _opts));
        Assert.Equal(100, BetMath.Odds(0.001, _opts));         // стеля
        Assert.Equal(100, BetMath.Odds(0, _opts));             // «світ думає 0%» — не нескінченність
        Assert.Equal(1.01, BetMath.Odds(0.99, _opts));         // 0,93 → нижня межа
        Assert.Equal(1.23, BetMath.Clamp(1.234, _opts));
        Assert.True(BetMath.Fits(2.4, _opts));
        Assert.False(BetMath.Fits(2.456, _opts));
        Assert.False(BetMath.Fits(1, _opts));
        Assert.False(BetMath.Fits(101, _opts));
        // виплата — униз до цілого, але не менше ставки
        Assert.Equal(24, BetMath.Payout(10, 2.4));
        Assert.Equal(1, BetMath.Payout(1, 1.5));
        Assert.Equal(115, BetMath.Payout(100, 1.15));
        Assert.Equal("×2,4", BetMath.Show(2.4));
    }

    // =============================================================================================
    // Ядро: гроші
    // =============================================================================================

    [Fact]
    public void Placing_debits_once_off_the_books_and_guests_cannot_bet()
    {
        Assert.Equal(BetBook.AccountsOnly, _book.Place(Guest.Nick, false, new BetPlace("table", "r1:1", "win", "Влад", "Влад", 2, 10)).Message);

        var before = _eco.Economy.Wallet("Оля");
        var r = _book.Place("Оля", true, new BetPlace("table", "r1:1", "win", "Влад", "хто виграє: Влад", 2.4, 50));
        Assert.True(r.Ok, r.Message);
        Assert.Equal("open", r.Bet!.Status);
        Assert.Equal(950, Balance("Оля"));
        var after = _eco.Economy.Wallet("Оля");
        // ставка не йде у «витрачено» (як обмін на гривні)
        Assert.Equal(before.Spent, after.Spent);
        Assert.Equal(before.Earned, after.Earned);
        Assert.Contains(_eco.Economy.Moves("bet:")!, m => m.Ref == BetBook.DebitRef(r.Bet.Id) && m.Delta == -50);
        Assert.Single(_book.Open("table", "r1:1"));
    }

    [Fact]
    public void Same_key_twice_is_one_bet_and_one_debit()
    {
        var p = new BetPlace("event", "7", "event", "o1", "Реал", 2, 100, Key: "k-1");
        var a = _book.Place("Влад", true, p);
        var b = _book.Place("Влад", true, p);
        Assert.True(a.Ok && b.Ok);
        Assert.True(b.Duplicate);
        Assert.Equal(a.Bet!.Id, b.Bet!.Id);
        Assert.Equal(900, Balance("Влад"));
        // інший ключ — нова ставка; той самий ключ в іншої людини — своя
        Assert.False(_book.Place("Влад", true, p with { Key = "k-2" }).Duplicate);
        Assert.False(_book.Place("Оля", true, p).Duplicate);
        Assert.Equal(800, Balance("Влад"));
    }

    [Fact]
    public void Parallel_double_click_with_one_key_debits_once()
    {
        var p = new BetPlace("event", "8", "event", "o1", "Реал", 2, 100, Key: "dbl");
        var results = Enumerable.Range(0, 8).AsParallel().Select(_ => _book.Place("Петро", true, p)).ToList();
        Assert.All(results, r => Assert.True(r.Ok, r.Message));
        Assert.Single(results.Select(r => r.Bet!.Id).Distinct());
        Assert.Equal(900, Balance("Петро"));
    }

    [Fact]
    public void Stake_bounds_odds_bounds_and_balance_are_checked()
    {
        _opts.MinBet = 5;
        _opts.MaxTableBet = 100;
        BetPlace P(int stake, double odds = 2) => new("table", "r2:1", "win", "x", "x", odds, stake);
        Assert.False(_book.Place("Влад", true, P(4)).Ok);
        Assert.False(_book.Place("Влад", true, P(10, 1.0)).Ok);
        Assert.False(_book.Place("Влад", true, P(10, 150)).Ok);
        Assert.False(_book.Place("Влад", true, P(10, 2.345)).Ok);   // кеф — з 2 знаками, як побачила людина
        Assert.False(_book.Place("Влад", true, P(101)).Ok);
        // стеля — на людину на партію, не на одну ставку
        Assert.True(_book.Place("Влад", true, P(60)).Ok);
        var over = _book.Place("Влад", true, P(50));
        Assert.False(over.Ok);
        Assert.Contains("можна ще 40", over.Message);
        Assert.True(_book.Place("Влад", true, P(40)).Ok);
        // 0 — без стелі; не вистачає черепків — відмова без списання
        _opts.MaxTableBet = 0;
        Assert.False(_book.Place("Влад", true, P(5000)).Ok);
        Assert.Equal(900, Balance("Влад"));
    }

    [Fact]
    public void Settling_pays_winners_once_losers_get_nothing_and_a_second_settle_pays_nothing()
    {
        var w = _book.Place("Влад", true, new BetPlace("table", "r3:1", "win", "Влад", "Влад", 2.4, 50)).Bet!;
        var l = _book.Place("Оля", true, new BetPlace("table", "r3:1", "win", "Петро", "Петро", 3, 100)).Bet!;
        var s = _book.Settle("table", "r3:1", b => b.Option == "Влад" ? BetVerdict.Won : BetVerdict.Lost);
        Assert.Equal(2, s.Closed.Count);
        Assert.Equal(100 - 70, s.Glek);   // Оля програла 100, Владу Глек доплатив 70
        Assert.Equal(1000 - 50 + 120, Balance("Влад"));
        Assert.Equal(900, Balance("Оля"));
        Assert.Equal("won", _store.Bet(w.Id)!.Status);
        Assert.Equal(120, _store.Bet(w.Id)!.Payout);
        Assert.Equal("lost", _store.Bet(l.Id)!.Status);

        var again = _book.Settle("table", "r3:1", _ => BetVerdict.Won);
        Assert.Empty(again.Closed);
        Assert.Equal(1070, Balance("Влад"));
        Assert.Equal(900, Balance("Оля"));
        Assert.Empty(_book.Open("table", "r3:1"));
    }

    [Fact]
    public void Settle_after_a_crash_between_money_and_state_does_not_pay_twice()
    {
        var b = _book.Place("Влад", true, new BetPlace("table", "r4:1", "win", "Влад", "Влад", 2, 100)).Bet!;
        // виплата пройшла, а стан записати не встигли (падіння)
        Assert.Equal(GrantResult.Applied, _eco.Economy.Grant("Влад", 200, "bet-win:table", BetBook.WinRef(b.Id)));
        var s = _book.Settle("table", "r4:1", _ => BetVerdict.Won);
        Assert.Single(s.Closed);
        Assert.Equal(1100, Balance("Влад"));
        Assert.Equal("won", _store.Bet(b.Id)!.Status);
    }

    [Fact]
    public void Refund_returns_every_open_stake_once()
    {
        _book.Place("Влад", true, new BetPlace("table", "r5:1", "win", "Оля", "Оля", 2, 30));
        _book.Place("Влад", true, new BetPlace("table", "r5:1", "win", "Петро", "Петро", 2, 20));
        var s = _book.Refund("table", "r5:1", "склад змінився — ставки повернуто");
        Assert.Equal(2, s.Closed.Count);
        Assert.Equal(0, s.Glek);
        var who = Assert.Single(s.ByNick());
        Assert.True(who.AllBack);
        Assert.Equal(50, who.Paid);
        Assert.Equal(1000, Balance("Влад"));
        Assert.Empty(_book.Refund("table", "r5:1", "ще раз").Closed);
        Assert.Equal(1000, Balance("Влад"));
        Assert.All(_book.All("table", "r5:1"), b => Assert.Equal("back", b.Status));
    }

    [Fact]
    public void A_bet_row_left_without_its_debit_by_a_crash_disappears_at_start_and_a_paid_one_opens()
    {
        var paid = _store.AddNew(new Bet(0, "table", "r6:1", "win", "x", "x", "Влад", 10, 2, "new", 0, _eco.Clock.UtcNow, null, ""), null)!.Value;
        _eco.Economy.TrySpend("Влад", 10, "bet:table", BetBook.DebitRef(paid));
        var unpaid = _store.AddNew(new Bet(0, "table", "r6:1", "win", "x", "x", "Оля", 10, 2, "new", 0, _eco.Clock.UtcNow, null, ""), null)!.Value;
        var fresh = new BetsStore(_eco.Db);   // перезапуск
        Assert.Equal("open", fresh.Bet(paid)!.Status);
        Assert.Null(fresh.Bet(unpaid));
    }

    [Fact]
    public void Ledger_reasons_read_as_bets_and_stay_off_the_books()
    {
        Assert.True(EconomyStore.OffBook("bet:event"));
        Assert.True(EconomyStore.OffBook("bet-win:table"));
        Assert.True(EconomyStore.OffBook("bet-back:event"));
        Assert.True(EconomyStore.OffBook("buy:3"));
        Assert.False(EconomyStore.OffBook("stake"));
        Assert.False(EconomyStore.Exchange("bet:event"));
        Assert.Equal("🎲 ставка на подію", _eco.Economy.Reason("bet:event"));
        Assert.Equal("🎲 ставка на столі зіграла", _eco.Economy.Reason("bet-win:table"));
        Assert.Equal("🎲 ставку повернуто", _eco.Economy.Reason("bet-back:table"));
        Assert.Equal("bet", PeopleEndpoints.Cat("bet-win:event"));
        Assert.Equal("ставки в Глека", PeopleEndpoints.CatTitle("bet"));

        var b = _book.Place("Влад", true, new BetPlace("table", "r7:1", "win", "x", "x", 3, 100)).Bet!;
        _book.Settle("table", "r7:1", _ => BetVerdict.Won);
        _eco.Economy.Rebuild();   // перерахунок з леджера теж не бачить ставок у «зароблено/витрачено»
        var w = _eco.Economy.Wallet("Влад");
        Assert.Equal(1200, Balance("Влад"));
        Assert.Equal(1000 + _extra["Влад"], w.Earned);
        Assert.Equal(0, w.Spent);
        Assert.Equal("won", _store.Bet(b.Id)!.Status);
    }

    // =============================================================================================
    // Події
    // =============================================================================================

    [Fact]
    public void Draft_is_admin_only_and_open_takes_bets()
    {
        var d = Data(_bets.Create(Admin, new BetEvents.EventRequest("Хто виграє Євро?", "опис", null, null,
            [new(null, "Україна", null, 0.1), new(null, "Інший", 1.05, null)], null, null)));
        var id = d.GetProperty("id").GetInt64();
        Assert.Equal("draft", d.GetProperty("status").GetString());
        Assert.Equal(9.2, d.GetProperty("options")[0].GetProperty("odds").GetDouble());   // кеф із ціни
        Assert.Equal(0.1, d.GetProperty("options")[0].GetProperty("worldP").GetDouble());
        Assert.Empty(Views.Json(_bets.View(Olia)).GetProperty("events").EnumerateArray());
        Assert.Equal(404, Bet(Olia, id, "o1", 10).Status);
        Assert.False(_bets.Create(Olia, new BetEvents.EventRequest("x", "", null, null, [new(null, "a", 2, null), new(null, "b", 2, null)], null, null)).Ok);

        Assert.True(_bets.SetStatus(Admin, id, "open").Ok);
        Assert.Single(Views.Json(_bets.View(Olia)).GetProperty("events").EnumerateArray());
        var placed = Data(Bet(Olia, id, "o1", 10, 9.2));
        Assert.Equal(92, placed.GetProperty("bet").GetProperty("payout").GetInt32());
        Assert.Equal(990, Balance("Оля"));
        Assert.True(_wire.EventsChanged > 0);
    }

    [Fact]
    public void Paused_closed_and_past_closes_at_refuse_new_bets()
    {
        var id = OpenEvent(closes: _eco.Clock.UtcNow.AddHours(1));
        Assert.True(Bet(Vlad, id, "o1", 10).Ok);
        Assert.True(_bets.SetStatus(Admin, id, "paused").Ok);
        Assert.Contains("призупинено", Bet(Vlad, id, "o1", 10).Message);
        Assert.True(_bets.SetStatus(Admin, id, "closed").Ok);
        Assert.Contains("закрито", Bet(Vlad, id, "o1", 10).Message);
        Assert.True(_bets.SetStatus(Admin, id, "open").Ok);
        Assert.True(Bet(Vlad, id, "o2", 10).Ok);
        _eco.Clock.Advance(TimeSpan.FromHours(2));   // час вийшов — прийом закрився сам, розрахунку нема
        Assert.False(Bet(Vlad, id, "o1", 10).Ok);
        Assert.False(Views.Json(_bets.View(Vlad)).GetProperty("events")[0].GetProperty("accepting").GetBoolean());
        // невідомий стан і назад у чернетку — ні
        Assert.False(_bets.SetStatus(Admin, id, "draft").Ok);
        Assert.False(_bets.SetStatus(Admin, id, "won").Ok);
        Assert.False(_bets.SetStatus(Vlad, id, "paused").Ok);
    }

    [Fact]
    public void Events_switched_off_take_no_bets_but_the_admin_can_still_cancel()
    {
        var id = OpenEvent();
        Assert.True(Bet(Vlad, id, "o1", 100).Ok);
        _opts.Events = false;
        Assert.Equal(BetEvents.Off, Bet(Vlad, id, "o1", 10).Message);
        _opts.Events = true;
        _opts.Enabled = false;
        Assert.Equal(BetEvents.Off, Bet(Vlad, id, "o1", 10).Message);
        Assert.True(_bets.Cancel(Admin, id, null).Ok);
        Assert.Equal(1000, Balance("Влад"));
        Assert.Equal(BetBook.AccountsOnly, Bet(Guest, id, "o1", 10).Message);
    }

    [Fact]
    public void Changed_odds_refuse_a_stale_bet_and_old_bets_keep_theirs()
    {
        var id = OpenEvent();
        Assert.True(Bet(Vlad, id, "o1", 100, 2).Ok);
        Assert.True(_bets.Edit(Admin, id, new BetEvents.EventRequest("Реал — Барса", "", null, null,
            [new("o1", "Реал", 1.6, null), new("o2", "Барса", 2.2, null)], null, null)).Ok);
        var stale = Bet(Olia, id, "o1", 100, 2);
        Assert.False(stale.Ok);
        Assert.Equal(409, stale.Status);
        Assert.True(Bet(Olia, id, "o1", 100, 1.6).Ok);
        Assert.True(_bets.Settle(Admin, id, "o1").Ok);
        Assert.Equal(1000 - 100 + 200, Balance("Влад"));   // свій ×2
        Assert.Equal(1000 - 100 + 160, Balance("Оля"));    // новий ×1,6
    }

    [Fact]
    public void An_option_with_bets_cannot_be_removed_but_can_be_renamed()
    {
        var id = OpenEvent();
        Assert.True(Bet(Vlad, id, "o2", 10).Ok);
        var drop = _bets.Edit(Admin, id, new BetEvents.EventRequest("Реал — Барса", "", null, null,
            [new("o1", "Реал", 2, null), new(null, "Нічия", 3, null)], null, null));
        Assert.False(drop.Ok);
        Assert.Contains("Барса", drop.Message);
        var ok = Data(_bets.Edit(Admin, id, new BetEvents.EventRequest("Реал — Барселона", "", null, null,
            [new("o1", "Реал", 2, null), new("o2", "Барселона", 1.5, null), new(null, "Нічия", 3, null)], null, null)));
        Assert.Equal(["o1", "o2", "o3"], ok.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("key").GetString()));
        // дві однакові назви, один варіант, кеф поза межами — ні
        Assert.False(_bets.Edit(Admin, id, new BetEvents.EventRequest("x", "", null, null, [new("o1", "Реал", 2, null), new("o2", "реал", 2, null)], null, null)).Ok);
        Assert.False(_bets.Edit(Admin, id, new BetEvents.EventRequest("x", "", null, null, [new("o1", "Реал", 2, null)], null, null)).Ok);
        Assert.False(_bets.Edit(Admin, id, new BetEvents.EventRequest("x", "", null, null, [new("o1", "Реал", 0.5, null), new("o2", "Б", 2, null)], null, null)).Ok);
    }

    [Fact]
    public void Settle_notifies_each_player_once_and_cannot_repeat_or_be_cancelled()
    {
        var id = OpenEvent();
        Assert.True(Bet(Vlad, id, "o1", 100).Ok);
        Assert.True(Bet(Vlad, id, "o2", 50).Ok);
        Assert.True(Bet(Olia, id, "o2", 40).Ok);
        Assert.False(_bets.Settle(Admin, id, "o9").Ok);
        var r = _bets.Settle(Admin, id, "o1");
        Assert.True(r.Ok, r.Message);
        Assert.Equal(1000 - 150 + 200, Balance("Влад"));
        Assert.Equal(960, Balance("Оля"));
        Assert.Contains(_wire.Mine, m => m.Nick == "Влад" && m.Text == "🎲 Реал — Барса: +200 🏺" && !m.Toast);
        Assert.Contains(_wire.Mine, m => m.Nick == "Оля" && m.Text == "🎲 Реал — Барса: не зіграло" && m.Toast);
        Assert.Equal(2, _wire.Mine.Count);

        Assert.False(_bets.Settle(Admin, id, "o2").Ok);
        Assert.False(_bets.Cancel(Admin, id, null).Ok);
        Assert.Equal(1050, Balance("Влад"));
        var ev = Views.Json(_bets.View(Vlad)).GetProperty("done")[0];
        Assert.Equal("settled", ev.GetProperty("status").GetString());
        Assert.Equal("o1", ev.GetProperty("winner").GetString());
        Assert.Equal(-10, ev.GetProperty("glek").GetInt32());   // 50 + 40 програли, 100 Глек доплатив
    }

    [Fact]
    public void Cancel_returns_every_stake_and_says_so()
    {
        var id = OpenEvent();
        Assert.True(Bet(Vlad, id, "o1", 100).Ok);
        Assert.True(Bet(Olia, id, "o2", 40).Ok);
        Assert.True(_bets.Cancel(Admin, id, "матч перенесли").Ok);
        Assert.Equal(1000, Balance("Влад"));
        Assert.Equal(1000, Balance("Оля"));
        Assert.Contains(_wire.Mine, m => m.Nick == "Оля" && m.Text.Contains("скасовано (матч перенесли)") && m.Text.Contains("+40"));
        Assert.False(_bets.Settle(Admin, id, "o1").Ok);
        Assert.False(Bet(Vlad, id, "o1", 10).Ok);
        Assert.Equal(1000, Balance("Влад"));
    }

    [Fact]
    public void Mine_lists_both_sources_with_event_titles()
    {
        var id = OpenEvent();
        Assert.True(Bet(Vlad, id, "o1", 100).Ok);
        Assert.True(_book.Place("Влад", true, new BetPlace("table", "r9:1", "win", "Оля", "хто виграє: Оля", 2, 20)).Ok);
        _book.Settle("table", "r9:1", _ => BetVerdict.Lost);
        var m = Data(_bets.Mine(Vlad, null));
        var open = Assert.Single(m.GetProperty("open").EnumerateArray());
        Assert.Equal("Реал — Барса", open.GetProperty("eventTitle").GetString());
        Assert.Equal(200, open.GetProperty("bet").GetProperty("payout").GetInt32());
        var hist = Assert.Single(m.GetProperty("history").EnumerateArray());
        Assert.Equal("table", hist.GetProperty("bet").GetProperty("source").GetString());
        Assert.Equal(-20, m.GetProperty("net").GetInt32());
        Assert.False(_bets.Mine(Guest, null).Ok);
    }

    [Fact]
    public void Event_cap_is_per_person_per_event()
    {
        _opts.MaxEventBet = 150;
        var id = OpenEvent();
        Assert.True(Bet(Vlad, id, "o1", 100).Ok);
        Assert.False(Bet(Vlad, id, "o2", 100).Ok);
        Assert.True(Bet(Vlad, id, "o2", 50).Ok);
        Assert.True(Bet(Olia, id, "o2", 150).Ok);
        Assert.True(Bet(Vlad, OpenEvent("Інша"), "o1", 150).Ok);
    }

    // =============================================================================================
    // Polymarket
    // =============================================================================================

    const string Feed = """
        [
          {"title":"Ballon d'Or Winner 2026","slug":"ballon-dor-2026","endDate":"2026-12-01T00:00:00Z","volume":"123456.5","volume24hr":999,
           "negRisk":true,"active":true,"closed":false,"markets":[
             {"id":"1","question":"Mbappé?","groupItemTitle":"Kylian Mbappé","outcomes":"[\"Yes\", \"No\"]","outcomePrices":"[\"0.3\", \"0.7\"]","closed":false,"active":true},
             {"id":"2","question":"Haaland?","groupItemTitle":"Erling Haaland","outcomes":"[\"Yes\", \"No\"]","outcomePrices":"[\"0.5\", \"0.5\"]","closed":false,"active":true},
             {"id":"3","question":"Gone?","groupItemTitle":"Retired","outcomes":"[\"Yes\", \"No\"]","outcomePrices":"[\"0.2\", \"0.8\"]","closed":true,"active":true},
             {"id":"4","question":"Zero?","groupItemTitle":"Nobody","outcomes":"[\"Yes\", \"No\"]","outcomePrices":"[\"0\", \"1\"]","closed":false,"active":true}
           ]},
          {"title":"Dynamo vs Shakhtar","slug":"dyn-shakh","endDate":"2026-10-20T18:00:00Z","volume":10,"active":true,"closed":false,"markets":[
             {"id":"9","question":"Dynamo vs Shakhtar","outcomes":"[\"Dynamo\", \"Shakhtar\"]","outcomePrices":"[\"0.45\", \"0.55\"]","closed":false,"active":true}
           ]}
        ]
        """;

    [Fact]
    public async Task Feed_makes_cards_from_the_tag_and_caches_them()
    {
        _http.Bodies["/events?"] = Feed;
        var r = await _bets.PmFeed(Admin, "soccer", null, 0);
        var d = Data(r);
        Assert.Contains("tag_slug=soccer", _http.Urls[0]);
        Assert.Contains("order=volume24hr", _http.Urls[0]);
        var cards = d.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, cards.Count);
        Assert.Equal("https://polymarket.com/event/ballon-dor-2026", cards[0].GetProperty("url").GetString());
        Assert.Equal(123456.5, cards[0].GetProperty("volume").GetDouble());
        // Yes/No-ринки → кандидати з ціною Yes, без закритих і нульових, від найімовірнішого
        Assert.Equal(["Erling Haaland", "Kylian Mbappé"], cards[0].GetProperty("top").EnumerateArray().Select(o => o.GetProperty("title").GetString()));
        Assert.Equal(["Dynamo", "Shakhtar"], cards[1].GetProperty("top").EnumerateArray().Select(o => o.GetProperty("title").GetString()));

        Assert.True((await _bets.PmFeed(Admin, "soccer", null, 0)).Ok);
        Assert.Single(_http.Urls);   // друге — з кешу
        _eco.Clock.Advance(TimeSpan.FromSeconds(91));
        Assert.True((await _bets.PmFeed(Admin, "soccer", null, 0)).Ok);
        Assert.Equal(2, _http.Urls.Count);

        Assert.True((await _bets.PmFeed(Admin, "soon", null, 0)).Ok);
        Assert.Contains("order=endDate&ascending=true&end_date_min=", _http.Urls[^1]);
        Assert.False((await _bets.PmFeed(Admin, "nope", null, 0)).Ok);
    }

    [Fact]
    public async Task Search_skips_closed_events_and_guests_cannot_browse()
    {
        _http.Bodies["/public-search?"] = """
            {"events":[{"title":"Old","slug":"old","closed":true,"active":true,"markets":[]},
                       {"title":"Zelenskyy out?","slug":"zel","closed":false,"active":true,"markets":[
                         {"id":"5","question":"Zelenskyy out?","outcomes":"[\"Yes\",\"No\"]","outcomePrices":"[\"0.1\",\"0.9\"]"}]}],
             "pagination":{"hasMore":true}}
            """;
        var d = Data(await _bets.PmFeed(Olia, null, "ukraine", 20));
        Assert.Contains("q=ukraine", _http.Urls[0]);
        Assert.Contains("page=2", _http.Urls[0]);
        var card = Assert.Single(d.GetProperty("items").EnumerateArray());
        Assert.Equal(["Так", "Ні"], card.GetProperty("top").EnumerateArray().Select(o => o.GetProperty("title").GetString()));
        Assert.True(d.GetProperty("more").GetBoolean());
        Assert.Equal(403, (await _bets.PmFeed(Guest, null, "x", 0)).Status);
    }

    [Fact]
    public async Task Event_draft_comes_with_prices_odds_and_market_choice()
    {
        _http.Bodies["/events/slug/ballon-dor-2026"] = JsonDocument.Parse(Feed).RootElement[0].GetRawText();
        _http.Bodies["/events/slug/more"] = """
            {"title":"A vs B - More Markets","slug":"more","endDate":"2026-10-20T18:00:00Z","markets":[
              {"id":"m1","question":"A (-1.5)","outcomes":"[\"A\",\"B\"]","outcomePrices":"[\"0.4\",\"0.6\"]"},
              {"id":"m2","question":"Total 2.5","outcomes":"[\"Over\",\"Under\"]","outcomePrices":"[\"0.55\",\"0.45\"]"}]}
            """;
        var d = Data(await _bets.PmEvent(Admin, "ballon-dor-2026", null));
        Assert.Equal("Ballon d'Or Winner 2026", d.GetProperty("title").GetString());
        var first = d.GetProperty("options")[0];
        Assert.Equal("Erling Haaland", first.GetProperty("title").GetString());
        Assert.Equal(0.5, first.GetProperty("worldP").GetDouble());
        Assert.Equal(1.84, first.GetProperty("odds").GetDouble());
        Assert.Empty(d.GetProperty("markets").EnumerateArray());

        var more = Data(await _bets.PmEvent(Admin, "more", "m2"));
        Assert.Equal(2, more.GetProperty("markets").GetArrayLength());
        Assert.Equal("m2", more.GetProperty("market").GetString());
        Assert.Equal(["Over", "Under"], more.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("title").GetString()));

        var missing = await _bets.PmEvent(Admin, "nope", null);
        Assert.Equal(404, missing.Status);
        Assert.Equal(Polymarket.NotFound, missing.Message);
        Assert.Equal(404, (await _bets.PmEvent(Admin, "../etc", null)).Status);
    }

    [Fact]
    public async Task Polymarket_down_or_slow_is_a_clear_refusal()
    {
        _http.Fail = new HttpRequestException("нема мережі");
        var r = await _bets.PmFeed(Admin, "top", null, 0);
        Assert.Equal(502, r.Status);
        Assert.Equal(Polymarket.Down, r.Message);
        _http.Fail = null;
        _http.Hang = true;   // таймаут 1 с із конфігу
        var slow = await _bets.PmEvent(Admin, "ballon-dor-2026", null);
        Assert.Equal(Polymarket.Down, slow.Message);
        _http.Hang = false;
        _http.Bodies["/events?"] = "не json";
        Assert.Equal(Polymarket.Down, (await _bets.PmFeed(Admin, "crypto", null, 0)).Message);
    }

    [Fact]
    public async Task Fresh_prices_sit_next_to_our_options_without_saving()
    {
        _http.Bodies["/events/slug/ballon-dor-2026"] = JsonDocument.Parse(Feed).RootElement[0].GetRawText();
        var id = Data(_bets.Create(Admin, new BetEvents.EventRequest("Золотий м'яч", "", "ballon-dor-2026", null,
            [new(null, "Erling Haaland", 3, 0.3), new(null, "Інший", 1.2, null)], "open", null))).GetProperty("id").GetInt64();
        var d = Data(await _bets.Prices(Admin, id, null));
        var h = d.GetProperty("options")[0];
        Assert.Equal(0.5, h.GetProperty("freshP").GetDouble());
        Assert.Equal(1.84, h.GetProperty("freshOdds").GetDouble());
        Assert.Equal(3, h.GetProperty("odds").GetDouble());
        Assert.Equal(JsonValueKind.Null, d.GetProperty("options")[1].GetProperty("freshP").ValueKind);
        Assert.Equal(3, _store.Event(id)!.Options[0].Odds);   // нічого не збережено
    }

    // =============================================================================================
    // Пропозиції
    // =============================================================================================

    [Fact]
    public async Task Suggestions_are_for_accounts_limited_and_answered_personally()
    {
        _opts.SuggestPending = 2;
        Assert.Equal(403, (await _bets.Suggest(Guest, "Чи буде сніг?", null)).Status);
        Assert.False((await _bets.Suggest(Olia, "", null)).Ok);
        Assert.False((await _bets.Suggest(Olia, new string('а', 301), null)).Ok);
        var a = Data(await _bets.Suggest(Olia, "Чи буде сніг на Новий рік?", null)).GetProperty("id").GetInt64();
        _http.Bodies["/events/slug/zel"] = """{"title":"Zelenskyy out?","slug":"zel","markets":[]}""";
        var b = Data(await _bets.Suggest(Olia, null, "zel"));
        Assert.Equal("Zelenskyy out?", b.GetProperty("text").GetString());   // без тексту — назва з Polymarket
        Assert.Contains("чекають", (await _bets.Suggest(Olia, "третя", null)).Message);
        Assert.Equal(Polymarket.NotFound, (await _bets.Suggest(Vlad, null, "nope")).Message);
        Assert.Contains(_wire.Suggest, s => s.Pending == 2 && s.Toast!.Contains("Оля"));

        var desk = Data(_bets.Admin(Admin));
        Assert.Equal(2, desk.GetProperty("suggestions").GetArrayLength());
        Assert.False(_bets.Admin(Olia).Ok);

        // «Додати» = подія з пропозиції — автор отримує рядок
        Assert.True(_bets.Create(Admin, new BetEvents.EventRequest("Сніг на Новий рік", "", null, null,
            [new(null, "Так", 3, null), new(null, "Ні", 1.3, null)], "open", a)).Ok);
        Assert.Equal("added", _store.Suggestion(a)!.Status);
        Assert.Contains(_wire.Mine, m => m.Nick == "Оля" && m.Text.Contains("додано") && m.Text.Contains("уже можна ставити"));

        var bid = b.GetProperty("id").GetInt64();
        Assert.True(_bets.SuggestionRejected(Admin, bid, "уже є").Ok);
        Assert.Contains(_wire.Mine, m => m.Nick == "Оля" && m.Text.Contains("відхилено (уже є)"));
        Assert.False(_bets.SuggestionRejected(Admin, bid, null).Ok);
        Assert.False(_bets.SuggestionAdded(Admin, bid, null).Ok);
        // місце звільнилось
        Assert.True((await _bets.Suggest(Olia, "нова", null)).Ok);
        Assert.Equal(3, Views.Json(_bets.View(Olia)).GetProperty("suggestions").GetArrayLength());
    }

    // =============================================================================================
    // «Дядько Глек у мінусі»
    // =============================================================================================

    [Fact]
    public void Glek_stats_count_both_sources_by_period()
    {
        var id = OpenEvent();
        Assert.True(Bet(Vlad, id, "o1", 100).Ok);   // ×2 → +100 Владу
        Assert.True(Bet(Olia, id, "o2", 40).Ok);    // −40 Олі
        Assert.True(_bets.Settle(Admin, id, "o1").Ok);
        _eco.Clock.Advance(TimeSpan.FromDays(10));
        _book.Place("Петро", true, new BetPlace("table", "t1:1", "win", "Оля", "хто виграє: Оля", 3, 30));
        _book.Place("Влад", true, new BetPlace("table", "t1:1", "win", "Петро", "хто виграє: Петро", 2, 10));
        _book.Settle("table", "t1:1", b => b.Option == "Оля" ? BetVerdict.Won : BetVerdict.Lost);   // Петро +60, Влад −10
        _book.Place("Оля", true, new BetPlace("table", "t2:1", "win", "x", "x", 2, 500));
        _book.Refund("table", "t2:1", "склад змінився");   // повернення — не в сальдо

        var g = Views.Json(_bets.Glek());
        var all = g.GetProperty("saldo").GetProperty("all");
        Assert.Equal(-60, all.GetProperty("events").GetInt32());
        Assert.Equal(-50, all.GetProperty("tables").GetInt32());
        Assert.Equal(-110, all.GetProperty("total").GetInt32());
        var week = g.GetProperty("saldo").GetProperty("week");
        Assert.Equal(0, week.GetProperty("events").GetInt32());
        Assert.Equal(-50, week.GetProperty("tables").GetInt32());

        Assert.Equal(["Влад", "Петро"], g.GetProperty("beat").EnumerateArray().Select(x => x.GetProperty("nick").GetString()));
        Assert.Equal(90, g.GetProperty("beat")[0].GetProperty("net").GetInt32());
        Assert.Equal(-40, Assert.Single(g.GetProperty("lost").EnumerateArray()).GetProperty("net").GetInt32());
        Assert.Equal(100, g.GetProperty("biggest")[0].GetProperty("net").GetInt32());
        var ev = Assert.Single(g.GetProperty("events").EnumerateArray());
        Assert.Equal(-60, ev.GetProperty("glek").GetInt32());
        Assert.Equal(2, ev.GetProperty("bets").GetInt32());
    }
}
