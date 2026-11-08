using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>Річ Зали скарбів: ключ (той самий, що в <c>data/clicker/riddles.json</c>), назва, емодзі й вітрина (0 село, 1 козаки, 2 заморське).</summary>
public sealed record ClickerTreasureItem(string Key, string Name, string Emoji, int Case);

/// <summary>Загадка Глека: річ-відповідь, текст і три варіанти (ключі речей).</summary>
public sealed record ClickerRiddle(string Id, string Item, string Text, string[] Options);

/// <summary>
/// Книга загадок (<c>data/clicker/riddles.json</c>). Непридатну загадку (невідома річ, не три різні варіанти, нема правильної
/// серед них) пропускаємо й повідомляємо в <c>problems</c> — рядок у лозі, а не тиша чи падіння на проді.
/// </summary>
public sealed class ClickerRiddleBook(IReadOnlyList<ClickerRiddle> riddles)
{
    public static readonly ClickerRiddleBook Empty = new([]);

    public IReadOnlyList<ClickerRiddle> Riddles => riddles;

    public ClickerRiddle? ById(string? id) => riddles.FirstOrDefault(r => r.Id == id);

    public static (ClickerRiddleBook Book, IReadOnlyList<string> Problems) Parse(string json)
    {
        var problems = new List<string>();
        var list = new List<ClickerRiddle>();
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("riddles", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            problems.Add("нема масиву riddles");
            return (Empty, problems);
        }
        var n = 0;
        foreach (var r in arr.EnumerateArray())
        {
            n++;
            var item = r.TryGetProperty("item", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() ?? "" : "";
            var text = r.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? (t.GetString() ?? "").Trim() : "";
            var opts = r.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array
                ? o.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").ToArray() : [];
            if (!Clicker.Treasures.Any(x => x.Key == item)) { problems.Add($"загадка {n}: невідома річ «{item}»"); continue; }
            if (text.Length == 0) { problems.Add($"загадка {n}: порожній текст"); continue; }
            if (opts.Length != 3 || opts.Distinct().Count() != 3 || !opts.Contains(item) || opts.Any(k => Clicker.Treasures.All(x => x.Key != k)))
            { problems.Add($"загадка {n}: варіанти мають бути трьома різними речами, серед них «{item}»"); continue; }
            var id = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)))[..8].ToLowerInvariant();
            if (list.Any(x => x.Id == id)) { problems.Add($"загадка {n}: такий самий текст уже є"); continue; }
            list.Add(new ClickerRiddle(id, item, text, opts));
        }
        return (new ClickerRiddleBook(list), problems);
    }
}

/// <summary>Книга загадок з файлу: перечитується, коли файл змінився (перевірка не частіше за раз на пів хвилини).</summary>
public sealed class ClickerRiddleService(string file = ClickerRiddleService.DefaultFile, ILogger<ClickerRiddleService>? log = null)
{
    public const string DefaultFile = "data/clicker/riddles.json";
    static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(30);
    readonly object _gate = new();
    ClickerRiddleBook _book = ClickerRiddleBook.Empty;
    DateTime _stamp = DateTime.MinValue, _checked = DateTime.MinValue;

    /// <summary>Книга для тестів і для тих, кому файл не потрібен.</summary>
    public ClickerRiddleBook? Fixed { get; init; }

    public ClickerRiddleBook Book
    {
        get
        {
            if (Fixed is { } f) return f;
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                if (_stamp != DateTime.MinValue && now - _checked < CheckEvery) return _book;
                _checked = now;
                var path = Paths.Resolve(file);
                var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                if (stamp == _stamp && stamp != DateTime.MinValue) return _book;
                _stamp = stamp;
                if (stamp == DateTime.MinValue)
                {
                    log?.LogWarning("коло: книги загадок {Path} нема — загадки не буде", path);
                    return _book = ClickerRiddleBook.Empty;
                }
                try
                {
                    var (book, problems) = ClickerRiddleBook.Parse(File.ReadAllText(path));
                    foreach (var p in problems) log?.LogWarning("коло: книга загадок: {Problem}", p);
                    return _book = book;
                }
                catch (Exception e) when (e is IOException or JsonException)
                {
                    log?.LogWarning("коло: книгу загадок {Path} не прочитати: {Err}", path, e.Message);
                    return _book;
                }
            }
        }
    }
}

