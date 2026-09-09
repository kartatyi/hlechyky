using System.Numerics;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Один верстат майстерні: скільки коштує перший рівень, що дає і чи є в нього стеля.
/// Ціна росте вп'ятеро-навпіл за кожен куплений рівень (×1,5), округлення — вгору.
/// </summary>
/// <param name="MaxLevel">0 — купуй скільки хочеш; більше нуля — далі цього рівня верстат не тягнеться.</param>
public sealed record ClickerUpgrade(string Key, string Name, string Desc, long Base, int MaxLevel = 0)
{
    /// <summary>Далі цього рівня ціна вже не влазить у long, і рахувати її нема сенсу — стільки глеків не наліпить ніхто.</summary>
    const int Ceiling = 128;

    public bool Capped(int level) => MaxLevel > 0 && level >= MaxLevel;

    /// <summary>
    /// Ціна наступного рівня: <c>ceil(Base × 1,5^level)</c>. Рахуємо цілими (3^n / 2^n), бо double на
    /// високих рівнях промахується на одиницю, а ціна в магазині мусить бути та сама і в тесті, і на екрані.
    /// </summary>
    public long Price(int level)
    {
        if (level < 0) level = 0;
        if (level >= Ceiling) return long.MaxValue;
        var num = BigInteger.Pow(3, level) * Base;
        var den = BigInteger.Pow(2, level);
        var price = (num + den - 1) / den;
        return price > long.MaxValue ? long.MaxValue : (long)price;
    }
}

/// <summary>
/// Гончарне коло — соло-клікер на одного назавжди. Кімната приватна й persistent: закрив вкладку, прийшов
/// через тиждень — коло крутилось і без тебе (але не більше ніж вісім годин, інакше з відпустки повертались
/// би мільйонери). Валюта тут своя, глеки; у черепки вони переходять лише через прилавок, сотнями.
///
/// Правила живуть тільки тут: клієнт батчить кліки й малює плавний долік, але кожен глек рахує сервер за
/// <see cref="IRoomContext.Clock"/>.
/// </summary>
public sealed class Clicker : Game
{
    /// <summary>Скільки глеків іде за один черепок.</summary>
    public const int Rate = 100;
    /// <summary>Більше кліків за секунду — це вже не палець, а скрипт; зайве мовчки відкидаємо.</summary>
    public const int MaxClicksPerSecond = 12;
    /// <summary>Скільки черепків на день можна виміняти, коли економіки поруч нема (тести, гола гра).</summary>
    public const int DefaultDailyCap = 20;
    /// <summary>За скільки офлайну коло ще платить. Далі — тиша: інакше відпустка коштувала б грі балансу.</summary>
    public static readonly TimeSpan OfflineCap = TimeSpan.FromHours(8);

    /// <summary>Магазин. Порядок тут — це порядок кнопок на екрані, від дешевого до дорогого.</summary>
    public static readonly ClickerUpgrade[] Shop =
    [
        new("wheel", "Швидше коло", "+1 глек за клік", 15),
        new("apprentice", "Підмайстер", "+0,5 глека за секунду", 100),
        new("kiln", "Піч", "+3 глеки за секунду", 1_000),
        new("clay", "Гарна глина", "×1,25 до всього", 10_000, MaxLevel: 5),
    ];

    static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    public override GameInfo Info { get; } = new(
        "clicker", "Гончарне коло", "гончарне коло", GameGroup.Solo, 1, 1,
        Start: StartMode.Immediate, Private: true, Persistent: true, Score: ScoreOrder.HigherIsBetter,
        Hint: "Крути коло, ліпи глеки. Глеки — у черепки, черепки — у гаманець. "
            + "Коло крутиться і без тебе, поки ти слухаєш радіо");

    readonly Dictionary<string, int> _levels = new(StringComparer.Ordinal);

    /// <summary>
    /// Відро дозволів на кліки: дванадцять на дні, доливається дванадцять за секунду. Жорстке вікно «12 за
    /// останню секунду» тут не годиться — клієнт шле пачку раз на 700 мс, і дві сусідні пачки завжди падали б
    /// в одне вікно, з'їдаючи в чесного гравця кожен другий клік. Відро тримає ту саму швидкість, але не
    /// карає за те, що кліки приїхали купкою.
    /// </summary>
    double _tokens;
    DateTimeOffset _tokensAt;

