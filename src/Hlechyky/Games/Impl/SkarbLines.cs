using System.Text.RegularExpressions;

namespace Hlechyky.Games.Impl;

/// <summary>Моменти «Скарбу Полуботка», на які Дядько Глек може озватися в балачці столу (контракт §14.1).</summary>
public enum SkMoment
{
    Start, RoundStart, LastRound, Break, Fix, FixSelf, Map, Rockfall, DeadEnd, Close, Stone, Gold, MolesWin,
    NoMoles, DeckOut, Win, Tie, Away, Back,
}

/// <summary>
/// Книга реплік Глека-ведучого «Скарбу Полуботка». Плейсхолдери: {who} {victim} {other} — ніки; {toolAcc} — «кирку /
/// лампу / тачку»; {round} {moles} {n} {gold} — числа; {names} — ніки через кому. Чого не передали — м'яка заглушка.
/// Посеред раунду Глек ролей не знає й на правду не натякає: лише жартує про підозру (рядки однакові для копача й крота).
/// </summary>
public static class SkarbLines
{
    /// <summary>Усі плейсхолдери, які вміє книга.</summary>
    public static readonly IReadOnlySet<string> Known =
        new HashSet<string> { "who", "victim", "other", "toolAcc", "round", "moles", "n", "gold", "names" };

    static readonly Dictionary<string, string> Fallback = new()
    {
        ["who"] = "хтось", ["victim"] = "сусід", ["other"] = "сусід", ["toolAcc"] = "інструмент", ["round"] = "новий",
        ["moles"] = "кілька", ["n"] = "кілька", ["gold"] = "купу", ["names"] = "ті, кого ми й не думали",
    };

