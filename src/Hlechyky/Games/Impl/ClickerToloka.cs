using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>Що просить етап будови: виріб, скільки, якість не нижче, розпис (порожній — будь-який).</summary>
public sealed record TolokaNeed(string Ware, int N, int Q, string Style = "");

/// <summary>Етап будови: назва, гроші (глеки), вироби з комори й години будови реального часу.</summary>
public sealed record TolokaStage(string Name, double Pay, double Hours, TolokaNeed[] Needs);

/// <summary>Будова Толоки: мала (з гривні за весь час) чи велика (з червоного золотого), етапи й вічна нагорода.</summary>
public sealed record ClickerBuilding(string Key, string Name, string Icon, bool Big, string Reward, TolokaStage[] Stages);

/// <summary>
/// Толока — одинадцяте оновлення (docs/games/specs/clicker-v11.md §2). Дванадцять будов Опішні, по черзі, кожна з
/// трьох-чотирьох етапів. Етап закладають глеками й виробами з комори, а далі він будується годинами реального часу
/// — і ніякий множник глеків цього не пришвидшить. Пришвидшують лише друзі з цеху, що піднесли вироби (−15 % кожен,
/// до трьох), і «Родова толока» (реліквія, до −30 %); разом не більше −60 %.
///
/// Навіщо так: активна година лідера важить сотні годин пасиву, тож будь-яка ціна в глеках для нього тане за вечір
/// (v10 пройшли за добу). Годинник і вироби з горна (60–100 за годину гри) — те, що тримає тижнями.
/// </summary>
public sealed partial class Clicker
{
    /// <summary>З якої суми за весь час відкривається Толока (мала) і велика толока.</summary>
    public const double TolokaSmallFrom = Hryvnia, TolokaBigFrom = Gold;
    /// <summary>Друг, що підніс на етап хоч один виріб, скорочує його на стільки; не більше трьох друзів.</summary>
    public const double TolokaHelperCut = 0.15;
    public const int TolokaHelpersMax = 3;
    /// <summary>Разом (друзі + реліквія) будова не коротша за 40 % свого часу.</summary>
    public const double TolokaCutMax = 0.60;
    /// <summary>Кожна готова будова — плюс стільки до всього.</summary>
    public const double TolokaAllBonus = 0.05;
    /// <summary>Фестиваль: година ×2 до всього раз на добу.</summary>
    public static readonly TimeSpan FestivalFor = TimeSpan.FromHours(1), FestivalEvery = TimeSpan.FromHours(24);
    public const double FestivalMult = 2;
    /// <summary>Скільки виробів друзям — на ачівку «Прийшов на толоку».</summary>
    public const int TolokaHelpForAchievement = 100;
    /// <summary>
    /// Своя праця (v12 §2): поки етап будується, ручні кліки по колу наповнюють смугу; повна смуга — етап коротший на
    /// стільки, поверх друзів і реліквії. Слабко навмисно (слово користувача 21.10: «пришвидшувати, але дуууже слабко»).
    /// </summary>
    public const double TolokaWorkCut = 0.15;
    /// <summary>
    /// Скільки ручних кліків на годину етапу (без скорочень) — повна смуга. Калібрування 21.10 (§17): етап на 20 год —
    /// 19 000 кліків, тобто повний ігровий день лідера (dayClicks 20.10: Smaug 19 151 за 1,5 год у колі, Назар 18 572 за 2 год)
    /// — 1,5–2 год гри. Було 1 500: 30 000 кліків — 2,3–3,3 год, і за реальних 1,2–1,9 год гри на добу смуга не наповнювалась.
    /// </summary>
    public const int TolokaWorkPerHour = 950;

    // Нагороди будов — числа тут, а де вони діють — у своїх пакетах (гачки нижче).
    public const double WellDryMult = 0.8, MillWindWait = 0.5, SquareMktPay = 1.25, SchoolApprentice = 0.3,
        MuseumValueMult = 1.5, InstituteShine = 0.2, HallGuestRep = 1.5;
    public const int ForgeStore = 100, ForgeKiln = 2, BigKilnSlots = 6, BigKilnMax = 12, SquareMktSlots = 1, PierGuestSlots = 1;

    static TolokaNeed N(string ware, int n, int q, string style = "") => new(ware, n, q, style);

    static TolokaStage S(string name, double pay, double hours, params TolokaNeed[] needs) => new(name, pay, hours, needs);

