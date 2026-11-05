using System.Text.RegularExpressions;
using static Hlechyky.Games.Impl.SolyanyiMap;

namespace Hlechyky.Games.Impl;

/// <summary>Моменти «Соляного шляху», на які Дядько Глек може озватися в балачці столу (контракт §14.1).</summary>
public enum SlMoment
{
    Start, FirstClaim, LongClaim, Crimea, Double, Blocked, FaceWild, Wipe, Tickets, RoadLead, LastRound, Stall,
    Win, Tie, Away, Back, LongThink,
}

/// <summary>
/// Книга реплік Глека-ведучого «Соляного шляху». Плейсхолдери: {who} {other} — ніки; {a} {b} — міста шляху; {cargo} — вантаж
/// (знахідний); {points} — число; {n} {road} — число разом зі словом («2 вози»). Чого не передали — м'яка заглушка. Глек чує лише відкрите: про замовлення — лише
/// жарти, ніяких натяків, чиї вони й куди.
/// </summary>
public static class SolyanyiLines
{
    /// <summary>Усі плейсхолдери, які вміє книга.</summary>
    public static readonly IReadOnlySet<string> Known =
        new HashSet<string> { "who", "other", "a", "b", "cargo", "n", "points", "road" };

    static readonly Dictionary<string, string> Fallback = new()
    {
        ["who"] = "хтось", ["other"] = "сусід", ["a"] = "одного міста", ["b"] = "іншого", ["cargo"] = "вантаж",
        ["n"] = "кілька возів", ["points"] = "купу", ["road"] = "чимало возів",
    };

