using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Скільки?» — гра на відчуття числа. П'ять запитань, на які ніхто не знає точної відповіді: кожен пише
/// своє число, потім усе розкривається, і кожен бере очки за те, наскільки близько влучив, а найближчий —
/// ще й бонус. Виграє не той, хто знає, а той, хто краще відчуває порядок величин.
/// <para>
/// Очки за точність, а не за місце: при місцях 3/2/1 удвох той, хто промазав у десять разів, однаково
/// брав +2, і кожне питання важило одне очко різниці. Шкала — <see cref="Accuracy"/>; найближчий завжди бере
/// ще <see cref="BestBonus"/>, а за однакової відстані швидший на секунду — ще <see cref="SpeedBonus"/>.
/// </para>
/// <para>
/// Запитання не повторюються без потреби: <see cref="SkilkySeen"/> пам'ятає, хто яке вже бачив, і в партію
/// йдуть ті, яких ніхто за столом не бачив або бачив найдавніше.
/// </para>
/// <para>
/// Партія живе не від ходу до ходу, а від тика (раз на секунду): фази міняє час, а не гравці, і саме тому
/// вся логіка переходів зібрана в одному <see cref="Tick"/> — з <see cref="Act"/> нічого не «стрибає».
/// Гра <c>Hidden</c>: доки триває фаза відповіді, чужих чисел у виді нема взагалі, лише галочки «відповів».
/// </para>
/// </summary>
public class Skilky : Game
{
    /// <summary>Скільки запитань у партії, якщо господар не обрав іншого. Менше буває лише тоді, коли тема геть куца.</summary>
    public const int Questions = 5;
    /// <summary>Скільки секунд дано на число, якщо господар не обрав іншого.</summary>
    public const int AskSeconds = 30;
    /// <summary>Скільки очок партії коштує один черепок. Без денної стелі — рішення господаря сайту.</summary>
    public const int PointsPerShard = 5;

    static readonly int[] QuestionChoices = [3, 5, 7, 10, 15];
    static readonly int[] SecondChoices = [15, 30, 45, 60];
    /// <summary>Скільки секунд висить розкриття, перш ніж поїхати далі.</summary>
    public const int RevealSeconds = 6;
    /// <summary>Коротка пауза перед кожним запитанням: «зараз буде», щоб питання не впало людям на голову.</summary>
    public const int BetweenSeconds = 3;
    /// <summary>Найбільший стіл. Більше дванадцяти чисел на одному екрані вже не роздивитись.</summary>
    public const int MaxSeats = 12;

    public const string PhaseBetween = "between";
    public const string PhaseAsk = "ask";
    /// <summary>«Ставлю на чуже»: числа вже видно без правди, кожен тисне, чиє найближче.</summary>
    public const string PhaseBet = "bet";
    public const string PhaseReveal = "reveal";
    public const string PhaseDone = "done";

    /// <summary>Очки за влучання «в яблучко» — найвищий ярус шкали точності.</summary>
    public const int Bullseye = 5;
    /// <summary>Промах на одиницю на цілій відповіді — «майже»: щонайменше стільки, хоч у відсотках це й 11 %.</summary>
    public const int OffByOne = 4;
    /// <summary>
    /// Найближчому за столом — завжди, навіть коли за точність він нічого не взяв: тоді це його єдине очко.
    /// Крім гри самому: «найближчий з одного» — не заслуга.
    /// </summary>
    public const int BestBonus = 1;
    /// <summary>За однакової відстані швидшому хоча б на <see cref="SpeedGap"/>.</summary>
    public const int SpeedBonus = 1;
    public static readonly TimeSpan SpeedGap = TimeSpan.FromSeconds(1);
    /// <summary>«Ставлю на чуже»: вгадав, чиє число найближче, — стільки очок.</summary>
    public const int BetBonus = 2;
    /// <summary>Скільки секунд на ставку.</summary>
    public const int BetSeconds = 10;
    /// <summary>Ставки мають сенс від трьох за столом: удвох «чиє найближче» — це просто «чи не я».</summary>
    public const int BetMinPlayers = 3;
    /// <summary>Назви команд (опція «Команди»).</summary>
    public static readonly string[] TeamNames = ["🔴 Червоні", "🔵 Сині", "🟢 Зелені", "🟡 Жовті"];
    /// <summary>«Питання про нас»: межі тексту й одиниці.</summary>
    public const int OursMin = 8, OursMax = 160, OursUnitMax = 24;

    /// <summary>
    /// Шкала точності для звичайних чисел: промах у частках від правильної відповіді → очки. Далі за 50 %
    /// ще є 1 «до двох разів» (<see cref="Accuracy"/>): удвічі більше й удвічі менше — однаково далеко.
    /// </summary>
    static readonly (double Off, int Points)[] Tiers = [(0.02, Bullseye), (0.10, 4), (0.25, 3), (0.50, 2)];
    /// <summary>Шкала для років: там відсотки брешуть (1900 проти 1990 — «лише 4,5 %»), тож міряємо роками.</summary>
    static readonly (double Off, int Points)[] YearTiers = [(0, Bullseye), (2, 4), (5, 3), (15, 2), (50, 1)];

    /// <summary>Рядок таблиці розкриття: чиє число, наскільки повз і скільки за що дали.</summary>
    private protected sealed record Row(int Seat, double Value, double Diff, int Accuracy, int Bonus = 0, int Fast = 0, int Team = -1)
    {
        public int Points => Accuracy + Bonus + Fast;
    }

    /// <summary>
    /// Одне зігране запитання для підсумку партії: що питали, яка правда і хто підібрався найближче.
    /// Наприкінці люди хочуть не лише рахунок, а й «а пам'ятаєш Маттергорн?» — список усіх запитань.
    /// </summary>
    private protected sealed record Recap(string Question, string? Unit, double Answer, bool Years, int[] Best, double? Value, int Points,
        int By = -1, bool Photo = false);

    // Мінімум — один: господар може почати й сам, а хто встигне підсісти до старту, грає разом.
    public override GameInfo Info { get; } = new(
        "skilky", "Скільки?", "«Скільки?»", GameGroup.Party, 1, MaxSeats,
        TickMs: 1000, Start: StartMode.ByHost, Hidden: true, Rated: false,
        Options:
        [
            new GameOption("questions", "Питань", [.. QuestionChoices.Select(n => (Str(n), Str(n)))], Str(Questions)),
            new GameOption("seconds", "Час на відповідь", [.. SecondChoices.Select(n => (Str(n), $"{n} с"))], Str(AskSeconds)),
            new GameOption("topic", "Теми", SkilkyTopics.All, SkilkyTopics.Any, Multi: true),
            new GameOption("bets", "Ставлю на чуже", [("off", "ні"), ("on", "так: +2 за вгадане чуже число (від трьох)")], "off"),
            new GameOption("teams", "Команди", [("off", "кожен сам"), ("2", "2 команди"), ("3", "3 команди"), ("4", "4 команди")], "off"),
        ],
        Hint: "Питання, на яке ніхто не знає точної відповіді. Кожен пише число: що ближче — то більше очок і черепків. Можна й самому");

