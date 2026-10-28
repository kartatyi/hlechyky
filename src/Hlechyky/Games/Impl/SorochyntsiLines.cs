using System.Text.RegularExpressions;

namespace Hlechyky.Games.Impl;

/// <summary>Моменти «Сорочинського ярмарку», на які Дядько Глек може озватися в балачці столу (контракт §14.1).</summary>
public enum SoMoment
{
    Start, FirstBuy, FirstTier3, Fern, Guest, Starshyna, StarshynaLost, BlindReserve, Hoard, NearBulava,
    Bulava, LastRounds, Stall, Win, Tie, Away, Back, LongThink,
}

/// <summary>
/// Книга реплік Глека-ведучого «Сорочинського ярмарку». Плейсхолдери: {who} {other} — ніки; {card} — назва крамниці;
/// {guest} — гість; {good} — товар у знахідному («рибу»); {n} {points} {icons} — числа. Чого не передали — м'яка
/// заглушка. Глек чує лише відкрите: бронь наосліп для нього — «щось із колоди».
/// </summary>
public static class SorochyntsiLines
{
    /// <summary>Усі плейсхолдери, які вміє книга.</summary>
    public static readonly IReadOnlySet<string> Known =
        new HashSet<string> { "who", "other", "card", "guest", "good", "n", "points", "icons" };

    static readonly Dictionary<string, string> Fallback = new()
    {
        ["who"] = "хтось", ["other"] = "сусід", ["card"] = "крамницю", ["guest"] = "гість", ["good"] = "товар",
        ["n"] = "кілька", ["points"] = "купу", ["icons"] = "три",
    };