public static class ClickerRiddleSetup
{
    public static IServiceCollection Add(IServiceCollection services)
    {
        services.AddSingleton(sp => new ClickerRiddleService(ClickerRiddleService.DefaultFile, sp.GetService<ILogger<ClickerRiddleService>>()));
        return services;
    }
}

/// <summary>
/// Зала скарбів (v13 §Д2, docs/games/specs/clicker-v13.md): друга зала Трипільського музею в альбомі. Вісімнадцять речей
/// у трьох вітринах по шість; кожна річ +2 % до всього, уся зала ще +10 % (окремий множник, стеля ×1,46). Речі падають зі
/// спійманого глека з полиці (🌾, 2 %), спійманого розписного (⚔️, 10 %) і повного корабля (⛵, 25 %, посол — напевно; річ за
/// портом). Річ, що вже є, — уламок її вітрини; п'ять уламків склеюються (одразу, як набралось) у річ, якої бракує в
/// тій самій вітрині. Повна вітрина більше не дає уламків і дає свій бонус. Раз на добу — «❓ Загадка Глека»: вгадав — уламок.
/// Перша зала (<c>Finds</c>) окремо й не змінилась.
/// </summary>
public sealed partial class Clicker
{
    public const int TreasureCases = 3, TreasurePerCase = 6;
    public const double TreasureItemBonus = 0.02, TreasureHallBonus = 0.10;
    /// <summary>Повні вітрини: глеки з полиці +25 %, розписні частіше на 20 % (проміжок коротший), кораблі платять +25 %.</summary>
    public const double TreasureFallBonus = 0.25, TreasureGoldenOften = 0.20, TreasureShipBonus = 0.25;
    /// <summary>Шанс речі на подію: спійманий глек з полиці, спійманий розписний, повний звичайний корабель (посол — напевно).</summary>
    public static readonly double[] TreasureChance = [0.02, 0.10, 0.25];
    /// <summary>Скільки уламків склеюються в річ.</summary>
    public const int TreasureGlue = 5;

    public static readonly string[] TreasureCaseKeys = ["village", "cossack", "overseas"];
    public static readonly string[] TreasureCaseNames = ["Сільська скриня", "Козацька скарбниця", "Заморська вітрина"];
    public static readonly string[] TreasureCaseEmoji = ["🌾", "⚔️", "⛵"];
    /// <summary>Що дає повна вітрина (текст для каталогу й тосту).</summary>
    public static readonly string[] TreasureCaseBonusText =
        ["глеки з полиці +25 %", "розписні глеки частіше на 20 %", "кораблі платять +25 %"];
    /// <summary>Звідки береться (підказка для порожньої клітинки).</summary>
    public static readonly string[] TreasureCaseHint =
        ["Береться зі спійманих глеків з полиці — один із п'ятдесяти", "Береться зі спійманих розписних глеків — один із десяти",
         "Береться з повних кораблів на Пристані; посольський дає напевно"];

    /// <summary>Вісімнадцять речей: спершу шість сільських, потім козацькі, потім заморські (порядок = біти <c>_treasure</c>).</summary>
    public static readonly ClickerTreasureItem[] Treasures =
    [
        new("pysanka", "Писанка", "🥚", 0), new("rushnyk", "Вишитий рушник", "🧵", 0), new("lozhka", "Дерев'яна ложка", "🥄", 0),
        new("sopilka", "Сопілка", "🪈", 0), new("namysto", "Коралове намисто", "📿", 0), new("khustka", "Бабусина хустка", "🧣", 0),
        new("lyulka", "Люлька-носогрійка", "💨", 1), new("porokhivnytsia", "Порохівниця", "📯", 1), new("pernach", "Пернач", "🔨", 1),
        new("taler", "Срібний талер", "🪙", 1), new("pechatka", "Козацька печатка", "🔏", 1), new("kresalo", "Кресало", "🔥", 1),
        new("chashka", "Порцелянова чашка", "🍵", 2), new("tiulpan", "Тюльпанна кахля", "🌷", 2), new("vitriak", "Кахля з вітряком", "🌬️", 2),
        new("amfora", "Амфора", "⚱️", 2), new("pastushka", "Порцелянова пастушка", "🐑", 2), new("vaza", "Селадонова ваза", "🏺", 2),
    ];

