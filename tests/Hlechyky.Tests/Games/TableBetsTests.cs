using System.Text.Json;
using Hlechyky.Bets;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>Компанія на 2–4 з ручним стартом: кінець — «end» з переможцями й очками (команди, нічия, останній).</summary>
public class TestBetsGame : Game
{
    public override GameInfo Info { get; } = new(
        "t-bets", "Тестові ставки", "тестові ставки", GameGroup.Party, 2, 4, Start: StartMode.ByHost,
        Options: [new GameOption("len", "Довжина", [("1", "коротка"), ("2", "довга")], "1")]);

    public override void Start() { }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action != "end") return ActResult.Done;
        var winners = payload.GetProperty("w").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        Dictionary<int, long>? scores = null;
        if (payload.TryGetProperty("s", out var s) && s.ValueKind == JsonValueKind.Array)
        {
            scores = [];
            var i = 0;
            foreach (var v in s.EnumerateArray()) { if (v.ValueKind == JsonValueKind.Number) scores[i] = v.GetInt64(); i++; }
        }
        Ctx.Finish(winners, "кінець", scores);
        return ActResult.Done;
    }

    public override object View(int? seat) => new { turn = 0 };
}

/// <summary>Те саме, але стартує, щойно сіли троє: ставки з лобі мусять повернутись, коли третій сів і стіл пішов сам.</summary>
public sealed class TestBetsFull : TestBetsGame
{
    public override GameInfo Info { get; } = new(
        "t-bets3", "Тестові ставки на трьох", "тестові ставки", GameGroup.Party, 2, 3, Start: StartMode.WhenFull);
}

/// <summary>Те саме з «🤖 + бот» у лобі (справжній SoloBot): поки бот лише покликаний, ставок нема.</summary>
public sealed class TestBetsBot : TestBetsGame
{
    readonly Hlechyky.Games.Impl.SoloBot _solo = new();

    public override GameInfo Info { get; } = new(
        "t-bets-bot", "Тестові ставки з ботом", "тестові ставки", GameGroup.Party, 2, 4, Start: StartMode.ByHost);

    public override bool ActsInLobby => true;

    public override ActResult Act(int seat, string action, JsonElement payload) =>
        action == Hlechyky.Games.Impl.LiveBots.Toggle ? _solo.Switch(Ctx, seat, payload, 4) : base.Act(seat, action, payload);
}

/// <summary>
/// Ставки на столах (bets-contract §4): справжні Rooms (RoomHarness) і справжній BetBook на тимчасовій базі, розсилка —
/// заглушки. Кефи, заборони, повернення на зміні складу, коротка партія, розрахунок, повтор, звірка після перезапуску.
/// </summary>
public sealed class TableBetsTests : IDisposable
{
    sealed class Hook : ITableBetsHook
    {
        public TableBets? Bets;
        public void CrewChanged(string roomId, string refKey) => Bets?.CrewChanged(roomId, refKey);
    }

    sealed class Wire : ITableBetsWire, IBetsWire
    {
        public List<string> Changed { get; } = [];
        public List<(string Nick, string Text)> Mine { get; } = [];
        void ITableBetsWire.Changed(string roomId) => Changed.Add(roomId);
        void IBetsWire.Mine(string nick, string text, bool toast) => Mine.Add((nick, text));
        public void Events() { }
        public void Suggestions(int pending, string? toast) { }
    }

    readonly EconomyRig _eco = new();
    readonly BetsOptions _opts = new();
    readonly Wire _wire = new();
    readonly Hook _hook = new();
    readonly BetsStore _store;
    readonly BetBook _book;
    RoomHarness _h = null!;
    TableBets _bets = null!;

    static readonly BetActor Vlad = new("Влад", true, false);
    static readonly BetActor Olia = new("Оля", true, false);
    static readonly BetActor Petro = new("Петро", true, false);
    static readonly BetActor Ivan = new("Іван", true, false);
    static readonly BetActor Guest = new("гість Вася", false, false);
    const string SpecConn = "c-spec";

