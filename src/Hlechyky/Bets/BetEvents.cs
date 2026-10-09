using System.Globalization;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Microsoft.Extensions.Options;

namespace Hlechyky.Bets;

/// <summary>Хто прийшов: нік, чи акаунт, чи адмінська кука.</summary>
public sealed record BetActor(string Nick, bool Account, bool Admin)
{
    public static BetActor Of(HttpContext c) => new(Auth.Nick(c), Auth.IsUser(c), Auth.IsAdmin(c));
}

public sealed record BetReply(bool Ok, string Message, int Status = 200, object? Data = null);

/// <summary>Сповіщення ставок: особистий рядок людині, «події змінились» усім, лічильник пропозицій адмінам.</summary>
public interface IBetsWire
{
    /// <summary>
    /// Особистий рядок (розрахунок, повернення, пропозицію додано/відхилено). <paramref name="toast"/> — ще й тост: коли
    /// гроші впали, тост гаманця вже сказав своє, і другий був би луною.
    /// </summary>
    void Mine(string nick, string text, bool toast);
    /// <summary>Події змінились (нова, статус, кефи, розрахунок) — відкриті сторінки перечитують список.</summary>
    void Events();
    /// <summary>Скільки пропозицій чекає — адмінам; <paramref name="toast"/> — рядок про нову.</summary>
    void Suggestions(int pending, string? toast);
}