    /// <summary>
    /// Дванадцять будов. Гроші — рівними кроками в порядку етапів: мала толока від 1 ₴ до ~10⁹ ₴, велика від 1 золотого
    /// до ~10⁹ золотих. Години — 70 на малу й 544 на велику (без друзів). Вироби — ~750 на малу й ~5 000 на велику.
    /// </summary>
    public static readonly ClickerBuilding[] Buildings =
    [
        new("well", "Криниця з журавлем", "🪣", false, "Сирець сохне на 20 % швидше",
        [
            S("Копаємо яму", 1e15, 1, N("pot", 15, 1), N("bowl", 15, 1)),
            S("Дубовий зруб", 4.4e15, 2, N("jug", 20, 1), N("makitra", 10, 2)),
            S("Журавель із відром", 1.9e16, 3, N("barrel", 10, 2), N("jug", 20, 2)),
        ]),
        new("mill", "Вітряк", "🌬", false, "Вітер із поля приходить удвічі частіше",
        [
            S("Кам'яний підмурок", 8.5e16, 2, N("makitra", 20, 2), N("dish", 15, 1)),
            S("Крила з полотна", 3.7e17, 3, N("candle", 20, 2), N("whistle", 20, 1)),
            S("Жорна", 1.6e18, 4, N("barrel", 20, 2), N("tile", 15, 2)),
        ]),
        new("forge", "Кузня", "⚒", false, "Комора +100 місць, горно +2 місця",
        [
            S("Горн і міх", 7.2e18, 3, N("tile", 25, 2), N("kumanets", 10, 2)),
            S("Ковадло", 3.2e19, 4, N("barrel", 25, 2), N("ram", 15, 2)),
            S("Вивіска з підковою", 1.4e20, 6, N("lion", 10, 2), N("kumanets", 20, 2)),
        ]),
        new("square", "Ярмарковий майдан", "🎪", false, "На дошці сіл +1 замовлення, а села платять +25 %",
        [
            S("Бруківка", 6.1e20, 4, N("dish", 30, 2), N("candle", 20, 2)),
            S("Намети й ятки", 2.7e21, 6, N("whistle", 25, 2), N("ram", 20, 2), N("kukhol", 10, 2)),
            S("Гойдалка для дітей", 1.2e22, 8, N("tykva", 15, 2), N("kumanets", 20, 2)),
        ]),
        new("school", "Гончарна школа", "🏫", false, "Підмайстри ліплять ще +0,3 роботи за секунду",
        [
            S("Стіни з лампача", 5.2e22, 6, N("tile", 30, 2), N("barrel", 25, 2)),
            S("Класи з колами", 2.3e23, 8, N("pot", 30, 3), N("bowl", 30, 3)),
            S("Перший дзвоник", 1e24, 10, N("pleskanets", 15, 2), N("tykva", 20, 2), N("lion", 10, 3)),
        ]),
        // ---------- велика толока (з червоного золотого) ----------
        new("pier", "Пристань на Ворсклі", "⚓", true, "Гостинний двір +1 місце; відкриває Цзиндечжень",
        [
            S("Палі у Ворсклу", 1e27, 8, N("barrel", 60, 3), N("makitra", 50, 2)),
            S("Поміст", 2.2e27, 10, N("tile", 80, 2), N("jug", 50, 3)),
            S("Човни", 4.6e27, 12, N("kumanets", 60, 3), N("dish", 60, 2), N("barrel", 30, 3, "opishnia")),
            S("Ліхтар на кінці пристані", 1e28, 14, N("candle", 60, 3), N("lion", 30, 3), N("jug", 20, 4)),
        ]),
        new("bigkiln", "Горн-велетень", "🔥", true, "Горно +6 місць, стеля горна 36",
        [
            S("Фундамент", 2.2e28, 10, N("tile", 100, 3), N("pot", 50, 2)),
            S("Склепіння з цегли", 4.6e28, 12, N("tile", 80, 3), N("barrel", 60, 3)),
            S("Труба до неба", 1e29, 14, N("makitra", 80, 3), N("kumanets", 50, 3, "kosiv")),
            S("Перший вогонь", 2.2e29, 16, N("jug", 60, 3, "gavarets"), N("dish", 60, 3), N("lion", 20, 4)),
        ]),
        new("museum", "Музей гончарства", "🏛", true, "Вироби на 50 % дорожчі; відкриває Ізнік",
        [
            S("Стіни", 4.6e29, 12, N("tile", 100, 3), N("pleskanets", 40, 3)),
            S("Вітрини", 1e30, 14, N("lion", 50, 3), N("ram", 60, 3, "trypillia")),
            S("Зала гончарів", 2.2e30, 16, N("jug", 60, 3, "jingdezhen"), N("tykva", 50, 3), N("kukhol", 60, 3)),
            S("Відкриття для гостей", 4.6e30, 20, N("pleskanets", 40, 4), N("dish", 60, 3, "jingdezhen"), N("candle", 60, 3)),
        ]),
        new("institute", "Інститут керамології", "🔬", true, "Блиск партії +20 %; відкриває Делфт і Майсен",
        [
            S("Лабораторія", 1e31, 14, N("bowl", 100, 3), N("pot", 80, 3)),
            S("Бібліотека черепків", 2.2e31, 16, N("tile", 80, 3, "iznik"), N("makitra", 70, 3)),
            S("Печі для дослідів", 4.6e31, 20, N("kumanets", 80, 3), N("barrel", 60, 3, "mezhyhirya"), N("lion", 30, 4)),
            S("Вчена рада", 1e32, 24, N("pleskanets", 60, 3, "iznik"), N("tykva", 60, 3), N("ram", 40, 4)),
        ]),
        new("hall", "Виставкова зала", "🖼", true, "Шана заморських гостей ×1,5; відкриває Севр",
        [
            S("Світлі зали", 2.2e32, 16, N("dish", 100, 3), N("candle", 80, 3)),
            S("Постаменти", 4.6e32, 20, N("lion", 60, 3, "delft"), N("jug", 80, 3)),
            S("Каталог виставки", 1e33, 24, N("tykva", 80, 3, "meissen"), N("kukhol", 80, 3), N("whistle", 60, 3)),
            S("Вернісаж", 2.2e33, 28, N("pleskanets", 60, 4), N("dish", 60, 4, "delft"), N("lion", 40, 3, "meissen")),
        ]),
        new("festival", "Гончарний фестиваль", "🎉", true, "Раз на добу — година ×2 до всього; відкриває раку",
        [
            S("Сцена", 4.6e33, 20, N("barrel", 100, 3), N("tile", 100, 3)),
            S("Ряди майстрів", 1e34, 24, N("jug", 100, 3, "sevres"), N("bowl", 80, 3)),
            S("Музики з усієї округи", 2.2e34, 28, N("whistle", 100, 3), N("ram", 80, 3), N("kumanets", 60, 4)),
            S("Феєрверк", 4.6e34, 32, N("candle", 80, 4), N("pleskanets", 60, 3, "sevres"), N("tykva", 60, 4)),
        ]),
        new("bigjug", "Глек на майдані", "🏺", true, "Найбільший глек округи — і слава на весь світ",
        [
            S("Каркас", 1e35, 24, N("tile", 150, 3), N("makitra", 100, 3)),
            S("Глина на глину", 2.2e35, 28, N("jug", 120, 3), N("pot", 120, 3)),
            S("Розпис на весь бік", 4.6e35, 32, N("jug", 60, 4, "raku"), N("pleskanets", 80, 4), N("lion", 60, 4)),
            S("Глек на майдані", 1e36, 36, N("jug", 100, 4), N("tykva", 80, 4, "opishnia"), N("kukhol", 80, 4)),
        ]),
    ];

