using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Книга фраз Дядька Глека для вечірки (§13): <c>data/vechirka/lines.json</c> — пул = масив рядків, випадковий не
/// той, що минулого разу; підстановки <c>{nick} {nick2} {n} {item} {place} {game} {title}</c>. Файла нема чи кривий —
/// вбудований мінімум по рядку на пул. Рандом фраз — свій: на стан гри він не впливає.
/// </summary>
public sealed class VechirkaLines
{
    /// <summary>✱-пули: озвучуються й не підпадають під темп «раз на 20 с».</summary>
    public static readonly HashSet<string> Starred =
        ["intro", "buy", "ev.swap", "duel", "late", "lastGame", "final", "win", "tie",
         "bonus.mgwins", "bonus.earned", "bonus.steps", "bonus.bully", "bonus.traps", "bonus.shop", "bonus.chests"];

    static readonly Dictionary<string, string[]> Builtin = new()
    {
        ["intro"] = ["Добрий вечір, люди добрі! Глечикова вечірка починається — до золотих глеків!"],
        ["order"] = ["Кубики сказали своє: першим іде {nick}."],
        ["turnFirst"] = ["{nick}, кидай кубик і йди до мого золотого глека!"],
        ["double"] = ["Дубль! {nick}, тримай два шеляги на радощах."],
        ["buy"] = ["Продано! Золотий глек їде до нового господаря."],
        ["buyMove"] = ["Лавка переїжджає — шукайте мене деінде!"],
        ["broke"] = ["{nick}, глек є, а шелягів нема. Заходь, як розбагатієш."],
        ["noBuy"] = ["{nick} відмовився від глека? Ну, хазяїн — пан."],
        ["pan"] = ["{nick} пригостив {nick2} пательнею. Дзвінко!"],
        ["fork"] = ["{nick} вилами підчепив шеляги в {nick2}."],
        ["rope"] = ["{nick} арканом притягнув {nick2} до себе."],
        ["pumpkin"] = ["Гарбуз! {nick} і {nick2} помінялись місцями."],
        ["feather"] = ["Лелека несе {nick} просто під мою лавку!"],
        ["gate"] = ["{nick} поставив шлагбаум. Хто наступний — плати!"],
        ["gateHit"] = ["{nick} уперся в шлагбаум {nick2}. Плати, куме!"],
        ["charm"] = ["Оберіг відбив! Не того дня, не тому {nick}."],
        ["bump"] = ["Ой, гуля в {nick}. Наступний хід — на одному кубику."],
        ["trap"] = ["{nick}, обережно — тут ями!"],
        ["bank"] = ["{nick} зірвав скарбничку: {n} шелягів!"],
        ["ferry"] = ["{nick} пливе поромом — з вітерцем!"],
        ["chest"] = ["{nick} знайшов у скрині: {item}."],
        ["chestEmpty"] = ["Скриня порожня, лише три шеляги на дні."],
        ["shopBuy"] = ["{nick} купив {item}. Добрий вибір!"],
        ["duel"] = ["Дуель! {nick} проти {nick2} — розступіться!"],
        ["duelWin"] = ["{nick} виграв дуель!"],
        ["duelSplit"] = ["Годі битись, я вас розняв. Тримай п'ять шелягів на заспокоєння."],
        ["betWin"] = ["Хто вгадав переможця дуелі — по два шеляги від мене."],
        ["pick"] = ["{nick} обирає забаву — останнім можна."],
        ["mgStart"] = ["Забава: {game}!"],
        ["mgWin"] = ["{nick} найкращий у «{game}»!"],
        ["mgBroken"] = ["Забава зламалась — усім порівну, не сваріться."],
        ["away"] = ["{nick} кудись подівся — за нього походить бот."],
        ["back"] = ["{nick} повернувся! Сідай, твоє місце тепле."],
        ["afk"] = ["{nick} задрімав — походжу за нього."],
        ["late"] = ["Пізній вечір! Монети й пастки — удвічі, скарбничку розбито."],
        ["lateGift"] = ["{nick}, тримай подарунок від Глека."],
        ["bankSplit"] = ["Скарбничку розбито — гроші тим, кому скрутно."],
        ["lastGame"] = ["Остання забава — шеляги удвічі!"],
        ["lead"] = ["{nick} тепер попереду всіх!"],
        ["swapIn"] = ["{nick} сідає замість бота — вітаємо!"],
        ["empty"] = ["Нікого нема? Почекаю, я терплячий."],
        ["final"] = ["Ну що, рахуємо глеки! А ще в мене три бонусні."],
        ["win"] = ["Голова вечірки — {nick}! Слава!"],
        ["winBot"] = ["Переміг {nick}… Люди, ну як так?"],
        ["tie"] = ["Нічия на вершині — обидва молодці!"],
        ["pause"] = ["Пауза. Глек ставить самовар."],
        ["unpause"] = ["Граємо далі!"],
        ["ev.vodyanyk"] = ["Водяник просить: три шеляги — або гуля."],
        ["comeback"] = ["Хто на половині вечора пас задніх, той тепер у трійці. Отак!"],
        ["noGames"] = ["Забав сьогодні нема — граємо далі без них."],
    };

