using System.Globalization;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Hlechyky.Bets;

/// <summary>Ставки столу змінились (хтось поставив, повернуло, розрахувало) — відкриті картки столу перечитують панель.</summary>
public interface ITableBetsWire
{
    void Changed(string roomId);
}

/// <summary>Через хаб: групі столу (ті, хто сидить і дивиться) — <c>tableBets</c> { room }.</summary>
public sealed class HubTableBetsWire(IHubContext<RadioHub> hub, ILogger<HubTableBetsWire> log) : ITableBetsWire
{
    public void Changed(string roomId) => _ = SendAsync(roomId);

    async Task SendAsync(string roomId)
    {
        try { await hub.Clients.Group(Broadcaster.RoomGroup(roomId)).SendAsync("tableBets", new { room = roomId }); }
        catch (Exception ex) { log.LogWarning(ex, "ставки столу {Room} не розіслали", roomId); }
    }
}

/// <summary>Ринки столу: хто виграє, нічия (дуелі рейтингових ігор), хто останній (де це видно з очок).</summary>
public static class TableMarkets
{
    public const string Win = "win";
    public const string Draw = "draw";
    public const string Last = "last";
    /// <summary>Варіант «переможе бот» (будь-який з ботів за столом).</summary>
    public const string Bot = "bot";
}

/// <summary>Варіант ринку столу з кефом. <see cref="Nick"/> — на кого (null — нічия чи бот).</summary>
public sealed record TableOption(string Key, string Title, string Phrase, double P, double Odds, string? Nick);

public sealed record TableMarket(string Key, string Title, IReadOnlyList<TableOption> Options);