    /// <summary>Будова за ключем (null — такої нема).</summary>
    static ClickerBuilding? BuildingOf(string key) => Buildings.FirstOrDefault(b => b.Key == key);

    /// <summary>Назва будови для підписів ядра («спершу збудуй …»).</summary>
    static string TolokaName(string key) => BuildingOf(key)?.Name ?? key;

    // ---------- стан ----------

    readonly HashSet<string> _tBuilt = new(StringComparer.Ordinal);
    /// <summary>Номер етапу поточної будови (поточна — перша незбудована).</summary>
    int _tStage;
    /// <summary>Коли етап заклали (default — ще збирають).</summary>
    DateTimeOffset _tLaidAt;
    /// <summary>Хто з друзів підносив вироби на цей етап (ніки) — кожен скорочує будову.</summary>
    readonly List<string> _tHelpers = [];
    /// <summary>Піднесене друзями на цей етап: номер вимоги → скільки.</summary>
    readonly Dictionary<int, int> _tGot = [];
    /// <summary>Скільки виробів гончар сам підніс друзям (ачівка «Прийшов на толоку»).</summary>
    int _tHelped;
    DateTimeOffset _festUntil, _festNext;
    /// <summary>Скільки етапів уже готово за весь час — клієнт помічає новий за приростом (стрічка, сцена).</summary>
    int _tDone;
    /// <summary>
    /// Склад толоки (v12 §2): вироби, віднесені наперед на будь-який незакладений етап. Ключ — «будова|етап|рядок»,
    /// значення — які саме вироби (ключ комори → скільки): «забрати назад» вертає те саме, що віднесли.
    /// </summary>
    readonly Dictionary<string, Dictionary<string, int>> _tDepot = new(StringComparer.Ordinal);
    /// <summary>Ручні кліки на поточному закладеному етапі (своя праця).</summary>
    long _tWork;

    public sealed record TolokaRow(List<string>? Built = null, int Stage = 0, DateTimeOffset LaidAt = default,
        List<string>? Helpers = null, Dictionary<int, int>? Got = null, int Helped = 0,
        DateTimeOffset FestivalUntil = default, DateTimeOffset FestivalNext = default, int Done = 0,
        Dictionary<string, Dictionary<string, int>>? Depot = null, long Work = 0);

    // ---------- гачки для решти гри ----------

    bool TolokaHas(string key) => _tBuilt.Contains(key);

    double TolokaAllMult => (1 + TolokaAllBonus * _tBuilt.Count) * (Ctx.Clock.UtcNow < _festUntil ? FestivalMult : 1);
    double TolokaDryMult => TolokaHas("well") ? WellDryMult : 1;
    double TolokaWindWait => TolokaHas("mill") ? MillWindWait : 1;
    int TolokaStoreExtra => TolokaHas("forge") ? ForgeStore : 0;
    int TolokaKilnExtra => (TolokaHas("forge") ? ForgeKiln : 0) + (TolokaHas("bigkiln") ? BigKilnSlots : 0);
    int TolokaKilnMaxExtra => (TolokaHas("forge") ? ForgeKiln : 0) + (TolokaHas("bigkiln") ? BigKilnMax : 0);
    int TolokaMktSlots => TolokaHas("square") ? SquareMktSlots : 0;
    const int TolokaMktSlotsMax = SquareMktSlots;
    double TolokaMktPay => TolokaHas("square") ? SquareMktPay : 1;
    double TolokaApprenticeExtra => TolokaHas("school") ? SchoolApprentice : 0;
    int TolokaGuestSlots => TolokaHas("pier") ? PierGuestSlots : 0;
    const int TolokaGuestSlotsMax = PierGuestSlots;
    double TolokaValueMult => TolokaHas("museum") ? MuseumValueMult : 1;
    double TolokaShineBonus => TolokaHas("institute") ? InstituteShine : 0;
    double TolokaGuestRepMult => TolokaHas("hall") ? HallGuestRep : 1;