    /// <summary>
    /// Підстановки, дозволені пулу (таблиця етапу C, <c>lines-allowed.md</c>). Рядок файла з чужою підстановкою
    /// відкидаємо на завантаженні: інакше в балачку полетіло б «{item}» сирцем. Пулу нема в таблиці — лише без підстановок.
    /// </summary>
    public static readonly Dictionary<string, string[]> Allowed = new()
    {
        ["intro"] = ["n"], ["order"] = ["nick"], ["turnFirst"] = ["nick"], ["double"] = ["nick"],
        ["buy"] = ["nick", "n"], ["buyMove"] = [], ["broke"] = ["nick", "n"], ["noBuy"] = ["nick"],
        ["pan"] = ["nick", "nick2"], ["fork"] = ["nick", "nick2", "n"], ["rope"] = ["nick", "nick2"],
        ["pumpkin"] = ["nick", "nick2"], ["feather"] = ["nick"], ["gate"] = ["nick"], ["gateHit"] = ["nick", "nick2", "n"],
        ["charm"] = ["nick", "nick2", "item"], ["bump"] = ["nick"], ["trap"] = ["nick", "n"], ["bank"] = ["nick", "n"],
        ["ferry"] = ["nick"], ["chest"] = ["nick", "item"], ["chestEmpty"] = ["nick"],
        ["ev.fair"] = ["nick"], ["ev.swap"] = ["nick"], ["ev.rain"] = ["nick"], ["ev.gift"] = ["nick", "item"],
        ["ev.wind"] = ["nick", "n"], ["ev.dog"] = ["nick"], ["ev.wedding"] = ["nick"], ["ev.wheel"] = ["nick"],
        ["ev.poor"] = ["nick"], ["ev.move"] = ["nick"], ["ev.sale"] = ["nick"], ["ev.tax"] = ["nick", "nick2", "n"],
        ["ev.vodyanyk"] = ["nick"], ["shopBuy"] = ["nick", "item"], ["duel"] = ["nick", "nick2", "n"],
        ["duelWin"] = ["nick", "nick2", "n"], ["pick"] = ["nick"], ["mgStart"] = ["game"], ["mgWin"] = ["nick", "game"],
        ["mgBroken"] = ["game"], ["away"] = ["nick"], ["back"] = ["nick"], ["afk"] = ["nick"], ["late"] = ["nick"],
        ["lateGift"] = ["nick"], ["bankSplit"] = ["n"], ["lastGame"] = ["nick"], ["lead"] = ["nick", "n"],
        ["comeback"] = ["nick"], ["duelSplit"] = ["nick"], ["betWin"] = ["n"], ["swapIn"] = ["nick", "nick2"],
        ["empty"] = [], ["final"] = [], ["win"] = ["nick", "n"], ["winBot"] = ["nick"], ["tie"] = ["nick"],
        ["pause"] = [], ["unpause"] = [], ["noMinis"] = [], ["noGames"] = [],
    };