    long _pots;
    long _total;
    /// <summary>Недоліплений глек: пасив рідко дає ціле число, а губити півглека щосекунди — це половина доходу.</summary>
    double _carry;
    DateTimeOffset _lastSync;
    string _soldDay = "";
    int _soldShards;
    /// <summary>Останнє число, яке вже пішло в таблицю: те саме слати вдруге — марно смикати базу.</summary>
    long _scored = -1;
    DateTimeOffset _scoredAt;
    IOptionsMonitor<EconomyOptions>? _opts;

    /// <summary>
    /// Як часто число з таблиці оновлюється під час клацання. Кожна пачка кліків — це запис у ту саму
    /// SQLite, у яку пише ефір, тож півхвилини затримки в таблиці «Гончарі» коштують дешевше, ніж
    /// півтора запису на секунду з кожного гончаря.
    /// </summary>
    static readonly TimeSpan ScoreEvery = TimeSpan.FromSeconds(30);

    /// <summary>Пороги ачівок «Гончар» і «Майстер-гончар»: платформа бачить їх саме з таблиці, тож ці
    /// числа мусять летіти негайно, а не чекати своєї півхвилини.</summary>
    static readonly long[] Milestones = [1_000, 100_000];

    // ---------- те, з чого складається дохід ----------

    int Level(string key) => _levels.TryGetValue(key, out var n) ? n : 0;

    /// <summary>Гарна глина множить усе — і клік, і пасив. 1,25^n рахується в double точно, степінь мала.</summary>
    double ClayMult => Math.Pow(1.25, Level("clay"));

    /// <summary>
    /// Глеків за один клік. Клік мусить бути цілим числом — «+1,25 глека» на екрані виглядало б як помилка,
    /// тож множник глини тут округлюємо, а дробову частину віддаємо пасиву, де вона рахується чесно.
    /// </summary>
    public long PerClick => Math.Max(1, (long)Math.Round((1 + Level("wheel")) * ClayMult, MidpointRounding.AwayFromZero));

    /// <summary>Глеків за секунду без тебе.</summary>
    public double PerSecond => (Level("apprentice") * 0.5 + Level("kiln") * 3) * ClayMult;

    /// <summary>Стеля обміну на сьогодні: беремо з налаштувань економіки, а без неї — типову.</summary>
    int DailyCap => Math.Max(0, _opts?.CurrentValue.ClickerDailyCap ?? DefaultDailyCap);

    /// <summary>Скільки вже виміняно САМЕ сьогодні: після півночі лічильник сам стає нулем.</summary>
    int SoldToday => _soldDay == Days.Today(Ctx.Clock) ? _soldShards : 0;

    // ---------- життя партії ----------

    public override string SeatName(int seat) => "гончар";