/// <summary>
/// Події: адмін створює (найчастіше з Polymarket), відкриває, призупиняє, закриває прийом, правит кефи, розраховує чи
/// скасовує; акаунти ставлять. Плюс пропозиції гравців і статистика «Глек у мінусі». Гроші — через <see cref="BetBook"/>.
/// </summary>
public sealed class BetEvents(BetsStore store, BetBook book, Economy economy, Polymarket pm, IBetsWire wire, IClock clock,
    IOptionsMonitor<BetsOptions> opts, ILogger<BetEvents> log)
{
    public const string AdminOnly = "Події веде адмін";
    public const string Off = "Ставки на події зараз вимкнено";
    public const string AllOff = "Ставки зараз вимкнено";
    public const string SuggestAccountsOnly = "Пропонувати можуть лише акаунти — закріпи нік";
    public const int TitleMax = 200, DescriptionMax = 2000, OptionTitleMax = 120, OptionsMax = 120, SuggestMax = 300;
    const int MineMax = 100, DoneMax = 20, AdminMax = 200;
    // Ставка й розрахунок/скасування однієї події не мусять перехреститись: перевірка «прийом відкритий» і списання —
    // під тим самим замком, що й закриття
    readonly object _gate = new();

    BetsOptions O => opts.CurrentValue;

    static string Num(int n) => ShardShop.Num(n);

    bool Accepting(BetEvent e) => e.Status == "open" && (e.ClosesAt is not { } ca || clock.UtcNow < ca);

    // ---------------------------------------------------------------- вигляд

    object OptionView(BetOption o, IReadOnlyList<Bet> bets, bool admin)
    {
        var on = bets.Where(b => b.Option == o.Key && b.Status != "back").ToList();
        return new
        {
            key = o.Key, title = o.Title, odds = o.Odds, worldP = o.WorldP, n = on.Count,
            // адміну — ризик: скільки на варіант поставлено і скільки Глек віддасть, якщо зіграє
            staked = admin ? on.Sum(b => b.Stake) : (int?)null,
            liability = admin ? on.Where(b => b.IsOpen).Sum(b => BetMath.Payout(b.Stake, b.Odds)) : (int?)null,
        };
    }

    static object BetView(Bet b) => new
    {
        id = b.Id, source = b.Source, @ref = b.Ref, market = b.Market, option = b.Option, label = b.Label, nick = b.Nick,
        stake = b.Stake, odds = b.Odds, status = b.Status,
        // відкрита — можливий виграш, закрита — що впало
        payout = b.IsOpen ? BetMath.Payout(b.Stake, b.Odds) : b.Payout,
        at = b.At.UtcDateTime, settledAt = b.SettledAt?.UtcDateTime, note = b.Note,
    };

    object EventView(BetEvent e, BetActor me, bool admin)
    {
        var bets = store.ByRef(BetSources.Event, e.Ref);
        var real = bets.Where(b => b.Status != "back").ToList();
        return new
        {
            id = e.Id, title = e.Title, description = e.Description, source = e.Source, pmSlug = e.PmSlug, pmUrl = e.PmUrl,
            closesAt = e.ClosesAt?.UtcDateTime, status = e.Status, accepting = Accepting(e), winner = e.Winner,
            settledAt = e.SettledAt?.UtcDateTime, note = e.Note,
            options = e.Options.Select(o => OptionView(o, bets, admin)).ToList(),
            bets = real.Count, staked = real.Sum(b => b.Stake),
            glek = e.Status == "settled" ? bets.Sum(b => b.Glek) : (int?)null,
            mine = me.Account ? bets.Where(b => Auth.NickKey(b.Nick) == Auth.NickKey(me.Nick)).Select(BetView).ToList() : [],
            createdBy = admin ? e.CreatedBy : null,
        };
    }

    object Limits() => new { min = O.MinBetOk, maxEvent = O.MaxFor(BetSources.Event), maxTable = O.MaxFor(BetSources.Table), minOdds = O.MinOddsOk, maxOdds = O.MaxOddsOk, margin = O.MarginOk };

    /// <summary>GET /api/bets — сторінка «🎲 Ставки»: живі події, нещодавно розраховані, мої пропозиції, межі.</summary>
    public object View(BetActor me)
    {
        var o = O;
        return new
        {
            on = new { events = o.EventsOn, tables = o.TablesOn },
            account = me.Account,
            admin = me.Admin,
            balance = me.Account ? economy.Balance(me.Nick) : 0,
            limits = Limits(),
            events = o.EventsOn || me.Admin ? store.LiveEvents().Select(e => EventView(e, me, false)).ToList() : [],
            done = o.EventsOn || me.Admin ? store.DoneEvents(DoneMax).Select(e => EventView(e, me, false)).ToList() : [],
            suggestions = me.Account ? store.SuggestionsOf(me.Nick, 20).Select(SuggestionView).ToList() : [],
            suggestPending = o.SuggestPending,
            pendingSuggestions = me.Admin ? store.PendingAll() : (int?)null,
        };
    }

    /// <summary>GET /api/bets/admin — «🛠 Керування»: усі події з ризиком по варіантах, пропозиції нові й закриті.</summary>
    public BetReply Admin(BetActor me)
    {
        if (!me.Admin) return new(false, AdminOnly, 403);
        return new(true, "", Data: new
        {
            events = store.AllEvents(AdminMax).Select(e => EventView(e, me, true)).ToList(),
            suggestions = store.NewSuggestions().Select(SuggestionView).ToList(),
            recentSuggestions = store.RecentSuggestions(30).Select(SuggestionView).ToList(),
            limits = Limits(),
            cats = Polymarket.Cats.Select(c => new { id = c.Id, title = c.Title }).ToList(),
        });
    }

    /// <summary>GET /api/bets/mine — мої ставки обох джерел: активні й історія.</summary>
    public BetReply Mine(BetActor me, int? limit)
    {
        if (!me.Account) return new(false, BetBook.AccountsOnly, 403);
        if (!O.Enabled && !me.Admin) return new(false, AllOff, 404);
        var all = store.Of(me.Nick, Math.Clamp(limit ?? MineMax, 1, 500));
        var titles = all.Where(b => b.Source == BetSources.Event).Select(b => b.Ref).Distinct()
            .Select(r => long.TryParse(r, out var id) ? store.Event(id) : null).OfType<BetEvent>().ToDictionary(e => e.Ref, e => e.Title);
        object View(Bet b) => new
        {
            bet = BetView(b),
            eventTitle = b.Source == BetSources.Event && titles.TryGetValue(b.Ref, out var t) ? t : null,
        };
        var settled = all.Where(b => b.Status is "won" or "lost").ToList();
        return new(true, "", Data: new
        {
            open = all.Where(b => b.IsOpen).Select(View).ToList(),
            history = all.Where(b => !b.IsOpen).Select(View).ToList(),
            // чисте проти Глека з того, що видно в історії
            net = settled.Sum(b => b.Status == "won" ? b.Payout - b.Stake : -b.Stake),
        });
    }

    // ---------------------------------------------------------------- ставка

    public sealed record BetRequest(string? Option, int? Stake, double? Odds, string? Key);

    /// <summary>
    /// POST /api/bets/events/{id}/bet { option, stake, odds?, key? } — поставити. <c>odds</c> — кеф, який людина бачила:
    /// адмін міг змінити його тієї ж секунди, і тоді ставка не йде, а людина бачить новий.
    /// </summary>
    public BetReply Bet(BetActor me, long id, BetRequest r)
    {
        if (!me.Account) return new(false, BetBook.AccountsOnly, 403);
        if (!O.EventsOn) return new(false, Off);
        lock (_gate)
        {
            if (store.Event(id) is not { } e || (e.Status == "draft" && !me.Admin)) return new(false, "Нема такої події", 404);
            if (!Accepting(e))
                return new(false, e.Status switch
                {
                    "paused" => "Прийом ставок призупинено — зазирни трохи згодом",
                    "settled" => "Подію вже розраховано",
                    "cancelled" => "Подію скасовано",
                    "draft" => "Подія ще чернетка",
                    _ => "Прийом ставок закрито — чекаємо результату",
                });
            if (e.Option(r.Option) is not { } opt) return new(false, "Нема такого варіанта");
            if (r.Odds is { } seen && Math.Abs(seen - opt.Odds) > 0.0049)
                return new(false, $"Кеф змінився: тепер {BetMath.Show(opt.Odds)} — глянь і постав ще раз", 409, new { odds = opt.Odds, option = opt.Key });
            var placed = book.Place(me.Nick, me.Account, new BetPlace(BetSources.Event, e.Ref, "event", opt.Key, $"{e.Title} — {opt.Title}",
                opt.Odds, r.Stake ?? 0, r.Key));
            if (!placed.Ok) return new(false, placed.Message, placed.Status);
            if (!placed.Duplicate) wire.Events();
            return new(true, placed.Message, Data: new { bet = BetView(placed.Bet!), duplicate = placed.Duplicate, balance = economy.Balance(me.Nick) });
        }
    }

    // ---------------------------------------------------------------- адмін: створити й правити

    public sealed record OptionRequest(string? Key, string? Title, double? Odds, double? WorldP);
    public sealed record EventRequest(string? Title, string? Description, string? PmSlug, DateTimeOffset? ClosesAt,
        OptionRequest[]? Options, string? Status, long? Suggestion);

    /// <summary>Перевірити й зібрати варіанти: ключі старих лишаються, нові — o1, o2…; кеф — вписаний або з ціни.</summary>
    (List<BetOption>? Options, string? Error) Clean(OptionRequest[]? given, IReadOnlyList<BetOption> old)
    {
        var o = O;
        var list = given ?? [];
        if (list.Length < 2) return (null, "Варіантів — щонайменше два");
        if (list.Length > OptionsMax) return (null, $"Варіантів — до {OptionsMax}");
        var used = new HashSet<string>(old.Select(x => x.Key), StringComparer.Ordinal);
        var next = 1;
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<BetOption>();
        foreach (var x in list)
        {
            var title = (x.Title ?? "").Trim();
            if (title.Length == 0) return (null, "У кожного варіанта мусить бути назва");
            if (title.Length > OptionTitleMax) return (null, $"Назва варіанта — до {OptionTitleMax} знаків");
            if (!titles.Add(title)) return (null, $"Варіант «{title}» двічі");
            double? p = x.WorldP is { } wp && !double.IsNaN(wp) ? Math.Clamp(Math.Round(wp, 4), 0, 1) : null;
            double odds;
            if (x.Odds is { } given1)
            {
                odds = Math.Round(given1, 2, MidpointRounding.AwayFromZero);
                if (!BetMath.Fits(odds, o)) return (null, $"Кеф «{title}» — від {BetMath.Show(o.MinOddsOk)} до {BetMath.Show(o.MaxOddsOk)}");
            }
            else if (p is { } pp) odds = BetMath.Odds(pp, o);
            else return (null, $"Нема кефа для «{title}»");
            string key;
            if (x.Key is { Length: > 0 } k && old.Any(b => b.Key == k)) key = k;
            else
            {
                while (used.Contains("o" + next)) next++;
                key = "o" + next;
                used.Add(key);
            }
            if (!keys.Add(key)) return (null, "Варіант двічі");
            result.Add(new BetOption(key, title, odds, p));
        }
        // Кефи всі з цін, а певняк уперся в найменший кеф: ставка на всі разом була б у плюсі — підрівнюємо (рецензія 09.10).
        // Вписані адміном — як вписав.
        if (list.All(x => x.Odds is null))
            result = [.. result.Zip(BetMath.NoArb([.. result.Select(x => x.Odds)], o), (x, k) => x with { Odds = k })];
        return (result, null);
    }

    static string? Text(string? s, int max, string what, out string clean)
    {
        clean = (s ?? "").Trim();
        return clean.Length > max ? $"{what} — до {max} знаків" : null;
    }

    /// <summary>
    /// POST /api/bets/events { title, description, pmSlug?, closesAt?, options:[{title, odds?, worldP?}], status?: draft|open,
    /// suggestion? } — нова подія (типово чернетка). З пропозиції — пропозицію позначено «додано», автор отримує рядок.
    /// </summary>
    public BetReply Create(BetActor me, EventRequest r)
    {
        if (!me.Admin) return new(false, AdminOnly, 403);
        if (Text(r.Title, TitleMax, "Назва", out var title) is { } e1) return new(false, e1);
        if (title.Length == 0) return new(false, "Подія без назви");
        if (Text(r.Description, DescriptionMax, "Опис", out var description) is { } e2) return new(false, e2);
        var slug = string.IsNullOrWhiteSpace(r.PmSlug) ? null : r.PmSlug.Trim();
        if (slug is not null && !Polymarket.SlugOk(slug)) return new(false, "Дивний slug Polymarket");
        var (options, error) = Clean(r.Options, []);
        if (options is null) return new(false, error!);
        var status = r.Status is "open" ? "open" : "draft";
        var now = clock.UtcNow;
        var ev = new BetEvent(0, title, description, slug is null ? "own" : "polymarket", slug, slug is null ? null : Polymarket.EventUrl(slug),
            r.ClosesAt?.ToUniversalTime(), status, null, me.Nick, now, null, "", options);
        ev = ev with { Id = store.AddEvent(ev) };
        log.LogInformation("Ставки: {Who} додав подію {Id} «{Title}» ({Status}, {N} варіантів)", me.Nick, ev.Id, title, status, options.Count);
        if (r.Suggestion is { } sid && store.Suggestion(sid) is { Status: "new" } s && store.CloseSuggestion(sid, "added", "", ev.Id, now))
        {
            wire.Mine(s.Nick, $"💡 Твоє питання додано: «{title}»{(status == "open" ? " — уже можна ставити" : "")}", true);
            wire.Suggestions(store.PendingAll(), null);
        }
        if (status == "open") wire.Events();
        return new(true, status == "open" ? "Подію відкрито — ставки приймаються" : "Чернетку збережено", Data: EventView(ev, me, true));
    }

    /// <summary>
    /// PUT /api/bets/events/{id} { title, description, closesAt, options } — правити будь-коли до розрахунку. Нові ставки
    /// йдуть за новим кефом, старі лишаються зі своїм. Варіант, на який уже ставили, прибрати не можна.
    /// </summary>
    public BetReply Edit(BetActor me, long id, EventRequest r)
    {
        if (!me.Admin) return new(false, AdminOnly, 403);
        if (Text(r.Title, TitleMax, "Назва", out var title) is { } e1) return new(false, e1);
        if (title.Length == 0) return new(false, "Подія без назви");
        if (Text(r.Description, DescriptionMax, "Опис", out var description) is { } e2) return new(false, e2);
        BetEvent ev;
        lock (_gate)
        {
            if (store.Event(id) is not { } old) return new(false, "Нема такої події", 404);
            if (old.Final) return new(false, old.Status == "settled" ? "Подію вже розраховано" : "Подію скасовано");
            var (options, error) = Clean(r.Options, old.Options);
            if (options is null) return new(false, error!);
            var betOn = store.ByRef(BetSources.Event, old.Ref).Where(b => b.Status != "back").Select(b => b.Option).ToHashSet(StringComparer.Ordinal);
            if (old.Options.FirstOrDefault(x => betOn.Contains(x.Key) && options.All(n => n.Key != x.Key)) is { } gone)
                return new(false, $"На «{gone.Title}» уже є ставки — цей варіант не прибереш (можна перейменувати)");
            store.UpdateEvent(id, title, description, r.ClosesAt?.ToUniversalTime(), options);
            ev = store.Event(id)!;
        }
        log.LogInformation("Ставки: {Who} правив подію {Id}", me.Nick, id);
        if (ev.Status != "draft") wire.Events();
        return new(true, "Збережено", Data: EventView(ev, me, true));
    }

    static readonly Dictionary<string, string[]> Moves = new(StringComparer.Ordinal)
    {
        ["open"] = ["draft", "paused", "closed"],
        ["paused"] = ["open", "closed"],
        ["closed"] = ["open", "paused"],
    };

    /// <summary>POST /api/bets/events/{id}/status { status: open|paused|closed } — відкрити, призупинити, закрити прийом.</summary>
    public BetReply SetStatus(BetActor me, long id, string? status)
    {
        if (!me.Admin) return new(false, AdminOnly, 403);
        if (status is null || !Moves.TryGetValue(status, out var from)) return new(false, "Такого стану нема: open, paused чи closed");
        BetEvent ev;
        lock (_gate)
        {
            if (store.Event(id) is not { } old) return new(false, "Нема такої події", 404);
            if (old.Status == status) return new(true, "Уже так", Data: EventView(old, me, true));
            if (!store.MoveEvent(id, from, status, clock.UtcNow))
                return new(false, old.Final ? "Подію вже закрито назавжди" : "З цього стану так не можна");
            ev = store.Event(id)!;
        }
        log.LogInformation("Ставки: {Who} — подія {Id}: {Status}", me.Nick, id, status);
        wire.Events();
        return new(true, status switch { "open" => "Відкрито — ставки приймаються", "paused" => "Призупинено", _ => "Прийом закрито — чекаємо результату" },
            Data: EventView(ev, me, true));
    }

    /// <summary>
    /// POST /api/bets/events/{id}/settle { winner } — розрахувати: ставки на переможця отримують ставка × кеф, решта згорає.
    /// Повтор нічого не заплатить удруге: закривається лише те, що ще відкрите.
    /// </summary>
    public BetReply Settle(BetActor me, long id, string? winner)
    {
        if (!me.Admin) return new(false, AdminOnly, 403);
        BetEvent ev;
        BetSettlement done;
        lock (_gate)
        {
            if (store.Event(id) is not { } e) return new(false, "Нема такої події", 404);
            if (e.Final) return new(false, e.Status == "settled" ? "Уже розраховано" : "Подію скасовано");
            if (e.Status == "draft") return new(false, "Чернетку не розраховують — її скасовують");
            if (e.Option(winner) is not { } win) return new(false, "Обери варіант, що зіграв");
            done = book.Settle(BetSources.Event, e.Ref, b => b.Option == win.Key ? BetVerdict.Won : BetVerdict.Lost, $"зіграв варіант «{win.Title}»");
            store.MoveEvent(id, ["open", "paused", "closed"], "settled", clock.UtcNow, win.Key);
            ev = store.Event(id)!;
        }
        foreach (var p in done.ByNick())
            wire.Mine(p.Nick, p.Paid > 0 ? $"🎲 {ev.Title}: +{Num(p.Paid)} 🏺" : $"🎲 {ev.Title}: не зіграло", p.Paid == 0);
        log.LogInformation("Ставки: {Who} розрахував подію {Id}: {Winner}, Глек {Glek:+#;-#;0}", me.Nick, id, winner, done.Glek);
        wire.Events();
        return new(true, $"Розраховано: {done.Closed.Count} {Plural(done.Closed.Count, "ставка", "ставки", "ставок")}, Глек {Signed(done.Glek)} 🏺",
            Data: EventView(ev, me, true));
    }

    /// <summary>POST /api/bets/events/{id}/cancel { reason? } — скасувати з будь-якого стану до розрахунку: усі ставки повертаються.</summary>
    public BetReply Cancel(BetActor me, long id, string? reason)
    {
        if (!me.Admin) return new(false, AdminOnly, 403);
        var why = (reason ?? "").Trim();
        if (why.Length > 200) why = why[..200];
        BetEvent ev;
        BetSettlement done;
        lock (_gate)
        {
            if (store.Event(id) is not { } e) return new(false, "Нема такої події", 404);
            if (e.Final) return new(false, e.Status == "settled" ? "Уже розраховано — скасувати не можна" : "Уже скасовано");
            done = book.Refund(BetSources.Event, e.Ref, why.Length > 0 ? "скасовано: " + why : "подію скасовано");
            store.MoveEvent(id, ["draft", "open", "paused", "closed"], "cancelled", clock.UtcNow, note: why);
            ev = store.Event(id)!;
        }
        foreach (var p in done.ByNick())
            wire.Mine(p.Nick, $"🎲 {ev.Title}: подію скасовано{(why.Length > 0 ? $" ({why})" : "")} — ставки повернуто, +{Num(p.Paid)} 🏺", false);
        log.LogInformation("Ставки: {Who} скасував подію {Id}, повернуто {N}", me.Nick, id, done.Closed.Count);
        wire.Events();
        return new(true, done.Closed.Count > 0 ? $"Скасовано, повернуто {done.Closed.Count} {Plural(done.Closed.Count, "ставку", "ставки", "ставок")}" : "Скасовано",
            Data: EventView(ev, me, true));
    }

    /// <summary>
    /// GET /api/bets/events/{id}/pm — «Оновити ціни з Polymarket»: свіжі ціни поруч із нашими варіантами (за назвою) і кефи
    /// з них. Нічого не зберігає — адмін сам вирішує, чи брати «кефи з цін».
    /// </summary>
    public async Task<BetReply> Prices(BetActor me, long id, string? market, CancellationToken ct = default)
    {
        if (!me.Admin) return new(false, AdminOnly, 403);
        if (store.Event(id) is not { } e) return new(false, "Нема такої події", 404);
        if (e.PmSlug is null) return new(false, "Подія своя, не з Polymarket");
        var r = await pm.Event(e.PmSlug, market, ct);
        if (!r.Ok) return new(false, r.Error!, r.Status);
        var fresh = r.Value!.Options.ToDictionary(x => x.Title, x => x.P, StringComparer.OrdinalIgnoreCase);
        return new(true, "", Data: new
        {
            options = e.Options.Select(o => new
            {
                key = o.Key, title = o.Title, odds = o.Odds, worldP = o.WorldP,
                freshP = fresh.TryGetValue(o.Title, out var p) ? p : (double?)null,
                freshOdds = fresh.TryGetValue(o.Title, out var p2) ? BetMath.Odds(p2, O) : (double?)null,
            }).ToList(),
            draft = DraftView(r.Value!),
        });
    }

    // ---------------------------------------------------------------- Polymarket (адмін і акаунти — для пропозицій)

    public async Task<BetReply> PmFeed(BetActor me, string? cat, string? q, int? offset, CancellationToken ct = default)
    {
        if (!me.Admin && !me.Account) return new(false, "Огляд Polymarket — для акаунтів", 403);
        if (!me.Admin && !O.EventsOn) return new(false, Off);
        var r = await pm.Feed(cat, q, offset ?? 0, ct);
        if (!r.Ok) return new(false, r.Error!, r.Status);
        return new(true, "", Data: new
        {
            cats = Polymarket.Cats.Select(c => new { id = c.Id, title = c.Title }).ToList(),
            items = r.Value!.Items.Select(c => new
            {
                slug = c.Slug, title = c.Title, url = c.Url, endDate = c.EndDate?.UtcDateTime, volume = c.Volume, volume24h = c.Volume24h,
                image = c.Image, markets = c.Markets, top = c.Top.Select(o => new { title = o.Title, p = o.P }).ToList(),
            }).ToList(),
            more = r.Value.More,
        });
    }

    object DraftView(PmDraft d) => new
    {
        slug = d.Slug, title = d.Title, description = d.Description, url = d.Url, closesAt = d.EndDate?.UtcDateTime, market = d.Market,
        options = d.Options.Zip(BetMath.NoArb([.. d.Options.Select(o => BetMath.Odds(o.P, O))], O), (o, k) => new { title = o.Title, worldP = o.P, odds = k }).ToList(),
        markets = d.Markets.Select(m => new { id = m.Id, question = m.Question, options = m.Options.Select(o => new { title = o.Title, p = o.P }).ToList() }).ToList(),
    };

    /// <summary>GET /api/bets/pm/event/{slug}?market= — чернетка для редактора: питання, варіанти з цінами й кефами, кінець.</summary>
    public async Task<BetReply> PmEvent(BetActor me, string slug, string? market, CancellationToken ct = default)
    {
        if (!me.Admin && !me.Account) return new(false, "Огляд Polymarket — для акаунтів", 403);
        if (!me.Admin && !O.EventsOn) return new(false, Off);
        var r = await pm.Event(slug, market, ct);
        return r.Ok ? new(true, "", Data: DraftView(r.Value!)) : new(false, r.Error!, r.Status);
    }

    // ---------------------------------------------------------------- пропозиції

    static object SuggestionView(BetSuggestion s) => new
    {
        id = s.Id, nick = s.Nick, text = s.Text, pmSlug = s.PmSlug, pmUrl = s.PmSlug is null ? null : Polymarket.EventUrl(s.PmSlug),
        status = s.Status, reason = s.Reason, eventId = s.EventId, at = s.At.UtcDateTime, doneAt = s.DoneAt?.UtcDateTime,
    };

    /// <summary>
    /// POST /api/bets/suggest { text?, pmSlug? } — «💡 Запропонувати»: своє питання (до 300 знаків) і/або подія з огляду.
    /// Лише акаунти, до <c>SuggestPending</c> в очікуванні. Подію Polymarket перевіряємо: нема — кажемо; Polymarket мовчить —
    /// приймаємо як є (адмін однаково подивиться).
    /// </summary>
    public async Task<BetReply> Suggest(BetActor me, string? text, string? pmSlug, CancellationToken ct = default)
    {
        if (!me.Account) return new(false, SuggestAccountsOnly, 403);
        if (!O.EventsOn) return new(false, Off);
        var t = (text ?? "").Trim();
        if (t.Length > SuggestMax) return new(false, $"Питання — до {SuggestMax} знаків");
        var slug = string.IsNullOrWhiteSpace(pmSlug) ? null : pmSlug.Trim();
        if (slug is not null && !Polymarket.SlugOk(slug)) return new(false, Polymarket.NotFound);
        if (t.Length == 0 && slug is null) return new(false, "Напиши питання або обери подію з огляду");
        if (store.PendingOf(me.Nick) is var pending && pending >= Math.Max(1, O.SuggestPending))
            return new(false, $"Уже {pending} {Plural(pending, "пропозиція", "пропозиції", "пропозицій")} чекають адміна — дочекайся відповіді");
        if (slug is not null)
        {
            var r = await pm.Event(slug, null, ct);
            if (r.Status == 404) return new(false, Polymarket.NotFound);
            if (t.Length == 0 && r.Value is { } d) t = d.Title.Length <= SuggestMax ? d.Title : d.Title[..SuggestMax];
        }
        var id = store.AddSuggestion(me.Nick, t, slug, clock.UtcNow);
        log.LogInformation("Ставки: {Nick} пропонує {Id}: {Text} {Slug}", me.Nick, id, t, slug);
        wire.Suggestions(store.PendingAll(), $"💡 {me.Nick} пропонує ставку: {(t.Length > 80 ? t[..80] + "…" : t)}");
        return new(true, "Передано Глекові — адмін гляне й скаже", Data: SuggestionView(store.Suggestion(id)!));
    }

    /// <summary>POST /api/bets/suggest/{id}/add { eventId } — позначити «додано» (подію вже створено окремо).</summary>
    public BetReply SuggestionAdded(BetActor me, long id, long? eventId)
    {
        if (!me.Admin) return new(false, AdminOnly, 403);
        if (store.Suggestion(id) is not { } s) return new(false, "Нема такої пропозиції", 404);
        var ev = eventId is { } eid ? store.Event(eid) : null;
        if (eventId is not null && ev is null) return new(false, "Нема такої події", 404);
        if (!store.CloseSuggestion(id, "added", "", ev?.Id, clock.UtcNow)) return new(false, "Уже розглянуто");
        wire.Mine(s.Nick, ev is null ? "💡 Твоє питання додано" : $"💡 Твоє питання додано: «{ev.Title}»", true);
        wire.Suggestions(store.PendingAll(), null);
        return new(true, "Додано — автор знає");
    }

    /// <summary>POST /api/bets/suggest/{id}/reject { reason? } — відхилити; автор отримує рядок із причиною.</summary>
    public BetReply SuggestionRejected(BetActor me, long id, string? reason)
    {
        if (!me.Admin) return new(false, AdminOnly, 403);
        if (store.Suggestion(id) is not { } s) return new(false, "Нема такої пропозиції", 404);
        var why = (reason ?? "").Trim();
        if (why.Length > 200) why = why[..200];
        if (!store.CloseSuggestion(id, "rejected", why, null, clock.UtcNow)) return new(false, "Уже розглянуто");
        var what = s.Text.Length > 60 ? s.Text[..60] + "…" : s.Text;
        wire.Mine(s.Nick, $"💡 Твоє питання «{what}» відхилено{(why.Length > 0 ? $" ({why})" : "")}", true);
        wire.Suggestions(store.PendingAll(), null);
        return new(true, "Відхилено — автор знає");
    }

    // ---------------------------------------------------------------- «Дядько Глек у мінусі»

    /// <summary>
    /// GET /api/bets/glek — публічна статистика: сальдо Глека за весь час / місяць / тиждень (окремо події й столи), хто
    /// найбільше обіграв Глека і хто йому найбільше програв (чисте), найбільші виграші, події з сальдо. Плюс — Глек у плюсі.
    /// </summary>
    public object Glek()
    {
        object Saldo(DateTimeOffset since)
        {
            var (ev, tb) = store.Saldo(since);
            return new { total = ev + tb, events = ev, tables = tb };
        }
        var net = store.Net(DateTimeOffset.MinValue);
        var per = store.PerEvent();
        var events = store.AllEvents(AdminMax).Where(e => e.Status != "draft" && per.ContainsKey(e.Ref)).Select(e =>
        {
            var (count, staked, glek) = per[e.Ref];
            return new { id = e.Id, title = e.Title, status = e.Status, bets = count, staked, glek, settledAt = e.SettledAt?.UtcDateTime };
        }).ToList();
        return new
        {
            saldo = new { all = Saldo(DateTimeOffset.MinValue), month = Saldo(Periods.Since("month", clock)), week = Saldo(Periods.Since("week", clock)) },
            beat = net.Where(x => x.Net > 0).OrderByDescending(x => x.Net).Take(10).Select(x => new { nick = x.Nick, net = x.Net, bets = x.Count }).ToList(),
            lost = net.Where(x => x.Net < 0).OrderBy(x => x.Net).Take(10).Select(x => new { nick = x.Nick, net = x.Net, bets = x.Count }).ToList(),
            biggest = store.BiggestWins(10).Select(b => new
            {
                nick = b.Nick, source = b.Source, label = b.Label, stake = b.Stake, odds = b.Odds, payout = b.Payout, net = b.Payout - b.Stake,
                at = (b.SettledAt ?? b.At).UtcDateTime,
            }).ToList(),
            events,
        };
    }

    static string Signed(int n) => n > 0 ? "+" + Num(n) : n < 0 ? "−" + Num(-n) : "0";

    static string Plural(int n, string one, string few, string many)
    {
        var t = n % 100;
        var u = n % 10;
        return t is >= 11 and <= 14 ? many : u == 1 ? one : u is >= 2 and <= 4 ? few : many;
    }
}