    static readonly Dictionary<SoMoment, string[]> Book = new()
    {
        [SoMoment.Start] =
        [
            "Гей, на ярмарок! Риба, зілля, горщики — бери, що до душі, а хто розумніший — купує крамниці 🏺",
            "Сорочинці гудуть: вози з товаром, ятки, музики. Хто першим здобуде гетьманську булаву?",
            "Розкладайте товар, панове купці! Крамниця дає знижку назавжди — а булава тільки одна",
            "Ярмарок відкрито. Кажуть, десь тут ходить червона свитка… Та нам до того діла нема — торгуймо!",
            "Ну, хто сьогодні голова ярмарку? Збирайте товари, кличте гостей і не забудьте про цвіт папороті 🌸",
            "Ятки стоять, дукати блищать. Гарного торгу, а чорта в корчмі не чіпайте",
        ],
        [SoMoment.FirstBuy] =
        [
            "Перша крамниця за {who}. Тепер {good} йому дешевше — торгівля пішла!",
            "{who} купив «{card}». З почином! Перша ятка — як перший млинець",
            "О, {who} уже при крамниці! «{card}» — для початку зовсім непогано",
            "{who} відкрив свою першу ятку. Тепер за {good} платитиме менше",
            "Перший торг у {who}: «{card}». Хто наступний?",
        ],
        [SoMoment.FirstTier3] =
        [
            "{who} узяв славного майстра — «{card}»! Оце розмах",
            "Перший славний майстер на ярмарку — «{card}» у {who}. Решта, наздоганяйте!",
            "«{card}» — у {who}. Тепер з ним рахуватимуться всі Сорочинці",
            "{who} не дрібнить: одразу «{card}». Ну й купець!",
            "Ого, {who} купив «{card}». Такі крамниці абикому не дістаються",
        ],
        [SoMoment.Fern] =
        [
            "{who} знайшов цвіт папороті! Кажуть, без нього булави не бачити",
            "Цвіт папороті в {who} — «{card}». Тепер і булава ближче",
            "{who} має цвіт папороті 🌸 Рідкісна квітка, а хто її має — тому щастить",
            "Оце так: «{card}» — у {who}. Цвіт папороті на дорозі не валяється",
            "{who} зірвав цвіт папороті. Хоч ніч і не Купальська, а пощастило",
        ],
        [SoMoment.Guest] =
        [
            "До {who} завітав {guest}. Ставте вареники — +3 ⭐",
            "{guest} зайшов до {who} — видно, крамниці сподобались. +3 ⭐",
            "У {who} гості: {guest}! Тепер і слава, і три зірки",
            "{guest} обрав прилавок {who}. Ну, кум знає, де добрий товар",
            "{who} приймає гостя — {guest}. Решті лишається заздрити",
        ],
        [SoMoment.Starshyna] =
        [
            "Січова старшина — у {who}: {icons} ⚔ на крамницях. Козацька душа!",
            "{who} зібрав {icons} козаків — і Січова старшина тепер його. +3 ⭐",
            "Козаки стали за {who}: Січова старшина знайшла господаря",
            "{who} тепер старшина — {icons} ⚔. Хто більше збере, той перебере",
            "Січова старшина пішла до {who}. Козацька позначка, бачите, теж товар",
        ],
        [SoMoment.StarshynaLost] =
        [
            "Січова старшина перейшла від {other} до {who} — козаків у того більше, нічого не вдієш",
            "{who} перебрав старшину в {other}: {icons} ⚔ проти менше. Козаки — народ вільний",
            "Ой, {other}, а старшина вже не твоя — тепер у {who}",
            "{who} переманив козаків від {other}. Січова старшина міняє господаря",
            "Козаки пішли від {other} до {who} — там, кажуть, борщ густіший",
        ],
        [SoMoment.BlindReserve] =
        [
            "{who} бронює наосліп — що там у колоді, знає тільки він",
            "{who} тягне крамницю з колоди навмання. Сміливо! Або хитро",
            "Наосліп, {who}? Ну-ну, побачимо, що за товар під сорочкою",
            "{who} відклав собі щось із колоди. Секрети на ярмарку — звична річ",
            "{who} бере кота в мішку — бронь наосліп. Хтозна, може, там цвіт папороті",
        ],
        [SoMoment.Hoard] =
        [
            "{who}, у тебе руки не гумові: більше десяти жетонів не втримаєш",
            "{who} набрав стільки, що мусить {n} віддати назад. Жадібність до добра не доводить",
            "Ой, {who}, кишені тріщать! Повертай зайве в купу",
            "{who} перебрав товару — десять жетонів, і годі",
            "Хто багато хапає, той {n} повертає. Правда, {who}?",
        ],
        [SoMoment.NearBulava] =
        [
            "{who} уже майже при булаві — бракує {points} ⭐. Дивіться пильно!",
            "Гей, купці, {who} підбирається до булави! Ще трохи — і останнє коло",
            "{who} за крок до гетьманської булави. Хто має чим завадити — саме час",
            "Булава вже виглядає {who} з-за прилавка. Не проґавте!",
            "{who} от-от візьме булаву: {points} ⭐ і ще трохи. Ох, буде торг!",
        ],
        [SoMoment.Bulava] =
        [
            "Булава в {who}! Догравайте коло, панове, — а там побачимо, хто тут голова ярмарку",
            "{who} бере гетьманську булаву ⚜ Останнє коло — хто встигне ще щось купити?",
            "Гетьманська булава — у {who}! Коло догравають, а рахують зірки",
            "{who} підняв булаву над ярмарком! Ще по ходу — і рахунок",
            "Ось і булава знайшла руку: {who}. Останнє коло, купці, не баріться",
        ],
        [SoMoment.LastRounds] =
        [
            "Коло сорокове — останнє. Ярмарок зачиняється, а булави так ніхто й не взяв",
            "Сонце сідає, вози запрягають: це останнє коло ярмарку",
            "Останнє коло, панове! Хто що не продав — продавайте зараз",
            "Сорок кіл відторгували. Ще по ходу — і по домівках",
            "Ярмарок згортається. Останнє коло — рахуємо зірки",
        ],
        [SoMoment.Stall] =
        [
            "Товари скінчились, купці позіхають — ярмарок розходиться",
            "Ніхто нічого не бере й не купує. Що ж, ярмарок розійшовся сам",
            "Купи порожні, руки повні, а торгу нема. Розходимось!",
            "Всі пропустили — значить, ярмарок своє відгув",
            "Тихо на ярмарку: ні товару, ні торгу. Рахуймо, що наторгували",
        ],
        [SoMoment.Win] =
        [
            "Голова ярмарку — {who}: {points} ⭐. Сорочинці такого торгу давно не бачили",
            "Перемагає {who} — {points} ⭐! Гуляймо, поки музики грають",
            "{who} — найкращий купець ярмарку: {points} ⭐. Ставлю могорич!",
            "Ось і голова ярмарку: {who}, {points} ⭐. Решта — до наступного року",
            "{who} переторгував усіх — {points} ⭐. Слава купцеві!",
        ],
        [SoMoment.Tie] =
        [
            "Ярмарок поділили: {who} — по {points} ⭐. Мир і могорич",
            "Нічия! {who} — по {points} ⭐. Обидва голови ярмарку, хай і так",
            "{who} — рівно по {points} ⭐. Поділіть булаву навпіл",
            "Однаково наторгували: {who}, по {points} ⭐. Таке буває раз на сто ярмарків",
            "Двоє голів одного ярмарку — {who}, по {points} ⭐. Ну, кумів більше — не біда",
        ],
        [SoMoment.Away] =
        [
            "{who} задрімав за прилавком — поторгую за нього, поки прокинеться",
            "{who} кудись відійшов. Не біда: Глек постоїть біля ятки",
            "Тихо, {who} спить. Торгую замість нього, як умію",
            "{who} подався до корчми? Прилавок не стоятиме — Глек на місці",
            "Беру прилавок {who} — нехай відпочине, а товар не чекатиме",
        ],
        [SoMoment.Back] =
        [
            "{who} повернувся до свого прилавка. Тримай — я нічого не продешевив. Ну, майже",
            "З поверненням, {who}! Твої крамниці на місці",
            "{who} знову з нами. Глек віддає ключі від ятки",
            "О, {who} прокинувся! Торгуй сам, я своє відстояв",
            "{who} на місці. Далі вже без мене — я тільки підглядатиму",
        ],
        [SoMoment.LongThink] =
        [
            "{who} довго думає… Глек цей хід доторгує, а далі сам, гаразд?",
            "{who}, товар не чекає! Цього разу я походив за тебе",
            "Задумався {who} — ну, я підкинув за нього. Наступного разу швидше",
            "{who} рахує дукати так довго, що ярмарок уже позіхає. Походив за нього",
            "Час минув, {who}. Глек трохи поторгував за тебе — не сварись",
        ],
    };

