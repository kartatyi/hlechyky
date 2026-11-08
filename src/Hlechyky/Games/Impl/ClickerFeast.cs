using System.Globalization;
using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>Збережені запаси й відрізки бафів «Свято» та «Щасливий кошик» (v13 Г) і ключі вже забраних призів дня.</summary>
public sealed record FeastRow(int Feast = 0, int Basket = 0, DateTimeOffset FeastUntil = default, DateTimeOffset BasketUntil = default,
    List<string>? Taken = null);

/// <summary>
/// Бафи-запаси оновлення v13 (docs/games/specs/clicker-v13.md, розділ «Г»). Дві банки хвилин у збереженні (стеля
/// <see cref="BuffBankCap"/>): «🎉 Свято» — ×2 до всього, «🧺 Щасливий кошик» — глеки з полиці ×3. Хвилини сюди падають
/// з призів (Г1, Г2, далі Г3 і Г7) — одним методом <see cref="BuffBank"/>. Гончар вмикає баф сам (дія <c>buff</c>): уся
/// банка йде одним відрізком від «зараз» (<c>_feastUntil = now + запас</c>), і він тече в реальному часі, офлайн теж.
/// Свято складається з фестивалем Толоки множенням (окремий множник у <see cref="AllMult"/>).
///
/// Призи «Майстра» й «Ловця дня» гончар забирає сам на синхронізації (<see cref="ClaimDayPrizes"/>): сервіс (ClickerDay.cs)
/// віддає ще не забрані, гра кладе хвилини в банку й запам'ятовує ключ — повтор не платить. Старі ключі викидаємо.
/// </summary>
public sealed partial class Clicker
{
    /// <summary>Стеля банки хвилин кожного бафа.</summary>
    public const int BuffBankCap = 180;
    public const double FeastMult = 2, BasketMult = 3;
    public const string FeastKind = "feast", BasketKind = "basket";
    /// <summary>Ключі забраних призів старші за стільки днів викидаємо (сервіс віддає призи лише за останні 14).</summary>
    public const int PrizeKeepDays = 20;

    int _feastBank, _basketBank;
    DateTimeOffset _feastUntil, _basketUntil;
    readonly HashSet<string> _prizeTaken = new(StringComparer.Ordinal);
    readonly List<string> _feastToasts = [];

    /// <summary>×2 до всього, поки триває «Свято».</summary>
    double FeastAllMult => Ctx.Clock.UtcNow < _feastUntil ? FeastMult : 1;

    /// <summary>×3 до глеків з полиці, поки триває «Щасливий кошик».</summary>
    double BasketFallMult => Ctx.Clock.UtcNow < _basketUntil ? BasketMult : 1;

    static (string Icon, string Name) BuffName(string kind) => kind == FeastKind ? ("🎉", "Свята") : ("🧺", "Кошика");

    /// <summary>
    /// Покласти хвилини в банку бафа (<see cref="FeastKind"/> чи <see cref="BasketKind"/>) — єдиний вхід для призів дня,
    /// доручень, чарівного глека. Банка не більша за <see cref="BuffBankCap"/>; повертає, скільки хвилин влізло.
    /// <paramref name="why"/> — за що («За «Майстра дня»»), іде в тост: «🎉 За «Майстра дня»: +60 хв Свята в запасі».
    /// </summary>
    public int BuffBank(string kind, int minutes, string why)
    {
        if (minutes <= 0 || kind is not (FeastKind or BasketKind)) return 0;
        var have = kind == FeastKind ? _feastBank : _basketBank;
        var add = Math.Clamp(BuffBankCap - have, 0, minutes);
        if (kind == FeastKind) _feastBank = have + add; else _basketBank = have + add;
        var (icon, name) = BuffName(kind);
        _feastToasts.Add(add > 0
            ? $"{icon} {why}: +{add} хв {name} в запасі" + (add < minutes ? " (запас повний)" : "")
            : $"{icon} {why}: запас {name} повний — {minutes} хв не влізло");
        _viewVersion++;
        return add;
    }

    /// <summary>Тост про призи, що лягли в банку на синхронізації, — одним рядком на першій справжній дії.</summary>
    string TakeFeastToast()
    {
        if (_feastToasts.Count == 0) return "";
        var text = string.Join(" · ", _feastToasts);
        _feastToasts.Clear();
        return text;
    }