    /// <summary>Порт → річ Заморської вітрини (два порти на річ).</summary>
    public static readonly Dictionary<string, string> TreasurePortItem = new(StringComparer.Ordinal)
    {
        ["jingdezhen"] = "chashka", ["kyoto"] = "chashka", ["iznik"] = "tiulpan", ["lisbon"] = "tiulpan",
        ["delft"] = "vitriak", ["england"] = "vitriak", ["athens"] = "amfora", ["faenza"] = "amfora",
        ["meissen"] = "pastushka", ["sevres"] = "pastushka", ["korea"] = "vaza", ["talavera"] = "vaza",
    };

    static int TreasureCaseMask(int c) => ((1 << TreasurePerCase) - 1) << (c * TreasurePerCase);
    static int TreasureAllMask => (1 << Treasures.Length) - 1;

    int _treasure;
    readonly int[] _treasureShards = new int[TreasureCases];
    // Загадка дня: день (київський), її id у книзі, чи вже відповідав, чи вгадав, що обрав; id уже виданих у цьому колі.
    string _rdDay = "", _rdId = "", _rdPick = "";
    bool _rdDone, _rdRight;
    readonly List<string> _rdSeen = [];

    /// <summary>Джерело випадку для Зали (окреме від <c>Ctx.Rng</c>, щоб не зсувати решту гри); тести підставляють своє.</summary>
    public Random TreasureRng { get; set; } = Random.Shared;

    // ---------- чисті правила ----------

    /// <summary>Бонус зали до всього (частка): +2 % за річ, уся зала ще +10 %.</summary>
    public static double TreasureBonus(int mask)
    {
        var n = BitOperations.PopCount((uint)(mask & TreasureAllMask));
        return TreasureItemBonus * n + (n == Treasures.Length ? TreasureHallBonus : 0);
    }

    public static bool TreasureCaseFull(int mask, int c) => (mask & TreasureCaseMask(c)) == TreasureCaseMask(c);

    // ---------- гачки основи ----------

    /// <summary>Зала відкрита, коли в альбомі є хоч одна клітинка (тобто від першого обпаленого виробу).</summary>
    bool TreasureOpen => _albumCells.Any(m => m != 0);

    double TreasureAllMult => 1 + TreasureBonus(_treasure);
    double TreasureFallMult => TreasureCaseFull(_treasure, 0) ? 1 + TreasureFallBonus : 1;
    /// <summary>Проміжок між розписними глеками: повна козацька скарбниця скорочує його, щоб вони приходили на 20 % частіше.</summary>
    double TreasureGoldenWait => TreasureCaseFull(_treasure, 1) ? 1 / (1 + TreasureGoldenOften) : 1;
    double TreasureShipMult => TreasureCaseFull(_treasure, 2) ? 1 + TreasureShipBonus : 1;

    // ---------- знахідки ----------

    /// <summary>
    /// Подія вітрини <paramref name="c"/> (глек з полиці, розписний, корабель): кидок і, якщо пощастило, річ чи уламок.
    /// Повертає хвіст для тосту (починається з « · ») або порожній рядок. <paramref name="pick"/> — індекс речі у вітрині
    /// (для кораблів за портом), без нього — випадкова. <paramref name="sure"/> — без кидка (посол).
    /// </summary>
    string TreasureRoll(int c, int? pick = null, bool sure = false)
    {
        if (!TreasureOpen || TreasureCaseFull(_treasure, c)) return "";
        var roll = sure ? 0 : TreasureRng.NextDouble();
        var text = TreasureFind(c, roll, pick ?? TreasureRng.Next(TreasurePerCase));
        return text is null ? "" : " · " + text;
    }

    /// <summary>Те саме з наперед відомим кидком — для тестів: <c>roll</c> нижче шансу вітрини дає річ номер <c>pick</c>.</summary>
    public string? TreasureFind(int c, double roll, int pick)
    {
        if (c < 0 || c >= TreasureCases || !TreasureOpen || TreasureCaseFull(_treasure, c) || roll >= TreasureChance[c]) return null;
        return TreasureGive(c * TreasurePerCase + Math.Clamp(pick, 0, TreasurePerCase - 1));
    }