    static string Str(int n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>Скільки запитань у цій кімнаті (опція «Питань»).</summary>
    int _questions = Questions;
    /// <summary>Скільки секунд на число в цій кімнаті (опція «Час на відповідь»).</summary>
    int _seconds = AskSeconds;
    /// <summary>Теми запитань цієї кімнати (опція «Теми», можна кілька); null — усі.</summary>
    IReadOnlySet<string>? _topics;
    /// <summary>Опція «Ставлю на чуже».</summary>
    bool _bets;
    /// <summary>Опція «Команди»: 0 — кожен сам, інакше 2–4.</summary>
    int _teamsOpt;

    /// <summary>Запитання цієї партії разом із уже порахованою правильною відповіддю.</summary>
    private protected readonly List<(SkilkyQuestion Q, double A)> _asked = [];
    readonly double?[] _answers = new double?[MaxSeats];
    /// <summary>Коли прийшло останнє число місця: за однакової відстані швидший бере <see cref="SpeedBonus"/>.</summary>
    readonly DateTimeOffset[] _answeredAt = new DateTimeOffset[MaxSeats];
    private protected readonly long[] _scores = new long[MaxSeats];
    /// <summary>Уже розкриті запитання цієї партії — для підсумку в кінці.</summary>
    private protected readonly List<Recap> _recap = [];

    /// <summary>«Ставлю на чуже»: на чиє число поставило місце (−1 — ще ні).</summary>
    readonly int[] _betOn = new int[MaxSeats];
    /// <summary>Ставки розкритого раунду: хто, на кого, чи вгадав.</summary>
    (int Seat, int On, bool Ok)[] _betRows = [];

    /// <summary>Команда місця (−1 — команд нема або місце порожнє).</summary>
    readonly int[] _teamOf = new int[MaxSeats];
    /// <summary>Скільки команд у цій партії (0 — кожен сам).</summary>
    int _teams;
    /// <summary>Капітан кожної команди в цьому питанні — він подає число команди.</summary>
    readonly int[] _captain = new int[4];
    /// <summary>Число, яке подав капітан.</summary>
    readonly double?[] _teamFinal = new double?[4];
    readonly DateTimeOffset[] _teamAt = new DateTimeOffset[4];
    /// <summary>Пропозиції в команді: 👍 (+1) і 👎 (−1) — голосує [хто, за чию].</summary>
    readonly sbyte[,] _vote = new sbyte[MaxSeats, MaxSeats];

    /// <summary>«Питання про нас», що чекають на наступну партію: одне від місця.</summary>
    readonly SkilkyQuestion?[] _ours = new SkilkyQuestion?[MaxSeats];

    /// <summary>Фото поточного запитання «Якого року?» — адреса з токеном (null — не фото).</summary>
    string? _photoUrl;

    private protected int _at;
    private protected string _phase = PhaseBetween;
    private protected DateTimeOffset _endsAt;
    IReadOnlyList<Row>? _reveal;
    /// <summary>
    /// Слово Глека про цей раунд («Точнісінько — Оля! Шапки геть»). Живе у виді під таблицею розкриття, а не в
    /// Балачках: раніше кожне питання кожної партії лягало туди окремим рядком, і за вечір це були сотні.
    /// </summary>
    string? _say;
    double _answer;
    bool _years;
    private protected int[]? _winners;
    /// <summary>Хтось щойно написав число — на наступному тику треба розіслати не лише кадр, а й види.</summary>
    bool _touched;

    /// <summary>Статистика радіо для динамічних запитань. Береться в <see cref="Start"/>, як велить каркас.</summary>
    SkilkyStats? _stats;
    /// <summary>Хто які запитання вже бачив. Без бази — мовчить, і запитання просто тасуються.</summary>
    SkilkySeen? _seen;
    /// <summary>Бібліотека фото «Якого року?». Нема сервісу (тести) — фото-питань теж нема.</summary>
    private protected SkilkyPhotos? _photos;

    /// <summary>Місця називаємо числами: на столі їх до дванадцяти, і «гравець одинадцятий» у чіп не влізе.</summary>
    public override string SeatName(int seat) => (seat + 1).ToString(CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------------------------------
    // партія
    // ---------------------------------------------------------------------------------------

    /// <summary>Опції столу. Каркас уже звів кожну до одного з дозволених значень, лишається прочитати.</summary>
    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue("questions", out var q) && int.TryParse(q, CultureInfo.InvariantCulture, out var n)
            && QuestionChoices.Contains(n)) _questions = n;
        if (options.TryGetValue("seconds", out var s) && int.TryParse(s, CultureInfo.InvariantCulture, out var sec)
            && SecondChoices.Contains(sec)) _seconds = sec;
        if (options.TryGetValue("topic", out var t)) _topics = SkilkyTopics.Parse(t);
        _bets = options.TryGetValue("bets", out var b) && b == "on";
        _teamsOpt = options.TryGetValue("teams", out var tm) && int.TryParse(tm, CultureInfo.InvariantCulture, out var k) && k is >= 2 and <= 4 ? k : 0;
    }

    /// <summary>«Питання про нас» можна дописати ще в лобі, до «Почати».</summary>
    public override bool ActsInLobby => true;

    /// <summary>Лише фото, а фото ще не докачались — чесно кажемо й підганяємо фон.</summary>
    public override string? CanStart()
    {
        if (_topics is { Count: 1 } only && only.Contains(SkilkyTopics.Photo))
        {
            _photos ??= Ctx.Services.GetService<SkilkyPhotos>();
            if (_photos is null || _photos.ReadyCount == 0)
            {
                _photos?.Poke();
                return "Фото для «Якого року?» ще качаються — спробуй за хвилинку або додай іншу тему";
            }
        }
        return null;
    }