    static readonly Dictionary<SkMoment, string[]> Book = new()
    {
        [SkMoment.Start] =
        [
            "Ну що, копачі, лопати в руки! Десь під селом гетьманське золото — а серед вас, кажуть, є такі, що риють не туди 🏺",
            "Полуботок, кажуть, закопав скриню під нашим селом. Три мітки, три раунди — і хтось із вас точно кріт",
            "Колодязь є, мітки є, лопати роздано. Копайте до скарбу й придивляйтесь, хто копає вбік",
            "Сідайте ближче до колодязя. Рити будемо чесно — ну, більшість із вас",
            "Гетьманське золото само себе не відкопає. Починаймо, а я посторожую журавель",
            "Під кожною міткою щось лежить: десь скриня, десь каміння. А під кожним брилем — хтозна-що",
        ],
        [SkMoment.RoundStart] =
        [
            "Раунд {round}. Кротів у колоді — до {moles}. Хто з вас — не скажу, бо й сам не знаю",
            "Раунд {round}: ролі роздано наново. Учорашній кріт сьогодні, може, найчесніший копач",
            "Починаємо раунд {round}. До {moles} кротів десь між вами — дивіться, хто куди гне хід",
            "Раунд {round}. Земля та сама, мітки ті самі, а от хто є хто — знову загадка",
            "Новий раунд, {round}-й. Карти в руках, ролі під сорочкою. Копаймо!",
        ],
        [SkMoment.LastRound] =
        [
            "Останній, третій раунд! Хто відстає за злитками — зараз або ніколи",
            "Раунд {round}, фінальний. Кротів у колоді — до {moles}. Хто сьогодні вкраде гетьманське золото?",
            "Третій раунд — і по скарбу. Копайте так, наче Полуботок дивиться",
            "Ну все, останній захід. Хто багатший, той і гетьманський скарбник",
            "Раунд {round}, останній. Хто досі мовчки рив убік — покажіть, на що здатні",
        ],
        [SkMoment.Break] =
        [
            "{who} зламав {toolAcc} {victim}. Може, випадково. А може, й ні…",
            "Ой, {victim} лишився без справного знаряддя — це {who} постарався. Ну-ну",
            "{who} псує {victim} {toolAcc}. Чи то від злості, чи то від знання — хтозна",
            "Хрусь! {who} зіпсував {toolAcc} {victim}. Хтось щось знає, чи просто нерви?",
            "{victim}, тримай {toolAcc} міцніше — {who} уже приглядається, що б іще зламати",
            "{who} зламав {toolAcc} {victim}. Я б на місці {victim} теж задумався",
        ],
        [SkMoment.Fix] =
        [
            "{who} полагодив {toolAcc} {victim}. Добра душа — чи хитра?",
            "О, {victim} знову при ділі: {who} приніс справну {toolAcc}",
            "{who} рятує {victim}. Друзі під землею — рідкість",
            "Ремонт за рахунок {who}! {victim}, не забудь подякувати — або запідозрити",
            "{who} лагодить {victim} {toolAcc}. Кажуть, кроти теж уміють бути чемними",
        ],
        [SkMoment.FixSelf] =
        [
            "{who} сам собі полагодив {toolAcc}. Своя рука — владика",
            "{who} знову при справній {toolAcc}. Ну, тепер копай, не відмовляйся",
            "{who} швиденько полагодив собі {toolAcc} — видно, чекав цього",
            "{who} не став чекати допомоги — сам лагодить {toolAcc}",
            "Відремонтувався {who}. Тепер подивимось, куди він рити піде",
        ],
        [SkMoment.Map] =
        [
            "{who} розгорнув стару мапу й щось там побачив. Нам, звісно, не скаже",
            "{who} глянув під мітку. Обличчя — кам'яне. То скриня там чи каміння?",
            "Стара мапа в руках {who}. Знання — сила, а мовчання — золото",
            "{who} щось вивідав. Тепер стежте, куди він хід поведе",
            "{who} подивився мапу й хмикнув. Що б це значило…",
        ],
        [SkMoment.Rockfall] =
        [
            "Бабах! {who} засипав хід. Земля, кажуть, сама осіла — ага",
            "{who} завалив тунель. Чи то прибрав глухий кут, чи то сховав дорогу до скарбу",
            "Гуп! І ходу як не було. {who}, ти точно знав, що робиш?",
            "{who} обвалив стелю. Пил осяде — побачимо, кому це на руку",
            "Завал від {who}. Шахтарі б сказали: «Обережно!» Кроти б сказали: «Дякую»",
        ],
        [SkMoment.DeadEnd] =
        [
            "Глухий кут. Ну хто ж так копає, {who}?",
            "{who} прокопав у нікуди. Каменюка посеред ходу — і далі ні кроку",
            "Ой, {who}, там же обвал посередині! Чи ти навмисне?",
            "{who} поклав глухий хід. Бува, рука здригнулась. А бува — й ні",
            "Ну от, тупик від {who}. Хто має завал — тепер саме час",
        ],
        [SkMoment.Close] =
        [
            "Ого, хід уже майже під міткою! Тримайте кирки міцніше",
            "Ще кілька лопат — і щось відкопаємо. Тут кожен хід на вагу золота",
            "Близько, зовсім близько. Саме зараз кроти мали б нервувати",
            "Мітка вже ось-ось. Хто тепер завалить хід — той себе й видасть",
            "До мітки рукою подати. Тиша під землею така, що чути, як хтось думає недобре",
        ],
        [SkMoment.Stone] =
        [
            "Відкопали — а там каміння. Полуботок, видно, жартував",
            "Камінь! {who} докопався, а скарбу нема. Копаймо далі",
            "Порожньо — лише каміння. Ну хоч знаємо, де шукати не треба",
            "{who} розкрив мітку — і бачить каміння. Гетьман сміється з того світу",
            "Каміння під міткою. Мапи брехали — чи хтось просто не сказав?",
        ],
        [SkMoment.Gold] =
        [
            "Золото! {who} докопався до скрині Полуботка — копачі, ділімо злитки!",
            "Скриня! Справжня гетьманська скриня! {who}, ти герой села",
            "Блищить! {who} відкопав скарб Полуботка — кротам цього разу зась",
            "Є! Скарб знайдено, і першим до нього дістався {who}. Ділимо злитки!",
            "Ох і скриня — кована, важка, повна злитків! Молодець, {who}",
        ],
        [SkMoment.MolesWin] =
        [
            "Колода скінчилась, а скарб — у землі. Кроти тут були: {names}. Ну й хитруни",
            "Скарб не знайдено! Бо копали з нами {names} — от хто водив усіх манівцями",
            "Кроти взяли своє: {names}. По {n} злитки з гетьманської скрині — за старання",
            "Ось хто рив не туди: {names}. А ви їм ще й кирки лагодили",
            "Кроти перемогли! {names}, вклоніться — граєте ви чудово, хоч і підступно",
        ],
        [SkMoment.NoMoles] =
        [
            "Кротів цього разу не було зовсім — а скарб однаково лишився в землі. Ото копачі!",
            "Ніхто не шкодив, а скриню не знайшли. Самі собі кроти, виходить",
            "Роль крота лежала набік — усі були свої. І все одно скарб у землі",
            "Без жодного крота — і без скарбу. Полуботок би похитав головою",
            "Чесна артіль, ні одного шкідника — а золото досі під селом. Буває",
        ],
        [SkMoment.DeckOut] =
        [
            "Колода скінчилась. Тепер — тим, що в руках",
            "Карт більше нема. Кожна лопата тепер на вагу золота",
            "Останню карту витягнуто. Хто тримав козирі — час їх показати",
            "Колода порожня. Скарб або зараз, або вже ніколи в цьому раунді",
            "Все, нових карт не буде. Рахуйте, що лишилось у руках",
        ],
        [SkMoment.Win] =
        [
            "Найбагатший за столом — {who}: {gold} злитків. Гетьман би похвалив",
            "{who} — гетьманський скарбник цього вечора! {gold} злитків у кишені",
            "Партію виграє {who}: {gold} злитків. Хоч копачем, хоч кротом — а золото любить",
            "Перемога за {who}! {gold} злитків — це вам не каміння під міткою",
            "{who} забирає перемогу з {gold} злитками. Лопату — на стіну, як трофей",
        ],
        [SkMoment.Tie] =
        [
            "Скарб поділили: {names} — по {gold} злитків. Мир і злагода під землею",
            "Нічия в багатстві: {names} мають по {gold}. Гетьман ділить по-братськи",
            "{names} — однаково багаті, по {gold} злитків. Хоч бери й копай ще раз",
            "Рівно по {gold} злитків у {names}. Скарб поділено по-чесному",
            "Двоє, а то й більше скарбників: {names}. По {gold} злитків кожному",
        ],
        [SkMoment.Away] =
        [
            "{who} задрімав над лопатою — я покопаю за нього, поки прокинеться",
            "{who} кудись подівся. Не біда: Глек підхопить лопату",
            "Тихо, {who} спить. Копаю замість нього, як умію",
            "{who} відійшов від колодязя. Лопата не мерзне — Глек на варті",
            "Беру лопату {who} — нехай відпочине, а хід не стоятиме",
        ],
        [SkMoment.Back] =
        [
            "{who} повернувся до своєї лопати. Тримай — я нічого не зламав. Ну, майже",
            "З поверненням, {who}! Твоя лопата чекала",
            "{who} знову з нами. Глек віддає кирку господареві",
            "О, {who} прокинувся! Копай сам, я свою роботу зробив",
            "{who} на місці. Далі вже без мене, я тільки підглядатиму",
        ],
    };

