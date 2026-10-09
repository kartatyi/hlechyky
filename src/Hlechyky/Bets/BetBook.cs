using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Microsoft.Extensions.Options;

namespace Hlechyky.Bets;

/// <summary>Що ставлять: джерело, ref (id події / <c>roomId:round</c>), ринок, варіант, підпис для людей, кеф і сума.</summary>
/// <param name="Key">Ключ ідемпотентності від клієнта (подвійний клік, повтор запиту): та сама людина з тим самим ключем
/// отримує ту саму ставку, а гроші не списуються вдруге. null — без захисту від повтору.</param>
public sealed record BetPlace(string Source, string Ref, string Market, string Option, string Label, double Odds, int Stake,
    string? Key = null, string Note = "");

/// <summary>Відповідь ставки: Ok, текст, сама ставка (на повтор — та сама, <see cref="Duplicate"/>).</summary>
public sealed record BetPlaced(bool Ok, string Message, Bet? Bet = null, bool Duplicate = false, int Status = 200);

public enum BetVerdict { Won, Lost, Back }

/// <summary>Як закрилась одна ставка: вердикт і скільки впало людині (виграш, повернення; програш — 0).</summary>
public sealed record BetClosed(Bet Bet, BetVerdict Verdict, int Paid);

/// <summary>Підсумок розрахунку ref: кожна ставка, що закрилась саме цим викликом, і сальдо Глека з них.</summary>
public sealed record BetSettlement(IReadOnlyList<BetClosed> Closed)
{
    public static readonly BetSettlement Empty = new([]);

    /// <summary>Скільки Глек виграв на цьому розрахунку (мінус — програв). Повернення не рахуються.</summary>
    public int Glek => Closed.Sum(c => c.Verdict switch { BetVerdict.Lost => c.Bet.Stake, BetVerdict.Won => c.Bet.Stake - c.Paid, _ => 0 });

    /// <summary>По людях: нік, скільки поставив, скільки впало, чисте (плюс — обіграв Глека). Порядок — як ставили.</summary>
    public IReadOnlyList<(string Nick, int Staked, int Paid, int Net, bool AllBack)> ByNick() => Closed
        .GroupBy(c => Auth.NickKey(c.Bet.Nick))
        .Select(g => (g.First().Bet.Nick, g.Sum(c => c.Bet.Stake), g.Sum(c => c.Paid),
            g.Sum(c => c.Verdict == BetVerdict.Back ? 0 : c.Paid - c.Bet.Stake), g.All(c => c.Verdict == BetVerdict.Back)))
        .ToList();
}

/// <summary>
/// Ядро ставок — спільне для подій і столів: поставити, розрахувати ref, повернути все відкрите на ref. Правила джерела
/// (чи відкритий прийом, хто може, який кеф) — у того, хто кличе; тут — гроші, стелі суми й межі кефа.
///
/// Гроші: списання <c>bet:&lt;id&gt;</c>, виграш <c>bet-win:&lt;id&gt;</c>, повернення <c>bet-back:&lt;id&gt;</c> — це й причини, й
/// ключі ідемпотентності в леджері; у «зароблено/витрачено» не йдуть (EconomyStore.OffBook). Порядок завжди «спершу
/// гроші з ref, тоді стан ставки»: падіння між ними лишає ставку відкритою, а повторний розрахунок не заплатить удруге
/// (Duplicate) — лише допише стан. Ставку ж записуємо до списання (у стані <c>new</c>), щоб ref мав id; падіння між
/// записом і списанням BetsStore розбирає при старті.
///
/// Під замком кімнати це не кликати: тут база.
/// </summary>
public sealed class BetBook(BetsStore store, Economy economy, IClock clock, IOptionsMonitor<BetsOptions> opts, ILogger<BetBook> log)
{
    public const string AccountsOnly = "Ставки — для акаунтів: закріпи нік";
    // Ставка й розрахунок одного ref з двох запитів не мусять розминутись: стеля на людину рахується від відкритих ставок
    readonly object _gate = new();

    BetsOptions O => opts.CurrentValue;