    public TableBetsTests()
    {
        var o = new FixedOptions<BetsOptions>(_opts);
        _store = new BetsStore(_eco.Db);
        _book = new BetBook(_store, _eco.Economy, _eco.Clock, o, NullLogger<BetBook>.Instance);
        foreach (var n in new[] { "Влад", "Оля", "Петро", "Іван" }) _eco.Economy.Grant(n, 1000, "award", "seed:" + n);
    }

    public void Dispose() => _eco.Dispose();

    RoomHarness Table(string game)
    {
        var sp = new ServiceCollection().AddSingleton<ITableBetsHook>(_hook).BuildServiceProvider();
        _h = new RoomHarness(game, services: sp);
        _bets = Service(_h);
        _hook.Bets = _bets;
        _bets.Attach();
        return _h;
    }

    TableBets Service(RoomHarness h) => new(h.Rooms, _book, _store, _eco.Economy, _eco.Ratings, h.Events, _eco.Outbox, _wire, _wire,
        h.Clock, new FixedOptions<BetsOptions>(_opts), new FixedOptions<EconomyOptions>(_eco.Options), new ServiceCollection().BuildServiceProvider(),
        NullLogger<TableBets>.Instance);

    int Balance(BetActor a) => _eco.Economy.Balance(a.Nick);

    JsonElement View(BetActor who, string? conn = null) => Views.Json(_bets.View(who, _h.RoomId, conn));

    BetReply Bet(BetActor who, string market, string option, int stake, string? conn = null, double? odds = null, string? key = null) =>
        _bets.Place(who, _h.RoomId, conn, new TableBets.TableBetRequest(market, option, stake, odds, key));

    static string K(BetActor a) => Auth.NickKey(a.Nick);

    void Watch(string conn = SpecConn) => _h.Rooms.Watch(_h.RoomId, conn, Petro.Nick);

    int SeatOf(BetActor a) => Array.FindIndex(_h.Room.Seats, s => s == a.Nick);

    /// <summary>Доіграти партію компанії: час, щоб не вийшла «закоротка», і кінець з переможцями й очками.</summary>
    void End(int[] winners, long?[]? scores = null, bool quick = false)
    {
        if (!quick) _h.Clock.Advance(TimeSpan.FromSeconds(30));
        var r = _h.Act(_h.Room.Seats.ToList().FindIndex(s => s is not null), "end", new { w = winners, s = scores });
        Assert.True(r.Ok, r.Message);
    }

    /// <summary>Дуель: обоє сіли (стіл стартує сам), перша партія дограна — тепер ставлять на «Ще раз».</summary>
    void FinishedDuel()
    {
        Table("t-duel");
        _h.Join(Vlad.Nick);
        _h.Join(Olia.Nick);
        _h.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(_h.Act(0, "win").Ok);
        Watch();
    }

    void Lobby3(string game = "t-bets")
    {
        Table(game);
        _h.Join(Vlad.Nick);
        _h.Join(Olia.Nick);
        if (game == "t-bets") _h.Join(Ivan.Nick);
        Watch();
    }

    double Odds(BetActor who, string market, string option, string? conn = null)
    {
        var m = View(who, conn).GetProperty("markets").EnumerateArray().First(x => x.GetProperty("key").GetString() == market);
        return m.GetProperty("options").EnumerateArray().First(x => x.GetProperty("key").GetString() == option).GetProperty("odds").GetDouble();
    }

    IEnumerable<string> Talk() => _h.Rooms.TableLines(_h.RoomId).Select(l => l.Text);

    /// <summary>Історія гри на трьох з очками (більше — краще): Іван — щоразу останній.</summary>
    void History(string game, int parties)
    {
        for (var i = 0; i < parties; i++)
            _eco.Store.AddResults([
                new ResultRow("old" + i, game, 1, K(Vlad), Vlad.Nick, "win", 30, null, 0, _eco.Clock.UtcNow),
                new ResultRow("old" + i, game, 1, K(Olia), Olia.Nick, "loss", 20, null, 0, _eco.Clock.UtcNow),
                new ResultRow("old" + i, game, 1, K(Ivan), Ivan.Nick, "loss", 10, null, 0, _eco.Clock.UtcNow),
            ]);
    }

