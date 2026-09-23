using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Сорт глини на колі — це стратегія: червона для того, хто клацає, біла для того, хто чекає, чорна для
/// ловця глеків. <paramref name="Events"/> множить проміжки між розписними й падаючими глеками (менше — частіше),
/// <paramref name="Loot"/> — глек з полиці й щедрого купця. <paramref name="Body"/> — колір простого глека на колі.
/// </summary>
public sealed record ClickerClay(string Key, string Name, string Desc, double Price, double Click, double Passive, double Events, double Loot, string Body);

/// <summary>Знаряддя гончаря: одноразова покупка зі своїм ефектом, видима на стіні майстерні, обпал не спалює.</summary>
public sealed record ClickerTool(string Key, string Name, string Desc, double Price);

/// <summary>
/// Прикраса хати: краса й відсоток до всього (<paramref name="Bonus"/>), деякі ще щось уміють. Лишається назавжди.
/// Перший ряд дає 2 %, другий (дев'яте оновлення) — 3 %.
/// </summary>
public sealed record ClickerDecor(string Key, string Name, string Desc, double Price, double Bonus = 0.02);

/// <summary>Один варіант оздоби: «черепиця» для стріхи, «вишня» для дерева. Ціна — у клеймах; нульова — те, що й так було.</summary>
public sealed record ClickerLookOption(string Value, string Name, int Price);

/// <summary>
/// Оздоба хати: що саме гравець міняє (стріха, стіни, тин, дерево, колір кола, кіт із собакою) і з чого вибирає.
/// Купується за клейма раз і назавжди, обране лежить у <c>house.look</c>; на дохід не впливає — це про «свою хату».
/// </summary>
public sealed record ClickerLook(string Key, string Name, string Desc, ClickerLookOption[] Options);

/// <summary>
/// Дивовижа: річ із байкою, що трапляється сама, коли в хаті стається щось рідкісне. <paramref name="Triggers"/> —
/// з яких подій її можна знайти (імена — з контракту v9 §F.4). Кожна знайдена додає відсоток до всього.
/// </summary>
public sealed record ClickerWonder(string Key, string Name, string Tale, string[] Triggers);

/// <summary>
/// Замовлення на дошці купців. <c>invest</c> — купець бере глеки і за <paramref name="Minutes"/> повертає більше;
/// <c>style</c> — бере глеки в розписі <paramref name="Style"/> і платить одразу. <paramref name="Until"/> — коли
/// купець поїде (дошка оновлюється).
/// </summary>
public sealed record ClickerOrder(int Id, string Kind, string Merchant, double Need, double Pay, int Minutes, string Style, DateTimeOffset Until);

/// <summary>Купець у дорозі: повернеться о <paramref name="PayAt"/> з <paramref name="Pay"/> глеками.</summary>
public sealed record ClickerTaken(int Id, string Merchant, double Pay, DateTimeOffset PayAt);

/// <summary>Купець повернувся: клієнт малює «+N» над сценою, коли бачить новий запис.</summary>
public sealed record ClickerPaid(int Id, string Merchant, double Pay, DateTimeOffset At);

/// <summary>
/// Хата гончаря: те, що купується не рівнями, а речами, і що видно на сцені — глина, знаряддя, прикраси — і
/// дошка купців із замовленнями. Правила доходу лишаються в <c>Clicker.cs</c>; тут — каталоги, дії, вид і
/// збереження цього шматка. Знаряддя, прикраси й куплені глини обпал не чіпає; глина на колі, дошка й купці в
/// дорозі — згорають.
/// </summary>
public sealed partial class Clicker
{
    // ---------- глина ----------

    /// <summary>Замішана глина відлежується: перемкнути наступну можна не раніше. Інакше глину міняли б на кожен клік.</summary>
    public static readonly TimeSpan ClayRest = TimeSpan.FromMinutes(10);
    /// <summary>З годинником із зозулею глина відлежується вдвічі менше: стратегію можна міняти частіше.</summary>
    public static readonly TimeSpan ClayRestQuick = TimeSpan.FromMinutes(5);

    /// <summary>Скільки відлежується глина саме в цій хаті.</summary>
    TimeSpan ClayRestNow => Tool("clock") ? ClayRestQuick : ClayRest;

    public static readonly ClickerClay[] Clays =
    [
        new("", "Звичайна глина", "Як є: без сильних і слабких сторін", 0, 1, 1, 1, 1, ""),
        new("red", "Червона глина", "Клік ×2, пасив ×0,7 — для того, хто клацає", 3_000, 2, 0.7, 1, 1, "#a8402c"),
        new("white", "Біла глина", "Пасив ×1,5, клік ×0,5 — для того, хто чекає", 30_000, 0.5, 1.5, 1, 1, "#e6ddcb"),
        new("black", "Чорна глина", "Розписні й падаючі глеки на 40 % частіші, глек з полиці й купець ×1,5 — для ловця", 300_000, 1, 1, 0.6, 1.5, "#2f2b2e"),
    ];

    readonly HashSet<string> _clays = new(StringComparer.Ordinal);
    string _clay = "";
    DateTimeOffset _clayRestUntil;

    ClickerClay ClayNow => Clays.FirstOrDefault(c => c.Key == _clay) ?? Clays[0];