    public static IReadOnlyList<string> Lines(SkMoment m) => Book[m];

    static readonly Regex Slot = new(@"\{(\w+)\}", RegexOptions.Compiled);

    /// <summary>Плейсхолдери шаблону.</summary>
    public static IEnumerable<string> Slots(string template) => Slot.Matches(template).Select(x => x.Groups[1].Value);

    /// <summary>Підставити змінні в шаблон; невідомі й непередані — заглушкою.</summary>
    public static string Fill(string template, IReadOnlyDictionary<string, string>? vars) =>
        Slot.Replace(template, x =>
        {
            var key = x.Groups[1].Value;
            if (vars != null && vars.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v)) return v;
            return Fallback.TryGetValue(key, out var f) ? f : "";
        });

    /// <summary>Номер випадкової репліки моменту; <paramref name="avoid"/> — не брати цей (якщо є інші).</summary>
    public static int PickIndex(SkMoment m, Random rng, int avoid = -1)
    {
        var n = Book[m].Length;
        if (avoid < 0 || avoid >= n || n < 2) return rng.Next(n);
        var i = rng.Next(n - 1);
        return i >= avoid ? i + 1 : i;
    }

    /// <summary>
    /// Що Глек почув у події ядра: момент і змінні. Посеред раунду — лише з публічних полів події й ніків (ролі не
    /// читаються); на кінці раунду й партії — з відкритого підсумку <see cref="SkarbCore.Ends"/>.
    /// </summary>
    public static (SkMoment M, Dictionary<string, string> Vars)? Hear(SkEvent e, SkarbCore c, Func<int, string> nick)
    {
        int? Int(string key) => e[key] as int?;
        string Who(string key) => Int(key) is { } s ? nick(s) : "";
        string ToolAcc() => SkarbCards.ToolIdx(e["tool"] as string) is var t and >= 0 ? SkarbCards.ToolAcc[t] : "";
        switch (e.K)
        {
            case "round":
                return (Int("n") >= SkarbCards.Rounds ? SkMoment.LastRound : SkMoment.RoundStart,
                    new() { ["round"] = (Int("n") ?? 1).ToString(), ["moles"] = c.MolesInDeck.ToString() });
            case "break":
                return (SkMoment.Break, new() { ["who"] = Who("seat"), ["victim"] = Who("target"), ["toolAcc"] = ToolAcc() });
            case "fix":
                return Int("seat") == Int("target")
                    ? (SkMoment.FixSelf, new() { ["who"] = Who("seat"), ["toolAcc"] = ToolAcc() })
                    : (SkMoment.Fix, new() { ["who"] = Who("seat"), ["victim"] = Who("target"), ["toolAcc"] = ToolAcc() });
            case "map":
                return (SkMoment.Map, new() { ["who"] = Who("seat") });
            case "rockfall":
                return (SkMoment.Rockfall, new() { ["who"] = Who("seat") });
            case "place" when e["c"] is false:
                return (SkMoment.DeadEnd, new() { ["who"] = Who("seat") });
            case "goal":
                return (e["kind"] as string == "gold" ? SkMoment.Gold : SkMoment.Stone, new() { ["who"] = Who("by") });
            case "deckout":
                return (SkMoment.DeckOut, []);
            case "end" when c.Ends.LastOrDefault() is { } end:
                if (end.Side == "moles")
                    return (SkMoment.MolesWin, new()
                    {
                        ["names"] = string.Join(", ", end.Gold.Select(g => nick(g.Seat))),
                        ["n"] = (end.Gold.FirstOrDefault().N).ToString(),
                    });
                return end.Side == "none" ? (SkMoment.NoMoles, []) : null;
            case "over" when e["winners"] is int[] { Length: > 0 } w:
                var gold = c.Gold[w[0]].ToString();
                return w.Length == 1
                    ? (SkMoment.Win, new() { ["who"] = nick(w[0]), ["gold"] = gold })
                    : (SkMoment.Tie, new() { ["names"] = string.Join(", ", w.Select(nick)), ["gold"] = gold });
        }
        return null;
    }
}