    // =============================================================================================
    // Ринки й кефи
    // =============================================================================================

    [Fact]
    public void Duel_has_win_and_draw_markets_with_odds_in_bounds_and_the_margin()
    {
        FinishedDuel();
        var v = View(Petro, SpecConn);
        Assert.True(v.GetProperty("show").GetBoolean());
        Assert.True(v.GetProperty("can").GetBoolean(), v.GetProperty("why").ToString());
        Assert.Equal($"{_h.RoomId}:2", v.GetProperty("ref").GetString());   // ставки — на «Ще раз»
        var markets = v.GetProperty("markets").EnumerateArray().ToList();
        Assert.Equal(["win", "draw"], markets.Select(m => m.GetProperty("key").GetString()));
        var odds = markets.SelectMany(m => m.GetProperty("options").EnumerateArray()).Select(o => o.GetProperty("odds").GetDouble()).ToList();
        Assert.Equal(3, odds.Count);
        Assert.All(odds, x => Assert.InRange(x, _opts.MinOddsOk, _opts.MaxOddsOk));
        // Σ (1 − маржа) / кеф ≈ 1: кефи — з однієї розкладки ймовірностей
        Assert.InRange(odds.Sum(x => (1 - _opts.Margin) / x), 0.98, 1.02);
        // Ело рівне — кефи на обох однакові
        Assert.Equal(odds[0], odds[1]);
    }

    [Fact]
    public void Stronger_duelist_by_elo_gets_the_lower_odds()
    {
        FinishedDuel();
        for (var i = 0; i < 8; i++) _eco.Ratings.Apply("t-duel", (K(Vlad), Vlad.Nick), (K(Olia), Olia.Nick), 1.0);
        Assert.True(Odds(Petro, "win", K(Vlad), SpecConn) < Odds(Petro, "win", K(Olia), SpecConn));
    }

    [Fact]
    public void Company_has_no_draw_market_and_last_only_when_history_shows_the_score_order()
    {
        Lobby3();
        var keys = View(Petro, SpecConn).GetProperty("markets").EnumerateArray().Select(m => m.GetProperty("key").GetString()).ToList();
        Assert.Equal(["win"], keys);

        History("t-bets", 5);
        _h.Clock.Advance(TimeSpan.FromMinutes(1));   // кеш історії
        var v = View(Petro, SpecConn);
        Assert.Equal(["win", "last"], v.GetProperty("markets").EnumerateArray().Select(m => m.GetProperty("key").GetString()));
        // Іван щоразу був останнім і ні разу не вигравав: на «останній» — найнижчий кеф, на перемогу — найвищий
        Assert.True(Odds(Petro, "last", K(Ivan), SpecConn) < Odds(Petro, "last", K(Vlad), SpecConn));
        Assert.True(Odds(Petro, "win", K(Vlad), SpecConn) < Odds(Petro, "win", K(Ivan), SpecConn));
    }

    // =============================================================================================
    // Хто й коли
    // =============================================================================================

    [Fact]
    public void Duelist_cannot_bet_on_the_opponent_or_a_draw_but_can_on_self_and_a_watcher_on_anything()
    {
        FinishedDuel();
        Assert.Contains("суперника", Bet(Vlad, "win", K(Olia), 10).Message);
        Assert.Contains("нічию", Bet(Vlad, "draw", "draw", 10).Message);
        Assert.True(Bet(Vlad, "win", K(Vlad), 10).Ok);
        Assert.True(Bet(Petro, "win", K(Olia), 10, SpecConn).Ok);
        Assert.True(Bet(Petro, "draw", "draw", 10, SpecConn).Ok);
        // заборонене видно й у панелі: кнопка неактивна з підказкою
        var opts = View(Vlad).GetProperty("markets")[0].GetProperty("options").EnumerateArray().ToList();
        Assert.Equal(JsonValueKind.Null, opts.First(o => o.GetProperty("key").GetString() == K(Vlad)).GetProperty("no").ValueKind);
        Assert.Contains("суперника", opts.First(o => o.GetProperty("key").GetString() == K(Olia)).GetProperty("no").GetString());
    }