    /// <summary>Купити глину (одноразово) або замісити вже куплену на коло. Звичайна — завжди своя.</summary>
    ActResult Knead(JsonElement payload)
    {
        var key = Str(payload, "kind");
        if (Clays.FirstOrDefault(c => c.Key == key) is not { } clay) return ActResult.Fail("Такої глини нема");
        var now = Ctx.Clock.UtcNow;
        if (key.Length > 0 && !_clays.Contains(key))
        {
            if (_pots < clay.Price) return ActResult.Fail($"Бракує глеків: треба ще {Short(clay.Price - _pots)}");
            _pots -= clay.Price;
            _clays.Add(key);
            _clay = key;
            _clayRestUntil = now + ClayRestNow;
            return ActResult.Accept($"🪣 {clay.Name} — куплена й уже на колі");
        }
        if (_clay == key) return ActResult.Fail("Ця глина вже на колі");
        if (now < _clayRestUntil) return ActResult.Fail($"Глина ще відлежується: {Wait(_clayRestUntil - now)}");
        _clay = key;
        _clayRestUntil = now + ClayRestNow;
        return ActResult.Accept($"🪣 {clay.Name} — на колі");
    }

    // ---------- знаряддя ----------

    /// <summary>Скільки до розгону додає лопатка: тепло спадає вдвічі повільніше.</summary>
    public const double PaddleTau = 2;
    public const double RibsClick = 1.25, BucketPassive = 1.1, WhistleEvents = 0.75, ScalesBonus = 0.2;
    public static readonly TimeSpan LanternHours = TimeSpan.FromHours(2);
    /// <summary>З губкою глек з полиці летить довше — ловити легше.</summary>
    public static readonly TimeSpan FallShownLong = TimeSpan.FromSeconds(4.5);
    /// <summary>Тавро майстра: кожен обпал дає ще одне клеймо (лише коли обпал і так щось дає).</summary>
    public const int IronStamps = 1;

    // Другий ряд знарядь (дев'яте оновлення): те саме «раз і назавжди», тільки для тих, у кого вже мільйони.
    /// <summary>Рахівниця: купці платять ще стільки зверху (додається до ваг, а не множиться).</summary>
    public const double AbacusBonus = 0.2;
    /// <summary>Візок: наскільки більше платить базар (множник читає ClickerCraft.cs через HouseBazaarMult).</summary>
    public const double CartBazaar = 0.1;
    /// <summary>Скриня з замком: стільки глеків переживає обпал.</summary>
    public const double LockKeep = 0.05;
    /// <summary>Сітка під полицею: глек з полиці дає стільки зверху.</summary>
    public const double NetFall = 0.3;
    /// <summary>Люстро: дивовижі трапляються вдвічі частіше.</summary>
    public const double MirrorWonder = 2;
    /// <summary>Гасова лампа: ще дві години офлайну поверх ліхтаря.</summary>
    public static readonly TimeSpan LampHours = TimeSpan.FromHours(2);
    /// <summary>Дзвіночок над дверима: розписний глек стоїть на п'ять секунд довше.</summary>
    public static readonly TimeSpan BellGolden = TimeSpan.FromSeconds(5);

    public static readonly ClickerTool[] Tools =
    [
        new("paddle", "Дерев'яна лопатка", "Розгін спадає вдвічі повільніше", 5_000),
        new("string", "Струна для зрізання", "Клік бере ще +1 % пасиву", 20_000),
        new("sponge", "Губка", "Глек з полиці летить 4,5 с замість 3,2", 50_000),
        new("ribs", "Гончарні ребра", "Клік ×1,25", 100_000),
        new("lantern", "Ліхтар", "Коло крутиться без тебе ще +2 години", 200_000),
        new("apron", "Шкіряний фартух", "Один розбитий глек серії не обриває", 500_000),
        new("bucket", "Відро з водою", "Пасив +10 %", 1_000_000),
        new("whistle", "Глиняний свисток", "Розписні глеки на 25 % частіші", 2_000_000),
        new("scales", "Ваги купця", "Купці платять на 20 % більше", 5_000_000),
        new("iron", "Тавро майстра", "Кожен обпал дає ще одне клеймо", 50_000_000),
        // Другий ряд: хата вже повна, тож ці речі не про красу, а про те, чого бракує пізній грі.
        new("abacus", "Рахівниця", "Купці платять ще на 20 % більше", 100_000_000),
        new("cart", "Візок", "Базар платить на 10 % більше", 500_000_000),
        new("lamp", "Гасова лампа", "Коло крутиться без тебе ще +2 години", 2_000_000_000),
        new("clock", "Годинник із зозулею", "Глина відлежується 5 хв замість 10", 10_000_000_000),
        new("lock", "Скриня з замком", "Після обпалу лишається 5 % глеків", 50_000_000_000),
        new("net", "Сітка під полицею", "Глек з полиці дає на 30 % більше", 250_000_000_000),
        new("bell2", "Дзвіночок над дверима", "Розписний глек стоїть на 5 с довше", 1e12),
        new("mirror", "Люстро", "Дивовижі трапляються вдвічі частіше", 1e13),
    ];

    readonly HashSet<string> _tools = new(StringComparer.Ordinal);
    /// <summary>Фартух уже вибачив один промах у цій серії.</summary>
    bool _apronUsed;