    static readonly Dictionary<SlMoment, string[]> Book = new()
    {
        [SlMoment.Start] =
        [
            "Запрягайте волів, чумаки! Солі в Криму досить, а доріг на всіх — ні 🏺",
            "Рушаймо, панове чумаки! Хто перший займе шлях, той і веде валку, а решта — в об'їзд",
            "Мазніть вози дьогтем і перевірте ярма. Шлях неблизький, а замовлення самі себе не довезуть",
            "Ринок відкрито, колода перетасована, воли ремигають. Гарної дороги й сухих шляхів!",
            "Ну, чумаки: збирайте вантаж, займайте шляхи, а наприкінці порахуємо, хто довіз, а хто лише курив люльку",
            "Від Перекопу до Харкова — довга дорога. Хто дійде першим, тому й пісню складуть",
        ],
        [SlMoment.FirstClaim] =
        [
            "{who} вирушає: {a} — {b}. Перший віз покотився",
            "Є почин! {who} став на шлях {a} — {b}",
            "{who} рушив із місця: {a} — {b}. Хай колеса не скриплять",
            "Перша валка {who} — від {a} до {b}. Дорога вже вкочена, нема чого стояти",
            "Заскрипіли вози: {who} повіз {cargo}, {a} — {b}",
        ],
        [SlMoment.LongClaim] =
        [
            "{n} розтягнулись від {a} до {b}. Оце валка, {who}!",
            "{who} одним махом: {a} — {b}, аж {n}. Волам буде що розказати",
            "Ого, {a} — {b}! {n} у {who}. З такої дороги вже не звернеш",
            "{who} став на довгий шлях: {a} — {b}. Пилюка стоїть до самого обрію",
            "{n} за один хід. {who}, а воли в тебе двожильні?",
        ],
        [SlMoment.Crimea] =
        [
            "{who} уже коло Перекопу. Ну все, зараз сіль подорожчає",
            "{who} поліз у Крим: {a} — {b}. Хто до солі ближче, той і солоніший",
            "Валка {who} пахне морем: {a} — {b}. Туди чумаки ходять по сіль, а назад — по славу",
            "О, {who} уже на кримських шляхах! Сивашем віє аж сюди",
            "{who} торує дорогу до солі. Решта, не проспіть Перекоп",
        ],
        [SlMoment.Double] =
        [
            "Обидві смуги {a} — {b} зайняті. Там уже так тісно, що воли ремигають один одному у вухо",
            "{who} став поруч із {other} на шляху {a} — {b}. Сусіди: одна дорога, два обози",
            "Парний шлях {a} — {b} повний. Більше там ні проїхати, ні обійти",
            "{a} — {b}: і {other}, і {who}. Хто кого випередить — побачимо на переправі",
            "Дві валки пліч-о-пліч — {a} — {b}. Чумаки перемовляються, волів не поганяють",
        ],
        [SlMoment.Blocked] =
        [
            "Парний шлях закрито. На такому столі на двох дорогах не роз'їдешся, {other}",
            "{who} перейняв шлях {a} — {b}. Шукай об'їзд, {other}",
            "Ось так, {other}: {who} став на {a} — {b}, і дорога вже не твоя",
            "Хтось комусь перетнув дорогу: {a} — {b} за {who}. Об'їзд тепер довгий, {other}",
            "{who} поставив вози впоперек: {a} — {b}. Чумацька ввічливість, {other}, не інакше",
            "{a} — {b} зайнято, другої смуги нема. Така вже наша дорога, {other}",
        ],
        [SlMoment.FaceWild] =
        [
            "{who} забрав вола з ринку. Круторогий цілого ходу вартий",
            "Вола — та й по всьому. {who} цього ходу більше нічого не бере",
            "{who} повів круторогого з ринку. Таке ярмо й на будь-який вантаж годиться",
            "Добрий віл, {who}! Хоч цілий хід на нього пішов",
            "{who} торгувався недовго: віл із ринку — і додому",
        ],
        [SlMoment.Wipe] =
        [
            "Три воли на ринку — такого й Сорочинці не бачили. Міняємо карти",
            "Стільки волів, що ринок ревти почав. Усе на скид — і нові карти!",
            "Волів забагато, вантажу мало. Ринок міняємо, панове",
            "Три круторогі поруч — ринок замість торгу став загоном. Карти змінено",
            "Гей, розженіть тих волів! Новий ринок замість старого",
        ],
        [SlMoment.Tickets] =
        [
            "{who} ще замовлень набрав. Сміливий чумак — або дуже забудькуватий",
            "{who} бере нові замовлення. Куди — то вже його таємниця",
            "У {who} ще папірці в кишені. Чи довезе все — побачимо наприкінці",
            "Ще одне замовлення до {who}. Купці знають, кого просити",
            "{who} перебирає замовлення. Хтось чекає сіль, а хтось — чи встигне до зими",
            "{who} читає нові замовлення так, ніби вміє читати. Що там — не скаже",
        ],
        [SlMoment.RoadLead] =
        [
            "{who} тепер має найдовшу валку: {road} поспіль. Круторогий уже приглядається",
            "Дорога {who} — {road} без розриву! Хто більше?",
            "{road} в одну нитку. {who}, це вже не дорога, а цілий чумацький шлях",
            "Найдовша валка наразі в {who} — {road}. Бонус чекає, але ж партія ще не скінчилась",
            "У {who} {road} поспіль. Такий обоз із Києва видно аж у Полтаві",
        ],
        [SlMoment.LastRound] =
        [
            "У {who} лишилось {n}. Усі по разу — і рахуємо сіль!",
            "{who} майже все розвіз: {n} лишилось. Кожному ще один хід, поспішайте!",
            "Останнє коло! {who} дійшов до краю. Довозьте, хто що обіцяв",
            "Усе, чумаки: у {who} {n}. Ще по ходу — і за рахунки",
            "Ось і кінець дороги: {who} вже без возів майже. Останній хід кожному!",
        ],
        [SlMoment.Stall] =
        [
            "Ні карт, ні шляхів, ні замовлень — стали всі. Рахуймо, що довезли",
            "Валка стала: ніхто нічого не може. Такого шляху я ще не бачив. Рахуємо!",
            "Усі пропустили. Коли й воли не рушають — час рахувати сіль",
            "Дорога скінчилась раніше за вози. Чумаки, до рахунку",
            "Стоїмо. Вантажу нема, шляхів нема, лише мухи над волами. Підсумки!",
        ],
        [SlMoment.Win] =
        [
            "Голова валки — {who}: {points} ⭐. Найдовша дорога — {road}. Такого соляного торгу шлях ще не бачив",
            "{who} довіз найбільше: {points} ⭐! Наливайте чумакам, та й волам води",
            "Перемога за {who} — {points} ⭐. Найдовша дорога — {road}, а пісню вже складають",
            "{points} ⭐ — і голова валки {who}. Решта теж добре їхали, але не так далеко",
            "Рахунки зведено: {who} — {points} ⭐. Відтепер у всіх корчмах від Перекопу до Харкова знають це ім'я",
        ],
        [SlMoment.Tie] =
        [
            "Валку довели разом: {who} — по {points} ⭐. Сіль ділити навпіл!",
            "Нічия на шляху: {who}, по {points} ⭐. Волам усе одно, а чумакам приємно",
            "{who} — порівну, по {points} ⭐. Дорога широка, усім вистачило",
            "Отакої! {who} — по {points} ⭐. Такого й старі чумаки не пригадають",
            "Спільна перемога: {who}, {points} ⭐. Обоз один, слава на всіх",
        ],
        [SlMoment.Away] =
        [
            "{who} задрімав на возі. Нічого, волів поведу я",
            "{who} відійшов — Глек бере батіг і веде його валку",
            "За {who} тепер їду я. Воли знають дорогу, а я — де корчма",
            "{who} кудись подівся. Обоз не кидають: сідаю на його місце",
            "Гей, {who}! Поки тебе нема, твої вози на мені",
        ],
        [SlMoment.Back] =
        [
            "{who} повернувся! Віддаю батіг, дорогу не загубив",
            "З поверненням, {who}! Вози цілі, сіль суха",
            "{who} знову веде свою валку. А я — в тінь, під віз",
            "О, {who} прокинувся. Твої воли вже скучили",
            "{who} на місці — Глек злазить із воза",
        ],
        [SlMoment.LongThink] =
        [
            "{who} задумався, а воли не чекають. Цей хід за нього я проїхав",
            "{who}, на возі спати можна, а на ринку — ні. Хід зроблено за тебе",
            "Сонце вже над обрієм, а {who} ще міркує. Поїхав за нього",
            "Поки {who} чухав потилицю, валка рушила сама",
            "{who} прогавив свій хід. Я підсобив — ще раз так, і далі сам повезу",
        ],
    };

