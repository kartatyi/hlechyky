using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Доручення на добу, їхній прогрес, чи забрана скриня і серія днів (ClickerChores.cs). Старе збереження — порожньо.
/// </summary>
/// <param name="Day">Київська доба, на яку обрано доручення.</param>
/// <param name="Pick">Ключі обраних доручень (до трьох).</param>
/// <param name="Prog">Скільки зроблено з кожного (в тому ж порядку).</param>
/// <param name="Taken">Скриню цієї доби вже забрано.</param>
/// <param name="Streak">Серія днів 1…7, на яку дійшла остання забрана скриня.</param>
/// <param name="Last">Доба, коли скриню забрано востаннє.</param>
public sealed record ChoresRow(string Day = "", List<string>? Pick = null, List<int>? Prog = null, bool Taken = false, int Streak = 0, string Last = "");

/// <summary>
/// «📜 Доручення Глека» (v13 Г3, docs/games/specs/clicker-v13.md). Щодоби кожен гончар має три різні доручення з пулу
/// (вибір за ніком і днем — однаковий скільки не питай, лише з доступних саме йому). Рахуються лише справи руками:
/// палій, підмайстри, комора й Толока без гончаря лічильників не крутять. Усі три — «Забрати скриню»: хвилини гри
/// грошима, Слава (немає торгівлі — ще хвилини гри) і черепки × Economy:Boost. Серія днів 1…7: на сьомий — велика скриня
/// («Свято» і ще черепки), далі знову з першого; пропущена доба (скриню не забрано) — серія з першого.
/// </summary>
public sealed partial class Clicker
{
    public const int ChoresPerDay = 3, ChoresStreakMax = 7;
    /// <summary>Скриня: хвилини гри грошима, Слава (або, без торгівлі, ще хвилини гри) і базові черепки (× Boost).</summary>
    public const int ChestMinutes = 30, ChestSlava = 200, ChestNoTradeMinutes = 15, ChestShards = 30;
    /// <summary>Велика скриня сьомого дня: «Свято» у запас і ще базові черепки (× Boost).</summary>
    public const int ChestBigFeastMinutes = 60, ChestBigShards = 100;
    /// <summary>Причина в леджері: категорія «нагороди» в гаманці.</summary>
    public const string ChoresReason = "award:📜 Доручення Глека";

    /// <summary>Доручення пулу: ключ (його ж знає клієнт), скільки треба зробити.</summary>
    public sealed record ChoreDef(string Key, string Text, int Goal);

    public static readonly ChoreDef[] ChoreCatalog =
    [
        new("catch", "Спіймай 30 глеків з полиці", 30),
        new("gold", "Спіймай 3 розписні глеки", 3),
        new("fire", "Обпали руками розписну партію", 1),
        new("ship", "Завантаж корабель до повного", 1),
        new("village", "Відвези замовлення в село", 1),
        new("guest", "Віддай гостю замовлення", 1),
        new("toloka", "Поклади 20 виробів на Толоку", 20),
        new("gift", "Подаруй дарунок другові", 1),
        new("fest", "Влаштуй фестиваль", 1),
        new("riddle", "Відповідай на загадку Глека", 1),
    ];

    string _chDay = "", _chLast = "";
    readonly List<string> _chPick = [];
    readonly List<int> _chProg = [];
    bool _chTaken;
    int _chStreak;

    /// <summary>Чи це доручення доступне саме цьому гончареві (нема Пристані — нема кораблів, нема гостей — нема гостей).</summary>
    bool ChoreAvailable(string key) => key switch
    {
        "catch" or "gold" => true,
        "fire" => _styles.Count > 0,
        "ship" => TradeOpen,
        "village" => FiredTotal >= 1,
        "guest" => _gMet.Count > 0 && _gOrders.Count > 0,   // рецензія v13: лише коли є замовлення гостей
        "toloka" => TolokaOpen,
        "gift" => GuildOpen && _guildSvc is not null && GuildKey.Length > 0,
        // Рецензія v13: не брати, якщо наступний фестиваль пізніше за кінець доби (виконати не вийде).
        "fest" => TolokaHas("festival") && (_festNext == default || _festNext <= Days.NextMidnight(Ctx.Clock.UtcNow)),
        "riddle" => TreasureOpen && RiddleBook is { Riddles.Count: > 0 },
        _ => false,
    };