    // ---------- правила ----------

    bool TolokaOpen => _total >= TolokaSmallFrom || _tBuilt.Count > 0;

    /// <summary>Поточна будова: перша незбудована, якщо до неї вже доріс (велика — з червоного золотого). null — нема.</summary>
    ClickerBuilding? TolokaCurrent()
    {
        if (!TolokaOpen) return null;
        foreach (var b in Buildings)
        {
            if (_tBuilt.Contains(b.Key)) continue;
            return b.Big && _total < TolokaBigFrom && !_tBuilt.Any(k => BuildingOf(k)?.Big == true) ? null : b;
        }
        return null;
    }

    /// <summary>Наскільки коротша будова зараз: друзі й реліквія, не більше <see cref="TolokaCutMax"/>.</summary>
    double TolokaCut => Math.Min(TolokaCutMax, TolokaHelperCut * Math.Min(TolokaHelpersMax, _tHelpers.Count) + Relic("toloka"));

    /// <summary>Скільки ручних кліків — повна смуга своєї праці на етапі.</summary>
    static long TolokaWorkNeed(TolokaStage st) => (long)Math.Ceiling(TolokaWorkPerHour * st.Hours);

    /// <summary>Скільки скорочує своя праця просто зараз (0 … <see cref="TolokaWorkCut"/>).</summary>
    double TolokaWorkCutNow(TolokaStage st) => TolokaWorkCut * Math.Min(1, (double)_tWork / Math.Max(1, TolokaWorkNeed(st)));

    /// <summary>Коли закладений етап буде готовий: друзі й реліквія (до −60 %), а поверх — своя праця (до −15 %).</summary>
    DateTimeOffset TolokaEndsAt(TolokaStage st) =>
        _tLaidAt + TimeSpan.FromHours(st.Hours * (1 - TolokaCut) * (1 - TolokaWorkCutNow(st)));

    static Func<ItemInfo, bool> TolokaMatch(TolokaNeed n) =>
        it => it.Ware == n.Ware && it.Quality >= n.Q && (n.Style.Length == 0 || it.Style == n.Style);

    static string DepotKey(string building, int stage, int need) => $"{building}|{stage}|{need}";

    int DepotCount(string key) => _tDepot.TryGetValue(key, out var bag) ? bag.Values.Sum() : 0;

    /// <summary>Скільки ще бракує за вимогою <paramref name="i"/> поточного етапу після того, що піднесли друзі й що на складі.</summary>
    int TolokaLeft(TolokaStage st, int i)
    {
        var depot = TolokaCurrent() is { } b ? DepotCount(DepotKey(b.Key, Math.Clamp(_tStage, 0, b.Stages.Length - 1), i)) : 0;
        return Math.Max(0, st.Needs[i].N - (_tGot.TryGetValue(i, out var g) ? g : 0) - depot);
    }

    void ResetToloka(DateTimeOffset now)
    {
        _tBuilt.Clear();
        _tStage = 0;
        _tLaidAt = default;
        _tHelpers.Clear();
        _tGot.Clear();
        _tHelped = 0;
        _festUntil = default;
        _festNext = default;
        _tDone = 0;
        _tDepot.Clear();
        _tWork = 0;
    }

    /// <summary>Годинник будови: етап готовий, коли минув його час (і офлайн теж).</summary>
    void SyncToloka(DateTimeOffset now)
    {
        if (_tLaidAt == default || TolokaCurrent() is not { } b) return;
        var st = b.Stages[Math.Clamp(_tStage, 0, b.Stages.Length - 1)];
        if (now < TolokaEndsAt(st)) return;
        _tLaidAt = default;
        _tHelpers.Clear();
        _tGot.Clear();
        _tWork = 0;
        _tDone++;
        _tStage++;
        Wonder("toloka");
        if (_tStage < b.Stages.Length)
        {
            AwayNote($"{b.Icon} Толока: «{st.Name}» готово — можна закладати «{b.Stages[_tStage].Name}»");
            return;
        }
        _tBuilt.Add(b.Key);
        _tStage = 0;
        AwayNote($"{b.Icon} Толока: «{b.Name}» збудовано! {b.Reward}");
        _viewVersion++;
        if (_tBuilt.Count == 1) Achieve("potter-toloka-first");
        if (Buildings.Where(x => !x.Big).All(x => _tBuilt.Contains(x.Key))) Achieve("potter-toloka-small");
        if (Buildings.All(x => _tBuilt.Contains(x.Key))) Achieve("potter-toloka-all");
    }

    ActResult? ActToloka(string action, JsonElement payload)
    {
        if (action != "toloka") return null;
        var now = Ctx.Clock.UtcNow;
        return Str(payload, "do") switch
        {
            "lay" => TolokaLay(now),
            "festival" => TolokaFestival(now),
            "stock" => TolokaStock(Str(payload, "b"), (int)(Num(payload, "s") ?? -1), (int)(Num(payload, "i") ?? -1),
                (int)Math.Clamp(Num(payload, "n") ?? 1, 1, 10_000), now),
            "stockAll" => TolokaStockAll(now),
            "unstock" => TolokaUnstock(Str(payload, "b"), (int)(Num(payload, "s") ?? -1), (int)(Num(payload, "i") ?? -1)),
            _ => ActResult.Fail("Такого на толоці не роблять"),
        };
    }