    public static string DebitRef(long id) => "bet:" + id;
    public static string WinRef(long id) => "bet-win:" + id;
    public static string BackRef(long id) => "bet-back:" + id;

    static string Num(int n) => ShardShop.Num(n);

    /// <summary>
    /// Поставити: перевіряє акаунт, суму (MinBet, стеля на людину на ref), кеф (у межах, 2 знаки — ровно той, що побачила
    /// людина), списує й записує. Повтор з тим самим <see cref="BetPlace.Key"/> — та сама ставка без другого списання.
    /// </summary>
    public BetPlaced Place(string nick, bool account, BetPlace p)
    {
        if (!account) return new(false, AccountsOnly, Status: 403);
        if (!BetSources.Known(p.Source)) return new(false, "Невідоме джерело ставки", Status: 400);
        var o = O;
        var key = string.IsNullOrWhiteSpace(p.Key) ? null : p.Key.Trim();
        if (key is { Length: > 64 }) key = key[..64];
        if (key is not null && store.ByIdem(nick, key) is { } again) return new(true, "Ставку вже прийнято", again, Duplicate: true);
        if (p.Stake < o.MinBetOk) return new(false, $"Ставка — від {Num(o.MinBetOk)} 🏺");
        if (!BetMath.Fits(p.Odds, o)) return new(false, $"Кеф — від {BetMath.Show(o.MinOddsOk)} до {BetMath.Show(o.MaxOddsOk)}");
        var max = o.MaxFor(p.Source);
        Bet bet;
        lock (_gate)
        {
            if (key is not null && store.ByIdem(nick, key) is { } dup) return new(true, "Ставку вже прийнято", dup, Duplicate: true);
            if (max > 0)
            {
                var already = store.OpenStakeOf(nick, p.Source, p.Ref);
                if (already + (long)p.Stake > max)
                    return new(false, already == 0
                        ? $"Ставка — до {Num(max)} 🏺"
                        : $"Стеля — {Num(max)} 🏺 на {(p.Source == BetSources.Table ? "партію" : "подію")}, уже поставлено {Num(already)}: можна ще {Num(Math.Max(0, max - already))}");
            }
            var have = economy.Balance(nick);
            if (have < p.Stake) return new(false, $"У глечику {Num(have)} 🏺 — на таку ставку не вистачає");
            bet = new Bet(0, p.Source, p.Ref, p.Market, p.Option, p.Label, nick, p.Stake, p.Odds, "new", 0, clock.UtcNow, null, p.Note);
            if (store.AddNew(bet, key) is not { } id)
                return store.ByIdem(nick, key!) is { } raced ? new(true, "Ставку вже прийнято", raced, Duplicate: true) : new(false, "Ставка не записалась — спробуй ще раз");
            bet = bet with { Id = id };
            if (!economy.TrySpend(nick, p.Stake, $"bet:{p.Source}", DebitRef(id), $"−{Num(p.Stake)} {Economy.Shards(p.Stake)}: 🎲 ставка — {p.Label} {BetMath.Show(p.Odds)}"))
            {
                store.DropNew(id);
                return new(false, "Черепків уже не вистачає — щось витратилось саме зараз");
            }
            store.Opened(id);
            bet = bet with { Status = "open" };
        }
        log.LogInformation("Ставки: {Nick} поставив {Stake} на {Source}:{Ref} {Market}/{Option} {Odds}, ставка {Id}",
            nick, p.Stake, p.Source, p.Ref, p.Market, p.Option, p.Odds, bet.Id);
        return new(true, $"Прийнято: {Num(p.Stake)} 🏺 на «{p.Label}» {BetMath.Show(p.Odds)} — можливий виграш {Num(BetMath.Payout(p.Stake, p.Odds))} 🏺", bet);
    }

    /// <summary>Відкриті ставки на ref — те, що ще чекає розрахунку.</summary>
    public IReadOnlyList<Bet> Open(string source, string refKey) => store.OpenByRef(source, refKey);

    /// <summary>Усі ставки на ref, разом із розрахованими.</summary>
    public IReadOnlyList<Bet> All(string source, string refKey) => store.ByRef(source, refKey);