    public override void Start()
    {
        // Сервіси беремо тут, а не в конструкторі: гру створює реєстр без параметрів (INTEGRATION-NOTES §1).
        // Db може не бути взагалі (тести з порожнім провайдером) — тоді динамічні запитання просто не грають.
        var db = Ctx.Services.GetService<Db>();
        _stats ??= new SkilkyStats(db, Ctx.Clock);
        _seen ??= new SkilkySeen(db);
        _photos ??= Ctx.Services.GetService<SkilkyPhotos>();

        Array.Clear(_scores);
        Array.Clear(_answers);
        Array.Clear(_answeredAt);
        _asked.Clear();
        _recap.Clear();
        SplitTeams();
        _asked.AddRange(Pick());
        _at = 0;
        _reveal = null;
        _say = null;
        _winners = null;
        _touched = false;

        if (_asked.Count == 0)
        {
            // Без банку грати нема в що. Кажемо це один раз і чесно, а не мовчимо порожнім екраном.
            _phase = PhaseDone;
            _endsAt = Ctx.Clock.UtcNow;
            Ctx.Finish([], $"{Info.Title}: банк запитань не знайшовся, партії не буде");
            return;
        }
        Open(Ctx.Clock.UtcNow);
    }

    /// <summary>Команди: сидячі по черзі в 1-шу, 2-гу… Менше людей, ніж команд, — команд стільки, скільки людей.</summary>
    void SplitTeams()
    {
        Array.Fill(_teamOf, -1);
        var seated = Enumerable.Range(0, MaxSeats).Where(Ctx.Seated).ToList();
        _teams = _teamsOpt == 0 ? 0 : Math.Min(_teamsOpt, seated.Count);
        if (_teams < 2) { _teams = 0; return; }
        for (var i = 0; i < seated.Count; i++) _teamOf[seated[i]] = i % _teams;
    }

    /// <summary>Сидячі члени команди в порядку місць.</summary>
    List<int> Members(int team) => [.. Enumerable.Range(0, MaxSeats).Where(s => _teamOf[s] == team && Ctx.Seated(s))];

    /// <summary>
    /// Різні запитання обраних тем на партію: спершу ті, яких ніхто за столом ще не бачив, далі — бачені
    /// найдавніше. Динамічне беремо лише тоді, коли фон уже порахував щось ненульове: питати «скільки треків
    /// зіграло», коли відповідь нуль, — не загадка, а знущання. «Питання про нас» ідуть понад те, врозсип.
    /// </summary>
    private protected virtual List<(SkilkyQuestion Q, double A)> Pick()
    {
        var ours = new List<(SkilkyQuestion Q, double A)>();
        for (var s = 0; s < MaxSeats; s++)
            if (_ours[s] is { } mine && Ctx.Seated(s)) { ours.Add((mine, mine.A!.Value)); _ours[s] = null; }

        var pool = Pool(_topics);
        // Обрана тема виявилась порожньою (скажімо, «Наше» на свіжій базі) — краще всі теми, ніж порожня партія.
        if (pool.Count == 0 && _topics is not null) pool = Pool(null);
        // Повне тасування Фішера — Єйтса: серед однаково свіжих (зокрема всіх небачених) порядок випадковий.
        for (var i = pool.Count - 1; i > 0; i--)
        {
            var j = Ctx.Rng.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        var need = Math.Max(0, _questions - ours.Count);
        var picked = need == 0 ? [] : SkilkySeen.Freshest(pool, x => x.Q.Key, _seen!.LastSeen(Nicks()), need);
        foreach (var o in ours) picked.Insert(Ctx.Rng.Next(picked.Count + 1), o);
        return picked;
    }

    /// <summary>Усі запитання обраних тем (null — усі), з уже порахованою правдою.</summary>
    private protected List<(SkilkyQuestion Q, double A)> Pool(IReadOnlySet<string>? topics)
    {
        var pool = new List<(SkilkyQuestion Q, double A)>();
        foreach (var q in SkilkyBank.All)
        {
            if (!SkilkyTopics.Fits(q, topics)) continue;
            if (q.IsDynamic)
            {
                var value = _stats!.Peek(q.Dyn);
                if (value > 0) pool.Add((q, value));
            }
            else if (q.A is { } a) pool.Add((q, a));
        }
        if (_photos is not null && (topics is null || topics.Contains(SkilkyTopics.Photo)))
            foreach (var p in _photos.ReadyPhotos) pool.Add((PhotoQuestion(p), p.Year));
        return pool;
    }

    /// <summary>Запитання рубрики «📷 Якого року?» з фото бібліотеки.</summary>
    public static SkilkyQuestion PhotoQuestion(SkilkyPhoto p) => new()
    {
        Q = "📷 Якого року це фото?", A = p.Year, Unit = "рік", Topic = SkilkyTopics.Photo, Photo = p.Id,
    };

    /// <summary>Ключі ніків за столом — так їх пам'ятає <see cref="SkilkySeen"/>.</summary>
    List<string> Nicks() => [.. Enumerable.Range(0, MaxSeats)
        .Where(Ctx.Seated).Select(s => Ctx.NickOf(s)).OfType<string>().Select(SkilkySeen.NickKey).Distinct()];

    /// <summary>Нове запитання: чистий стіл і коротке «готуйсь».</summary>
    private protected void Open(DateTimeOffset now)
    {
        Array.Clear(_answers);
        Array.Clear(_answeredAt);
        Array.Fill(_betOn, -1);
        Array.Clear(_vote);
        Array.Clear(_teamFinal);
        _betRows = [];
        _photoUrl = null;
        for (var t = 0; t < _teams; t++)
        {
            // Капітан по колу: кожне питання — інший, щоб подавати число встиг кожен.
            var m = Members(t);
            _captain[t] = m.Count == 0 ? -1 : m[_at % m.Count];
        }
        _reveal = null;
        _say = null;
        _touched = false;
        _phase = PhaseBetween;
        _endsAt = now.AddSeconds(BetweenSeconds);
    }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action == "ours") return Ours(seat, payload);
        if (Ctx.Seated(seat) && _phase != PhaseDone && _asked.Count == 0) return ActResult.Fail("Чекаємо на «Почати»");
        if (action == "bet") return Bet(seat, payload);
        if (action == "vote") return Vote(seat, payload);
        if (action != "answer") return ActResult.Fail("Тут так не ходять");
        if (_phase == PhaseDone) return ActResult.Fail("Партію зіграно, тисни «Ану ще раз»");
        if (_phase == PhaseBet || _phase == PhaseReveal) return ActResult.Fail("Час на число вийшов");
        if (_phase != PhaseAsk) return ActResult.Fail("Мить — зараз буде запитання");
        if (Ctx.Clock.UtcNow >= _endsAt) return ActResult.Fail("От халепа — час вийшов");
        if (Current is { Author: var by } && by == seat) return ActResult.Fail("Це твоє питання — ти й так знаєш 😉 Дивись, як мучаться інші");
        if (Number(payload) is not { } value) return ActResult.Fail("Тут треба число");
        if (double.IsNaN(value) || double.IsInfinity(value)) return ActResult.Fail("Тут треба число");
        if (Math.Abs(value) > 1e15) return ActResult.Fail("Це вже занадто велике число");