    bool Tool(string key) => _tools.Contains(key);

    ActResult BuyTool(JsonElement payload)
    {
        if (Tools.FirstOrDefault(t => t.Key == Str(payload, "key")) is not { } tool) return ActResult.Fail("Такого знаряддя нема");
        if (_tools.Contains(tool.Key)) return ActResult.Fail($"{tool.Name} уже на стіні");
        if (_pots < tool.Price) return ActResult.Fail($"Бракує глеків: треба ще {Short(tool.Price - _pots)}");
        _pots -= tool.Price;
        _tools.Add(tool.Key);
        return ActResult.Accept($"🔧 {tool.Name}: {tool.Desc.ToLowerInvariant()}");
    }

    // ---------- прикраси ----------

    public const double DecorBonus = 0.02;
    /// <summary>Другий ряд прикрас (дев'яте оновлення) коштує дорожче й дає більше.</summary>
    public const double Decor2Bonus = 0.03;
    /// <summary>Собака стереже: розписний глек стоїть довше.</summary>
    public static readonly TimeSpan DogGuard = TimeSpan.FromSeconds(3);

    public static readonly ClickerDecor[] Decor =
    [
        new("towel", "Рушник над колом", "+2 % до всього", 10_000),
        new("icon", "Ікона в куті", "+2 % до всього", 100_000),
        new("window", "Вікно з калиною", "+2 % до всього", 1_000_000),
        new("rooster", "Півень на тину", "+2 % до всього", 10_000_000),
        new("dog", "Собака під лавою", "+2 % до всього, розписний глек стоїть на 3 с довше", 100_000_000),
        new("chest", "Мальована скриня", "+2 % до всього", 1_000_000_000),
        // Другий ряд: по три відсотки, і кожну видно на сцені.
        new("plakhta", "Плахта на стіні", "+3 % до всього", 10_000_000_000, Decor2Bonus),
        new("didukh", "Дідух на покуті", "+3 % до всього", 100_000_000_000, Decor2Bonus),
        new("posag", "Скриня-посаг", "+3 % до всього", 1e12, Decor2Bonus),
        new("khodyky", "Ходики", "+3 % до всього", 1e13, Decor2Bonus),
        new("portret", "Портрет Тараса", "+3 % до всього", 1e14, Decor2Bonus),
        new("lustra", "Люстра з рогів", "+3 % до всього", 1e15, Decor2Bonus),
    ];

    readonly HashSet<string> _decor = new(StringComparer.Ordinal);

    bool Adorned(string key) => _decor.Contains(key);

    /// <summary>
    /// Множник хати до всього: прикраси (2 % перший ряд, 3 % другий) і по відсотку за кожну знайдену дивовижу.
    /// Складається додаванням — як і решта «до всього» всередині свого гурту.
    /// </summary>
    double HouseAllMult => 1 + Decor.Where(d => _decor.Contains(d.Key)).Sum(d => d.Bonus) + WonderBonus * _wonders.Count;
    /// <summary>Множник глека з полиці: сітка під полицею ловить те, що інакше розбилось би.</summary>
    double HouseFallMult => Tool("net") ? 1 + NetFall : 1;
    /// <summary>На скільки довше стоїть розписний глек: дзвіночок над дверима попереджає завчасу.</summary>
    TimeSpan HouseGoldenExtra => Tool("bell2") ? BellGolden : TimeSpan.Zero;
    /// <summary>Частка глеків, що переживає обпал (0…0,5): скриня з замком. Секрет «Дідова скриня» пакет «Коло» додає тут же через Has("ashes").</summary>
    double HouseKeepShare => Tool("lock") ? LockKeep : 0;
    /// <summary>Скільки ще годин офлайну додає хата: гасова лампа світить після ліхтаря.</summary>
    TimeSpan HouseOfflineExtra => Tool("lamp") ? LampHours : TimeSpan.Zero;
    /// <summary>
    /// Наскільки щедріший базар від хати (візок). Гроші за вироби рахує ремесло (ClickerCraft.cs) — воно й множить.
    /// </summary>
    internal double HouseBazaarMult => Tool("cart") ? 1 + CartBazaar : 1;
    /// <summary>Скільки зверху платять купці: ваги й рахівниця складаються.</summary>
    double MerchantMult => 1 + (Tool("scales") ? ScalesBonus : 0) + (Tool("abacus") ? AbacusBonus : 0);

    ActResult Adorn(JsonElement payload)
    {
        if (Decor.FirstOrDefault(d => d.Key == Str(payload, "key")) is not { } decor) return ActResult.Fail("Такої прикраси нема");
        if (_decor.Contains(decor.Key)) return ActResult.Fail($"{decor.Name} уже в хаті");
        if (_pots < decor.Price) return ActResult.Fail($"Бракує глеків: треба ще {Short(decor.Price - _pots)}");
        _pots -= decor.Price;
        _decor.Add(decor.Key);
        return ActResult.Accept($"🏠 {decor.Name} — тепер у хаті, +{decor.Bonus * 100:0} % до всього");
    }

    // ---------- оздоба за клейма ----------