/// <summary>
/// Ставки на столах (bets-contract §4). Ставлять перед партією — у лобі чи після партії на «Ще раз» — на тих, хто сидить;
/// ref — <c>roomId:round</c> тієї партії, що почнеться (<see cref="BetTable.NextRef"/>). Кеф рахується з історії гри
/// (Ело дуелі, частки перемог і останніх місць) і фіксується на ставці.
///
/// Склад, на який ставили, мусить бути тим, що сяде грати: будь-яка зміна складу чи налаштувань (Rooms.CrewChanged →
/// <see cref="CrewChanged"/>) повертає всі відкриті ставки столу, а ставка, під час запису якої стіл змінився, одразу
/// йде назад (перевірка після <see cref="BetBook.Place"/>). Розрахунок — на <see cref="RoomFinishedEvent"/> поза
/// замком; коротка партія чи покинутий стіл — повернення. Стіл зник (прибрали, перезапуск без нього) — звірка
/// <see cref="Reconcile"/> на старті й раз на <see cref="Every"/> повертає все, що лишилось висіти.
///
/// Під замком кімнати тут нічого не робиться: стіл читаємо знімком (<see cref="Rooms.BetTableOf"/>), гроші — поза ним.
/// </summary>
public sealed class TableBets(Rooms rooms, BetBook book, BetsStore store, Economy economy, Ratings ratings, GameEvents events,
    IOutbox outbox, IBetsWire mine, ITableBetsWire wire, IClock clock, IOptionsMonitor<BetsOptions> opts,
    IOptionsMonitor<EconomyOptions> eco, IServiceProvider services, ILogger<TableBets> log) : BackgroundService, ITableBetsHook
{
    public const string Off = "Ставки на столах вимкнено";
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(15);
    /// <summary>Стільки чекаємо розрахунку дограної партії, перш ніж звірка вирішить, що його вже не буде (перервана перезапуском).</summary>
    public static readonly TimeSpan SettleWait = TimeSpan.FromMinutes(2);
    /// <summary>Скільки останніх результатів гри беремо в кефи і як довго їх тримаємо.</summary>
    const int HistoryRows = 4000;
    static readonly TimeSpan StatsLife = TimeSpan.FromSeconds(30);
    /// <summary>Згладжування: новачок — як 5 партій із часткою 1/N.</summary>
    const double PriorGames = 5;
    /// <summary>Нічиї дуелі: апріорні 10% з вагою 10 партій, межі за контрактом.</summary>
    const double DrawPrior = 0.1, DrawPriorGames = 10, DrawMin = 0.02, DrawMax = 0.5;
    /// <summary>Жодний варіант не дешевший за це — кеф не впирається в нескінченність, а Глек не роздає ×1,01 на певняк.</summary>
    const double MinP = 0.01;

    const string Crew = "склад столу змінився — ставки повернуто";
    const string Short = "партія закоротка — ставки повернуто";
    const string Abandoned = "стіл покинули — ставки повернуто";
    const string Gone = "стіл закрили — ставку повернуто";
    const string Changed = "стіл змінився — ставку повернуто";

    BetsOptions O => opts.CurrentValue;
    bool _attached;

    // ================================================================ хто й коли

    /// <summary>Чому за цим столом зараз не ставлять (для всіх); null — ставлять.</summary>
    string? Closed(BetTable? t)
    {
        if (!O.TablesOn) return Off;
        if (t is null) return Games.Say.NoRoom;
        if (InTour(t.Id)) return "На турнірних столах ставок нема";
        // Бота кликали, а він ще не сів: скільки їх буде й на яких місцях, гра вирішить лише на старті — кефи нема з чого рахувати.
        if (t.BotCalled) return BotFirst;
        if (t.Humans < 2) return "Ставки — коли за столом хоча б двоє";
        if (t.Humans < t.Info.MinPlayers) return "Ставки — коли всі сядуть";
        if (t.Status == RoomStatus.Playing) return "Партія йде — ставки на наступну приймаються після неї";
        return null;
    }

    public const string BotFirst = "З ботом ставки — після першої партії, коли видно склад";

    bool InTour(string roomId) => services.GetService<Tournament>()?.Holds(roomId) == true;

    /// <summary>Заборона ставити на свій програш (§4.3): гравцю — ні на суперника в дуелі, ні на нічию, ні на себе «останнім».</summary>
    static string? Forbidden(BetTable t, string nick, string market, TableOption o)
    {
        if (t.MySeat is null) return null;   // глядач — на будь-що
        var me = Auth.NickKey(nick);
        return market switch
        {
            TableMarkets.Draw => "Гравцеві на нічию не можна — це ставка на свій програш",
            TableMarkets.Last when o.Key == me => "На себе «останнім» не можна",
            TableMarkets.Win when Duel(t) && o.Key != me => "На суперника не можна — це ставка на свій програш",
            _ => null,
        };
    }

    /// <summary>Скільки учасників у партії: люди й боти, яких видно.</summary>
    static int Field(BetTable t) => t.Humans + t.Bots.Count(b => b is not null);

    static bool Duel(BetTable t) => Field(t) == 2;

    /// <summary>Ринок «нічия» — лише дуелі рейтингових ігор (§4.2): там нічия — звичайний кінець, і Ело її рахує.</summary>
    static bool DrawGame(GameInfo info) => info.Rated && info.MaxPlayers == 2;

    // ================================================================ кефи

    /// <summary>Історія гри одним проходом: партії, нічиї, хто скільки грав, вигравав і бував останнім; напрям очок.</summary>
    sealed class GameStats
    {
        public int Games, Draws;
        public ScoreOrder Order = ScoreOrder.None;
        public readonly Dictionary<string, (int Games, int Wins, int Scored, int Lasts)> Of = new(StringComparer.Ordinal);
        public DateTimeOffset At;
    }

    readonly Dictionary<string, GameStats> _stats = new(StringComparer.Ordinal);
    readonly object _statsGate = new();

    GameStats Stats(string game)
    {
        var now = clock.UtcNow;
        lock (_statsGate)
            if (_stats.TryGetValue(game, out var cached) && now - cached.At < StatsLife) return cached;
        var rows = store.GameHistory(game, HistoryRows);
        var s = new GameStats { At = now };
        var parties = rows.GroupBy(r => (r.Room, r.Round)).Select(g => g.ToList()).ToList();
        int up = 0, down = 0;
        foreach (var p in parties)
        {
            s.Games++;
            if (p.All(r => r.Outcome == "draw")) s.Draws++;
            foreach (var r in p)
            {
                var x = s.Of.GetValueOrDefault(r.NickKey);
                s.Of[r.NickKey] = (x.Games + 1, x.Wins + (r.Outcome == "win" ? 1 : 0), x.Scored, x.Lasts);
            }
            if (Direction(p) is { } d) { if (d == ScoreOrder.HigherIsBetter) up++; else down++; }
        }
        // Напрям очок — лише коли історія каже його однозначно: ринок «останній» на здогад — це подарунок Глекові чи гравцям
        s.Order = up >= 3 && up >= 4 * down ? ScoreOrder.HigherIsBetter : down >= 3 && down >= 4 * up ? ScoreOrder.LowerIsBetter : ScoreOrder.None;
        if (s.Order != ScoreOrder.None)
            foreach (var p in parties)
            {
                if (p.Count < 3 || p.Any(r => r.Score is null)) continue;
                var worst = s.Order == ScoreOrder.HigherIsBetter ? p.Min(r => r.Score!.Value) : p.Max(r => r.Score!.Value);
                var bottom = p.Where(r => r.Score == worst).ToList();
                foreach (var r in p)
                {
                    var x = s.Of.GetValueOrDefault(r.NickKey);
                    s.Of[r.NickKey] = (x.Games, x.Wins, x.Scored + 1, x.Lasts + (bottom.Count == 1 && bottom[0] == r ? 1 : 0));
                }
            }
        lock (_statsGate) _stats[game] = s;
        return s;
    }

    /// <summary>Партія на 3+ з очками в усіх: переможці — з найбільшими (більше — краще) чи найменшими; інакше не скажеш.</summary>
    static ScoreOrder? Direction(List<BetsStore.GameRow> p)
    {
        if (p.Count < 3 || p.Any(r => r.Score is null)) return null;
        var won = p.Where(r => r.Outcome == "win").ToList();
        if (won.Count == 0 || won.Count == p.Count) return null;
        double max = p.Max(r => r.Score!.Value), min = p.Min(r => r.Score!.Value);
        if (max == min) return null;
        if (won.All(r => r.Score == max)) return ScoreOrder.HigherIsBetter;
        if (won.All(r => r.Score == min)) return ScoreOrder.LowerIsBetter;
        return null;
    }

    /// <summary>
    /// Ринки столу з кефами. Дуель рейтингової гри — з Ело й частки нічиїх; решта — з часток перемог і останніх місць у
    /// цій грі зі згладжуванням до 1/N; бот — 1/N. Варіанти людей — за ключем ніка: «Ще раз» обертає місця, а ставка — на людину.
    /// </summary>
    public IReadOnlyList<TableMarket> Markets(BetTable t)
    {
        var o = O;
        var people = t.Seats.Where(s => s is not null).Select(s => s!).DistinctBy(Auth.NickKey).ToList();
        var bots = t.Bots.Count(b => b is not null);
        var n = people.Count + bots;
        if (people.Count < 2) return [];
        var st = Stats(t.Info.Id);
        var botTitle = bots > 1 ? "🤖 боти" : "🤖 бот";
        var list = new List<TableMarket>();

        double[] win;
        double draw = 0;
        if (DrawGame(t.Info) && n == 2)
        {
            var a = ratings.Of(people[0], t.Info.Id);
            var b = ratings.Of(people[1], t.Info.Id);
            var e = Ratings.Expected(a.Elo, b.Elo);
            draw = Math.Clamp((st.Draws + DrawPrior * DrawPriorGames) / (st.Games + DrawPriorGames), DrawMin, DrawMax);
            win = [Math.Max(MinP, e - draw / 2), Math.Max(MinP, 1 - e - draw / 2)];
        }
        else
        {
            var raw = people.Select(p =>
            {
                var x = st.Of.GetValueOrDefault(Auth.NickKey(p));
                return (x.Wins + PriorGames / n) / (x.Games + PriorGames);
            }).Concat(Enumerable.Repeat(1.0 / n, bots)).ToArray();
            win = Normalize(raw);
        }
        var winOpts = people.Select((p, i) => Option(Auth.NickKey(p), p, "виграє " + p, win[i], p, o)).ToList();
        if (bots > 0) winOpts.Add(Option(TableMarkets.Bot, botTitle, "виграє " + botTitle, win.Skip(people.Count).Sum(), null, o));
        list.Add(new TableMarket(TableMarkets.Win, "🏆 Хто виграє", winOpts));
        if (draw > 0) list.Add(new TableMarket(TableMarkets.Draw, "🤝 Нічия", [Option(TableMarkets.Draw, "нічия", "нічия", draw, null, o)]));

        if (n >= 3 && st.Order != ScoreOrder.None)
        {
            var raw = people.Select(p =>
            {
                var x = st.Of.GetValueOrDefault(Auth.NickKey(p));
                return (x.Lasts + PriorGames / n) / (x.Scored + PriorGames);
            }).Concat(Enumerable.Repeat(1.0 / n, bots)).ToArray();
            var last = Normalize(raw);
            list.Add(new TableMarket(TableMarkets.Last, "🐌 Хто останній",
                people.Select((p, i) => Option(Auth.NickKey(p), p, "останній — " + p, last[i], p, o)).ToList()));
        }
        return list;
    }

    static double[] Normalize(double[] raw)
    {
        var sum = raw.Sum();
        return sum <= 0 ? raw.Select(_ => 1.0 / raw.Length).ToArray() : raw.Select(x => Math.Max(MinP, x / sum)).ToArray();
    }

    static TableOption Option(string key, string title, string phrase, double p, string? nick, BetsOptions o) =>
        new(key, title, phrase, Math.Round(p, 4), BetMath.Odds(p, o), nick);

    // ================================================================ панель столу

    /// <summary>
    /// Усе для панелі «🎲 Ставки» картки столу: чи показувати, чи можна ставити (і чому ні), ринки з кефами й
    /// заборонами, ставки на наступну партію (чи на ту, що йде), підсумок щойно дограної. <paramref name="connId"/> —
    /// з'єднання хаба: глядач — той, хто дивиться стіл саме ним.
    /// </summary>
    public object View(BetActor me, string roomId, string? connId)
    {
        var o = O;
        var t = rooms.BetTableOf(roomId ?? "", me.Nick, connId);
        if (!o.TablesOn || t is null || t.Humans < 2 && t.Status != RoomStatus.Finished && !t.BotCalled || InTour(t.Id))
            return new { ok = true, on = o.TablesOn, show = false };
        var why = Closed(t);
        if (why is null && !me.Account) why = BetBook.AccountsOnly;
        if (why is null && t.MySeat is null && !t.Watching) why = "Спершу підійди до столу";
        var nowRef = t.Status == RoomStatus.Playing ? Rooms.BetRef(t.Id, t.Round) : t.NextRef;
        var bets = book.All(BetSources.Table, nowRef);
        var markets = Closed(t) is null ? Markets(t) : [];
        var myKey = Auth.NickKey(me.Nick);
        object? done = null;
        if (t.Status == RoomStatus.Finished && book.All(BetSources.Table, Rooms.BetRef(t.Id, t.Round)) is { Count: > 0 } prev)
        {
            var s = new BetSettlement(prev.Where(b => !b.IsOpen).Select(b => new BetClosed(b,
                b.Status switch { "won" => BetVerdict.Won, "lost" => BetVerdict.Lost, _ => BetVerdict.Back }, b.Payout)).ToList());
            done = new { round = t.Round, glek = s.Glek, bets = prev.Select(b => BetRow(b, myKey)).ToList() };
        }
        return new
        {
            ok = true, on = true,
            show = markets.Count > 0 || bets.Count > 0 || done is not null || t.BotCalled,   // з ботом — щоб панель сказала, чому ставок ще нема
            account = me.Account,
            can = why is null,
            why,
            @ref = nowRef,
            status = t.Status.ToString().ToLowerInvariant(),
            min = o.MinBetOk,
            max = o.MaxTableBet > 0 ? o.MaxTableBet : 0,
            staked = bets.Where(b => b.IsOpen && Auth.NickKey(b.Nick) == myKey).Sum(b => b.Stake),
            balance = me.Account ? economy.Balance(me.Nick) : 0,
            markets = markets.Select(m => new
            {
                key = m.Key, title = m.Title,
                options = m.Options.Select(x => new
                {
                    key = x.Key, title = x.Title, phrase = x.Phrase, odds = x.Odds, p = x.P,
                    no = me.Account ? Forbidden(t, me.Nick, m.Key, x) : null,
                }).ToList(),
            }).ToList(),
            bets = bets.Select(b => BetRow(b, myKey)).ToList(),
            done,
        };
    }

    static object BetRow(Bet b, string myKey) => new
    {
        id = b.Id, nick = b.Nick, mine = Auth.NickKey(b.Nick) == myKey, market = b.Market, option = b.Option,
        label = Phrase(b.Label), stake = b.Stake, odds = b.Odds, status = b.Status,
        payout = b.IsOpen ? BetMath.Payout(b.Stake, b.Odds) : b.Payout,
    };

    /// <summary>Підпис ставки — «Шахи: виграє Оля»; у панелі столу назва гри зайва.</summary>
    static string Phrase(string label) => label.IndexOf(": ", StringComparison.Ordinal) is var i and >= 0 ? label[(i + 2)..] : label;

    // ================================================================ поставити

    public sealed record TableBetRequest(string? Market, string? Option, int? Stake, double? Odds, string? Key);

    public BetReply Place(BetActor me, string roomId, string? connId, TableBetRequest r)
    {
        if (!me.Account) return new(false, BetBook.AccountsOnly, 403);
        var t = rooms.BetTableOf(roomId ?? "", me.Nick, connId);
        if (Closed(t) is { } why) return new(false, why);
        if (t!.MySeat is null && !t.Watching) return new(false, "Спершу підійди до столу");
        var m = Markets(t).FirstOrDefault(x => x.Key == r.Market);
        if (m?.Options.FirstOrDefault(x => x.Key == r.Option) is not { } opt) return new(false, "Нема такого варіанта — глянь на стіл ще раз", 409);
        if (Forbidden(t, me.Nick, m.Key, opt) is { } no) return new(false, no, 403);
        if (r.Odds is { } seen && Math.Abs(seen - opt.Odds) > 0.0049)
            return new(false, $"Кеф змінився: тепер {BetMath.Show(opt.Odds)} — глянь і постав ще раз", 409,
                new { odds = opt.Odds, market = m.Key, option = opt.Key });
        var placed = book.Place(me.Nick, true, new BetPlace(BetSources.Table, t.NextRef, m.Key, opt.Key,
            $"{t.Info.Title}: {opt.Phrase}", opt.Odds, r.Stake ?? 0, r.Key));
        if (!placed.Ok) return new(false, placed.Message, placed.Status);
        var bet = placed.Bet!;
        if (!placed.Duplicate)
        {
            // Поки ставка писалась, стіл міг змінитись (сів, встав, налаштування — їхнє повернення вже пройшло повз неї)
            // або партія — початись. Прийом закривається на старті (§4.1), тож ставка, що дописалась уже в партію, — назад.
            var now = rooms.BetTableOf(t.Id);
            var started = now is { Status: RoomStatus.Playing };
            var stale = now is null || started || now.Crew != t.Crew || now.NextRef != t.NextRef;
            if (stale)
            {
                book.Refund(bet, Changed);
                wire.Changed(t.Id);
                return new(false, started ? "Партія вже почалась — ставку повернуто" : "Стіл змінився, поки ти ставив — ставку повернуто", 409);
            }
            Talk(t.Id, $"🎲 {me.Nick} ставить {Num(bet.Stake)} 🏺 на «{opt.Phrase}» {BetMath.Show(bet.Odds)}");
            wire.Changed(t.Id);
        }
        return new(true, placed.Message, Data: new
        {
            bet = BetRow(bet, Auth.NickKey(me.Nick)), duplicate = placed.Duplicate, balance = economy.Balance(me.Nick),
        });
    }

    // ================================================================ повернення

    /// <summary>Склад чи налаштування столу змінились (Rooms.CrewChanged): усе відкрите на ту партію — назад.</summary>
    public void CrewChanged(string roomId, string refKey)
    {
        try
        {
            var s = book.Refund(BetSources.Table, refKey, Crew);
            if (s.Closed.Count == 0) return;
            Talk(roomId, $"🎲 Склад столу змінився — ставки повернуто ({s.Closed.Count})");
            wire.Changed(roomId);
        }
        catch (Exception ex) { log.LogWarning(ex, "ставки столу {Ref} не повернулись", refKey); }
    }

    /// <summary>
    /// Звірка: відкрита ставка столу живе, лише поки її партія ще може зіграти — наступна партія столу, та, що йде, або
    /// щойно дограна, розрахунок якої ще в дорозі. Решта (стіл прибрали, перезапуск без нього, раунд пішов далі) — назад.
    /// </summary>
    public int Reconcile()
    {
        var back = 0;
        var touched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in book.OpenOf(BetSources.Table))
        {
            var cut = b.Ref.LastIndexOf(':');
            if (cut <= 0 || !int.TryParse(b.Ref[(cut + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var round)) continue;
            var id = b.Ref[..cut];
            var t = rooms.BetTableOf(id);
            var alive = t is not null && (
                (t.Status != RoomStatus.Playing && round == t.NextRound)
                || (t.Status == RoomStatus.Playing && round == t.Round)
                || (t.Status == RoomStatus.Finished && round == t.Round && t.FinishedAt is { } at && clock.UtcNow - at < SettleWait));
            if (alive) continue;
            if (book.Refund(b, t is null ? Gone : Changed) is null) continue;
            back++;
            touched.Add(id);
            mine.Mine(b.Nick, $"🎲 {(t is null ? "Стіл закрили" : "Стіл змінився")} — ставку повернуто: {b.Label}", false);
        }
        foreach (var id in touched) wire.Changed(id);
        if (back > 0) log.LogInformation("Ставки столів: звірка повернула {Count}", back);
        return back;
    }

    // ================================================================ розрахунок

    void OnFinished(RoomFinishedEvent e)
    {
        try { Settle(e); }
        catch (Exception ex) { log.LogWarning(ex, "ставки столу {Room} не розрахувались", e.RoomId); }
    }

    /// <summary>Партія скінчилась: розрахувати її ставки (повтор події — нічого вдруге: закриваються лише відкриті).</summary>
    public BetSettlement Settle(RoomFinishedEvent e)
    {
        if (e.Info.Solo) return BetSettlement.Empty;
        var refKey = Rooms.BetRef(e.RoomId, e.Round);
        if (book.Open(BetSources.Table, refKey).Count == 0) return BetSettlement.Empty;
        var ec = eco.CurrentValue;
        string? back = null;
        if (e.Seats.All(s => s is null)) back = Abandoned;
        else if (e.Moves < ec.MinRewardMoves && (e.FinishedAt - e.StartedAt).TotalSeconds < ec.MinRewardSeconds) back = Short;
        var s = back is not null ? book.Refund(BetSources.Table, refKey, back) : book.Settle(BetSources.Table, refKey, b => Decide(e, b));
        if (s.Closed.Count == 0) return s;
        foreach (var c in s.Closed.Where(c => c.Verdict == BetVerdict.Lost))
            mine.Mine(c.Bet.Nick, $"🎲 Не зіграло: {c.Bet.Label} — {Num(c.Bet.Stake)} 🏺 лишились у Глека", false);
        Talk(e.RoomId, Summary(s, back));
        wire.Changed(e.RoomId);
        return s;
    }

    static BetVerdict Decide(RoomFinishedEvent e, Bet b)
    {
        var winners = e.Result.Winners;
        switch (b.Market)
        {
            case TableMarkets.Win:
                // Нічия: у дуелі рейтингової гри це окремий варіант (ставки на перемогу програли), деінде — ніхто не виграв, назад
                if (winners.Length == 0) return DrawGame(e.Info) ? BetVerdict.Lost : BetVerdict.Back;
                if (b.Option == TableMarkets.Bot)
                    return winners.Any(w => w < e.Seats.Count && e.Seats[w] is null) ? BetVerdict.Won : BetVerdict.Lost;
                return SeatOf(e, b.Option) is { } seat && winners.Contains(seat) ? BetVerdict.Won : BetVerdict.Lost;
            case TableMarkets.Draw:
                return winners.Length == 0 ? BetVerdict.Won : BetVerdict.Lost;
            case TableMarkets.Last:
                if (LastSeat(e) is not { } last || SeatOf(e, b.Option) is not { } mineSeat) return BetVerdict.Back;
                return mineSeat == last ? BetVerdict.Won : BetVerdict.Lost;
            default:
                return BetVerdict.Back;
        }
    }

    static int? SeatOf(RoomFinishedEvent e, string nickKey)
    {
        for (var i = 0; i < e.Seats.Count; i++)
            if (e.Seats[i] is { } n && Auth.NickKey(n) == nickKey) return i;
        return null;
    }

    /// <summary>
    /// Однозначно останній у цій партії: очки є в кожного, хто сидить, учасників з очками 3+, переможці — з крайніми
    /// очками (так видно, що краще — більше чи менше), а внизу рівно один. Інакше — null (ставки «останній» назад).
    /// </summary>
    public static int? LastSeat(RoomFinishedEvent e)
    {
        if (e.Result.Scores is not { } scores || e.Result.Winners.Length == 0) return null;
        for (var i = 0; i < e.Seats.Count; i++)
            if (e.Seats[i] is not null && !scores.ContainsKey(i)) return null;
        if (scores.Count < 3) return null;
        long max = scores.Values.Max(), min = scores.Values.Min();
        if (max == min) return null;
        var w = e.Result.Winners.Where(scores.ContainsKey).Select(x => scores[x]).ToList();
        if (w.Count != e.Result.Winners.Length) return null;
        long worst;
        if (w.All(x => x == max)) worst = min;
        else if (w.All(x => x == min)) worst = max;
        else return null;
        var bottom = scores.Where(kv => kv.Value == worst).Select(kv => kv.Key).ToList();
        return bottom.Count == 1 ? bottom[0] : null;
    }

    /// <summary>«🎲 Ставки: Smaug +240, Владік −50. Глек −190» — або чому все повернуто.</summary>
    static string Summary(BetSettlement s, string? back)
    {
        if (back is not null) return "🎲 " + char.ToUpperInvariant(back[0]) + back[1..];
        var people = s.ByNick().Where(x => !x.AllBack).Select(x => $"{x.Nick} {Signed(x.Net)}").ToList();
        if (people.Count == 0) return "🎲 Ставки повернуто — ніхто не виграв";
        return $"🎲 Ставки: {string.Join(", ", people)}. Глек {Signed(s.Glek)}";
    }

    static string Signed(int n) => n > 0 ? "+" + Num(n) : n < 0 ? "−" + Num(-n) : "0";

    static string Num(int n) => ShardShop.Num(n);

    void Talk(string roomId, string text)
    {
        try { foreach (var m in rooms.BetSay(roomId, text)) outbox.Post(m); }
        catch (Exception ex) { log.LogWarning(ex, "рядок ставок у балачку столу {Room} не ліг", roomId); }
    }

    // ================================================================ життя сервісу

    public override Task StartAsync(CancellationToken ct)
    {
        Attach();
        return base.StartAsync(ct);
    }

    public override Task StopAsync(CancellationToken ct)
    {
        if (_attached) { events.RoomFinished -= OnFinished; _attached = false; }
        return base.StopAsync(ct);
    }

    /// <summary>Слухати кінці партій (тести кличуть самі, без хоста).</summary>
    public void Attach()
    {
        if (_attached) return;
        events.RoomFinished += OnFinished;
        _attached = true;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Перша звірка — одразу: столи вже відновлено (Program.cs кличе RestoreAtStart до Run), і ставки столів, яких
        // після перезапуску нема, мають повернутись, а не висіти до першого кроку таймера.
        Safe();
        using var timer = new PeriodicTimer(Every);
        try { while (await timer.WaitForNextTickAsync(ct)) Safe(); }
        catch (OperationCanceledException) { }

        void Safe()
        {
            try { Reconcile(); }
            catch (Exception ex) { log.LogWarning(ex, "звірка ставок столів упала"); }
        }
    }
}