    [Fact]
    public void Player_of_three_bets_on_others_and_not_on_self_last()
    {
        History("t-bets", 5);
        Lobby3();
        Assert.True(Bet(Vlad, "win", K(Olia), 10).Ok);
        Assert.True(Bet(Vlad, "last", K(Ivan), 10).Ok);
        Assert.Contains("останнім", Bet(Vlad, "last", K(Vlad), 10).Message);
    }

    [Fact]
    public void Guests_strangers_lone_tables_and_games_in_progress_are_refused()
    {
        Table("t-bets");
        _h.Join(Vlad.Nick);
        Assert.False(View(Vlad).GetProperty("show").GetBoolean());   // сам за столом — ставити нема на кого
        _h.Join(Olia.Nick);
        Assert.Contains("акаунт", Bet(Guest, "win", K(Vlad), 10).Message);
        Assert.Contains("для акаунтів", View(Guest).GetProperty("why").GetString());
        Assert.Contains("підійди", Bet(Petro, "win", K(Vlad), 10).Message);   // не сидить і не дивиться
        Assert.True(_h.Start().Ok);
        Watch();
        Assert.Contains("Партія йде", Bet(Petro, "win", K(Vlad), 10, SpecConn).Message);
        _opts.Tables = false;
        Assert.Equal(TableBets.Off, Bet(Petro, "win", K(Vlad), 10, SpecConn).Message);
        Assert.False(View(Petro, SpecConn).GetProperty("show").GetBoolean());
    }

    [Fact]
    public void Stale_odds_unknown_option_and_double_click_key()
    {
        FinishedDuel();
        var r = Bet(Petro, "win", K(Vlad), 10, SpecConn, odds: 7.77);
        Assert.Equal(409, r.Status);
        Assert.Equal(409, Bet(Petro, "win", "nobody", 10, SpecConn).Status);
        var before = Balance(Petro);
        var odds = Odds(Petro, "win", K(Vlad), SpecConn);
        Assert.True(Bet(Petro, "win", K(Vlad), 25, SpecConn, odds, key: "k1").Ok);
        var again = Bet(Petro, "win", K(Vlad), 25, SpecConn, odds, key: "k1");
        Assert.True(again.Ok);
        Assert.Equal(before - 25, Balance(Petro));
        Assert.Single(Talk(), l => l.Contains("Петро ставить 25"));
        Assert.Single(_book.Open(BetSources.Table, $"{_h.RoomId}:2"));
    }

    [Fact]
    public void Table_bet_cap_is_per_person_per_party()
    {
        _opts.MaxTableBet = 100;
        FinishedDuel();
        Assert.True(Bet(Petro, "win", K(Vlad), 60, SpecConn).Ok);
        Assert.Contains("Стеля", Bet(Petro, "win", K(Olia), 60, SpecConn).Message);
    }

    // =============================================================================================
    // Повернення
    // =============================================================================================

    [Fact]
    public void Anyone_sitting_down_or_leaving_or_new_settings_refund_the_open_bets_with_a_line()
    {
        Lobby3();
        var before = Balance(Petro);
        Assert.True(Bet(Petro, "win", K(Vlad), 40, SpecConn).Ok);
        Assert.Equal(before - 40, Balance(Petro));
        _h.Leave(Ivan.Nick);
        Assert.Equal(before, Balance(Petro));
        Assert.Contains(Talk(), l => l.Contains("Склад столу змінився"));
        Assert.Empty(_book.Open(BetSources.Table, $"{_h.RoomId}:1"));

        Assert.True(Bet(Petro, "win", K(Vlad), 40, SpecConn).Ok);
        _h.Join(Ivan.Nick);
        Assert.Equal(before, Balance(Petro));

        Assert.True(Bet(Petro, "win", K(Vlad), 40, SpecConn).Ok);
        Assert.True(_h.Rooms.Reconfigure(_h.RoomId, Vlad.Nick, new Dictionary<string, string> { ["len"] = "2" }).Reply.Ok);
        Assert.Equal(before, Balance(Petro));
        Assert.Contains(_wire.Changed, id => id == _h.RoomId);
    }