    /// <summary>
    /// Хата «під себе»: стріха, стіни, тин, дерево коло хати, колір кола й масть кота з собакою. Платиться
    /// клеймами (вони на оздобі не згорають і бонусу не гублять — як і на секретах), куплене лишається назавжди,
    /// а обпал оздоби не чіпає: хата ж не горить. Перший варіант кожного гурту безплатний — це те, що вже було.
    /// </summary>
    public static readonly ClickerLook[] Looks =
    [
        new("roof", "Стріха", "Чим укрита хата", [
            new("straw", "Солома", 0), new("reed", "Очерет", 15), new("shingle", "Ґонт", 25), new("tile", "Черепиця", 40),
        ]),
        new("wall", "Стіни", "Чим мащена й білена хата", [
            new("white", "Біла", 0), new("blue", "Блакитна", 15), new("yellow", "Жовта", 20), new("green", "Зелена", 25),
        ]),
        new("fence", "Тин", "Що навколо двору", [
            new("wattle", "Тин", 0), new("planks", "Паркан", 20), new("hedge", "Живопліт", 35),
        ]),
        new("tree", "Дерево коло хати", "Що росте під вікном", [
            new("kalyna", "Калина", 0), new("cherry", "Вишня", 20), new("oak", "Дуб", 30), new("willow", "Верба", 30),
        ]),
        new("wheel", "Колір кола", "З якого дерева зроблене коло", [
            new("oakwood", "Дубове", 0), new("cherrywood", "Вишневе", 25), new("black", "Чорне", 35), new("painted", "Мальоване", 60),
        ]),
        new("pet", "Кіт і собака", "Якої масті хатня живність", [
            new("grey", "Сірі", 0), new("ginger", "Руді", 15), new("black", "Чорні", 20), new("white", "Білі", 30), new("patched", "Рябі", 45),
        ]),
    ];

    /// <summary>Куплені варіанти оздоби, ключем «гурт:варіант». Безплатні тут не лежать — вони є в усіх.</summary>
    readonly HashSet<string> _looksOwned = new(StringComparer.Ordinal);
    /// <summary>Що саме обране в кожному гурті. Чого нема — перший варіант гурту.</summary>
    readonly Dictionary<string, string> _look = new(StringComparer.Ordinal);

    static string LookKey(string group, string value) => group + ":" + value;

    /// <summary>Обраний варіант гурту (або перший, безплатний).</summary>
    string LookOf(ClickerLook group) =>
        _look.TryGetValue(group.Key, out var v) && group.Options.Any(o => o.Value == v) ? v : group.Options[0].Value;

    bool LookOwned(ClickerLook group, ClickerLookOption option) =>
        option.Price <= 0 || _looksOwned.Contains(LookKey(group.Key, option.Value));

    /// <summary>
    /// Оздоба: <c>look { key, value }</c>. Куплений варіант просто вдягається, новий — за клейма. Ядро віддає
    /// сюди дію «look» перед своєю (та без «key» лише просить каталоги), тож без ключа ми мовчимо.
    /// </summary>
    ActResult? LookHouse(JsonElement payload)
    {
        var key = Str(payload, "key");
        if (key.Length == 0) return null;
        if (Looks.FirstOrDefault(l => l.Key == key) is not { } group) return ActResult.Fail("Такої оздоби в хаті нема");
        var value = Str(payload, "value");
        if (group.Options.FirstOrDefault(o => o.Value == value) is not { } option) return ActResult.Fail($"{group.Name}: такого не буває");
        if (LookOf(group) == option.Value) return ActResult.Fail($"{group.Name} уже така: {option.Name.ToLowerInvariant()}");
        if (!LookOwned(group, option))
        {
            if (SpendStamps(option.Price) is { } no) return no;
            _looksOwned.Add(LookKey(group.Key, option.Value));
            _look[group.Key] = option.Value;
            Achieve("potter-look");
            return ActResult.Accept($"🎨 {group.Name}: {option.Name.ToLowerInvariant()} — за {option.Price} {Stamps(option.Price)}");
        }
        _look[group.Key] = option.Value;
        return ActResult.Accept($"🎨 {group.Name}: {option.Name.ToLowerInvariant()}");
    }

    // ---------- ім'я хати ----------

    /// <summary>Довше однаково не влізе на вивіску, та й вивіска не для повісті.</summary>
    public const int HouseNameMax = 24;
    const string HouseNameDefault = "Хата гончаря";

    string _houseName = "";

    /// <summary>Що написано на вивісці: своє ім'я або типове.</summary>
    string HouseName => _houseName.Length > 0 ? _houseName : HouseNameDefault;

    /// <summary>
    /// Ім'я хати: <c>name { text }</c>, безплатно й скільки завгодно разів. Розмітки, керівних знаків і
    /// подвійних пробілів у ньому не буває — вивіска мальована, а не html.
    /// </summary>
    public static string CleanName(string raw)
    {
        var chars = new List<char>(HouseNameMax);
        var space = true;                                       // з пробілу не починаємо
        foreach (var ch in raw ?? "")
        {
            if (chars.Count >= HouseNameMax) break;
            if (char.IsControl(ch) || ch is '<' or '>' or '&' or '"' or '\'' or '\\') continue;
            if (char.IsWhiteSpace(ch))
            {
                if (space) continue;
                space = true;
                chars.Add(' ');
                continue;
            }
            space = false;
            chars.Add(ch);
        }
        return new string([.. chars]).Trim();
    }