    public static IReadOnlyList<string> Lines(SoMoment m) => Book[m];

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
    public static int PickIndex(SoMoment m, Random rng, int avoid = -1)
    {
        var n = Book[m].Length;
        if (avoid < 0 || avoid >= n || n < 2) return rng.Next(n);
        var i = rng.Next(n - 1);
        return i >= avoid ? i + 1 : i;
    }

    /// <summary>
    /// Що Глек почув у події ядра: момент і змінні. Лише відкриті поля подій (бронь наосліп — без карти) і відкриті
    /// прилавки. <paramref name="firstBuy"/> — чи це перша купівля цього місця, <paramref name="firstT3"/> — перша
    /// крамниця III рівня за партію (гра пам'ятає, що вже чула).
    /// </summary>
    public static (SoMoment M, Dictionary<string, string> Vars)? Hear(SoEvent e, SorochyntsiCore c, Func<int, string> nick,
        bool firstBuy, bool firstT3)
    {
        string Who(string key) => e[key] is int s ? nick(s) : "";
        switch (e.K)
        {
            case "buy" when e["card"] is int id:
                var card = SorochyntsiCards.Deck[id];
                var vars = new Dictionary<string, string>
                {
                    ["who"] = Who("seat"), ["card"] = card.Name, ["good"] = SorochyntsiCards.GoodAcc[card.Bonus],
                };
                if (card.Bonus == SorochyntsiCards.Fern) return (SoMoment.Fern, vars);
                if (card.Tier == 3 && firstT3) return (SoMoment.FirstTier3, vars);
                return firstBuy ? (SoMoment.FirstBuy, vars) : null;
            case "guest" when e["guest"] is int g:
                return (SoMoment.Guest, new() { ["who"] = Who("seat"), ["guest"] = SorochyntsiCards.Guests[g].Name });
            case "starshyna":
                var icons = (e["icons"] as int? ?? SorochyntsiCards.StarshynaIcons).ToString();
                return e["from"] is int
                    ? (SoMoment.StarshynaLost, new() { ["who"] = Who("seat"), ["other"] = Who("from"), ["icons"] = icons })
                    : (SoMoment.Starshyna, new() { ["who"] = Who("seat"), ["icons"] = icons });
            case "reserve" when e["slot"] is null:
                return (SoMoment.BlindReserve, new() { ["who"] = Who("seat") });
            case "discard":
                var n = e["gave"] is IDictionary<string, int> gave ? gave.Values.Sum() : 0;
                return (SoMoment.Hoard, new() { ["who"] = Who("seat"), ["n"] = n > 0 ? n.ToString() : "" });
            case "bulava":
                return (SoMoment.Bulava, new() { ["who"] = Who("seat") });
            case "last" when e["reason"] as string == "rounds":
                return (SoMoment.LastRounds, []);
            case "over" when e["winners"] is int[] { Length: > 0 } w:
                if (e["reason"] as string == "stall") return (SoMoment.Stall, []);
                var points = c.Points(w[0]).ToString();
                return w.Length == 1
                    ? (SoMoment.Win, new() { ["who"] = nick(w[0]), ["points"] = points })
                    : (SoMoment.Tie, new() { ["who"] = string.Join(" і ", w.Select(nick)), ["points"] = points });
        }
        return null;
    }
}