    // ---------- склад толоки (v12 §2) ----------

    /// <summary>Чи можна вже носити на будову: мала — з відкриття Толоки, велика — з червоного золотого (чи вже почата).</summary>
    bool TolokaReachable(ClickerBuilding b) =>
        TolokaOpen && (!b.Big || _total >= TolokaBigFrom || _tBuilt.Any(k => BuildingOf(k)?.Big == true));

    /// <summary>
    /// Етапи, на які ще можна носити: поточна будова від поточного етапу (закладений — уже без нього) і всі наступні
    /// незбудовані, до яких гончар доріс. Порядок — порядок будов і етапів.
    /// </summary>
    IEnumerable<(ClickerBuilding B, int S)> TolokaAhead()
    {
        var cur = TolokaCurrent();
        foreach (var b in Buildings)
        {
            if (_tBuilt.Contains(b.Key) || !TolokaReachable(b)) continue;
            var from = 0;
            if (cur is not null && b.Key == cur.Key) from = Math.Clamp(_tStage, 0, b.Stages.Length - 1) + (_tLaidAt != default ? 1 : 0);
            for (var s = from; s < b.Stages.Length; s++) yield return (b, s);
        }
    }

    /// <summary>Скільки ще можна віднести в рядок: просить мінус піднесене друзями (лише поточний етап) мінус уже на складі.</summary>
    int DepotRoom(ClickerBuilding b, int s, int i)
    {
        var got = TolokaCurrent() is { } cur && cur.Key == b.Key && s == Math.Clamp(_tStage, 0, b.Stages.Length - 1)
            ? _tGot.GetValueOrDefault(i) : 0;
        return Math.Max(0, b.Stages[s].Needs[i].N - got - DepotCount(DepotKey(b.Key, s, i)));
    }

    /// <summary>Віднести з комори в рядок складу; повертає, скільки віднесли (лише вільне — те, чого не чекають замовлення).</summary>
    int DepotPut(ClickerBuilding b, int s, int i, int want)
    {
        var take = Math.Min(want, DepotRoom(b, s, i));
        if (take <= 0) return 0;
        var plan = SparePlan(TolokaMatch(b.Stages[s].Needs[i]), take, new TakeFor("depot"));
        var key = DepotKey(b.Key, s, i);
        var moved = 0;
        foreach (var (item, n) in plan)
        {
            var ik = ItemKey(item.Ware, item.Style, item.Quality);
            if (_items[ik] == n) _items.Remove(ik);
            else _items[ik] -= n;
            if (!_tDepot.TryGetValue(key, out var bag)) _tDepot[key] = bag = new(StringComparer.Ordinal);
            bag[ik] = bag.GetValueOrDefault(ik) + n;
            moved += n;
        }
        return moved;
    }

    ActResult TolokaStock(string building, int s, int i, int n, DateTimeOffset now)
    {
        if (BuildingOf(building) is not { } b || s < 0 || s >= b.Stages.Length || i < 0 || i >= b.Stages[s].Needs.Length)
            return ActResult.Fail("Такого рядка на толоці нема");
        if (!TolokaAhead().Any(x => x.B.Key == b.Key && x.S == s))
            return ActResult.Fail("На цей етап уже не носять");
        if (DepotRoom(b, s, i) <= 0) return ActResult.Fail("Цей рядок уже повний");
        var moved = DepotPut(b, s, i, n);
        if (moved == 0)
            return ActResult.Fail(ItemCount(TolokaMatch(b.Stages[s].Needs[i])) > 0
                ? "Вільних нема: усе, що підходить, відкладене під замовлення"
                : $"У коморі нема: {NeedWord(b.Stages[s].Needs[i])}");
        return ActResult.Accept($"📦 На склад толоки — «{b.Stages[s].Name}»: {NeedWord(b.Stages[s].Needs[i])} ×{moved}");
    }

    /// <summary>
    /// «Віднести все, що підходить»: спершу найсуворіші рядки (розкішні з розписом, розкішні, з розписом, решта) — тоді
    /// розкішний не піде туди, де вистачить дзвінкого; у межах суворості — за порядком етапів.
    /// </summary>
    ActResult TolokaStockAll(DateTimeOffset now)
    {
        var rows = TolokaAhead()
            .SelectMany((x, order) => x.B.Stages[x.S].Needs.Select((need, i) => (x.B, x.S, I: i, Need: need, Order: order)))
            .OrderByDescending(r => r.Need.Q).ThenByDescending(r => r.Need.Style.Length > 0).ThenBy(r => r.Order)
            .ToList();
        var moved = 0;
        var lux = 0;
        foreach (var r in rows)
        {
            var m = DepotPut(r.B, r.S, r.I, int.MaxValue);
            moved += m;
            if (r.Need.Q >= QualityMax) lux += m;
        }
        if (moved == 0) return ActResult.Fail("Нічого вільного в коморі не підходить толоці");
        return ActResult.Accept($"📦 На склад толоки: {moved} {WaresWord(moved)}" + (lux > 0 ? $" (з них розкішних {lux})" : ""));
    }