    ActResult SetName(JsonElement payload)
    {
        var name = CleanName(Str(payload, "text"));
        if (name == _houseName) return ActResult.Fail("Вивіска вже така");
        _houseName = name;
        return ActResult.Accept(name.Length > 0 ? $"🏠 На вивісці тепер «{name}»" : "🏠 На вивісці знову «Хата гончаря»");
    }

    // ---------- дивовижі ----------

    /// <summary>Шанс знайти дивовижу, коли сталось щось рідкісне. З люстром — удвічі більший.</summary>
    public const double WonderChance = 0.08;
    /// <summary>Скільки до всього додає кожна знайдена дивовижа.</summary>
    public const double WonderBonus = 0.01;

    /// <summary>
    /// Шістнадцять речей, що самі знаходяться в хаті. Кожна — з байкою, кожна має свої події, з яких приходить
    /// (контракт v9 §F.4). Усе вигадане: це хатні побрехеньки, а не музейний опис.
    /// </summary>
    public static readonly ClickerWonder[] Wonders =
    [
        new("singer", "Глек, що співає",
            "Тріснув на обпалі, а як подме вітер у двір — гуде, ніби хтось усередині вчиться на сопілці. Викинути рука не піднялась.",
            ["kiln-perfect", "fire"]),
        new("pawprint", "Котячий слід на кахлі",
            "Кахля сохла на сонці, кіт ішов у своїх справах. Тепер це найдорожча кахля в печі: другої такої не вийде.",
            ["cat", "stove-full"]),
        new("horseshoe", "Підкова в глині",
            "Копнув глини — а там підкова, та ще й на сім цвяхів. Чия була, ніхто не згадав, але над дверима висить і досі.",
            ["fire", "lord-order"]),
        new("pipe", "Свищик на дві дірки",
            "Ліпив пташку онукові, а вона засвистіла на два голоси: один веселий, другий ніби трохи ображений.",
            ["paint-90", "holiday-guest"]),
        new("salt", "Чумацька сіль у горщику",
            "Чумак розплатився не грішми, а жменею солі. Горщик стоїть на полиці, і сіль у ньому не кінчається — бо її ніхто не чіпає.",
            ["wagon-gold", "treat"]),
        new("sky-stone", "Скалка з неба",
            "Упала зірка, а вранці в бур'яні знайшовся камінець — теплий і важчий, ніж має бути. Лежить у скриньці, гріє долоню.",
            ["star"]),
        new("mitten", "Рукавиця без пари",
            "Знайшлась у соломі. Ліва. Друга не знайшлась ніколи, але ця чомусь завжди тепла.",
            ["streak", "cat"]),
        new("glass", "Дзеркальце з тріщиною",
            "Тріщина лягла так, що в ній видно не тебе, а твою ж хату збоку. Дивитись весело, пояснити неможливо.",
            ["eye", "lucky"]),
        new("thread", "Червона нитка з рушника",
            "Вибилась із вишивки й сама обкрутилась круг пальця. Відтоді якось рівніше ліпиться.",
            ["album-row-stars", "mastery-10"]),
        new("coin", "Монета, якої нема",
            "Купець розплатився, а такої монети в окрузі ніхто не бачив: з одного боку віл, з другого — глечик. Ходити нею соромно, тримати приємно.",
            ["lord-order", "treat", "wagon-gold"]),
        new("amber", "Бджола в бурштині",
            "Дід казав: то не бурштин, то мед, який забув розтанути. Бджола всередині виглядає цілком згодною.",
            ["rep-10", "holiday-guest"]),
        new("ash", "Хрестик із попелу",
            "Виклався сам собою на черені після доброго обпалу. Змітати не стали — домели навколо.",
            ["fire", "kiln-perfect", "stove-full"]),
        new("moon", "Глечик із місяцем на дні",
            "Налий води й винеси в двір — місяць буде на дні, навіть коли на небі хмари. Узимку особливо.",
            ["star", "paint-90"]),
        new("cuckoo", "Зозуля, що недорахувала",
            "Сіла на тин і накувала рівно три. Три чого — не сказала, але відтоді все чомусь виходить по три.",
            ["streak", "lucky"]),
        new("thumb", "Дідів палець на денці",
            "На старому глекові з горища — відбиток пальця, більшого за твій. Приклав свого — стало тепло у вухах.",
            ["mastery-10", "album-row-stars", "eye"]),
        new("ribbon", "Стрічка з ярмарку",
            "Хтось прив'язав до воза на щастя й не зізнався. Віз відтоді не грузне навіть у найглибшій багнюці.",
            ["wagon-gold", "treat", "holiday-guest"]),
    ];

    /// <summary>Звідки дивовижа береться — словами, без чисел: «шанси» гравцеві знати нецікаво, а привід — дуже.</summary>
    static readonly Dictionary<string, string> WonderFrom = new(StringComparer.Ordinal)
    {
        ["eye"] = "коли майстер кивне на полицю",
        ["streak"] = "за довгу серію глеків з полиці",
        ["lucky"] = "за щасливий клік",
        ["cat"] = "коли погладиш кота",
        ["star"] = "коли впіймаєш зірку",
        ["fire"] = "на обпалі",
        ["kiln-perfect"] = "за бездоганну партію з горна",
        ["paint-90"] = "за дуже гарний розпис",
        ["album-row-stars"] = "за рядок зірок в альбомі",
        ["stove-full"] = "коли піч повна кахлів",
        ["mastery-10"] = "за золоті руки",
        ["lord-order"] = "за замовлення пана",
        ["holiday-guest"] = "за гостя на свято",
        ["rep-10"] = "за повну шану села",
        ["wagon-gold"] = "за щедрий віз від цеху",
        ["treat"] = "коли пошлеш гостинець другові",
    };