    [Fact]
    public void Full_table_that_starts_by_itself_returns_the_lobby_bets()
    {
        Lobby3("t-bets3");   // двоє в лобі, третій — і стіл пішов сам
        var before = Balance(Petro);
        Assert.True(Bet(Petro, "win", K(Vlad), 30, SpecConn).Ok);
        _h.Join(Ivan.Nick);
        Assert.Equal(RoomStatus.Playing, _h.Room.Status);
        Assert.Equal(before, Balance(Petro));
    }

    [Fact]
    public void Leaving_after_the_game_refunds_the_rematch_bets_and_a_new_crew_starts_clean()
    {
        FinishedDuel();
        var before = Balance(Petro);
        Assert.True(Bet(Petro, "win", K(Olia), 30, SpecConn).Ok);
        _h.Leave(Olia.Nick);
        Assert.Equal(before, Balance(Petro));
    }

    [Fact]
    public void Short_game_refunds_everything()
    {
        Lobby3();
        var before = Balance(Petro);
        Assert.True(Bet(Petro, "win", K(Vlad), 50, SpecConn).Ok);
        Assert.True(_h.Start().Ok);
        End([SeatOf(Vlad)], quick: true);
        Assert.Equal(before, Balance(Petro));
        Assert.Contains(Talk(), l => l.Contains("закоротка"));
    }

    [Fact]
    public void Abandoned_table_refunds()
    {
        Lobby3();
        var before = Balance(Petro);
        Assert.True(Bet(Petro, "win", K(Vlad), 50, SpecConn).Ok);
        Assert.True(_h.Start().Ok);
        var e = new RoomFinishedEvent(_h.RoomId, "t-bets", _h.Room.Info, 1, [null, null, null, null],
            new RoomResult([], true, "", null), 0, _h.Clock.UtcNow.AddMinutes(-5), _h.Clock.UtcNow, 40);
        _bets.Settle(e);
        Assert.Equal(before, Balance(Petro));
    }

    [Fact]
    public void Reconcile_after_restart_refunds_bets_of_tables_that_are_gone_or_moved_on()
    {
        FinishedDuel();
        var before = Balance(Petro);
        Assert.True(Bet(Petro, "win", K(Vlad), 30, SpecConn).Ok);
        Assert.Equal(0, _bets.Reconcile());   // стіл є, ставка — на його «Ще раз»
        // Новий процес без цього столу (знімок не відновився): звірка на старті повертає
        var fresh = Service(new RoomHarness("t-duel"));
        Assert.Equal(1, fresh.Reconcile());
        Assert.Equal(before, Balance(Petro));
        Assert.Contains(_wire.Mine, m => m.Nick == Petro.Nick && m.Text.Contains("Стіл закрили"));
        Assert.Equal(0, fresh.Reconcile());   // удруге — нічого

        // Стіл є, але партія вже інша (раунд пішов далі) — теж назад
        Assert.True(Bet(Petro, "win", K(Vlad), 30, SpecConn).Ok);
        _h.Room.Round += 5;
        Assert.Equal(1, _bets.Reconcile());
        Assert.Equal(before, Balance(Petro));
    }

    [Fact]
    public void Finished_round_waits_for_its_settlement_a_little_then_goes_back()
    {
        FinishedDuel();
        Assert.True(_h.Rematch().Ok);
        // ставки на партію, що йде, вже не приймаються; підкладемо відкриту ставку на неї напряму — як після падіння
        var placed = _book.Place(Petro.Nick, true, new BetPlace(BetSources.Table, $"{_h.RoomId}:2", "win", K(Vlad), "x", 2, 20));
        Assert.True(placed.Ok);
        Assert.Equal(0, _bets.Reconcile());
        _h.Room.Status = RoomStatus.Finished;   // перервана перезапуском: дограно, а розрахунку не буде
        _h.Room.FinishedAt = _h.Clock.UtcNow;
        Assert.Equal(0, _bets.Reconcile());
        _h.Clock.Advance(TableBets.SettleWait + TimeSpan.FromSeconds(1));
        Assert.Equal(1, _bets.Reconcile());
    }