    /// <summary>Забрати рядок складу назад у комору — лише якщо там є місце (помилку можна виправити, а не продати).</summary>
    ActResult TolokaUnstock(string building, int s, int i)
    {
        var key = DepotKey(building, s, i);
        if (!_tDepot.TryGetValue(key, out var bag) || bag.Count == 0) return ActResult.Fail("На складі в цьому рядку нічого нема");
        var n = bag.Values.Sum();
        if (StoreCapNow - ItemTotal < n) return ActResult.Fail($"У коморі мало місця: треба {n}, вільно {Math.Max(0, StoreCapNow - ItemTotal)}");
        foreach (var (ik, c) in bag)
            if (ParseItem(ik) is { } it) PutItems(it.Ware, it.Style, it.Quality, c);
        _tDepot.Remove(key);
        // Що саме повернулось — назвою рядка, а не «27 виробів» (QA-3).
        var what = BuildingOf(building) is { } b && s >= 0 && s < b.Stages.Length && i >= 0 && i < b.Stages[s].Needs.Length
            ? $"{NeedWord(b.Stages[s].Needs[i])} ×{n}" : $"{n} {WaresWord(n)}";
        return ActResult.Accept($"📦 Зі складу толоки назад у комору: {what}");
    }

    /// <summary>Своя праця: ручні кліки, поки етап будується (лише поточний закладений; смуга — до повної).</summary>
    void TolokaWork(int clicks, DateTimeOffset now)
    {
        if (_tLaidAt == default || clicks <= 0 || TolokaCurrent() is not { } b) return;
        var st = b.Stages[Math.Clamp(_tStage, 0, b.Stages.Length - 1)];
        var need = TolokaWorkNeed(st);
        if (_tWork >= need) return;
        _tWork = Math.Min(need, _tWork + clicks);
        if (_tWork >= need) Achieve("potter-toloka-work");
    }

    /// <summary>Закласти етап: забрати глеки й вироби з комори — і пішов годинник.</summary>
    ActResult TolokaLay(DateTimeOffset now)
    {
        if (TolokaCurrent() is not { } b)
            return ActResult.Fail(TolokaOpen ? "Велика толока почнеться, коли наліпиш червоний золотий" : $"Толока відкриється на {PotsShort(TolokaSmallFrom)} за весь час");
        var st = b.Stages[Math.Clamp(_tStage, 0, b.Stages.Length - 1)];
        if (_tLaidAt != default) return ActResult.Fail($"«{st.Name}» уже будується");
        if (_pots < st.Pay) return ActResult.Fail($"Бракує глеків: треба ще {Short(st.Pay - _pots)}");
        var lack = new List<string>();
        for (var i = 0; i < st.Needs.Length; i++)
        {
            var left = TolokaLeft(st, i);
            var have = ItemCount(TolokaMatch(st.Needs[i]));
            if (have < left) lack.Add($"{NeedWord(st.Needs[i])} — ще {left - have}");
        }
        if (lack.Count > 0) return ActResult.Fail("Бракує виробів: " + string.Join("; ", lack));
        for (var i = 0; i < st.Needs.Length; i++) TakeItems(TolokaMatch(st.Needs[i]), TolokaLeft(st, i), new TakeFor("toloka"));
        // Склад цього етапу пішов на будову разом із коморою.
        for (var i = 0; i < st.Needs.Length; i++) _tDepot.Remove(DepotKey(b.Key, Math.Clamp(_tStage, 0, b.Stages.Length - 1), i));
        _pots -= st.Pay;
        _tLaidAt = now;
        _tWork = 0;
        var ends = TolokaEndsAt(st);
        return ActResult.Accept($"{b.Icon} Заклали «{st.Name}»: будується {Hours(ends - now)}");
    }

    ActResult TolokaFestival(DateTimeOffset now)
    {
        if (!TolokaHas("festival")) return ActResult.Fail("Фестиваль буде, коли збудуєш «Гончарний фестиваль»");
        if (now < _festUntil) return ActResult.Fail("Фестиваль уже гуляє");
        if (now < _festNext) return ActResult.Fail($"Наступний фестиваль — за {Hours(_festNext - now)}");
        _festUntil = now + FestivalFor;
        _festNext = now + FestivalEvery;
        Achieve("potter-festival");
        return ActResult.Accept($"🎉 Гуляй, майдане! Година ×{FestivalMult:0} до всього");
    }

    /// <summary>
    /// Друг підніс вироби на поточний етап (пошта цеху, пакет A). Бере не більше, ніж ще бракує за вимогою; решта
    /// лягає в комору (що не влізе — на базар, як завжди). Повертає, скільки пішло на будову.
    /// </summary>
    internal int TolokaReceive(string from, ItemInfo item, int n)
    {
        if (n <= 0 || TolokaCurrent() is not { } b) { PutItems(item.Ware, item.Style, item.Quality, n); return 0; }
        var st = b.Stages[Math.Clamp(_tStage, 0, b.Stages.Length - 1)];
        var used = 0;
        if (_tLaidAt == default)
            for (var i = 0; i < st.Needs.Length && used < n; i++)
            {
                if (!TolokaMatch(st.Needs[i])(item)) continue;
                var take = Math.Min(n - used, TolokaLeft(st, i));
                if (take <= 0) continue;
                _tGot[i] = (_tGot.TryGetValue(i, out var g) ? g : 0) + take;
                used += take;
            }
        if (used < n) PutItems(item.Ware, item.Style, item.Quality, n - used);
        // Друг рахується, навіть якщо все пішло в комору: прийшов на толоку — вже поміч (але будова скорочується один раз).
        if (from.Length > 0 && !_tHelpers.Contains(from, StringComparer.OrdinalIgnoreCase) && _tHelpers.Count < 8) _tHelpers.Add(from);
        return used;
    }