    /// <summary>Знайдені дивовижі й коли саме: час потрібен клієнтові, щоб показати картку з байкою один раз.</summary>
    readonly Dictionary<string, DateTimeOffset> _wonders = new(StringComparer.Ordinal);

    /// <summary>Гачок для решти пакетів: сталось щось рідкісне — може, щось і знайдеться.</summary>
    partial void Wonder(string trigger) => RollWonder(trigger);

    /// <summary>
    /// Кидок дивовижі: 8 % (з люстром 16 %) і лише серед тих, що ще не знайдені й приходять саме з цієї події.
    /// Повертає знайдене або null. Публічний, щоб тести кликали тригери напряму — у грі його кличуть через
    /// <c>Wonder(trigger)</c> з того місця, де подія сталась.
    /// </summary>
    public ClickerWonder? RollWonder(string trigger)
    {
        if (string.IsNullOrEmpty(trigger)) return null;
        var pool = Wonders.Where(w => !_wonders.ContainsKey(w.Key) && w.Triggers.Contains(trigger, StringComparer.Ordinal)).ToList();
        if (pool.Count == 0) return null;
        if (Ctx.Rng.NextDouble() >= WonderChance * (Tool("mirror") ? MirrorWonder : 1)) return null;
        var found = pool[Ctx.Rng.Next(pool.Count)];
        _wonders[found.Key] = Ctx.Clock.UtcNow;
        Achieve("potter-wonder");
        if (_wonders.Count >= Wonders.Length) Achieve("potter-wonders");
        _viewVersion++;
        return found;
    }

    // ---------- купці ----------

    /// <summary>Як часто дошка оновлюється: хто не взяв — той купець поїхав.</summary>
    public static readonly TimeSpan BoardEvery = TimeSpan.FromMinutes(4);
    public const int BoardSize = 3, MaxTaken = 3;
    /// <summary>Великий обоз (секрет другого кола): і дошка ширша, і в дорозі більше.</summary>
    public const int CaravanBoard = 4, CaravanTaken = 5;

    /// <summary>Скільки замовлень на дошці саме зараз.</summary>
    int BoardSizeNow => Has("caravan") ? CaravanBoard : BoardSize;
    /// <summary>Скільки купців може бути в дорозі водночас.</summary>
    int MaxTakenNow => Has("caravan") ? CaravanTaken : MaxTaken;
    /// <summary>На скільки купець їде. Довше — щедріше: 1,2 + хвилини/30 (5 хв → ×1,37, 30 хв → ×2,2).</summary>
    public static readonly int[] OrderMinutes = [5, 10, 20, 30];
    public const double InvestBase = 1.2, InvestPerHalfHour = 1.0;
    /// <summary>Замовлення на розпис: три хвилини пасиву в цьому розписі, платить ×1,6 одразу.</summary>
    public const double StyleOrderSeconds = 180, StyleOrderPay = 1.6;
    /// <summary>Скільки пасиву купець просить у дорогу: від двох до десяти хвилин.</summary>
    public const double InvestMinSeconds = 120, InvestMaxSeconds = 600;

    static readonly string[] Merchants =
    [
        "Пані з Полтави", "Козак Мамай", "Чумак Іван", "Пан з Умані", "Циганка з ярмарку", "Дід Панас з Опішні",
        "Шинкарка Гапка", "Купець із Царграда", "Отаман Сірко", "Кума з Сорочинців", "Мандрівний дяк", "Коваль Вакула",
    ];

    readonly List<ClickerOrder> _board = [];
    readonly List<ClickerTaken> _taken = [];
    /// <summary>Останні повернення купців — не зберігаються, клієнт лише показує їх «+N».</summary>
    readonly List<ClickerPaid> _paid = [];
    DateTimeOffset _boardUntil;
    int _orderId;

    /// <summary>Нова дошка: два купці в дорогу і, якщо є розписи, один за розпис.</summary>
    void RefreshBoard(DateTimeOffset now)
    {
        _board.Clear();
        _boardUntil = now + BoardEvery;
        var passive = PassiveBase;
        var styles = _styles.Order(StringComparer.Ordinal).ToList();
        var size = BoardSizeNow;
        for (var i = 0; i < size; i++)
        {
            var merchant = Merchants[Ctx.Rng.Next(Merchants.Length)];
            if (i == size - 1 && styles.Count > 0)
            {
                var style = styles[Ctx.Rng.Next(styles.Count)];
                var need = Nice(Math.Max(200, passive * StyleOrderSeconds));
                _board.Add(new(++_orderId, "style", merchant, need, ToPots(need * StyleOrderPay), 0, style, _boardUntil));
                continue;
            }
            var minutes = OrderMinutes[Ctx.Rng.Next(OrderMinutes.Length)];
            var seconds = InvestMinSeconds + Ctx.Rng.NextDouble() * (InvestMaxSeconds - InvestMinSeconds);
            var ask = Nice(Math.Max(100 + Ctx.Rng.Next(0, 300), passive * seconds));
            _board.Add(new(++_orderId, "invest", merchant, ask, ToPots(ask * (InvestBase + minutes / 30.0 * InvestPerHalfHour)), minutes, "", _boardUntil));
        }
    }

