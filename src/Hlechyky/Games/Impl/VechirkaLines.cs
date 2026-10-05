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
        ["ev.water"] = ["Водяник просить: три шеляги — або гуля."],
    };

    readonly Dictionary<string, string[]> _pools;
    readonly Dictionary<string, int> _last = [];
    readonly Random _rng = new();

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
                    .Select(x => x.GetString()!).Where(x => x.Length > 0).ToArray();
                if (lines.Length > 0) res[p.Name] = lines;
            }
        }
        catch (Exception) { /* кривий файл — вбудований мінімум */ }
        return res;
    }

    public IEnumerable<string> Pool(string key) => _pools.TryGetValue(key, out var l) ? l : [];

    /// <summary>Рядок пулу з підстановками; пулу нема — null (ev.* без своїх реплік мовчать).</summary>
    public string? Render(string pool, IReadOnlyDictionary<string, string>? args)
    {
        if (!_pools.TryGetValue(pool, out var lines) || lines.Length == 0) return null;
        var k = _rng.Next(lines.Length);
        if (lines.Length > 1 && _last.TryGetValue(pool, out var was) && was == k) k = (k + 1) % lines.Length;
        _last[pool] = k;
        var s = lines[k];
        if (args is not null) foreach (var (key, v) in args) s = s.Replace("{" + key + "}", v);
        return s;
    }
}