/// <summary>
/// Глек за столом однієї партії: пам'ятає, що й коли казав, щоб не спамити балачку (як <see cref="KhutoryGlek"/>).
/// <list type="bullet">
/// <item>Завжди: Start, RoundStart, LastRound (замінює RoundStart третього раунду), Gold, MolesWin, NoMoles, Win, Tie,
///   Away, Back — їх мало, і це головні новини столу.</item>
/// <item>Решта — шанс <see cref="Chance"/>, не частіше ніж раз на <see cref="Cooldown"/> від будь-якої попередньої репліки
///   і не більше одного рядка на хід (ходи відмічає <see cref="NewTurn"/>; рядок «завжди» теж займає хід).</item>
/// <item>Та сама репліка того самого моменту двічі поспіль не звучить.</item>
/// </list>
/// </summary>
public sealed class SkarbGlek
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(25);
    public const double Chance = 0.40;

    readonly Dictionary<SkMoment, int> _last = new();
    DateTimeOffset? _lastAt;
    bool _saidThisTurn;

    public static bool IsAlways(SkMoment m) => m is SkMoment.Start or SkMoment.RoundStart or SkMoment.LastRound or SkMoment.Gold
        or SkMoment.MolesWin or SkMoment.NoMoles or SkMoment.Win or SkMoment.Tie or SkMoment.Away or SkMoment.Back;

    /// <summary>Новий хід — знову можна один «звичайний» рядок.</summary>
    public void NewTurn() => _saidThisTurn = false;

    public void Reset()
    {
        _lastAt = null;
        _saidThisTurn = false;
    }

    /// <summary>Репліка Глека на момент або null, якщо зараз краще помовчати.</summary>
    public string? Say(SkMoment m, DateTimeOffset now, Random rng, IReadOnlyDictionary<string, string>? vars = null)
    {
        if (m == SkMoment.Start) Reset();
        if (!IsAlways(m))
        {
            if (_saidThisTurn) return null;
            if (_lastAt is { } at && now - at < Cooldown) return null;
            if (rng.NextDouble() >= Chance) return null;
        }
        var i = SkarbLines.PickIndex(m, rng, _last.TryGetValue(m, out var prev) ? prev : -1);
        _last[m] = i;
        _lastAt = now;
        _saidThisTurn = true;
        return SkarbLines.Fill(SkarbLines.Lines(m)[i], vars);
    }
}