    /// <summary>Скільки черепків дає базове число з урахуванням Economy:Boost (його знає сервіс дня).</summary>
    int ChoreEarn(int shards) => DaySvc()?.Earn(shards) ?? shards;

    /// <summary>
    /// Доручення на сьогодні: нова доба — нова трійка. Вибір — перемішування доступних сідом нік+день, тож він однаковий
    /// скільки не питай; обрана трійка лягає в збереження й до кінця доби не змінюється (хоч би що гончар збудував).
    /// </summary>
    void ChoresEnsure()
    {
        var day = Days.Today(Ctx.Clock);
        if (_chDay == day && _chPick.Count > 0) return;
        if (Ctx.NickOf(0) is not { Length: > 0 } nick) return;
        _chDay = day;
        _chTaken = false;
        _chPick.Clear();
        _chProg.Clear();
        var pool = ChoreCatalog.Where(c => ChoreAvailable(c.Key)).Select(c => c.Key).ToList();
        var rng = new Random(Days.Seed("chores:" + Auth.NickKey(nick), day));
        for (var i = pool.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        foreach (var key in pool.Take(ChoresPerDay))
        {
            _chPick.Add(key);
            _chProg.Add(0);
        }
        _viewVersion++;
    }

    static ChoreDef ChoreOf(string key) => ChoreCatalog.First(c => c.Key == key);

    /// <summary>Однолітерний код доручення у виді (довгі ключі їм не потрібні): catch c, gold g, fire f, ship s, village v, guest u, toloka t, gift i, fest e, riddle r.</summary>
    public const string ChoreCodes = "cgfsvutier";

    static string ChoreCode(string key) => ChoreCodes[Array.FindIndex(ChoreCatalog, c => c.Key == key)].ToString();

    /// <summary>
    /// Зараховує справу руками: <paramref name="n"/> одиниць доручення <paramref name="key"/>, якщо воно сьогодні обране.
    /// Виконане — рядок у тост; усі три — «скриня чекає».
    /// </summary>
    void ChoreBump(string key, int n = 1)
    {
        if (n <= 0) return;
        ChoresEnsure();
        var i = _chPick.IndexOf(key);
        if (i < 0) return;
        var def = ChoreOf(key);
        var was = _chProg[i];
        if (was >= def.Goal) return;
        _chProg[i] = (int)Math.Min(def.Goal, (long)was + n);
        _viewVersion++;
        // Тост «виконано» малює клієнт за зміною прогресу (чужі повідомлення дій тут не чіпаємо).
    }

    bool ChoresAllDone => _chPick.Count > 0 && _chPick.Select((k, j) => _chProg[j] >= ChoreOf(k).Goal).All(x => x);

    /// <summary>Попередня доба до "yyyy-MM-dd" — календарем, а не відніманням доби від моменту (перехід часу не рве серію).</summary>
    static string DayBefore(string day) =>
        DateOnly.ParseExact(day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture).AddDays(-1)
            .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Яка серія зараз: остання скриня була вчора чи сьогодні — її лічба, інакше (і після сьомої) — нуль.</summary>
    int ChoresStreakNow(string today)
    {
        if (_chStreak <= 0 || _chLast.Length == 0) return 0;
        if (_chLast == today) return _chStreak;
        return _chLast == DayBefore(today) && _chStreak < ChoresStreakMax ? _chStreak : 0;
    }

    ActResult? ActChores(string action, JsonElement payload)
    {
        if (action != "chores") return null;
        if (Str(payload, "op") != "claim") return ActResult.Fail("З дорученнями так не ходять");
        ChoresEnsure();
        if (_chPick.Count == 0) return ActResult.Fail("Глек сьогодні нічого не доручає");
        if (_chTaken) return ActResult.Fail("Скриню за сьогодні вже забрано — завтра нові доручення");
        if (!ChoresAllDone) return ActResult.Fail("Скриня відчиняється, коли виконано всі доручення");
        var now = Ctx.Clock.UtcNow;
        var today = Days.Today(Ctx.Clock);
        var next = _chLast == DayBefore(today) && _chStreak is > 0 and < ChoresStreakMax ? _chStreak + 1 : 1;
        var big = next == ChoresStreakMax;
        var shards = ChoreEarn(ChestShards) + (big ? ChoreEarn(ChestBigShards) : 0);
        // Черепки — першими: ключ леджера на добу, тож повтор після збою не заплатить удруге, а збій не з'їсть скриню.
        if (Ctx.NickOf(0) is { Length: > 0 } nick && shards > 0 && Ctx.Services.GetService<IStakes>() is { } stakes)
        {
            try { stakes.Grant(nick, shards, ChoresReason, $"clicker-chores:{Auth.NickKey(nick)}:{today}"); }
            catch (Exception)
            {
                return ActResult.Fail("Скриня зараз не відчиняється — спробуй за мить");
            }
        }
        _chTaken = true;
        _chStreak = next;
        _chLast = today;
        var pay = PlayPay(ChestMinutes);
        var parts = new List<string> { $"+{PotsShort(pay)}" };
        Add(pay);
        if (TradeOpen)
        {
            _slava += ChestSlava;
            parts.Add($"Слава +{ChestSlava}");
        }
        else
        {
            var more = PlayPay(ChestNoTradeMinutes);
            Add(more);
            parts.Add($"ще +{PotsShort(more)}");
        }
        if (shards > 0) parts.Add($"{shards} 🏺");
        if (big) BuffBank(FeastKind, ChestBigFeastMinutes, "Велика скриня Глека");
        _viewVersion++;
        return ActResult.Accept((big ? $"🎁 Велика скриня — сьомий день поспіль! " : $"📜 Скриня Глека (день {next} із {ChoresStreakMax}): ") + string.Join(" · ", parts));
    }

    // ---------- вид і збереження ----------

    /// <summary>
    /// Вид доручень: три рядки (ключ, скільки, скільки треба), чи забрано скриню, серія днів і що в скрині. Тексти й
    /// піктограми знає клієнт за ключем. null — гончар без ніка (гість) чи доручень нема.
    /// </summary>
    object? ViewChores(DateTimeOffset now)
    {
        ChoresEnsure();
        if (_chPick.Count == 0) return ViewWish(now) is { } w ? new object?[] { "", 0, 0, w } : null;
        // Вид топ-гончаря має межу розміру (ClickerSpeedTests), тож коротко: цілі, тексти й Славу клієнт знає за ключем і видом торгівлі.
        var list = new List<object?>
        {
            string.Concat(_chPick.Select((k, i) => ChoreCode(k) + _chProg[i])),   // доручення: код і зроблено, «c12g1f0»
            ChoresStreakNow(Days.Today(Ctx.Clock)),                                // серія днів (з урахуванням сьогоднішньої скрині)
            _chTaken ? 1 : 0,                                                      // скриню сьогодні забрано
            ViewWish(now),                                                         // чарівний глек і бажання (ClickerWish.cs)
            ChoresAllDone && !_chTaken ? new[] { ChoreEarn(ChestShards), ChoreEarn(ChestBigShards) } : null,   // черепки скрині (× Boost)
        };
        // Хвостові null не шлемо: на що не вистачило — того й нема.
        while (list.Count > 3 && list[^1] is null) list.RemoveAt(list.Count - 1);
        return list;
    }

    void ResetChores()
    {
        _chDay = _chLast = "";
        _chPick.Clear();
        _chProg.Clear();
        _chTaken = false;
        _chStreak = 0;
    }

    ChoresRow? SaveChores() => _chDay.Length == 0 && _chStreak == 0
        ? null
        : new ChoresRow(_chDay, [.. _chPick], [.. _chProg], _chTaken, _chStreak, _chLast);

    void LoadChores(ChoresRow? row)
    {
        ResetChores();
        if (row is null) return;
        _chStreak = Math.Clamp(row.Streak, 0, ChoresStreakMax);
        _chLast = row.Last ?? "";
        if (_chLast.Length > 0 && !DateOnly.TryParseExact(_chLast, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _))
        {
            _chLast = "";
            _chStreak = 0;
        }
        if (string.IsNullOrEmpty(row.Day)) return;
        var keys = (row.Pick ?? []).Where(k => ChoreCatalog.Any(c => c.Key == k)).Distinct().Take(ChoresPerDay).ToList();
        if (keys.Count == 0) return;
        _chDay = row.Day;
        _chTaken = row.Taken;
        for (var i = 0; i < keys.Count; i++)
        {
            _chPick.Add(keys[i]);
            var done = row.Prog is { } p && i < p.Count ? p[i] : 0;
            _chProg.Add(Math.Clamp(done, 0, ChoreOf(keys[i]).Goal));
        }
    }
}