    public override void Start()
    {
        // Сервіси беремо тут: гру створює реєстр конструктором без параметрів, а в тестах економіки
        // взагалі нема — тоді стеля обміну лишається типовою, і гра від цього не ламається.
        _opts = Ctx.Services.GetService<IOptionsMonitor<EconomyOptions>>();

        _pots = 0;
        _total = 0;
        _carry = 0;
        _scored = -1;
        _scoredAt = default;
        _levels.Clear();
        foreach (var up in Shop) _levels[up.Key] = 0;
        _soldDay = Days.Today(Ctx.Clock);
        _soldShards = 0;
        _lastSync = Ctx.Clock.UtcNow;
        _tokens = MaxClicksPerSecond;
        _tokensAt = _lastSync;
    }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        // Пасив дораховуємо перед кожною дією: і клік, і покупка мусять бачити однакове число глеків.
        Sync();
        var result = action switch
        {
            "spin" => Spin(payload),
            "buy" => Buy(payload),
            "sell" => Sell(payload),
            _ => ActResult.Fail("Тут так не ходять"),
        };
        // Таблиця «Гончарі» — це глеки за весь час; те саме число вдруге їй нічого не додасть.
        if (result.Ok && WorthScoring())
        {
            _scored = _total;
            _scoredAt = Ctx.Clock.UtcNow;
            Ctx.Score(0, _total);
        }
        return result;
    }

    /// <summary>
    /// Чи варто зараз оновлювати рядок у таблиці. Клієнт шле пачку кліків раз на 700 мс, і <c>_total</c>
    /// росте від кожної — писати в базу півтора рази на секунду задорого. Тому під час клацання число
    /// оновлюється раз на <see cref="ScoreEvery"/>, а пороги ачівок ідуть одразу.
    /// </summary>
    bool WorthScoring()
    {
        if (_total == _scored) return false;
        if (_scored < 0) return true;                                    // перше число після Start/Load
        if (Ctx.Clock.UtcNow - _scoredAt >= ScoreEvery) return true;
        foreach (var mark in Milestones)
            if (_scored < mark && _total >= mark) return true;
        return false;
    }

    /// <summary>
    /// Пасив від останньої синхронізації. Стеля 8 год — не за сесію, а за один проміжок: хто заходить
    /// щодня, її й не помітить, а хто зник на тиждень, дістане рівно вісім годин роботи підмайстрів.
    /// </summary>
    void Sync()
    {
        RollDay();
        var now = Ctx.Clock.UtcNow;
        var idle = now - _lastSync;
        _lastSync = now;
        if (idle <= TimeSpan.Zero) return;
        if (idle > OfflineCap) idle = OfflineCap;

        _carry += idle.TotalSeconds * PerSecond;
        var whole = (long)Math.Floor(_carry);
        if (whole <= 0) return;
        _carry -= whole;
        _pots += whole;
        _total += whole;
    }

    /// <summary>
    /// Перекинути лічильник обміну на новий день. Робиться одним місцем навмисно: доти, доки день чистили
    /// «по дорозі», вчорашня сума встигала переїхати в сьогодні й з'їдала гравцеві цілу денну стелю.
    /// </summary>
    void RollDay()
    {
        var today = Days.Today(Ctx.Clock);
        if (_soldDay == today) return;
        _soldDay = today;
        _soldShards = 0;
    }

    // ---------- дії ----------

    /// <summary>
    /// Клік. Клієнт батчить: замість дванадцяти повідомлень за секунду шле одне з <c>n</c>. Понад
    /// дванадцять кліків на секунду не рахуємо, але й не сваримось — чесний гравець із лагом не має
    /// бачити червоних тостів через власний інтернет.
    /// </summary>
    ActResult Spin(JsonElement payload)
    {
        // «Поля нема» — це один клік (так шле кнопка), а от «n: 0» чи «n: −7» — це вже не клік, і мовчки
        // домальовувати з нього глек не можна: чого не просили, того й не нараховуємо.
        var raw = Num(payload, "n");
        if (raw is <= 0) return ActResult.Fail("Кліків має бути хоч один");
        var asked = (int)Math.Clamp(raw ?? 1, 1, MaxClicksPerSecond);
        var taken = Math.Min(asked, Allowance());
        _tokens -= taken;

        var gain = taken * PerClick;
        _pots += gain;
        _total += gain;
        return ActResult.Done;
    }

    /// <summary>Скільки кліків відро готове віддати просто зараз; заразом і доливає його.</summary>
    int Allowance()
    {
        var now = Ctx.Clock.UtcNow;
        var seconds = Math.Max(0, (now - _tokensAt).TotalSeconds);
        _tokens = Math.Min(MaxClicksPerSecond, _tokens + seconds * MaxClicksPerSecond);
        _tokensAt = now;
        return (int)Math.Floor(_tokens);
    }

    ActResult Buy(JsonElement payload)
    {
        if (Shop.FirstOrDefault(u => u.Key == Str(payload, "key")) is not { } up)
            return ActResult.Fail("Такого верстата в майстерні нема");

        var level = Level(up.Key);
        if (up.Capped(level)) return ActResult.Fail($"{up.Name}: кращої вже не буває");

        var price = up.Price(level);
        if (_pots < price) return ActResult.Fail($"Бракує глеків: треба ще {price - _pots}");

        _pots -= price;
        _levels[up.Key] = level + 1;
        return ActResult.Accept($"{up.Name} — рівень {level + 1}");
    }

    /// <summary>Прилавок: сотня глеків за черепок, не більше <see cref="DailyCap"/> черепків на день.</summary>
    ActResult Sell(JsonElement payload)
    {
        RollDay();   // після київської півночі лічильник дня чистий — і перевірка, і запис бачать нуль
        var pots = Num(payload, "pots") ?? 0;
        if (pots <= 0 || pots % Rate != 0) return ActResult.Fail($"Міняю сотнями: {Rate} глеків — один черепок");
        if (pots > _pots) return ActResult.Fail("Стільки глеків ще не наліплено");

        var left = Math.Max(0, DailyCap - _soldShards);
        if (left <= 0) return ActResult.Fail("Сьогодні черепки скінчились, приходь завтра");
        // Рахуємо в long і ріжемо стелею ДО приведення: глеків у стані може лежати скільки завгодно
        // (стан — це JSON у базі), а (int) від такої частки мовчки загорнувся б у мінус.
        var want = pots / Rate;
        if (want > left) return ActResult.Fail($"Сьогодні лишилось {left} — більше не візьму");
        var shards = (int)want;

        _pots -= pots;
        _soldShards += shards;
        // Стеля дня в економіці своя (Economy:ClickerDailyCap) — наш лічильник лише показує її гравцеві наперед.
        Ctx.Award(0, shards, "clicker");
        return ActResult.Accept($"Обміняв {pots} глеків на {shards} {Shards(shards)}");
    }

    /// <summary>Черепок / черепки / черепків — «2 черепків» ріже око так само, як і в гаманці.</summary>
    static string Shards(int n) =>
        n % 100 is >= 11 and <= 14 ? "черепків"
        : (n % 10) switch { 1 => "черепок", 2 or 3 or 4 => "черепки", _ => "черепків" };

    // ---------- вид ----------

    public override object View(int? seat)
    {
        // Пасив рахуємо і на відкритті, а не лише при дії (так каже spec): гончар, який повернувся й
        // просто дивиться на коло, мусить одразу бачити зароблене, а не чекати першого кліка. View
        // каркас кличе під замком кімнати (Rooms.ViewsFor), тож синхронізувати тут безпечно.
        Sync();
        return new
        {
            pots = _pots,
            total = _total,
            perClick = PerClick,
            perSecond = PerSecond,
            upgrades = Shop.ToDictionary(u => u.Key, u => (object)new
            {
                level = Level(u.Key),
                price = u.Price(Level(u.Key)),
                name = u.Name,
                desc = u.Desc,
                max = u.MaxLevel,
            }, StringComparer.Ordinal),
            canSellToday = Math.Max(0, DailyCap - SoldToday),
            soldToday = SoldToday,
            cap = DailyCap,
            rate = Rate,
            // Клієнт доліковує глеки від цієї мітки — тому вона мусить бути на дроті, а не лише в пам'яті.
            lastSync = _lastSync,
            // І серверне «зараз» поруч: інакше клієнт міряв би серверну мітку своїм годинником, а збитий
            // на кілька хвилин годинник малював би сотні глеків, яких на сервері нема.
            now = Ctx.Clock.UtcNow,
        };
    }

    // ---------- збереження ----------

    /// <summary>
    /// Стан у сховищі. Форма — зі spec, з двома дописками: <c>carry</c>, щоб недоліплений глек не губився
    /// на F5, і відро дозволів замість списку кліків (див. <see cref="Allowance"/>).
    /// </summary>
    sealed record SoldRow(string Day, int Shards);

    sealed record BucketRow(double Tokens, DateTimeOffset At);

    sealed record Snapshot(
        long Pots, long Total, double Carry, DateTimeOffset LastSync,
        Dictionary<string, int> Upgrades, SoldRow SoldToday, BucketRow Clicks);

    public override string? Save() => JsonSerializer.Serialize(
        new Snapshot(_pots, _total, _carry, _lastSync,
            new Dictionary<string, int>(_levels, StringComparer.Ordinal),
            new SoldRow(_soldDay, _soldShards), new BucketRow(_tokens, _tokensAt)),
        Wire);

    public override void Load(string json)
    {
        // Стан лежить у базі й міг застати попередню версію гри або чиюсь правку руками: чого не зрозуміли —
        // лишаємо чистим, але партію через це не ламаємо.
        if (JsonSerializer.Deserialize<Snapshot>(json, Wire) is not { } s) return;

        _pots = Math.Max(0, s.Pots);
        _total = Math.Max(_pots, s.Total);
        _carry = double.IsFinite(s.Carry) ? Math.Clamp(s.Carry, 0, 1) : 0;
        _lastSync = s.LastSync == default ? Ctx.Clock.UtcNow : s.LastSync;
        _scored = -1;
        _scoredAt = default;

        _levels.Clear();
        foreach (var up in Shop)
        {
            var level = s.Upgrades is not null && s.Upgrades.TryGetValue(up.Key, out var n) ? n : 0;
            _levels[up.Key] = up.MaxLevel > 0 ? Math.Clamp(level, 0, up.MaxLevel) : Math.Max(0, level);
        }

        _soldDay = s.SoldToday?.Day ?? "";
        _soldShards = Math.Max(0, s.SoldToday?.Shards ?? 0);

        // Відро переживає F5 навмисно: інакше перезавантаження сторінки скидало б захист від автоклікера.
        _tokens = s.Clicks is { } b && double.IsFinite(b.Tokens) ? Math.Clamp(b.Tokens, 0, MaxClicksPerSecond) : MaxClicksPerSecond;
        _tokensAt = s.Clicks is { At: var at } && at != default ? at : _lastSync;
    }

    // ---------- дрібниці ----------

    static long? Num(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    static string Str(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