    /// <summary>Річ (або уламок, якщо вона вже є) з номером <paramref name="idx"/> у залі; текст для тосту.</summary>
    string TreasureGive(int idx)
    {
        var item = Treasures[idx];
        var c = item.Case;
        if ((_treasure & (1 << idx)) == 0) return TreasureAdd(idx, "");
        _treasureShards[c]++;
        _viewVersion++;
        var text = $"🏛 Зала скарбів: ще одна «{item.Name.ToLowerInvariant()}» — уламок {Math.Min(_treasureShards[c], TreasureGlue)}/{TreasureGlue}";
        if (_treasureShards[c] >= TreasureGlue) text = TreasureGlueOne(c, text);
        return text;
    }

    /// <summary>П'ять уламків вітрини склеюються в річ, якої бракує (вибір випадковий).</summary>
    string TreasureGlueOne(int c, string text)
    {
        var missing = Enumerable.Range(c * TreasurePerCase, TreasurePerCase).Where(i => (_treasure & (1 << i)) == 0).ToList();
        if (missing.Count == 0) { _treasureShards[c] = 0; return text; }
        _treasureShards[c] -= TreasureGlue;
        var idx = missing[TreasureRng.Next(missing.Count)];
        return TreasureAdd(idx, text.Length > 0 ? text + " · склеєно: " : "");
    }

    /// <summary>Положити нову річ у залу; вітрина повна — свій бонус, уся зала — ачівка.</summary>
    string TreasureAdd(int idx, string prefix)
    {
        var item = Treasures[idx];
        var c = item.Case;
        _treasure |= 1 << idx;
        _viewVersion++;
        var text = prefix.Length > 0 ? $"{prefix}{item.Name}! +2 % до всього" : $"🏛 Зала скарбів: {item.Name}! +2 % до всього";
        if (TreasureCaseFull(_treasure, c))
        {
            _treasureShards[c] = 0;
            text += $" · {TreasureCaseEmoji[c]} вітрина «{TreasureCaseNames[c]}» повна: {TreasureCaseBonusText[c]}";
        }
        if ((_treasure & TreasureAllMask) == TreasureAllMask)
        {
            text += " · уся зала зібрана: ще +10 % до всього";
            Achieve("potter-hall");
        }
        return text;
    }

    // ---------- загадка Глека ----------

    ClickerRiddleBook? RiddleBook => Ctx.Services.GetService<ClickerRiddleService>()?.Book;

    /// <summary>
    /// Сьогоднішня загадка: нова з'являється з київською північчю — вибір за ніком і днем (однаковий скільки не питай),
    /// без повторів, доки не видано всі; тоді коло починається знову. Нічого не видаємо, поки зала зачинена.
    /// </summary>
    ClickerRiddle? RiddleToday()
    {
        if (!TreasureOpen || RiddleBook is not { Riddles.Count: > 0 } book) return null;
        var day = Days.Today(Ctx.Clock);
        if (_rdDay == day && book.ById(_rdId) is { } kept) return kept;
        if (_rdDay == day && _rdDone) return null;            // відповів, а загадку з книги прибрали — завтра нова
        var pool = book.Riddles.Where(r => !_rdSeen.Contains(r.Id)).ToList();
        if (pool.Count == 0) { _rdSeen.Clear(); pool = [.. book.Riddles]; }
        var nick = Ctx.NickOf(0) ?? "";
        var pick = pool[Days.Seed("riddle:" + Auth.NickKey(nick), day) % pool.Count];
        _rdDay = day;
        _rdId = pick.Id;
        _rdDone = _rdRight = false;
        _rdPick = "";
        _rdSeen.Add(pick.Id);
        return pick;
    }