    // =============================================================================================
    // Розрахунок
    // =============================================================================================

    [Fact]
    public void Rematch_settles_win_and_loss_with_a_summary_and_never_pays_twice()
    {
        FinishedDuel();
        var petro = Balance(Petro);
        var olia = Balance(Olia);
        var odds = Odds(Petro, "win", K(Vlad), SpecConn);
        Assert.True(Bet(Petro, "win", K(Vlad), 100, SpecConn).Ok);
        Assert.True(Bet(Olia, "win", K(Olia), 50).Ok);
        Assert.True(_h.Rematch().Ok);   // місця обертаються — ставки на людей, не на місця
        Assert.Contains("Партія йде", Bet(Petro, "win", K(Vlad), 10, SpecConn).Message);
        _h.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(_h.Act(SeatOf(Vlad), "win").Ok);
        var pay = BetMath.Payout(100, odds);
        Assert.Equal(petro - 100 + pay, Balance(Petro));
        Assert.True(Balance(Olia) <= olia - 50 + 5);   // ставка згоріла (+ хіба нагорода «за участь»)
        Assert.Contains(Talk(), l => l.StartsWith("🎲 Ставки: ") && l.Contains("Петро +") && l.Contains("Оля −50") && l.Contains("Глек"));
        Assert.Contains(_wire.Mine, m => m.Nick == Olia.Nick && m.Text.Contains("Не зіграло"));

        Assert.Empty(_bets.Settle(_h.Finished[^1]).Closed);   // подія вдруге — нічого вдруге
        Assert.Equal(petro - 100 + pay, Balance(Petro));
        // підсумок щойно дограної видно в панелі
        var done = View(Petro, SpecConn).GetProperty("done");
        Assert.Equal(2, done.GetProperty("round").GetInt32());
        Assert.Equal(2, done.GetProperty("bets").GetArrayLength());
    }

    [Fact]
    public void Duel_draw_pays_the_draw_bet_and_burns_the_win_bets()
    {
        FinishedDuel();
        var before = Balance(Petro);
        var odds = Odds(Petro, "draw", "draw", SpecConn);
        Assert.True(Bet(Petro, "draw", "draw", 20, SpecConn).Ok);
        Assert.True(Bet(Petro, "win", K(Vlad), 20, SpecConn).Ok);
        Assert.True(_h.Rematch().Ok);
        _h.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(_h.Act(0, "draw").Ok);
        Assert.Equal(before - 40 + BetMath.Payout(20, odds), Balance(Petro));
    }

    [Fact]
    public void Company_draw_returns_win_bets_and_team_winners_all_win()
    {
        Lobby3();
        var before = Balance(Petro);
        Assert.True(Bet(Petro, "win", K(Vlad), 10, SpecConn).Ok);
        Assert.True(_h.Start().Ok);
        End([]);
        Assert.Equal(before, Balance(Petro));
        Assert.Contains(Talk(), l => l.Contains("ніхто не виграв"));

        // команда з двох: обидві ставки зіграли
        var ov = Odds(Petro, "win", K(Vlad), SpecConn);
        var oo = Odds(Petro, "win", K(Olia), SpecConn);
        Assert.True(Bet(Petro, "win", K(Vlad), 10, SpecConn).Ok);
        Assert.True(Bet(Petro, "win", K(Olia), 10, SpecConn).Ok);
        Assert.True(Bet(Petro, "win", K(Ivan), 10, SpecConn).Ok);
        Assert.True(_h.Rematch().Ok);
        End([SeatOf(Vlad), SeatOf(Olia)]);
        Assert.Equal(before - 30 + BetMath.Payout(10, ov) + BetMath.Payout(10, oo), Balance(Petro));
    }