    ActResult? ActFeast(string action, JsonElement payload)
    {
        if (action != "buff") return null;
        var kind = Str(payload, "kind");
        if (kind is not (FeastKind or BasketKind)) return ActResult.Fail("Такого бафа нема");
        var now = Ctx.Clock.UtcNow;
        var bank = kind == FeastKind ? _feastBank : _basketBank;
        if (bank <= 0) return ActResult.Fail(kind == FeastKind ? "У запасі нема хвилин Свята" : "У запасі нема хвилин Кошика");
        // Увімкнений баф не губить запасу: нова хвилина дописується до кінця поточного відрізка.
        var until = kind == FeastKind ? _feastUntil : _basketUntil;
        var from = until > now ? until : now;
        var end = from + TimeSpan.FromMinutes(bank);
        if (kind == FeastKind) { _feastUntil = end; _feastBank = 0; } else { _basketUntil = end; _basketBank = 0; }
        return ActResult.Accept(kind == FeastKind
            ? $"🎉 Свято! {bank} хв ×{FeastMult:0} до всього"
            : $"🧺 Щасливий кошик! {bank} хв глеки з полиці ×{BasketMult:0}");
    }

    /// <summary>
    /// Вид бафів: запаси хвилин і кінці відрізків, що тривають. Порожньо (запасів нема, нічого не триває) — null: вид топ-гончаря
    /// має межу розміру (ClickerSpeedTests), а стеля банки й множники — сталі клієнта.
    /// </summary>
    object? ViewFeast(DateTimeOffset now)
    {
        DateTimeOffset? feast = _feastUntil > now ? _feastUntil : null, basket = _basketUntil > now ? _basketUntil : null;
        if (_feastBank == 0 && _basketBank == 0 && feast is null && basket is null) return null;
        return new { feast = new { bank = _feastBank, until = feast }, basket = new { bank = _basketBank, until = basket } };
    }

    // ---------- призи дня (Г1, Г2) ----------

    /// <summary>
    /// Забрати призи «Майстра» й «Ловця дня», що чекають цього гончаря в сервісі. Кличеться з <see cref="SyncDay"/> — на кожній
    /// синхронізації; ключ у збереженні, тож повтор (інший пристрій, перезапуск) не платить удруге.
    /// </summary>
    void ClaimDayPrizes(ClickerDayService svc, DateTimeOffset now)
    {
        if (Ctx.NickOf(0) is not { Length: > 0 } nick) return;
        foreach (var p in svc.PrizesFor(nick, now))
        {
            if (!_prizeTaken.Add(p.Key)) continue;
            BuffBank(p.Kind, p.Minutes, p.Why);
        }
        if (_prizeTaken.Count == 0) return;
        var cut = Days.Of(now.AddDays(-PrizeKeepDays));
        _prizeTaken.RemoveWhere(k => PrizeDay(k) is { } d && string.CompareOrdinal(d, cut) < 0);
    }

    /// <summary>День з ключа <c>day:2026-11-08:master:1</c>; чужий формат — null (такий ключ лишаємо).</summary>
    static string? PrizeDay(string key)
    {
        var parts = key.Split(':');
        return parts.Length >= 3 && parts[0] == "day" && DateOnly.TryParseExact(parts[1], "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _) ? parts[1] : null;
    }

    // ---------- збереження ----------

    void ResetFeast()
    {
        _feastBank = _basketBank = 0;
        _feastUntil = _basketUntil = default;
        _prizeTaken.Clear();
        _feastToasts.Clear();
    }

    FeastRow? SaveFeast() =>
        _feastBank == 0 && _basketBank == 0 && _feastUntil == default && _basketUntil == default && _prizeTaken.Count == 0
            ? null
            : new FeastRow(_feastBank, _basketBank, _feastUntil, _basketUntil,
                _prizeTaken.Count == 0 ? null : _prizeTaken.Order(StringComparer.Ordinal).ToList());

    void LoadFeast(FeastRow? row)
    {
        ResetFeast();
        if (row is null) return;
        _feastBank = Math.Clamp(row.Feast, 0, BuffBankCap);
        _basketBank = Math.Clamp(row.Basket, 0, BuffBankCap);
        // Відрізок з майбутнього далі за стелю банки (правлена база) — обрізаємо до «зараз + стеля».
        var max = Ctx.Clock.UtcNow + TimeSpan.FromMinutes(BuffBankCap * 4);
        _feastUntil = row.FeastUntil > max ? max : row.FeastUntil;
        _basketUntil = row.BasketUntil > max ? max : row.BasketUntil;
        foreach (var k in row.Taken ?? []) if (k is { Length: > 0 and < 64 }) _prizeTaken.Add(k);
    }
}