    ActResult Riddle(JsonElement payload)
    {
        if (!TreasureOpen) return ActResult.Fail("Зала скарбів відкриється, щойно в альбомі буде перша клітинка");
        if (RiddleToday() is not { } riddle) return ActResult.Fail("Сьогодні Глек не загадує");
        if (_rdDone) return ActResult.Fail("Сьогодні ти вже відповідав — завтра нова загадка");
        var pick = Str(payload, "pick");
        if (!riddle.Options.Contains(pick)) return ActResult.Fail("Такого варіанту в загадці нема");
        _rdDone = true;
        ChoreBump("riddle");
        _rdPick = pick;
        _rdRight = pick == riddle.Item;
        _viewVersion++;
        var answer = Treasures.First(t => t.Key == riddle.Item);
        if (!_rdRight)
            return ActResult.Accept($"❓ Не вгадав — це була {answer.Emoji} {answer.Name.ToLowerInvariant()}. Завтра Глек загадає ще");
        var c = answer.Case;
        if (TreasureCaseFull(_treasure, c))
            return ActResult.Accept($"❓ Вгадав: {answer.Emoji} {answer.Name.ToLowerInvariant()}! Вітрина «{TreasureCaseNames[c]}» уже повна — уламки їй не потрібні");
        _treasureShards[c]++;
        var text = $"❓ Вгадав: {answer.Emoji} {answer.Name.ToLowerInvariant()}! Уламок вітрини «{TreasureCaseNames[c]}» {Math.Min(_treasureShards[c], TreasureGlue)}/{TreasureGlue}";
        if (_treasureShards[c] >= TreasureGlue) text = TreasureGlueOne(c, text);
        return ActResult.Accept(text);
    }

    // ---------- вид, каталог ----------

    object? ViewTreasure()
    {
        if (!TreasureOpen) return null;
        var r = RiddleToday();
        return new
        {
            mask = _treasure,
            shards = _treasureShards,
            // Загадка дня: правильну відповідь клієнт дізнається, лише коли вже відповів (тоді item/pick).
            riddle = r is null ? null : new
            {
                text = r.Text,
                options = r.Options,
                done = _rdDone,
                right = _rdDone && _rdRight,
                item = _rdDone ? r.Item : null,
                pick = _rdDone ? _rdPick : null,
            },
        };
    }

    object CatalogTreasure() => new
    {
        item = TreasureItemBonus,
        hall = TreasureHallBonus,
        glue = TreasureGlue,
        chance = TreasureChance,
        items = Treasures.Select(t => new { key = t.Key, name = t.Name, emoji = t.Emoji, @case = t.Case }),
        cases = Enumerable.Range(0, TreasureCases).Select(c => new
        {
            key = TreasureCaseKeys[c], name = TreasureCaseNames[c], emoji = TreasureCaseEmoji[c],
            bonus = TreasureCaseBonusText[c], hint = TreasureCaseHint[c],
        }),
    };

    // ---------- збереження ----------

    sealed record TreasureRiddleRow(string Day, string Id, bool Done, bool Right, string Pick);

    void ResetTreasure()
    {
        _treasure = 0;
        Array.Clear(_treasureShards);
        _rdDay = _rdId = _rdPick = "";
        _rdDone = _rdRight = false;
        _rdSeen.Clear();
    }

    List<string>? SaveTreasure() =>
        _treasure == 0 ? null : Treasures.Where((_, i) => (_treasure & (1 << i)) != 0).Select(t => t.Key).ToList();

    List<int>? SaveTreasureShards() => _treasureShards.Any(n => n > 0) ? [.. _treasureShards] : null;

    TreasureRiddleRow? SaveRiddle() => _rdDay.Length == 0 ? null : new(_rdDay, _rdId, _rdDone, _rdRight, _rdPick);

    List<string>? SaveRiddleSeen() => _rdSeen.Count > 0 ? [.. _rdSeen] : null;

    void LoadTreasure(List<string>? items, List<int>? shards, TreasureRiddleRow? riddle, List<string>? seen)
    {
        ResetTreasure();
        foreach (var key in items ?? [])
        {
            var i = Array.FindIndex(Treasures, t => t.Key == key);
            if (i >= 0) _treasure |= 1 << i;
        }
        for (var c = 0; c < TreasureCases && shards is not null && c < shards.Count; c++)
            _treasureShards[c] = TreasureCaseFull(_treasure, c) ? 0 : Math.Clamp(shards[c], 0, TreasureGlue - 1);
        if (riddle is { Day.Length: > 0 })
        {
            _rdDay = riddle.Day;
            _rdId = riddle.Id ?? "";
            _rdDone = riddle.Done;
            _rdRight = riddle.Done && riddle.Right;
            _rdPick = riddle.Done ? riddle.Pick ?? "" : "";
        }
        foreach (var id in seen ?? [])
            if (id is { Length: > 0 } && _rdSeen.Count < 1000 && !_rdSeen.Contains(id)) _rdSeen.Add(id);
    }
}