    [Fact]
    public void Last_place_pays_the_unique_bottom_and_returns_on_a_tie()
    {
        History("t-bets", 5);
        Lobby3();
        var before = Balance(Petro);
        var odds = Odds(Petro, "last", K(Ivan), SpecConn);
        Assert.True(Bet(Petro, "last", K(Ivan), 10, SpecConn).Ok);
        Assert.True(Bet(Petro, "last", K(Olia), 10, SpecConn).Ok);
        Assert.True(_h.Start().Ok);
        var s = new long?[4];
        s[SeatOf(Vlad)] = 30; s[SeatOf(Olia)] = 20; s[SeatOf(Ivan)] = 5;
        End([SeatOf(Vlad)], s);
        Assert.Equal(before - 20 + BetMath.Payout(10, odds), Balance(Petro));

        // нічия внизу — «останній» не визначився: назад
        var mid = Balance(Petro);
        Assert.True(Bet(Petro, "last", K(Ivan), 10, SpecConn).Ok);
        Assert.True(_h.Rematch().Ok);
        s = new long?[4];
        s[SeatOf(Vlad)] = 30; s[SeatOf(Olia)] = 5; s[SeatOf(Ivan)] = 5;
        End([SeatOf(Vlad)], s);
        Assert.Equal(mid, Balance(Petro));
    }

    [Fact]
    public void Bot_called_in_the_lobby_closes_bets_until_the_first_party_shows_the_crew()
    {
        Table("t-bets-bot");
        _h.Join(Vlad.Nick);
        Watch();
        Assert.False(View(Petro, SpecConn).GetProperty("show").GetBoolean());   // сам і без бота — панелі нема, як і було
        Assert.True(_h.Act(0, "bot").Ok);
        var v = View(Petro, SpecConn);
        Assert.True(v.GetProperty("show").GetBoolean());                        // панель є — і каже, чому не ставлять
        Assert.False(v.GetProperty("can").GetBoolean());
        Assert.Equal(TableBets.BotFirst, v.GetProperty("why").GetString());

        // підсів друг, а бот лишився покликаним — однаково чекаємо першої партії
        _h.Join(Olia.Nick);
        Assert.Equal(TableBets.BotFirst, View(Petro, SpecConn).GetProperty("why").GetString());
        var before = Balance(Petro);
        var r = Bet(Petro, "win", K(Vlad), 10, SpecConn);
        Assert.False(r.Ok);
        Assert.Equal(TableBets.BotFirst, r.Message);
        Assert.Equal(before, Balance(Petro));

        // прогнали бота — ставлять як завжди
        Assert.True(_h.Act(0, "bot", new { on = false }).Ok);
        Assert.True(View(Petro, SpecConn).GetProperty("can").GetBoolean());

        // зіграли — після партії ставки на «Ще раз» як і раніше
        Assert.True(_h.Start().Ok);
        End([0]);
        v = View(Petro, SpecConn);
        Assert.True(v.GetProperty("can").GetBoolean(), v.GetProperty("why").ToString());
        Assert.True(Bet(Petro, "win", K(Vlad), 10, SpecConn).Ok);
    }

    [Fact]
    public void Last_seat_needs_scores_for_everyone_and_winners_at_the_edge()
    {
        RoomFinishedEvent E(int[] w, Dictionary<int, long>? s) => new("r", "g", new TestBetsGame().Info, 1, ["a", "b", "c", null],
            new RoomResult(w, w.Length == 0, "", s), 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 10);
        Assert.Equal(2, TableBets.LastSeat(E([0], new() { [0] = 9, [1] = 5, [2] = 1 })));
        Assert.Equal(0, TableBets.LastSeat(E([2], new() { [0] = 9, [1] = 5, [2] = 1 })));   // менше — краще (гольф)
        Assert.Null(TableBets.LastSeat(E([1], new() { [0] = 9, [1] = 5, [2] = 1 })));      // переможець посередині — не зрозуміло
        Assert.Null(TableBets.LastSeat(E([0], new() { [0] = 9, [1] = 5 })));               // не в усіх очки
        Assert.Null(TableBets.LastSeat(E([], new() { [0] = 9, [1] = 5, [2] = 1 })));       // нічия
        Assert.Null(TableBets.LastSeat(E([0], null)));
    }
}