        _answers[seat] = value;
        // Час саме останнього числа: хто передумав, той і відповів пізніше.
        _answeredAt[seat] = Ctx.Clock.UtcNow;
        _touched = true;
        var shown = Current is { } q && IsYears(q) && value == Math.Round(value) ? Math.Round(value).ToString(CultureInfo.InvariantCulture) : Num(value);
        if (_teams > 0 && _teamOf[seat] is var team and >= 0)
        {
            // У команді число капітана — це число команди; решта пропонує, а команда голосує 👍/👎.
            if (_captain[team] == seat)
            {
                _teamFinal[team] = value;
                _teamAt[team] = Ctx.Clock.UtcNow;
                return ActResult.Accept($"Подав від команди: {shown}");
            }
            for (var v = 0; v < MaxSeats; v++) _vote[v, seat] = 0;   // нова пропозиція — голоси з нуля
            return ActResult.Accept($"Запропонував команді: {shown}. Подає капітан");
        }
        // Реалтайм-кімната не розсилає види з Act (Rooms.Act, counts == false), тож підтвердження
        // гравцеві — оцей рядок; галочки в усіх інших приїдуть найближчим тиком.
        return ActResult.Accept($"Записав: {shown}");
    }

    /// <summary>«Питання про нас»: одне від місця, на наступну партію. Порожній текст — забрати своє.</summary>
    ActResult Ours(int seat, JsonElement payload)
    {
        if (!Ctx.Seated(seat)) return ActResult.Fail("Питання дописують ті, хто за столом");
        var q = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("q", out var qe) && qe.ValueKind == JsonValueKind.String
            ? string.Join(' ', (qe.GetString() ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) : "";
        if (q.Length == 0)
        {
            if (_ours[seat] is null) return ActResult.Fail("Своє питання ти ще не писав");
            _ours[seat] = null;
            return ActResult.Accept("Твоє питання прибрано");
        }
        if (q.Length < OursMin) return ActResult.Fail("Закоротке питання — допиши, про що саме");
        if (q.Length > OursMax) return ActResult.Fail($"Задовге питання — не більше {OursMax} знаків");
        var a = payload.TryGetProperty("a", out var ae) ? Number(ae) : null;
        if (a is not { } answer || double.IsNaN(answer) || double.IsInfinity(answer) || Math.Abs(answer) > 1e15)
            return ActResult.Fail("Відповідь — число: «42», «1 500», «2,5»");
        var unit = payload.TryGetProperty("unit", out var ue) && ue.ValueKind == JsonValueKind.String ? (ue.GetString() ?? "").Trim() : "";
        if (unit.Length > OursUnitMax) return ActResult.Fail("Одиниця — коротко: «км», «разів», «рік»");
        if (!q.EndsWith('?')) q += "?";
        _ours[seat] = new SkilkyQuestion { Q = q, A = answer, Unit = unit.Length == 0 ? null : unit, Author = seat, Topic = "ours" };
        var later = _phase is PhaseDone || _asked.Count == 0 ? "у партії" : "у наступній партії";
        return ActResult.Accept($"Записав! Твоє питання буде {later}, а ти на нього не відповідаєш");
    }

    /// <summary>«Ставлю на чуже»: чиє число, на мою думку, найближче. Своє не можна.</summary>
    ActResult Bet(int seat, JsonElement payload)
    {
        if (_phase != PhaseBet) return ActResult.Fail("Ставки — після чисел, коли вони вже на столі");
        if (!Ctx.Seated(seat)) return ActResult.Fail("Ставлять ті, хто за столом");
        var on = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("seat", out var se) && se.TryGetInt32(out var x) ? x
            : payload.ValueKind == JsonValueKind.Number && payload.TryGetInt32(out var y) ? y : -1;
        if (on == seat) return ActResult.Fail("На себе не ставлять — вгадай, хто з інших найточніший");
        if (on is < 0 or >= MaxSeats || _answers[on] is null) return ActResult.Fail("У цього місця нема числа");
        _betOn[seat] = on;
        _touched = true;
        return ActResult.Accept($"Ставиш на {Ctx.NickOf(on) ?? SeatName(on)}");
    }

    /// <summary>👍/👎 пропозиції тіммейта; вдруге те саме — зняти голос.</summary>
    ActResult Vote(int seat, JsonElement payload)
    {
        if (_teams == 0 || _teamOf[seat] < 0) return ActResult.Fail("Голосують лише в командах");
        if (_phase != PhaseAsk) return ActResult.Fail("Голосувати можна, поки думаємо над числом");
        var on = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("seat", out var se) && se.TryGetInt32(out var x) ? x : -1;
        if (on is < 0 or >= MaxSeats || on == seat || _teamOf[on] != _teamOf[seat] || _answers[on] is null)
            return ActResult.Fail("Голосують за пропозиції тіммейтів");
        sbyte v = payload.TryGetProperty("up", out var ue) && ue.ValueKind == JsonValueKind.False ? (sbyte)-1 : (sbyte)1;
        _vote[seat, on] = _vote[seat, on] == v ? (sbyte)0 : v;
        _touched = true;
        return ActResult.Done;
    }

    /// <summary>Число приймаємо і як <c>{value: 2061}</c>, і як голе число, і як рядок — клієнтам так простіше.</summary>
    static double? Number(JsonElement payload) => payload.ValueKind switch
    {
        JsonValueKind.Number when payload.TryGetDouble(out var n) => n,
        JsonValueKind.String => Text(payload.GetString()),
        JsonValueKind.Object when payload.TryGetProperty("value", out var v) => Number(v),
        _ => null,
    };

    /// <summary>Рядок із поля вводу: кома замість крапки й пробіли між тисячами — звична річ.</summary>
    static double? Text(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // Пробіли між тисячами (звичайні й нерозривні — char.IsWhiteSpace знає обидва) прибираємо,
        // а кому читаємо як крапку: людина пише «10 000» і «2,54», а не «10000» і «2.54».
        var clean = new string([.. raw.Where(ch => !char.IsWhiteSpace(ch))]).Replace(',', '.');
        return double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    public override TickResult Tick()
    {
        if (_phase == PhaseDone || _asked.Count == 0) return TickResult.None;
        var now = Ctx.Clock.UtcNow;

        // Усі, хто за столом, уже написали — чекати на таймер нема сенсу. Розкриємо наступним рухом циклу,
        // щоб усі переходи фаз лишались в одному місці.
        if (_phase == PhaseAsk && AllAnswered()) _endsAt = now;
        if (_phase == PhaseBet && AllBet()) _endsAt = now;

        if (now < _endsAt)
        {
            // Нічого не змінилось — нічого й не шлемо. Раніше кадр летів щосекунди всім за столом (на дванадцятьох
            // це дюжина перемальовувань галочок і рахунку на кожному телефоні), хоча дуга таймера цокає сама
            // від endsAt, а галочки й очки й так приходять разом із видом на кожну відповідь і зміну фази.
            if (!_touched) return TickResult.None;
            _touched = false;
            return TickResult.Both;      // хтось відповів: галочка всім, «моє число» — авторові
        }

        _touched = false;
        switch (_phase)
        {
            case PhaseBetween:
                _phase = PhaseAsk;
                _endsAt = now.AddSeconds(_seconds);
                if (Current is { Photo: { } photo } && _photos is not null) _photoUrl = $"/api/games/skilky/photo/{_photos.Issue(photo)}.jpg";
                // «Бачив» — з тієї секунди, коли запитання з'явилось на екрані. Ті, до яких недограна партія
                // так і не дійшла, лишаються свіжими. Свої питання компанії пам'ятати нема чого.
                if (Current is { Author: < 0 }) _seen!.Mark(Nicks(), _asked[_at].Q, now);
                break;
            case PhaseAsk:
                if (_teams > 0) SettleTeams();
                if (BetsNow()) { _phase = PhaseBet; _endsAt = now.AddSeconds(BetSeconds); }
                else Reveal(now);
                break;
            case PhaseBet:
                Reveal(now);
                break;
            case PhaseReveal:
                if (_at + 1 < _asked.Count) { _at++; Open(now); }
                else Done();
                break;
        }
        return TickResult.Both;
    }

    /// <summary>Капітан не подав — команда йде з найвподобанішою пропозицією (рівні — хто раніше).</summary>
    void SettleTeams()
    {
        for (var t = 0; t < _teams; t++)
        {
            if (_teamFinal[t] is not null) continue;
            int best = -1, bestVotes = int.MinValue;
            foreach (var s in Members(t))
            {
                if (_answers[s] is null) continue;
                var votes = 0;
                for (var v = 0; v < MaxSeats; v++) votes += _vote[v, s];
                if (votes > bestVotes || votes == bestVotes && _answeredAt[s] < _answeredAt[best])
                { best = s; bestVotes = votes; }
            }
            if (best < 0) continue;
            _teamFinal[t] = _answers[best];
            _teamAt[t] = _answeredAt[best];
        }
    }

    /// <summary>Ставки — лише коли опція ввімкнена, людей від трьох і на столі хоч два числа.</summary>
    bool BetsNow()
    {
        if (!_bets || _teams > 0) return false;
        var seated = 0;
        var numbers = 0;
        for (var s = 0; s < MaxSeats; s++)
        {
            if (!Ctx.Seated(s)) continue;
            seated++;
            if (_answers[s] is not null) numbers++;
        }
        return seated >= BetMinPlayers && numbers >= 2;
    }

    /// <summary>Усі, кому є на кого ставити, поставили.</summary>
    bool AllBet()
    {
        for (var s = 0; s < MaxSeats; s++)
        {
            if (!Ctx.Seated(s) || _betOn[s] >= 0) continue;
            for (var o = 0; o < MaxSeats; o++) if (o != s && _answers[o] is not null) return false;
        }
        return true;
    }

    bool AllAnswered()
    {
        if (_teams > 0)
        {
            var any = false;
            for (var t = 0; t < _teams; t++)
            {
                if (Members(t).Count == 0) continue;
                any = true;
                if (_teamFinal[t] is null) return false;
            }
            return any;
        }
        var seated = 0;
        var by = Current?.Author ?? -1;
        for (var s = 0; s < MaxSeats; s++)
        {
            if (!Ctx.Seated(s) || s == by) continue;
            seated++;
            if (_answers[s] is null) return false;
        }
        return seated > 0;
    }

    /// <summary>
    /// Розкриття: правильна відповідь, усі числа за відстанню, кожному — очки за точність
    /// (<see cref="Accuracy"/>). Найближчим — ще <see cref="BestBonus"/> (двоє однаково близьких беруть обидва,
    /// і навіть коли всі далеко: тоді найближчий хоч одне очко таки має). У кожній групі однакової відстані
    /// той, хто відповів раніше за решту хоча б на <see cref="SpeedGap"/>, бере ще <see cref="SpeedBonus"/>.
    /// У командах те саме, лише «гравець» — команда (її число подав капітан), а очки беруть усі її члени.
    /// </summary>
    void Reveal(DateTimeOffset now)
    {
        var (question, target) = _asked[_at];
        var years = IsYears(question);
        var sorted = new List<Row>();
        var at = new Dictionary<int, DateTimeOffset>();
        if (_teams > 0)
        {
            for (var t = 0; t < _teams; t++)
                if (_teamFinal[t] is { } raw && Members(t) is { Count: > 0 } m)
                {
                    var rep = _captain[t] >= 0 && Ctx.Seated(_captain[t]) ? _captain[t] : m[0];
                    var v = InUnits(raw, target, question.Unit);
                    sorted.Add(new Row(rep, v, Math.Abs(v - target), Accuracy(v, target, years), Team: t));
                    at[rep] = _teamAt[t];
                }
        }
        else
        {
            for (var s = 0; s < MaxSeats; s++)
                if (Ctx.Seated(s) && _answers[s] is { } raw)
                {
                    var v = InUnits(raw, target, question.Unit);
                    sorted.Add(new Row(s, v, Math.Abs(v - target), Accuracy(v, target, years)));
                    at[s] = _answeredAt[s];
                }
        }
        sorted = [.. sorted.OrderBy(r => r.Diff).ThenBy(r => r.Seat)];

        // Групи однакової відстані рахуємо з допуском, а не точною рівністю double: 36.4 і 36.8 промахнулись
        // повз 36.6 однаково, але в бітах це 0.20000000000000284 і 0.19999999999999574. Гравці побачили б
        // однакову різницю й бонус лише в одного. Усередині групи першим стоїть швидший.
        var rows = new List<Row>(sorted.Count);
        var company = _teams > 0 ? sorted.Count > 1 || Enumerable.Range(0, _teams).Count(t => Members(t).Count > 0) > 1
            : Enumerable.Range(0, MaxSeats).Count(Ctx.Seated) > 1;
        for (var i = 0; i < sorted.Count;)
        {
            var j = i + 1;
            while (j < sorted.Count && SameDiff(sorted[j].Diff, sorted[i].Diff)) j++;
            var group = sorted.GetRange(i, j - i).OrderBy(r => at[r.Seat]).ThenBy(r => r.Seat).ToList();
            var faster = group.Count > 1 && at[group[1].Seat] - at[group[0].Seat] >= SpeedGap;
            for (var k = 0; k < group.Count; k++)
                rows.Add(group[k] with { Bonus = i == 0 && company ? BestBonus : 0, Fast = faster && k == 0 ? SpeedBonus : 0 });
            i = j;
        }
        foreach (var r in rows)
        {
            if (r.Team < 0) _scores[r.Seat] += r.Points;
            else foreach (var s in Members(r.Team)) _scores[s] += r.Points;
        }
        // Найближчі (однаково близьких може бути кілька) — у підсумок партії.
        var best = rows.Count == 0 ? [] : rows.Where(r => SameDiff(r.Diff, rows[0].Diff)).Select(r => r.Seat).ToArray();

        // «Ставлю на чуже»: вгадав одного з найближчих — +2.
        var bets = new List<(int Seat, int On, bool Ok)>();
        for (var s = 0; s < MaxSeats; s++)
            if (Ctx.Seated(s) && _betOn[s] >= 0)
            {
                var ok = best.Contains(_betOn[s]);
                if (ok) _scores[s] += BetBonus;
                bets.Add((s, _betOn[s], ok));
            }
        _betRows = [.. bets];

        _recap.Add(new Recap(question.Q, question.Unit, target, years, best,
            rows.Count == 0 ? null : rows[0].Value, rows.Count == 0 ? 0 : rows[0].Points, question.Author, question.Photo is not null));

        _answer = target;
        _years = years;
        _reveal = rows;
        _say = Flavor(rows);
        _phase = PhaseReveal;
        _endsAt = now.AddSeconds(RevealSeconds);
    }

    /// <summary>Запитання про рік міряємо в роках, решту — у частках від відповіді.</summary>
    private protected static bool IsYears(SkilkyQuestion q) => q.Unit == "рік";

    /// <summary>Множник, закладений в одиницю: «млн км» означає, що відповідь у мільйонах кілометрів.</summary>
    static double Multiplier(string? unit)
    {
        var u = unit?.TrimStart() ?? "";
        return u.StartsWith("тис", StringComparison.Ordinal) ? 1e3
            : u.StartsWith("млрд", StringComparison.Ordinal) ? 1e9
            : u.StartsWith("млн", StringComparison.Ordinal) ? 1e6
            : u.StartsWith("трлн", StringComparison.Ordinal) ? 1e12
            : 1;
    }

    /// <summary>
    /// Число в одиницях запитання. Там, де відповідь у мільярдах років, людина цілком може написати повне
    /// 13 800 000 000 — і отримала б нуль за «помилку в мільярд разів». Тож якщо число не менше за множник,
    /// беремо те прочитання (як є чи поділене), яке ближче до правди за порядком величин: 1200 «тис. км²»
    /// лишається 1200, а 357 000 стає 357.
    /// </summary>
    public static double InUnits(double guess, double target, string? unit)
    {
        var m = Multiplier(unit);
        if (m == 1 || target <= 0 || guess < m) return guess;
        var scaled = guess / m;
        return Math.Abs(Math.Log(scaled / target)) < Math.Abs(Math.Log(guess / target)) ? scaled : guess;
    }

    /// <summary>
    /// Очки за точність одного числа, без огляду на суперників — тому шкала однаково чесна вдвох і
    /// вдванадцятьох. Звичайні числа: промах до 2 % — <see cref="Bullseye"/>, до 10 % — 4, до 25 % — 3,
    /// до 50 % — 2, до двох разів — 1, далі — 0; на цілій відповіді промах на одиницю — щонайменше
    /// <see cref="OffByOne"/>. Роки: точно — 5, ±2 — 4, ±5 — 3, ±15 — 2, ±50 — 1.
    /// Межі включні й із допуском: 2,794 проти 2,54 — рівно 10 %, хоч у double це 0.10000000000000009.
    /// </summary>
    public static int Accuracy(double guess, double target, bool years)
    {
        const double eps = 1e-9;
        var diff = Math.Abs(guess - target);
        if (years)
        {
            foreach (var (off, p) in YearTiers) if (diff <= off + eps) return p;
            return 0;
        }
        // У банку відповіді лише додатні (динамічні з нулем у партію не беремо), тож ділити є на що. Нуль і
        // від'ємне число проти додатної відповіді — не порядок величин, а мимо.
        if (target <= 0) return SameDiff(guess, target) ? Bullseye : 0;
        if (guess <= 0) return 0;
        var miss = diff / target;
        var points = 0;
        foreach (var (off, p) in Tiers) if (miss <= off + eps) { points = p; break; }
        if (points == 0 && guess >= target / 2 * (1 - eps) && guess <= target * 2 * (1 + eps)) points = 1;
        // На малих цілих відсотки кусаються: 8 замість 9 — це вже 11 %, а для людини — «майже». На дробових
        // відповідях (1,852 милі) одиниця — це пів відповіді, тож там правило не діє.
        if (Math.Abs(target - Math.Round(target)) < eps && diff <= 1 + eps) points = Math.Max(points, OffByOne);
        return points;
    }

    /// <summary>
    /// Чи це та сама відстань. Допуск відносний: на числах банку (від одиниць до сотень тисяч) абсолютний
    /// поріг був би або надто грубим, або марним.
    /// </summary>
    static bool SameDiff(double a, double b) =>
        Math.Abs(a - b) <= 1e-9 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));

    /// <summary>
    /// Кінець партії: лідери беруть перемогу, а якщо ніхто не набрав жодного очка — нічия. Кожен, хто дограв,
    /// отримує черепок за кожні <see cref="PointsPerShard"/> очок — і вдвох, і самому. Це понад звичайну
    /// виплату каркаса за перемогу чи участь (та — лише в компанії й зі стелею партій на день).
    /// </summary>
    private protected virtual void Done()
    {
        _phase = PhaseDone;
        var seats = Enumerable.Range(0, MaxSeats).Where(Ctx.Seated).ToList();
        var best = seats.Count == 0 ? 0 : seats.Max(s => _scores[s]);
        _winners = best > 0 ? [.. seats.Where(s => _scores[s] == best)] : [];
        var scores = seats.ToDictionary(s => s, s => _scores[s]);
        foreach (var s in seats)
            if (Shards(_scores[s]) is > 0 and var shards)
                // Номер партії в причині: «Ще раз» за тим самим столом — нова виплата, а не повтор старої.
                Ctx.Award(s, shards, $"points:{Ctx.Round.ToString(CultureInfo.InvariantCulture)}");
        Ctx.Finish(_winners, Summary(seats, best), scores);
    }

    /// <summary>Скільки черепків за стільки очок партії.</summary>
    public static int Shards(long points) => points <= 0 ? 0 : (int)Math.Min(int.MaxValue, points / PointsPerShard);

    /// <summary>Рядок Журналу: рахунок усіх за столом від більшого, бо ніки відмінювати нема як.</summary>
    string Summary(List<int> seats, long best)
    {
        if (seats.Count == 0) return $"{Info.Title}: за столом уже ні душі";
        var line = string.Join(", ", seats
            .OrderByDescending(s => _scores[s]).ThenBy(s => s)
            .Select(s => $"{Ctx.NickOf(s)} {_scores[s]}"));
        return best > 0 ? $"{Info.Title}: {line}" : $"{Info.Title}: {line} — ніхто нічого не вгадав";
    }

    /// <summary>
    /// Хтось встав посеред партії. Компанійська гра це переживає: решта грає далі (навіть один — тоді вже сам),
    /// а того, хто пішов, просто не рахуємо. Партія закінчується лише тоді, коли за столом нікого.
    /// </summary>
    public override void OnLeave(int seat)
    {
        _answers[seat] = null;
        _ours[seat] = null;
        _betOn[seat] = -1;
        // Капітан пішов — подає наступний у команді (його вже подане число команди лишається).
        if (_teams > 0 && _teamOf[seat] is var team and >= 0 && _captain[team] == seat)
            _captain[team] = Members(team).FirstOrDefault(s => s != seat, -1);
        var left = Enumerable.Range(0, MaxSeats).Where(s => s != seat && Ctx.Seated(s)).ToList();
        if (left.Count >= Info.MinPlayers) return;
        _phase = PhaseDone;
        _winners = [];
        Ctx.Finish(_winners, $"{Info.Title}: гравці розійшлись, партію не дограли");
    }

    // ---------------------------------------------------------------------------------------
    // види
    // ---------------------------------------------------------------------------------------

    public override object View(int? seat)
    {
        var me = seat is { } s0 && s0 >= 0 && s0 < MaxSeats ? s0 : -1;
        var hidden = _phase == PhaseBetween;
        var photo = Current?.Photo is { } pid && _photos is not null && _photos.ById.TryGetValue(pid, out var ph) ? ph : null;
        return new
        {
            round = _at + 1,
            of = _asked.Count,
            phase = _phase,
            // У паузі перед запитанням його ще не показуємо: тексту нема ні на екрані, ні у виді, тож
            // зазирнути в консоль на три секунди раніше за інших не вийде.
            question = hidden ? "" : Current?.Q ?? "",
            unit = hidden ? null : Current?.Unit,
            // «Питання про нас»: чиє це питання (автор на нього не відповідає).
            by = hidden || Current is not { Author: >= 0 } cur ? (int?)null : cur.Author,
            // Фото «Якого року?» — лише адреса з токеном; підпис і атрибуція — на розкритті.
            photo = hidden ? null : _photoUrl,
            credit = photo is null || _phase is not (PhaseReveal or PhaseDone) ? null : new
            {
                caption = photo.Caption, author = photo.Author, license = photo.License, page = photo.Page,
            },
            endsAt = _endsAt,
            // Скільки триває відповідь у цій кімнаті — клієнтові для повної дуги таймера.
            seconds = _seconds,
            answered = Answered(),
            // Єдине, що в цьому виді своє для кожного місця: чуже число до розкриття не бачить ніхто.
            my = me >= 0 ? _answers[me] : null,
            opts = new { bets = _bets, teams = _teamsOpt },
            teams = _teams == 0 ? null : Enumerable.Range(0, _teams).Select(t => new
            {
                name = TeamNames[t], seats = Members(t).ToArray(), captain = _captain[t], done = _teamFinal[t] is not null,
            }).ToArray(),
            // Своя команда бачить пропозиції одне одного й голоси; чужа — ні (Hidden).
            team = TeamView(me),
            // «Ставлю на чуже»: числа вже на столі, правди ще нема.
            bet = _phase != PhaseBet ? null : new
            {
                values = Enumerable.Range(0, MaxSeats).Where(x => Ctx.Seated(x) && _answers[x] is not null)
                    .OrderBy(x => _answers[x]).Select(x => new { seat = x, value = _answers[x]!.Value }).ToArray(),
                on = me >= 0 && _betOn[me] >= 0 ? _betOn[me] : (int?)null,
            },
            ours = new
            {
                mine = me >= 0 && _ours[me] is { } o ? new { q = o.Q, a = o.A, unit = o.Unit } : null,
                seats = Enumerable.Range(0, MaxSeats).Where(x => _ours[x] is not null).ToArray(),
            },
            reveal = _reveal is null ? null : new
            {
                answer = _answer,
                // Клієнтові треба знати, як підписати промах: «на 12 % менше» чи просто «різниця 12» для років.
                years = _years,
                // Глек коментує раунд прямо на картці, під таблицею.
                say = _say,
                rows = _reveal.Select(r => new
                {
                    seat = r.Seat, value = r.Value, diff = r.Diff, points = r.Points,
                    accuracy = r.Accuracy, bonus = r.Bonus, fast = r.Fast, team = r.Team,
                }).ToArray(),
                bets = _betRows.Select(b => new { seat = b.Seat, on = b.On, ok = b.Ok }).ToArray(),
            },
            scores = (long[])_scores.Clone(),
            result = _winners is null ? null : new { winners = (int[])_winners.Clone(), scores = (long[])_scores.Clone() },
            // Підсумок усіх запитань — лише коли партію зіграно: посеред гри він лише відволікав би.
            recap = _phase != PhaseDone ? null : _recap.Select(r => new
            {
                question = r.Question, unit = r.Unit, answer = r.Answer, years = r.Years,
                best = r.Best, value = r.Value, points = r.Points, by = r.By >= 0 ? r.By : (int?)null, photo = r.Photo,
            }).ToArray(),
            daily = DailyView(me),
        };
    }

    /// <summary>Пропозиції своєї команди (лише під час відповіді).</summary>
    object? TeamView(int me)
    {
        if (_teams == 0 || me < 0 || _teamOf[me] is not (var t and >= 0) || _phase != PhaseAsk) return null;
        return new
        {
            t,
            captain = _captain[t],
            final = _teamFinal[t],
            drafts = Members(t).Where(x => _answers[x] is not null).Select(x =>
            {
                int up = 0, down = 0;
                for (var v = 0; v < MaxSeats; v++) { if (_vote[v, x] > 0) up++; else if (_vote[v, x] < 0) down++; }
                return new { seat = x, value = _answers[x]!.Value, up, down, mine = (int)_vote[me, x] };
            }).ToArray(),
        };
    }

    /// <summary>Своє для «Скільки? дня» (таблиця дня, рядок «поділитись»). У звичайній грі — нічого.</summary>
    private protected virtual object? DailyView(int me) => null;

    /// <summary>Кадр — лише разом із новиною (чиєсь число, зміна фази): відлік, галочки й рахунок. Нічого прихованого — кадр летить усій кімнаті.</summary>
    public override object? Frame() => new
    {
        round = _at + 1,
        of = _asked.Count,
        phase = _phase,
        endsAt = _endsAt,
        answered = Answered(),
        scores = (long[])_scores.Clone(),
    };

    private protected SkilkyQuestion? Current => _at >= 0 && _at < _asked.Count ? _asked[_at].Q : null;

    bool[] Answered()
    {
        var flags = new bool[MaxSeats];
        // У фазі ставок галочка — «поставив», інакше — «написав число» (у командах — пропозицію).
        for (var s = 0; s < MaxSeats; s++) flags[s] = _phase == PhaseBet ? _betOn[s] >= 0 : _answers[s] is not null;
        return flags;
    }

    // ---------------------------------------------------------------------------------------
    // Дядько Глек
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Що Глек каже на розкритті (під таблицею раунду). Фрази статичні (модель заради одного рядка не будимо), і нік у них
    /// завжди стоїть у називному — підметом або одразу після тире. Інакше вилазить «Пальма першості в
    /// Петро» і «промахнулась» на чоловічому ніку: чужі імена ми відмінювати не вміємо.
    /// </summary>
    static readonly string[] Flavors =
    [
        "Найближче — {0}: різниця {1}.",
        "{0} — найточніше око цього раунду, повз усього на {1}.",
        "Ближче за всіх — {0}, {1} убік.",
        "Перше місце в цьому питанні — {0}, промах {1}.",
        "{0} майже в яблучко: {1} різниці.",
        "Найточніше — {0}: {1} повз.",
        "{0} на першому місці, різниця {1}. Непогано.",
        "Точніше за всіх — {0}: лише {1} убік.",
        "Найкращий результат — {0}, та й то повз на {1}.",
        "Найкраще чуття цього раунду — {0}, різниця {1}.",
        "{0} — переможець раунду з різницею {1}.",
        "Пальма першості цього раунду — {0}, {1} убік.",
        "{0} тримає марку: {1} різниці.",
        "Тут виграє {0} — {1} повз ціль.",
        "Найближче до правди — {0}, {1} убік.",
    ];

    /// <summary>Хтось назвав рівно правильне число — «різниця 0» тут звучала б як глузд.</summary>
    static readonly string[] Exact =
    [
        "Точнісінько — {0}! Шапки геть.",
        "Овва! {0} — рівно в ціль, без жодної похибки.",
        "В яблучко, і не збоку, а в саму серцевину — {0}.",
    ];

    /// <summary>Найближчий і той нічого не взяв за точність: хвалити нема за що, хіба одним очком за першість.</summary>
    static readonly string[] Wide =
    [
        "Ех, ніхто навіть близько. Найближче — {0}, та й то повз на {1}. Одне очко — за сміливість.",
        "Мимо всі. Найменший промах — {0}: {1} убік. Тримай одне очко втіхи.",
        "Порядок величин сьогодні не з нами: найближче — {0}, різниця {1}. Очко за першість, і все.",
    ];

    /// <summary>
    /// Самому й мимо: бонусу «найближчому» соло не дають, тож фрази з <see cref="Wide"/> («тримай очко втіхи»)
    /// тут брехали б. Нік — так само в називному.
    /// </summary>
    static readonly string[] Lonely =
    [
        "Мимо: {0} повз на {1}. Цього разу без очок.",
        "Далеченько — {1} убік. {0}, наступне буде ближче.",
        "Ех, {0}: різниця {1}. Очок нема, зате тепер ти це знаєш.",
    ];

    static readonly string[] Silence =
    [
        "Тиша. Ну добре, наступне.",
        "Жодного числа. Буває.",
        "Ніхто й не спробував — рахунок стоїть на місці.",
    ];

    string Flavor(IReadOnlyList<Row> rows)
    {
        if (rows.Count == 0) return Silence[Ctx.Rng.Next(Silence.Length)];
        var best = rows[0];
        // Найближчий без жодного очка за точність: у компанії він бере бонус («очко втіхи»), самому — нічого.
        var bank = best.Accuracy == 0 ? (best.Bonus > 0 ? Wide : Lonely) : SameDiff(best.Diff, 0) ? Exact : Flavors;
        return string.Format(CultureInfo.InvariantCulture, bank[Ctx.Rng.Next(bank.Length)],
            best.Team >= 0 ? TeamNames[best.Team] : Ctx.NickOf(best.Seat) ?? SeatName(best.Seat), Num(best.Diff));
    }

    /// <summary>
    /// Число для людини: ціле — з пробілами між тисячами, дробове — з комою й без хвоста нулів. Кому
    /// ставимо руками, а не культурою «uk-UA»: збірка може піти в режимі InvariantGlobalization, і тоді
    /// культура мовчки віддала б крапку — а людина набирала «2,5» і чекає «2,5» назад.
    /// </summary>
    private protected static string Num(double v) =>
        Math.Abs(v - Math.Round(v)) < 1e-9 && Math.Abs(v) < 1e15
            ? Math.Round(v).ToString("#,##0", CultureInfo.InvariantCulture).Replace(",", " ")
            : v.ToString("#,##0.###", CultureInfo.InvariantCulture).Replace(",", " ").Replace('.', ',');
}