/// <summary>
/// Глек за столом однієї партії: пам'ятає, що й коли казав, щоб не спамити балачку (як <see cref="KhutoryGlek"/>).
/// <list type="bullet">
/// <item>Завжди: Start, Bulava, LastRounds, Stall, Win, Tie, Away, Back — їх мало, і це головні новини столу.</item>
/// <item>Решта — шанс <see cref="Chance"/>, не частіше ніж раз на <see cref="Cooldown"/> від будь-якої попередньої репліки
///   і не більше одного рядка на хід (ходи відмічає <see cref="NewTurn"/>; рядок «завжди» теж займає хід).</item>
/// <item><see cref="SoMoment.NearBulava"/> — щонайбільше раз на гравця за партію.</item>
/// <item>Та сама репліка того самого моменту двічі поспіль не звучить.</item>
/// </list>
/// </summary>
public sealed class SorochyntsiGlek
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(20);
    public const double Chance = 0.40;

    readonly Dictionary<SoMoment, int> _last = new();
    readonly HashSet<string> _nearSaid = new();
    DateTimeOffset? _lastAt;
    bool _saidThisTurn;

    public static bool IsAlways(SoMoment m) => m is SoMoment.Start or SoMoment.Bulava or SoMoment.LastRounds or SoMoment.Stall
        or SoMoment.Win or SoMoment.Tie or SoMoment.Away or SoMoment.Back;

    /// <summary>Новий хід — знову можна один «звичайний» рядок.</summary>
    public void NewTurn() => _saidThisTurn = false;

    /// <summary>Нова партія: забути лічильники (останні репліки пам'ятаємо — щоб і «Ще раз» не почався тим самим).</summary>
    public void Reset()
    {
        _nearSaid.Clear();
        _lastAt = null;
        _saidThisTurn = false;
    }

    /// <summary>Чи вже казав «майже булава» про цього гравця.</summary>
    public bool NearSaid(string who) => _nearSaid.Contains(who);

    /// <summary>Репліка Глека на момент або null, якщо зараз краще помовчати.</summary>
    public string? Say(SoMoment m, DateTimeOffset now, Random rng, IReadOnlyDictionary<string, string>? vars = null)
    {
        if (m == SoMoment.Start) Reset();
        var who = vars is not null && vars.TryGetValue("who", out var w) ? w : "";
        if (!IsAlways(m))
        {
            if (_saidThisTurn) return null;
            if (_lastAt is { } at && now - at < Cooldown) return null;
            if (m == SoMoment.NearBulava && _nearSaid.Contains(who)) return null;
            if (rng.NextDouble() >= Chance) return null;
        }
        if (m == SoMoment.NearBulava) _nearSaid.Add(who);
        var i = SorochyntsiLines.PickIndex(m, rng, _last.TryGetValue(m, out var prev) ? prev : -1);
        _last[m] = i;
        _lastAt = now;
        _saidThisTurn = true;
        return SorochyntsiLines.Fill(SorochyntsiLines.Lines(m)[i], vars);
    }
}