    static readonly System.Text.RegularExpressions.Regex Slot = new(@"\{(\w+)\}");

    /// <summary>Підстановки рядка: <c>{nick}</c> → <c>nick</c>.</summary>
    public static IEnumerable<string> Slots(string line) => Slot.Matches(line).Select(m => m.Groups[1].Value);

    static string[] AllowedOf(string pool) =>
        pool.StartsWith("bonus.") ? ["nick", "title"] : Allowed.GetValueOrDefault(pool) ?? [];

    readonly Dictionary<string, string[]> _pools;
    readonly Dictionary<string, int> _last = [];
    Random _rng = new();

    /// <summary>Вибір репліки — з ГВЧ, засіяного з Ctx.Rng вечірки: детермінізм за сідом (R1 m4).</summary>
    public VechirkaLines Seeded(int seed) { _rng = new Random(seed); return this; }

    public VechirkaLines(Dictionary<string, string[]>? pools = null) => _pools = pools ?? Builtin;

    static readonly Lazy<VechirkaLines> Shared = new(() => new VechirkaLines(Read()));

    /// <summary>Книга з файла (кеш на процес). Пули, яких нема у файлі, — з вбудованого мінімуму.</summary>
    public static VechirkaLines Book => new(Shared.Value._pools);

    static Dictionary<string, string[]> Read()
    {
        var res = new Dictionary<string, string[]>(Builtin);
        try
        {
            var path = Path.Combine(Paths.Root, "data", "vechirka", "lines.json");
            if (!File.Exists(path)) return res;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Array) continue;
                var lines = p.Value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()!).Where(x => x.Length > 0)
                    .Where(x => Slots(x).All(AllowedOf(p.Name).Contains)).ToArray();
                if (lines.Length > 0) res[p.Name] = lines;
            }
        }
        catch (Exception) { /* кривий файл — вбудований мінімум */ }
        return res;
    }

    /// <summary>
    /// Що прогріти озвучці на старті (§13): рядки ✱-пулів без підстановок, по два з пулу по колу — спершу ті, що
    /// звучать найчастіше чи найважливіше (<c>buy late bonus.* win</c>), — не більше <paramref name="max"/>.
    /// </summary>
    public IReadOnlyList<string> Warm(int max)
    {
        static int Rank(string k) => k == "buy" ? 0 : k == "late" ? 1 : k.StartsWith("bonus.") ? 2 : k == "win" ? 3 : 4;
        var pools = Starred.OrderBy(Rank).ThenBy(k => k, StringComparer.Ordinal)
            .Select(k => Pool(k).Where(l => !l.Contains('{')).ToList()).Where(l => l.Count > 0).ToList();
        var res = new List<string>();
        for (var from = 0; res.Count < max && pools.Any(l => l.Count > from); from += 2)
            foreach (var l in pools)
                foreach (var line in l.Skip(from).Take(2))
                    if (res.Count < max && !res.Contains(line)) res.Add(line);
        return res;
    }

    public IEnumerable<string> Pool(string key) => _pools.TryGetValue(key, out var l) ? l : [];

    /// <summary>Рядок пулу з підстановками; пулу нема — null (ev.* без своїх реплік мовчать).</summary>
    public string? Render(string pool, IReadOnlyDictionary<string, string>? args)
    {
        if (!_pools.TryGetValue(pool, out var all) || all.Length == 0) return null;
        // лише рядки, чиї підстановки цей виклик дає (бонус без переможця — без {nick})
        var ok = Enumerable.Range(0, all.Length)
            .Where(i => Slots(all[i]).All(x => args is not null && args.TryGetValue(x, out var v) && v.Length > 0)).ToList();
        if (ok.Count == 0) return null;
        var j = _rng.Next(ok.Count);
        if (ok.Count > 1 && _last.TryGetValue(pool, out var was) && ok[j] == was) j = (j + 1) % ok.Count;
        var k = ok[j];
        _last[pool] = k;
        var s = all[k];
        if (args is not null) foreach (var (key, v) in args) s = s.Replace("{" + key + "}", v);
        return s;
    }
}