    /// <summary>Усі відкриті ставки джерела — після перезапуску: стіл не відновився — <see cref="Refund"/>.</summary>
    public IReadOnlyList<Bet> OpenOf(string source) => store.OpenOf(source);

    /// <summary>
    /// Розрахувати всі відкриті ставки на ref: <paramref name="decide"/> каже кожній «зіграла / ні / повернути».
    /// Повторний виклик закриває лише те, що лишилось відкритим (подвійного розрахунку не буде). <paramref name="note"/> —
    /// рядок на ставці (чому повернули тощо).
    /// </summary>
    public BetSettlement Settle(string source, string refKey, Func<Bet, BetVerdict> decide, string note = "")
    {
        var closed = new List<BetClosed>();
        lock (_gate)
        {
            foreach (var b in store.OpenByRef(source, refKey))
            {
                var verdict = decide(b);
                if (Close(b, verdict, note) is { } c) closed.Add(c);
            }
        }
        if (closed.Count > 0)
            log.LogInformation("Ставки: {Source}:{Ref} розраховано {Count}, Глек {Glek:+#;-#;0}", source, refKey, closed.Count, new BetSettlement(closed).Glek);
        return new BetSettlement(closed);
    }

    /// <summary>Повернути всі відкриті ставки на ref (подію скасовано, склад столу змінився, партія закоротка…).</summary>
    public BetSettlement Refund(string source, string refKey, string note) => Settle(source, refKey, _ => BetVerdict.Back, note);

    /// <summary>Повернути одну ставку (стіл після перезапуску не відновився — по одній, бо ref різні).</summary>
    public BetClosed? Refund(Bet bet, string note)
    {
        lock (_gate) return store.Bet(bet.Id) is { IsOpen: true } b ? Close(b, BetVerdict.Back, note) : null;
    }

    BetClosed? Close(Bet b, BetVerdict verdict, string note)
    {
        var now = clock.UtcNow;
        // Падіння між грошима й станом лишає ставку відкритою, хоча виграш чи повернення вже впали. Наступний, хто її
        // закриває, може вирішити інакше (подію скасували замість розрахунку, звірка повертає ставку столу) — тоді
        // ключ іншої причини не дублікат, і людина отримала б і виграш, і повернення. Гроші вже пішли — так і закриваємо.
        if (store.PaidRef(b.Id) is { } paid && paid != verdict)
        {
            log.LogWarning("Ставки: {Id} уже має {Paid} у леджері — закриваю так, а не {Verdict}", b.Id, paid, verdict);
            verdict = paid;
        }
        switch (verdict)
        {
            case BetVerdict.Won:
            {
                var pay = BetMath.Payout(b.Stake, b.Odds);
                var r = economy.Grant(b.Nick, pay, $"bet-win:{b.Source}", WinRef(b.Id),
                    $"+{Num(pay)} {Economy.Shards(pay)}: 🎲 зіграло — {b.Label} {BetMath.Show(b.Odds)}");
                if (r is not (GrantResult.Applied or GrantResult.Duplicate)) { log.LogWarning("Ставки: виграш {Id} не нарахувався ({R})", b.Id, r); return null; }
                return store.Close(b.Id, "won", pay, now, note) ? new BetClosed(b with { Status = "won", Payout = pay, SettledAt = now }, verdict, pay) : null;
            }
            case BetVerdict.Back:
            {
                var r = economy.Grant(b.Nick, b.Stake, $"bet-back:{b.Source}", BackRef(b.Id),
                    $"+{Num(b.Stake)} {Economy.Shards(b.Stake)}: 🎲 ставку повернуто — {b.Label}");
                if (r is not (GrantResult.Applied or GrantResult.Duplicate)) { log.LogWarning("Ставки: повернення {Id} не нарахувалось ({R})", b.Id, r); return null; }
                return store.Close(b.Id, "back", b.Stake, now, note) ? new BetClosed(b with { Status = "back", Payout = b.Stake, SettledAt = now }, verdict, b.Stake) : null;
            }
            default:
                return store.Close(b.Id, "lost", 0, now, note) ? new BetClosed(b with { Status = "lost", SettledAt = now }, verdict, 0) : null;
        }
    }
}