    /// <summary>Округлити до двох значущих цифр: «12 000», а не «11 873».</summary>
    public static double Nice(double v)
    {
        if (!(v > 0)) return 0;
        if (v < 100) return Math.Ceiling(v);
        var mag = Math.Pow(10, Math.Floor(Math.Log10(v)) - 1);
        return ToPots(Math.Round(v / mag) * mag);
    }

    /// <summary>Дошка й купці в дорозі: поїхали — нова дошка; повернулись — глеки на купу.</summary>
    void SyncOrders(DateTimeOffset now)
    {
        if (_boardUntil == default || now >= _boardUntil) RefreshBoard(now);
        for (var i = _taken.Count - 1; i >= 0; i--)
        {
            var t = _taken[i];
            if (t.PayAt > now) continue;
            Add(t.Pay);
            _taken.RemoveAt(i);
            _paid.Add(new(t.Id, t.Merchant, t.Pay, t.PayAt));
        }
        if (_paid.Count > 5) _paid.RemoveRange(0, _paid.Count - 5);
    }

    /// <summary>Взяти замовлення з дошки.</summary>
    ActResult Take(JsonElement payload)
    {
        var id = Num(payload, "id") ?? -1;
        if (_board.FirstOrDefault(o => o.Id == id) is not { } order) return ActResult.Fail("Цей купець уже поїхав");
        var now = Ctx.Clock.UtcNow;
        var styleName = Styles.FirstOrDefault(s => s.Key == order.Style)?.Name ?? "";
        if (order.Kind == "style" && !_styles.Contains(order.Style))
            return ActResult.Fail($"Купець хоче «{styleName}», а цього розпису в колекції ще нема");
        if (order.Kind == "invest" && _taken.Count >= MaxTakenNow)
            return ActResult.Fail($"Уже в дорозі {MaxTakenNow} {Plural(MaxTakenNow, "купець", "купці", "купців")} — почекай, поки хтось повернеться");
        if (_pots < order.Need) return ActResult.Fail($"Бракує глеків: треба ще {Short(order.Need - _pots)}");

        var pay = ToPots(order.Pay * MerchantMult);
        _pots -= order.Need;
        _board.Remove(order);
        if (order.Kind == "style")
        {
            Add(pay);
            return ActResult.Accept($"🧺 {order.Merchant} забрав {Short(order.Need)} глеків «{styleName}» і заплатив {Short(pay)}");
        }
        _taken.Add(new(order.Id, order.Merchant, pay, now + TimeSpan.FromMinutes(order.Minutes)));
        return ActResult.Accept($"🐴 {order.Merchant} поїхав із {Short(order.Need)} глеками, повернеться за {order.Minutes} хв із {Short(pay)}");
    }

    // ---------- життя хати ----------

    void ResetHouse(DateTimeOffset now)
    {
        _clays.Clear();
        _tools.Clear();
        _decor.Clear();
        _looksOwned.Clear();
        _look.Clear();
        _houseName = "";
        _wonders.Clear();
        _taken.Clear();
        _paid.Clear();
        _orderId = 0;
        FireHouse(now);
    }

    /// <summary>Що з хати згорає при обпалі: глина на колі, дошка, купці в дорозі. Речі й куплені глини лишаються.</summary>
    void FireHouse(DateTimeOffset now)
    {
        _clay = "";
        _clayRestUntil = default;
        _apronUsed = false;
        _taken.Clear();
        RefreshBoard(now);
    }