    public static IReadOnlyList<string> Lines(SlMoment m) => Book[m];

    /// <summary>Усі плейсхолдери рядка (для тесту книги).</summary>
    public static IEnumerable<string> Slots(string line) =>
        Regex.Matches(line, @"\{([^{}]*)\}").Select(x => x.Groups[1].Value);

    /// <summary>Номер випадкової репліки моменту, що не збігається з попередньою, якщо є вибір.</summary>
    public static int PickIndex(SlMoment m, Random rng, int last)
    {
        var n = Book[m].Length;
        if (n <= 1) return 0;
        var i = rng.Next(n);
        return i == last ? (i + 1) % n : i;
    }

    /// <summary>Підставити плейсхолдери; чого нема — заглушка.</summary>
    public static string Fill(string line, IReadOnlyDictionary<string, string>? vars)
    {
        foreach (var key in Known)
        {
            var token = "{" + key + "}";
            if (!line.Contains(token)) continue;
            var value = vars is not null && vars.TryGetValue(key, out var v) && v.Length > 0 ? v : Fallback[key];
            line = line.Replace(token, value);
        }
        return line;
    }

    /// <summary>
    /// На що Глек озветься з події: момент і змінні. Лише відкрите: подія <c>done</c>, хто що тримає й куди їде, сюди не
    /// доходить — слухає тільки зайняті шляхи, ринок, вози, відкриті дороги. <paramref name="flags"/> — пам'ять ведучого за партію.
    /// </summary>
    public static (SlMoment M, Dictionary<string, string> Vars)? Hear(SlEvent e, SolyanyiCore c, Func<int, string> nick, SlHeard flags)
    {
        string Who(string key = "seat") => e[key] is int s ? nick(s) : "";
        switch (e.K)
        {
            case "claim" when e["route"] is int id && e["seat"] is int seat:
                return HearClaim(c, nick, flags, Routes[id], seat);
            case "draw" when e["from"] as string == "market" && e["card"] as string == "wild" && e["pick"] is 1:
                return (SlMoment.FaceWild, new() { ["who"] = Who() });
            case "wipe":
                return (SlMoment.Wipe, []);
            case "tickets":
                return (SlMoment.Tickets, new() { ["who"] = Who() });
            case "last":
                return (SlMoment.LastRound, new() { ["who"] = Who(), ["n"] = Wagons(e["wagons"] as int? ?? 0) });
            case "over" when e["winners"] is int[] { Length: > 0 } w:
                if (e["reason"] as string == "stall") return (SlMoment.Stall, []);
                var points = c.Total(w[0]).ToString();
                return w.Length == 1
                    ? (SlMoment.Win, new() { ["who"] = nick(w[0]), ["points"] = points, ["road"] = Wagons(c.LongestLen) })
                    : (SlMoment.Tie, new() { ["who"] = string.Join(" і ", w.Select(nick)), ["points"] = points });
        }
        return null;
    }

    static string Wagons(int n) => $"{n} {SolyanyiCore.WagonWord(n)}";