    /// <summary>Гончар підніс друзям стільки виробів (пакет A кличе після відправки).</summary>
    internal void TolokaHelped(int n)
    {
        if (n <= 0) return;
        var before = _tHelped;
        _tHelped = (int)Math.Min(int.MaxValue, (long)_tHelped + n);
        if (before < TolokaHelpForAchievement && _tHelped >= TolokaHelpForAchievement) Achieve("potter-toloka-helper");
    }

    static string NeedWord(TolokaNeed n)
    {
        var name = WareOf(n.Ware)?.Name.ToLowerInvariant() ?? n.Ware;
        // Рід — за виробом: «макітра, добра і краща», а не «макітра, добрий і кращий» (пакет A).
        var g = WareGender.GetValueOrDefault(n.Ware, 'm');
        var better = g == 'f' ? "краща" : g == 'n' ? "краще" : "кращий";
        var q = n.Q switch { 2 or 3 => $", {QualityWord(n.Ware, n.Q)} і {better}", 4 => $", {QualityWord(n.Ware, 4)}", _ => "" };
        var style = n.Style.Length > 0 ? $", «{Styles.FirstOrDefault(s => s.Key == n.Style)?.Name ?? n.Style}»" : "";
        return name + q + style;
    }

    static string Hours(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{Math.Floor(t.TotalHours):0} год {t.Minutes:0} хв" : $"{Math.Max(1, Math.Ceiling(t.TotalMinutes)):0} хв";

    // ---------- вид, каталог, збереження ----------

    object? ViewToloka(DateTimeOffset now)
    {
        if (!TolokaOpen) return null;
        var b = TolokaCurrent();
        object? stage = null;
        if (b is not null)
        {
            var st = b.Stages[Math.Clamp(_tStage, 0, b.Stages.Length - 1)];
            stage = new
            {
                building = b.Key,
                index = _tStage,
                pay = st.Pay,
                hours = st.Hours,
                laidAt = _tLaidAt == default ? (DateTimeOffset?)null : _tLaidAt,
                endsAt = _tLaidAt == default ? (DateTimeOffset?)null : TolokaEndsAt(st),
                cut = TolokaCut,
                needs = st.Needs.Select((n, i) => new
                {
                    ware = n.Ware, n = n.N, q = n.Q, style = n.Style,
                    got = _tGot.TryGetValue(i, out var g) ? g : 0,
                    // v12: скільки вже на складі толоки (рахується як піднесене).
                    depot = DepotCount(DepotKey(b.Key, Math.Clamp(_tStage, 0, b.Stages.Length - 1), i)),
                    have = ItemCount(TolokaMatch(n)),
                }).ToList(),
                helpers = _tHelpers.ToList(),
                // v12: своя праця на закладеному етапі — ручні кліки, з них смуга і скорочення поверх друзів і реліквії.
                work = _tLaidAt == default ? null : new { n = _tWork, of = TolokaWorkNeed(st), cut = TolokaWorkCutNow(st) },
            };
        }
        // v12 §2: план до кінця — підсумок за рядками по всіх етапах, на які ще можна носити, а поетапно — лише поточна й
        // наступна будова (вид топ-гончаря мусить влазити в 25 КБ, а 43 етапи поетапно — ще ~1,5 КБ щопачки).
        var ahead = TolokaAhead().ToList();
        var near = ahead.Select(x => x.B.Key).Distinct().Take(2).ToHashSet(StringComparer.Ordinal);
        // Далі за двома будовами — лише етапи, де вже щось лежить на складі: «Віднести все» носить і туди, і забрати
        // назад («↩») мусить бути звідки (QA-3: 43 глечики на «Глеку на майдані» без вороття).
        var plan = ahead.Where(x => near.Contains(x.B.Key) || x.B.Stages[x.S].Needs.Where((_, i) => DepotCount(DepotKey(x.B.Key, x.S, i)) > 0).Any()).Select(x => new
        {
            building = x.B.Key, index = x.S,
            needs = x.B.Stages[x.S].Needs.Select((n, i) => new { ware = n.Ware, n = n.N, q = n.Q, style = n.Style, depot = DepotCount(DepotKey(x.B.Key, x.S, i)) }).ToList(),
        }).ToList();
        var totals = ahead.SelectMany(x => x.B.Stages[x.S].Needs.Select((n, i) => (n, depot: DepotCount(DepotKey(x.B.Key, x.S, i)))))
            .GroupBy(x => (x.n.Ware, x.n.Q, x.n.Style))
            .Select(g => new
            {
                ware = g.Key.Ware, q = g.Key.Q, style = g.Key.Style, n = g.Sum(x => x.n.N), depot = g.Sum(x => x.depot),
                have = ItemCount(TolokaMatch(new TolokaNeed(g.Key.Ware, 0, g.Key.Q, g.Key.Style))),
            })
            .OrderByDescending(x => x.q).ThenBy(x => x.ware, StringComparer.Ordinal).ToList();
        return new
        {
            built = Buildings.Where(x => _tBuilt.Contains(x.Key)).Select(x => x.Key).ToList(),
            stage,
            // Поки етап будується — що треба на наступний: вироби (і скільки вже є в коморі), гроші, години. Лише показати.
            next = b is not null && _tLaidAt != default
                ? TolokaNextView(TolokaAfter(b, Math.Clamp(_tStage, 0, b.Stages.Length - 1), _tBuilt, _total), n => ItemCount(TolokaMatch(n)))
                : null,
            // Чекаємо на червоний золотий: мала збудована, велика ще не відкрилась.
            waitBig = b is null && Buildings.Where(x => !x.Big).All(x => _tBuilt.Contains(x.Key)) && _tBuilt.Count < Buildings.Length,
            done = _tDone,
            helped = _tHelped,
            festival = TolokaHas("festival") ? new { until = _festUntil, next = _festNext, mult = FestivalMult } : null,
            allMult = 1 + TolokaAllBonus * _tBuilt.Count,
            plan,
            totals,
            depot = _tDepot.Values.Sum(bag => bag.Values.Sum()),
        };
    }

    /// <summary>Незмінне про Толоку для каталогу (їде разом із рештою каталогів, не щопачки).</summary>
    object CatalogToloka() => new
    {
        smallFrom = TolokaSmallFrom,
        bigFrom = TolokaBigFrom,
        helperCut = TolokaHelperCut,
        helpersMax = TolokaHelpersMax,
        cutMax = TolokaCutMax,
        allBonus = TolokaAllBonus,
        workCut = TolokaWorkCut,
        workPerHour = TolokaWorkPerHour,
        buildings = Buildings.Select(b => new
        {
            key = b.Key, name = b.Name, icon = b.Icon, big = b.Big, reward = b.Reward,
            stages = b.Stages.Select(s => new { name = s.Name, pay = s.Pay, hours = s.Hours }),
        }),
    };

    TolokaRow? SaveToloka() => _tBuilt.Count == 0 && _tStage == 0 && _tLaidAt == default && _tGot.Count == 0 && _tHelped == 0
                                && _tHelpers.Count == 0 && _tDone == 0 && _tDepot.Count == 0
        ? null
        : new TolokaRow(_tBuilt.Order(StringComparer.Ordinal).ToList(), _tStage, _tLaidAt, _tHelpers.ToList(),
            _tGot.Count == 0 ? null : new Dictionary<int, int>(_tGot), _tHelped, _festUntil, _festNext, _tDone,
            _tDepot.Count == 0 ? null : _tDepot.ToDictionary(x => x.Key, x => new Dictionary<string, int>(x.Value, StringComparer.Ordinal), StringComparer.Ordinal),
            _tWork);

    void LoadToloka(TolokaRow? row)
    {
        ResetToloka(Ctx.Clock.UtcNow);
        if (row is null) return;
        foreach (var k in row.Built ?? [])
            if (BuildingOf(k) is not null) _tBuilt.Add(k);
        _tStage = Math.Max(0, row.Stage);
        _tLaidAt = row.LaidAt > Ctx.Clock.UtcNow ? Ctx.Clock.UtcNow : row.LaidAt;
        foreach (var h in row.Helpers ?? [])
            if (h is { Length: > 0 and <= 64 } && _tHelpers.Count < 8) _tHelpers.Add(h);
        foreach (var (i, n) in row.Got ?? [])
            if (i is >= 0 and < 8 && n > 0) _tGot[i] = Math.Min(n, 10_000);
        _tHelped = Math.Max(0, row.Helped);
        _festUntil = row.FestivalUntil;
        _festNext = row.FestivalNext;
        _tDone = Math.Max(0, row.Done);
        // Етап за межами будови (каталог змінився) — з початку цієї будови, без годинника.
        if (TolokaCurrent() is { } b && _tStage >= b.Stages.Length) { _tStage = 0; _tLaidAt = default; _tGot.Clear(); }
        _tWork = _tLaidAt == default ? 0 : Math.Max(0, row.Work);
        // Склад: лише рядки, що існують у каталозі, і вироби, які гра розуміє; збудоване — геть (вироби вже не вернеш
        // у переповнену комору, а будова однаково стоїть).
        foreach (var (key, bag) in row.Depot ?? [])
        {
            var parts = key?.Split('|');
            if (parts is not { Length: 3 } || BuildingOf(parts[0]) is not { } db || _tBuilt.Contains(db.Key)
                || !int.TryParse(parts[1], out var s) || s < 0 || s >= db.Stages.Length
                || !int.TryParse(parts[2], out var i) || i < 0 || i >= db.Stages[s].Needs.Length || bag is null)
                continue;
            var clean = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (ik, n) in bag)
                if (n > 0 && ParseItem(ik) is not null) clean[ik] = Math.Min(n, 10_000);
            if (clean.Count > 0) _tDepot[key!] = clean;
        }
    }
}