    object HouseView(DateTimeOffset now) => new
    {
        name = HouseName,
        named = _houseName,
        nameMax = HouseNameMax,
        clays = Clays.Select(c => new
        {
            key = c.Key, name = c.Name, desc = c.Desc, price = c.Price, body = c.Body,
            owned = c.Key.Length == 0 || _clays.Contains(c.Key), on = _clay == c.Key,
        }),
        clay = _clay,
        clayBody = ClayNow.Body,
        clayRestUntil = _clayRestUntil,
        tools = Tools.Select(t => new { key = t.Key, name = t.Name, desc = t.Desc, price = t.Price, owned = _tools.Contains(t.Key) }),
        decor = Decor.Select(d => new { key = d.Key, name = d.Name, desc = d.Desc, price = d.Price, bonus = d.Bonus, owned = _decor.Contains(d.Key) }),
        // Оздоба: що обране й що з варіантів уже куплене. Ціни — у клеймах (вільні клейма вид уже шле як stampsFree).
        look = Looks.ToDictionary(l => l.Key, LookOf, StringComparer.Ordinal),
        looks = Looks.Select(l => new
        {
            key = l.Key, name = l.Name, desc = l.Desc, value = LookOf(l),
            options = l.Options.Select(o => new { value = o.Value, name = o.Name, price = o.Price, owned = LookOwned(l, o) }),
        }),
        // Дивовижі: знайдені — з байкою, решта — самі силуети й підказка, звідки їх ждати.
        wonders = new
        {
            found = _wonders.Count,
            total = Wonders.Length,
            list = Wonders.Select(w => new
            {
                key = w.Key,
                found = _wonders.ContainsKey(w.Key),
                at = _wonders.TryGetValue(w.Key, out var at) ? at : (DateTimeOffset?)null,
                name = _wonders.ContainsKey(w.Key) ? w.Name : "",
                tale = _wonders.ContainsKey(w.Key) ? w.Tale : "",
                from = string.Join(" або ", w.Triggers.Select(t => WonderFrom.TryGetValue(t, out var f) ? f : t)),
            }),
            bonus = WonderBonus,
        },
        orders = new
        {
            board = _board.Select(o => new
            {
                id = o.Id, kind = o.Kind, merchant = o.Merchant, need = o.Need,
                pay = ToPots(o.Pay * MerchantMult),
                minutes = o.Minutes, style = o.Style,
                styleName = Styles.FirstOrDefault(s => s.Key == o.Style)?.Name ?? "",
                can = o.Kind != "style" || _styles.Contains(o.Style),
            }),
            taken = _taken.Select(t => new { id = t.Id, merchant = t.Merchant, pay = t.Pay, payAt = t.PayAt }),
            paid = _paid.Select(p => new { id = p.Id, merchant = p.Merchant, pay = p.Pay, at = p.At }),
            refreshAt = _boardUntil,
            maxTaken = MaxTakenNow,
        },
    };

    sealed record HouseRow(
        List<string>? Clays, string? Clay, DateTimeOffset ClayRestUntil,
        List<string>? Tools, bool ApronUsed, List<string>? Decor,
        List<ClickerOrder>? Board, List<ClickerTaken>? Taken, DateTimeOffset BoardUntil, int OrderId,
        // Дев'яте оновлення — усе необов'язкове: старе збереження читається як «цього ще не було».
        List<string>? Looks = null, Dictionary<string, string>? Look = null, string? Name = null,
        Dictionary<string, DateTimeOffset>? Wonders = null);

    HouseRow SaveHouse() => new(
        _clays.Order(StringComparer.Ordinal).ToList(), _clay, _clayRestUntil,
        _tools.Order(StringComparer.Ordinal).ToList(), _apronUsed, _decor.Order(StringComparer.Ordinal).ToList(),
        _board.ToList(), _taken.ToList(), _boardUntil, _orderId,
        _looksOwned.Count > 0 ? _looksOwned.Order(StringComparer.Ordinal).ToList() : null,
        _look.Count > 0 ? new Dictionary<string, string>(_look, StringComparer.Ordinal) : null,
        _houseName.Length > 0 ? _houseName : null,
        _wonders.Count > 0 ? new Dictionary<string, DateTimeOffset>(_wonders, StringComparer.Ordinal) : null);

    /// <summary>Старе збереження без хати — чиста хата зі свіжою дошкою.</summary>
    void LoadHouse(HouseRow? row)
    {
        var now = Ctx.Clock.UtcNow;
        Fill(_clays, row?.Clays, k => Clays.Any(c => c.Key == k && k.Length > 0));
        _clay = row?.Clay is { } c && (c.Length == 0 || _clays.Contains(c)) ? c : "";
        _clayRestUntil = row?.ClayRestUntil ?? default;
        Fill(_tools, row?.Tools, k => Tools.Any(t => t.Key == k));
        _apronUsed = row?.ApronUsed ?? false;
        Fill(_decor, row?.Decor, k => Decor.Any(d => d.Key == k));

        // Оздоба: куплені варіанти й обране. Вигаданого з бази не вдягаємо — на вивісці б висіло невідомо що.
        Fill(_looksOwned, row?.Looks, k => Looks.Any(l => l.Options.Any(o => o.Price > 0 && LookKey(l.Key, o.Value) == k)));
        _look.Clear();
        foreach (var (key, value) in row?.Look ?? [])
            if (Looks.FirstOrDefault(l => l.Key == key) is { } group
                && group.Options.FirstOrDefault(o => o.Value == value) is { } option && LookOwned(group, option))
                _look[key] = value;
        _houseName = CleanName(row?.Name ?? "");
        _wonders.Clear();
        foreach (var (key, at) in row?.Wonders ?? [])
            if (Wonders.Any(w => w.Key == key)) _wonders[key] = at;

        _board.Clear();
        _taken.Clear();
        _paid.Clear();
        foreach (var o in row?.Board ?? [])
            if (o is not null && o.Need > 0 && o.Pay > 0) _board.Add(o);
        foreach (var t in row?.Taken ?? [])
            if (t is not null && t.Pay > 0) _taken.Add(t);
        _boardUntil = row?.BoardUntil ?? default;
        _orderId = Math.Max(0, row?.OrderId ?? 0);
        // Порожня дошка з майбутнім строком — це «усіх купців узято», хай чекає свого часу; без строку — хати ще не було.
        if (_boardUntil == default) RefreshBoard(now);
    }

    /// <summary>Дії хати, яких ядро не знає в обличчя: поки що це лише вивіска.</summary>
    ActResult? ActHouse(string action, JsonElement payload) => action switch
    {
        "name" => SetName(payload),
        _ => null,
    };
}