    static (SlMoment, Dictionary<string, string>)? HearClaim(SolyanyiCore c, Func<int, string> nick, SlHeard flags, SlRoute r, int seat)
    {
        var vars = new Dictionary<string, string>
        {
            ["who"] = nick(seat), ["a"] = Cities[r.A].Name, ["b"] = Cities[r.B].Name, ["n"] = Wagons(r.Len),
            ["cargo"] = r.Cargo == Grey ? "вантаж" : CargoAcc[r.Cargo], ["other"] = nick(c.Next(seat)),
        };
        var first = flags.Claimed.Add(seat);
        var crimea = (IsCrimea(r.A) || IsCrimea(r.B)) && flags.Crimea.Add(seat);
        var twin = r.Twin >= 0 && c.Owner[r.Twin] >= 0;
        // Голосний блок — не лише за столом на 2–3, а й коли Глек навмисне став на зв'язку чужих мереж.
        if (c.Blocked.TryGetValue(r.Id, out var victim))
        {
            vars["other"] = nick(victim);
            return (SlMoment.Blocked, vars);
        }
        if (r.Twin >= 0 && c.OnePairOnly) return (SlMoment.Blocked, vars);
        if (twin)
        {
            vars["other"] = nick(c.Owner[r.Twin]);
            return (SlMoment.Double, vars);
        }
        if (crimea) return (SlMoment.Crimea, vars);
        if (r.Len >= 4) return (SlMoment.LongClaim, vars);
        return first ? (SlMoment.FirstClaim, vars) : null;
    }

    /// <summary>Чи не час сказати про лідера за дорогою: щойно найбільша, ≥ 15, раз на гравця за партію.</summary>
    public static (SlMoment M, Dictionary<string, string> Vars)? HearRoad(SolyanyiCore c, Func<int, string> nick, SlHeard flags, int seat)
    {
        var road = c.Road[seat];
        if (road < 15 || c.Order.Any(o => o != seat && c.Road[o] >= road) || !flags.RoadLed.Add(seat)) return null;
        return (SlMoment.RoadLead, new() { ["who"] = nick(seat), ["road"] = Wagons(road) });
    }
}

/// <summary>Що Глек-ведучий уже помітив за партію (живе в знімку, щоб після перезапуску не повторюватись).</summary>
public sealed class SlHeard
{
    public readonly HashSet<int> Claimed = [], Crimea = [], RoadLed = [];

    public void Clear()
    {
        Claimed.Clear();
        Crimea.Clear();
        RoadLed.Clear();
    }
}

/// <summary>
/// Глек за столом однієї партії: пам'ятає, що й коли казав, щоб не спамити балачку (як <see cref="SorochyntsiGlek"/>).
/// <list type="bullet">
/// <item>Завжди: Start, LastRound, Stall, Win, Tie, Away, Back — їх мало, і це головні новини столу.</item>
/// <item>Решта — шанс <see cref="Chance"/>, не частіше ніж раз на <see cref="Cooldown"/> від будь-якої попередньої репліки
///   і не більше одного рядка на хід (ходи відмічає <see cref="NewTurn"/>; рядок «завжди» теж займає хід).</item>
/// <item>Та сама репліка того самого моменту двічі поспіль не звучить.</item>
/// </list>
/// </summary>
public sealed class SolyanyiGlek
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(20);
    public const double Chance = 0.40;

    readonly Dictionary<SlMoment, int> _last = [];
    DateTimeOffset? _lastAt;
    bool _saidThisTurn;

    public static bool IsAlways(SlMoment m) => m is SlMoment.Start or SlMoment.LastRound or SlMoment.Stall or SlMoment.Win or SlMoment.Tie
        or SlMoment.Away or SlMoment.Back;

    /// <summary>Новий хід — знову можна один «звичайний» рядок.</summary>
    public void NewTurn() => _saidThisTurn = false;

    /// <summary>Нова партія: забути запобіжник (останні репліки пам'ятаємо — щоб і «Ще раз» не почався тим самим).</summary>
    public void Reset()
    {
        _lastAt = null;
        _saidThisTurn = false;
    }

    /// <summary>Репліка Глека на момент або null, якщо зараз краще помовчати.</summary>
    public string? Say(SlMoment m, DateTimeOffset now, Random rng, IReadOnlyDictionary<string, string>? vars = null)
    {
        if (m == SlMoment.Start) Reset();
        if (!IsAlways(m))
        {
            if (_saidThisTurn) return null;
            if (_lastAt is { } at && now - at < Cooldown) return null;
            if (rng.NextDouble() >= Chance) return null;
        }
        var i = SolyanyiLines.PickIndex(m, rng, _last.TryGetValue(m, out var prev) ? prev : -1);
        _last[m] = i;
        _lastAt = now;
        _saidThisTurn = true;
        return SolyanyiLines.Fill(SolyanyiLines.Lines(m)[i], vars);
    }
}
