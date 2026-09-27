using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>Фаза партії «Шпигуна». На дроті — рядком (<see cref="Spy.Wire(SpyPhase)"/>).</summary>
public enum SpyPhase { Lobby, Deal, Play, Vote, Final, Reveal, Done }

/// <summary>Чим скінчився раунд. На дроті — рядком (<see cref="Spy.Wire(SpyHow)"/>).
/// <c>Fold</c> — раунд не дограли: за столом лишилось двоє (його розкривають, але в хроніку він не йде).</summary>
public enum SpyHow { Caught, Wrong, Guessed, Misguess, Timeout, Left, Fold }

// ---------------------------------------------------------------------------------------------
// Форми на дроті. Записи, а не анонімні типи там, де вид складається з кусків: так поле не загубиться
// в одній із гілок. Словники з ключем-місцем ідуть на дріт як {"3": …} — клієнт читає їх як Record.
// ---------------------------------------------------------------------------------------------

/// <summary>Годинник раунду: коли скінчиться (null, поки стоїть), скільки лишилось зараз, чи стоїть, скільки всього.</summary>
public sealed record SpyClockView(DateTimeOffset? EndsAt, long LeftMs, bool Paused, long TotalMs);

/// <summary>Правила столу, зведені до чисел. Один об'єкт на партію — не перебудовується на кожен вид.
/// <c>Each</c> — «кожен по разу»: раундів стільки, скільки гравців на старті (у лобі <c>Rounds</c> ще невідомо — 0).</summary>
public sealed record SpyRulesView(int Minutes, int Rounds, string[] Sets, int DealMs, int VoteMs, int FinalMs, int RevealMs, int AskGraceMs, bool Each);

/// <summary>Рядок гравця: лише публічне (ні ролей, ні хто шпигун).</summary>
public sealed record SpyPlayerView(int Seat, string? Nick, bool Here, long Score, bool Accused, bool Ready);

/// <summary>Голосування за підозрою: голоси відкриті, <c>Need</c> — скільки голосів має бути (усі, крім підозрюваного).</summary>
public sealed record SpyVoteView(int Suspect, int Accuser, Dictionary<int, bool> Votes, int Need, DateTimeOffset EndsAt);

/// <summary>Фінальне голосування: хто на кого показав (відкрито) і скільки треба для більшості.</summary>
public sealed record SpyBlameView(Dictionary<int, int> Votes, int Need);

/// <summary>Моя картка. Шпигунові — лише <c>Spy = true</c>, без локації й ролі.</summary>
public sealed record SpyMeView(bool Spy, string? Loc, string? Role);

/// <summary>
/// Розкриття раунду, що щойно скінчився: тепер це знають усі, і глядачі теж. <c>Pointed</c> — хто на кого показав,
/// якщо раунд скінчився у фінальному голосуванні (інакше null).
/// </summary>
public sealed record SpyRevealView(int Spy, string Loc, Dictionary<int, string> Roles, string How,
    Dictionary<int, long> Gained, string? Guess, int? Suspect, int? Accuser, Dictionary<int, int>? Pointed);

/// <summary>Дограний раунд у хроніці партії.</summary>
public sealed record SpyHistoryView(int Round, int Spy, string Loc, string How, Dictionary<int, long> Gained);

/// <summary>Підсумок партії: порожні переможці — нічия; <c>Folded</c> — партію згорнули, бо за столом лишилось двоє.</summary>
public sealed record SpyResultView(int[] Winners, bool Folded);

/// <summary>
/// «Шпигун» — Spyfall у балачці столу. Усі, крім одного, знають, де вони (локація) і ким там є (роль); шпигун знає
/// лише колоду з 24 можливих локацій. Питання одне одному ставлять у балачці столу, а гра тримає те, чого в чаті не
/// зробиш: таємні картки, м'який покажчик «хто зараз питає», підозру з одностайним голосуванням, спробу шпигуна
/// назвати локацію, фінальне голосування більшістю й очки за раунди.
/// <para>
/// Фази міняє лише <see cref="Tick"/> за <c>Ctx.Clock</c> (раз на 250 мс — щоб відгук на натиск був швидким);
/// <see cref="Act"/> лише міняє стан. Єдиний виняток — підозра: вона відкриває голосування одразу, щоб годинник
/// раунду став тієї ж миті. Гра <c>Hidden</c>: вид збирається на кожне місце окремо, і відрізняються вони лише
/// полем <c>me</c> — що в ньому нема, того в браузері не видобути.
/// </para>
/// </summary>
public sealed class Spy : Game
{
    public const int TickMs = 250;
    /// <summary>Роздача: кожен дивиться на свою картку, годинник раунду ще не йде.</summary>
    public const int DealMs = 8_000;
    public const int VoteMs = 20_000;
    public const int FinalMs = 45_000;
    public const int RevealMs = 15_000;
    /// <summary>Скільки той, чия черга питати, може мовчати, перш ніж слово перехоплять.</summary>
    public const int AskGraceMs = 30_000;
    /// <summary>Скільки локацій у колоді партії: більше — вгадати неможливо, а список на телефоні — три екрани.</summary>
    public const int DeckSize = 24;
    /// <summary>Менше локацій у пулі — партії не буде.</summary>
    public const int MinDeck = 3;
    public const int SpyGuessPts = 4;
    public const int SpyFramedPts = 4;
    public const int SpyTimeoutPts = 2;
    public const int VillagePts = 1;
    public const int AccuserBonus = 1;
    public const int MinSeats = 3;
    public const int MaxSeats = 10;
    public const int DefaultMinutes = 6;
    public const int DefaultRounds = 3;
    /// <summary>Значення опції «Раундів»: кожен за столом побуде шпигуном по разу.</summary>
    public const string EachRound = "each";

    static readonly int[] MinuteChoices = [4, 6, 8, 10];
    static readonly int[] RoundChoices = [1, 3, 5];

    public override GameInfo Info { get; } = new(
        "spy", "Шпигун", "шпигуна", GameGroup.Party, MinSeats, MaxSeats,
        TickMs: TickMs, Start: StartMode.ByHost, Hidden: true, Private: false, Persistent: false, Rated: false,
        Score: ScoreOrder.None,
        Options:
        [
            new GameOption("time", "Раунд", [("4", "4 хвилини"), ("6", "6 хвилин"), ("8", "8 хвилин"), ("10", "10 хвилин")], "6"),
            new GameOption("rounds", "Раундів", [("1", "Один"), ("3", "Три"), ("5", "П'ять"), (EachRound, "Кожен по разу")], "3"),
            new GameOption("set", "Локації", SpyLocations.Sets, SpyLocations.AnySet, Multi: true),
        ],
        Hint: "Усі знають, де вони, — крім шпигуна. Питайте одне одного в балачці столу: село шукає шпигуна, шпигун — локацію");

    /// <summary>
    /// Порядок колоди — за назвою, по-українськи. Під <c>InvariantGlobalization</c> культури може не бути —
    /// тоді ordinal: сортує сервер один раз, і список у всіх однаковий, а це головне.
    /// </summary>
    public static readonly StringComparer TitleOrder = MakeOrder();

    static StringComparer MakeOrder()
    {
        try { return StringComparer.Create(CultureInfo.GetCultureInfo("uk-UA"), ignoreCase: true); }
        catch (CultureNotFoundException) { return StringComparer.OrdinalIgnoreCase; }
    }

    /// <summary>Місць десять, «гравець десятий» у чіп не влізе — тож числами, як у «Скільки?».</summary>
    public override string SeatName(int seat) => (seat + 1).ToString(CultureInfo.InvariantCulture);

    // ---------- налаштування столу ----------

    int _minutes = DefaultMinutes;
    int _rounds = DefaultRounds;
    /// <summary>Раундів «кожен по разу»: скільки їх, стане відомо на старті (стільки, скільки гравців).</summary>
    bool _each;
    string[] _sets = [SpyLocations.AnySet];
    SpyRulesView _rules = new(DefaultMinutes, DefaultRounds, [SpyLocations.AnySet], DealMs, VoteMs, FinalMs, RevealMs, AskGraceMs, false);
    SpyLocations? _bank;

    long PlayMs => _minutes * 60_000L;

    // ---------- партія ----------

    /// <summary>Місця, що грають цю партію (зайняті на старті), за зростанням.</summary>
    int[] _seats = [];
    /// <summary>Нік на старті: той, хто встав, лишається іменем у таблиці, а не діркою.</summary>
    readonly string?[] _nicks = new string?[MaxSeats];
    readonly bool[] _inMatch = new bool[MaxSeats];
    /// <summary>Ще за столом (не встав). Очки тих, хто пішов, лишаються в таблиці.</summary>
    readonly bool[] _present = new bool[MaxSeats];
    int _presentCount;
    readonly long[] _scores = new long[MaxSeats];
    /// <summary>Скільки разів місце вже було шпигуном у цій партії: наступним стає той, у кого найменше.</summary>
    readonly int[] _spyTimes = new int[MaxSeats];
    /// <summary>Колода партії, за назвою; <see cref="_deckView"/> — вона ж, готова на дріт (одна на партію).</summary>
    SpyLocation[] _deck = [];
    string[][] _deckView = [];
    readonly HashSet<string> _playedLocs = new(StringComparer.Ordinal);
    readonly List<SpyHistoryView> _history = [];
    /// <summary>
    /// Імена всіх, хто грав партію, — для виду після неї: на звільнене місце вже може сісти новачок, а хроніка й
    /// переможці мусять лишитись при своїх іменах. Збирається раз на партію.
    /// </summary>
    Dictionary<int, string> _namesView = [];
    int _round;
    SpyResultView? _result;

    // ---------- раунд ----------

    SpyPhase _phase = SpyPhase.Lobby;
    /// <summary>Дедлайн поточної фази (у <c>play</c> — кінець годинника раунду).</summary>
    DateTimeOffset _endsAt;
    int _spy = -1;
    SpyLocation? _loc;
    readonly string?[] _roles = new string?[MaxSeats];
    /// <summary>Кому цього раунду роздано картку.</summary>
    readonly bool[] _dealt = new bool[MaxSeats];
    /// <summary>Раунд роздано, а ще не розкрито: якщо стіл розійдеться зараз, розкривати нема чого.</summary>
    bool _roundOpen;

    // черга питань — м'який покажчик
    int? _asker;
    int? _askedBy;
    DateTimeOffset _askedAt;
    /// <summary>Уже розіслали, що слово можна перехопити: вид має знати про це без жодного натиску.</summary>
    bool _graceShown;

    // годинник раунду
    long _clockLeftMs;
    DateTimeOffset? _clockEndsAt;

    // підозра
    readonly bool[] _accused = new bool[MaxSeats];
    bool _voting;
    int _suspect = -1;
    int _accuser = -1;
    /// <summary>0 — ще не голосував, 1 — «так», −1 — «ні».</summary>
    readonly sbyte[] _votes = new sbyte[MaxSeats];

    // фінал
    /// <summary>На кого показав кожен (−1 — ні на кого).</summary>
    readonly int[] _blame = new int[MaxSeats];
    readonly int[] _tally = new int[MaxSeats];

    // розкриття
    readonly bool[] _ready = new bool[MaxSeats];
    SpyRevealView? _reveal;

    /// <summary>Щось змінилось — найближчий тик рознесе свіжі види.</summary>
    bool _dirty;
    /// <summary>Змінилась фаза — найближчий тик рознесе ще й кадр.</summary>
    bool _phaseChanged;

    public Spy()
    {
        Array.Fill(_blame, -1);
    }

    // =========================================================================================
    // Налаштування
    // =========================================================================================

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _minutes = Pick(options, "time", MinuteChoices, DefaultMinutes);
        _each = options.TryGetValue("rounds", out var rr) && rr == EachRound;
        _rounds = _each ? 0 : Pick(options, "rounds", RoundChoices, DefaultRounds);
        var picked = options.TryGetValue("set", out var raw) ? GameOption.Split(raw) : [];
        var known = SpyLocations.Sets.Where(s => s.Value != SpyLocations.AnySet && picked.Contains(s.Value)).Select(s => s.Value).ToArray();
        _sets = known.Length == 0 || picked.Contains(SpyLocations.AnySet) ? [SpyLocations.AnySet] : known;
        _rules = new SpyRulesView(_minutes, _rounds, _sets, DealMs, VoteMs, FinalMs, RevealMs, AskGraceMs, _each);
        // Сервіси — тут, а не в конструкторі: гру створює реєстр без параметрів. Тести кладуть свій банк.
        _bank = Ctx.Services.GetService<SpyLocations>() ?? SpyLocations.Default;
    }

    static int Pick(IReadOnlyDictionary<string, string> options, string key, int[] allowed, int fallback) =>
        options.TryGetValue(key, out var s) && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            && Array.IndexOf(allowed, n) >= 0 ? n : fallback;

    // =========================================================================================
    // Старт партії й раунду
    // =========================================================================================

    public override void Start()
    {
        _bank ??= Ctx.Services.GetService<SpyLocations>() ?? SpyLocations.Default;

        var seats = new List<int>();
        for (var s = 0; s < MaxSeats; s++) if (Ctx.Seated(s)) seats.Add(s);
        _seats = [.. seats];
        Array.Clear(_nicks);
        Array.Clear(_inMatch);
        Array.Clear(_present);
        Array.Clear(_scores);
        Array.Clear(_spyTimes);
        foreach (var s in _seats)
        {
            _nicks[s] = Ctx.NickOf(s);
            _inMatch[s] = true;
            _present[s] = true;
        }
        _presentCount = _seats.Length;
        _namesView = [];
        foreach (var s in _seats) _namesView[s] = Name(s);
        if (_each) _rounds = _seats.Length;
        _rules = new SpyRulesView(_minutes, _rounds, _sets, DealMs, VoteMs, FinalMs, RevealMs, AskGraceMs, _each);
        _history.Clear();
        _playedLocs.Clear();
        _round = 0;
        _result = null;
        _reveal = null;
        _roundOpen = false;
        ClearRound();

        // Пул — у порядку файла; колода — тасування Фішера — Єйтса на генераторі кімнати, перші 24, за назвою.
        var pool = _bank.Pool(_sets);
        if (pool.Count < MinDeck)
        {
            _deck = [];
            _deckView = [];
            SetPhase(SpyPhase.Done);
            _result = new SpyResultView([], false);
            Ctx.Finish([], $"{Info.Title}: локацій не знайшлось, партії не буде");
            return;
        }
        for (var i = pool.Count - 1; i > 0; i--)
        {
            var j = Ctx.Rng.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        var deck = pool.GetRange(0, Math.Min(DeckSize, pool.Count));
        deck.Sort((a, b) => TitleOrder.Compare(a.Title, b.Title));
        _deck = [.. deck];
        _deckView = [.. _deck.Select(l => new[] { l.Id, l.Title, l.Icon })];

        NewRound();
    }

    /// <summary>Прибрати все, що живе один раунд.</summary>
    void ClearRound()
    {
        Array.Clear(_ready);
        Array.Clear(_accused);
        Array.Clear(_votes);
        Array.Clear(_dealt);
        Array.Clear(_roles);
        Array.Fill(_blame, -1);
        _voting = false;
        _suspect = -1;
        _accuser = -1;
        _spy = -1;
        _loc = null;
        _asker = null;
        _askedBy = null;
        _graceShown = false;
        _clockEndsAt = null;
        _clockLeftMs = PlayMs;
    }

    /// <summary>
    /// Новий раунд. Порядок звернень до генератора фіксований — шпигун, локація, ролі, перший питає — на нього
    /// спирається тест детермінізму.
    /// </summary>
    void NewRound()
    {
        var now = Ctx.Clock.UtcNow;
        _round++;
        ClearRound();
        _reveal = null;

        // Шпигун: серед присутніх — ті, хто ним був найменше; серед них — випадковий у порядку місць.
        var least = int.MaxValue;
        foreach (var s in _seats) if (_present[s] && _spyTimes[s] < least) least = _spyTimes[s];
        var candidates = new List<int>();
        foreach (var s in _seats) if (_present[s] && _spyTimes[s] == least) candidates.Add(s);
        _spy = candidates[Ctx.Rng.Next(candidates.Count)];
        _spyTimes[_spy]++;

        // Локація: не зіграна в цій партії, у порядку колоди. Колода скінчилась — беремо з усієї.
        var fresh = new List<SpyLocation>();
        foreach (var l in _deck) if (!_playedLocs.Contains(l.Id)) fresh.Add(l);
        List<SpyLocation> from = fresh.Count > 0 ? fresh : [.. _deck];
        _loc = from[Ctx.Rng.Next(from.Count)];
        _playedLocs.Add(_loc.Id);

        // Ролі: тасуємо ролі локації й роздаємо не-шпигунам у порядку місць. На 9–10 гравцях буває повтор.
        var roles = (string[])_loc.Roles.Clone();
        for (var i = roles.Length - 1; i > 0; i--)
        {
            var j = Ctx.Rng.Next(i + 1);
            (roles[i], roles[j]) = (roles[j], roles[i]);
        }
        var k = 0;
        foreach (var s in _seats)
        {
            if (!_present[s]) continue;
            _dealt[s] = true;
            if (s != _spy) _roles[s] = roles[k++ % roles.Length];
        }

        // Перший питає — будь-хто з присутніх.
        var here = new List<int>();
        foreach (var s in _seats) if (_present[s]) here.Add(s);
        _asker = here[Ctx.Rng.Next(here.Count)];
        _askedBy = null;
        _askedAt = now;

        _roundOpen = true;
        _clockLeftMs = PlayMs;
        _clockEndsAt = null;
        _endsAt = now.AddMilliseconds(DealMs);
        SetPhase(SpyPhase.Deal);

        Speak(_rounds == 1
            ? SpyGlek.Pick(Ctx.Rng, SpyGlek.RoundStartOne, _round, _rounds, Name(_asker.Value))
            : SpyGlek.Pick(Ctx.Rng, SpyGlek.RoundStart, _round, _rounds, Name(_asker.Value)));
    }

    void SetPhase(SpyPhase phase)
    {
        _phase = phase;
        _phaseChanged = true;
        _dirty = true;
    }

    // =========================================================================================
    // Годинник
    // =========================================================================================

    public override TickResult Tick()
    {
        if (_phase is SpyPhase.Lobby or SpyPhase.Done) return Take();
        var now = Ctx.Clock.UtcNow;
        switch (_phase)
        {
            case SpyPhase.Deal:
                if (now >= _endsAt) EnterPlay(now);
                break;
            case SpyPhase.Play:
                if (_clockEndsAt is { } end && now >= end) EnterFinal(now, SpyGlek.TimeUp);
                else if (!_graceShown && (now - _askedAt).TotalMilliseconds >= AskGraceMs)
                {
                    // Слово можна перехоплювати: кнопки «Перехопити» мають з'явитись без жодного натиску.
                    _graceShown = true;
                    _dirty = true;
                }
                break;
            case SpyPhase.Vote:
                if (AllVoted() || now >= _endsAt) ResolveVote(now);
                break;
            case SpyPhase.Final:
                if (AllBlamed() || now >= _endsAt) ResolveFinal();
                break;
            case SpyPhase.Reveal:
                if (AllReady() || now >= _endsAt) Advance();
                break;
        }
        return Take();
    }

    /// <summary>Що розіслати: кадр — лише коли змінилась фаза, види — коли є що показати. Без жодного new.</summary>
    TickResult Take()
    {
        var result = new TickResult(_phaseChanged, _dirty || _phaseChanged);
        _dirty = false;
        _phaseChanged = false;
        return result;
    }

    void EnterPlay(DateTimeOffset now)
    {
        _clockEndsAt = now.AddMilliseconds(_clockLeftMs);
        _endsAt = _clockEndsAt.Value;
        _askedAt = now;
        _graceShown = false;
        SetPhase(SpyPhase.Play);
    }

    /// <summary>Годинник раунду знову пішов (після невдалої підозри) — з того місця, де став.</summary>
    void ResumePlay(DateTimeOffset now)
    {
        _clockEndsAt = now.AddMilliseconds(_clockLeftMs);
        _endsAt = _clockEndsAt.Value;
        // Пауза — не мовчання: перехоплення слова не має спрацювати одразу після голосування.
        _askedAt = now;
        _graceShown = false;
        SetPhase(SpyPhase.Play);
    }

    void EnterFinal(DateTimeOffset now, string[] line)
    {
        _clockLeftMs = 0;
        _clockEndsAt = null;
        Array.Fill(_blame, -1);
        _endsAt = now.AddMilliseconds(FinalMs);
        SetPhase(SpyPhase.Final);
        Speak(SpyGlek.Pick(Ctx.Rng, line));
    }

    /// <summary>Зупинити годинник раунду (підозра). Скільки лишилось — не менше нуля.</summary>
    void PauseClock(DateTimeOffset now)
    {
        if (_clockEndsAt is { } end)
        {
            var left = (long)Math.Ceiling((end - now).TotalMilliseconds);
            _clockLeftMs = Math.Max(0, left);
        }
        _clockEndsAt = null;
    }

    bool AllVoted()
    {
        foreach (var s in _seats)
            if (_present[s] && s != _suspect && _votes[s] == 0) return false;
        return true;
    }

    bool AllBlamed()
    {
        foreach (var s in _seats)
            if (_present[s] && _blame[s] < 0) return false;
        return true;
    }

    bool AllReady()
    {
        foreach (var s in _seats)
            if (_present[s] && !_ready[s]) return false;
        return true;
    }

    // =========================================================================================
    // Підсумки голосувань
    // =========================================================================================

    /// <summary>Кінець голосування за підозрою: одностайне «так» (мовчання — «ні») закриває раунд.</summary>
    void ResolveVote(DateTimeOffset now)
    {
        var yes = true;
        foreach (var s in _seats)
            if (_present[s] && s != _suspect && _votes[s] != 1) { yes = false; break; }

        var suspect = _suspect;
        var accuser = _accuser;
        if (yes)
        {
            EndRound(suspect == _spy ? SpyHow.Caught : SpyHow.Wrong, suspect, accuser, null);
            return;
        }
        CloseVote();
        // Невдалу підозру картка й так показує (рядок «підозру не підтримали»), а балачка столу в «Шпигуні» — це
        // сама гра: друга бульбашка Глека на кожну підозру відсувала б питання вгору. Тож тут Глек мовчить.
        if (_clockLeftMs <= 0) EnterFinal(now, SpyGlek.NotUnanimousFinal);
        else ResumePlay(now);
    }

    void CloseVote()
    {
        _voting = false;
        _suspect = -1;
        _accuser = -1;
        Array.Clear(_votes);
    }

    /// <summary>Кінець фіналу: засуджений — той, на кого показала строга більшість УСІХ присутніх.</summary>
    void ResolveFinal()
    {
        Array.Clear(_tally);
        foreach (var s in _seats)
            if (_present[s] && _blame[s] >= 0 && _present[_blame[s]]) _tally[_blame[s]]++;
        var convicted = -1;
        foreach (var s in _seats)
            if (_present[s] && _tally[s] * 2 > _presentCount) convicted = s;

        if (convicted < 0) EndRound(SpyHow.Timeout, null, null, null);
        else EndRound(convicted == _spy ? SpyHow.Caught : SpyHow.Wrong, convicted, null, null, byMajority: true);
    }

    /// <summary>
    /// Раунд скінчився: очки, розкриття, рядок у хроніку, слово Глека. <paramref name="byMajority"/> — вердикт
    /// фінального голосування: коли засудили шпигуна, кожен, хто показав саме на нього, бере бонус, як обвинувач
    /// за підозру («влучне око»).
    /// </summary>
    void EndRound(SpyHow how, int? suspect, int? accuser, string? guess, bool byMajority = false)
    {
        var now = Ctx.Clock.UtcNow;
        // Хто на кого показав — якщо раунд скінчився у фінальному голосуванні (вердиктом чи здогадкою шпигуна).
        var pointed = _phase == SpyPhase.Final ? BlameMap() : null;
        var gained = new Dictionary<int, long>();
        foreach (var s in _seats) if (_dealt[s]) gained[s] = 0;

        void Give(int seat, long pts)
        {
            if (!_present[seat] || pts == 0) return;
            _scores[seat] += pts;
            gained[seat] = (gained.TryGetValue(seat, out var g) ? g : 0) + pts;
        }
        void Village()
        {
            foreach (var s in _seats) if (s != _spy && _dealt[s]) Give(s, VillagePts);
        }

        switch (how)
        {
            case SpyHow.Caught:
                Village();
                if (accuser is { } a && _present[a])
                {
                    Give(a, AccuserBonus);
                    Ctx.Award(a, 0, "ach:spy-catch");
                }
                if (byMajority)
                    foreach (var s in _seats)
                        if (_present[s] && _blame[s] == _spy) Give(s, AccuserBonus);   // «влучне око»
                break;
            case SpyHow.Wrong: Give(_spy, SpyFramedPts); break;
            case SpyHow.Guessed:
                Give(_spy, SpyGuessPts);
                Ctx.Award(_spy, 0, "ach:spy-guess");
                break;
            case SpyHow.Timeout: Give(_spy, SpyTimeoutPts); break;
            case SpyHow.Misguess:
            case SpyHow.Left:
                Village();
                break;
        }

        var loc = _loc!;
        _reveal = new SpyRevealView(_spy, loc.Id, RolesMap(), Wire(how), gained, guess, suspect, accuser, pointed);
        _history.Add(new SpyHistoryView(_round, _spy, loc.Id, Wire(how), new Dictionary<int, long>(gained)));

        var spyName = Name(_spy);
        var line = how switch
        {
            SpyHow.Caught when accuser is { } by && _present[by] => SpyGlek.Pick(Ctx.Rng, SpyGlek.CaughtBy, spyName, loc.Title, Name(by)),
            SpyHow.Caught when byMajority => SpyGlek.Pick(Ctx.Rng, SpyGlek.CaughtFinal, spyName, loc.Title),
            SpyHow.Caught => SpyGlek.Pick(Ctx.Rng, SpyGlek.Caught, spyName, loc.Title),
            SpyHow.Wrong => SpyGlek.Pick(Ctx.Rng, SpyGlek.Framed, Name(suspect ?? -1), spyName, loc.Title),
            SpyHow.Guessed => SpyGlek.Pick(Ctx.Rng, SpyGlek.Guessed, spyName, loc.Title),
            SpyHow.Misguess => SpyGlek.Pick(Ctx.Rng, SpyGlek.Misguess, spyName, TitleOf(guess), loc.Title),
            SpyHow.Timeout => SpyGlek.Pick(Ctx.Rng, SpyGlek.Timeout, spyName, loc.Title),
            _ => SpyGlek.Pick(Ctx.Rng, SpyGlek.Left, spyName, loc.Title),
        };
        Speak(line);

        CloseVote();
        Array.Fill(_blame, -1);
        Array.Clear(_ready);
        _roundOpen = false;
        _clockEndsAt = null;
        _endsAt = now.AddMilliseconds(RevealMs);
        SetPhase(SpyPhase.Reveal);
    }

    Dictionary<int, string> RolesMap()
    {
        var roles = new Dictionary<int, string>();
        foreach (var s in _seats) if (_dealt[s] && s != _spy && _roles[s] is { } r) roles[s] = r;
        return roles;
    }

    Dictionary<int, int> BlameMap()
    {
        var map = new Dictionary<int, int>();
        foreach (var s in _seats) if (_present[s] && _blame[s] >= 0) map[s] = _blame[s];
        return map;
    }

    /// <summary>Розкриття скінчилось: наступний раунд або кінець партії.</summary>
    void Advance()
    {
        if (_round < _rounds) NewRound();
        else FinishMatch();
    }

    /// <summary>Лідери серед присутніх — за умови, що хтось узагалі набрав очко.</summary>
    int[] Leaders()
    {
        var top = 0L;
        foreach (var s in _seats) if (_present[s] && _scores[s] > top) top = _scores[s];
        if (top <= 0) return [];
        var list = new List<int>();
        foreach (var s in _seats) if (_present[s] && _scores[s] == top) list.Add(s);
        return [.. list];
    }

    /// <summary>«Оля 5, Петро 3, Ганна 0» — усі, хто грав, за спаданням очок (рівні — за місцем).</summary>
    string Standings() =>
        string.Join(", ", _seats.OrderByDescending(s => _scores[s]).ThenBy(s => s).Select(s => $"{Name(s)} {_scores[s]}"));

    Dictionary<int, long> ScoreMap()
    {
        var map = new Dictionary<int, long>();
        foreach (var s in _seats) map[s] = _scores[s];
        return map;
    }

    void FinishMatch()
    {
        var leaders = Leaders();
        _result = new SpyResultView(leaders, false);
        SetPhase(SpyPhase.Done);
        if (leaders.Length == 0)
        {
            // Очки могли лишитись лише в тих, хто вже пішов: за неявку перемоги не дають — нічия.
            var any = false;
            foreach (var s in _seats) if (_scores[s] > 0) any = true;
            Speak(SpyGlek.Pick(Ctx.Rng, SpyGlek.GameOverDraw));
            Ctx.Finish([], any
                ? $"{Info.Title}: переможця за столом не лишилось — нічия. {Standings()}"
                : $"{Info.Title}: ніхто не набрав ні очка — нічия", ScoreMap());
            return;
        }
        var pts = SpyGlek.Points(_scores[leaders[0]]);
        Speak(leaders.Length == 1
            ? SpyGlek.Pick(Ctx.Rng, SpyGlek.GameOver, Name(leaders[0]), pts)
            : SpyGlek.Pick(Ctx.Rng, SpyGlek.GameOverTie, SpyGlek.Names([.. leaders.Select(Name)]), pts));
        Ctx.Finish(leaders, $"{Info.Title}: {Standings()}", ScoreMap());
    }

    // =========================================================================================
    // Дії гравців
    // =========================================================================================

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (_phase == SpyPhase.Done) return ActResult.Fail(Hlechyky.Games.Say.Played);
        if (_phase == SpyPhase.Lobby) return ActResult.Fail("Партія ще не почалась");
        if (seat < 0 || seat >= MaxSeats || !_inMatch[seat]) return ActResult.Fail("Ти тут не граєш");
        if (!_present[seat]) return ActResult.Fail("Ти вже встав з-за столу");

        return action switch
        {
            "ask" => Ask(seat, payload),
            "accuse" => Accuse(seat, payload),
            "vote" => Vote(seat, payload),
            "blame" => Blame(seat, payload),
            "guess" => Guess(seat, payload),
            "ready" => Ready(seat),
            _ => ActResult.Fail("Тут так не ходять"),
        };
    }

    ActResult Ask(int seat, JsonElement payload)
    {
        if (_phase != SpyPhase.Play) return ActResult.Fail("Зараз не час питати");
        var now = Ctx.Clock.UtcNow;
        var mine = seat == _asker;
        if (!mine && (now - _askedAt).TotalMilliseconds < AskGraceMs) return ActResult.Fail($"Зараз питає {Name(_asker ?? -1)}");
        if (Target(payload) is not { } t || !InMatch(t)) return ActResult.Fail("Не зрозумів, кого питати");
        if (!_present[t]) return ActResult.Fail("Його вже нема за столом");
        if (t == seat) return ActResult.Fail("Себе питати нема сенсу");
        if (mine && t == _askedBy && _presentCount > 2) return ActResult.Fail($"{Name(t)} щойно питав тебе — спитай когось іншого");

        // Своя черга — відповідач стає наступним, хто питає. Перехоплене слово — так само, але «щойно питав»
        // тепер той, хто перехопив: правило «не того, хто питав тебе» лишається осмисленим.
        _askedBy = seat;
        _asker = t;
        _askedAt = now;
        _graceShown = false;
        _dirty = true;
        return ActResult.Done;
    }

    ActResult Accuse(int seat, JsonElement payload)
    {
        if (_phase == SpyPhase.Vote) return ActResult.Fail("Зачекай, іде голосування");
        if (_phase != SpyPhase.Play) return ActResult.Fail("Підозру висувають, поки йде раунд");
        if (_accused[seat]) return ActResult.Fail("Ти вже висував підозру цього раунду");
        if (Target(payload) is not { } t || !InMatch(t)) return ActResult.Fail("Не зрозумів, кого підозрюєш");
        if (!_present[t]) return ActResult.Fail("Його вже нема за столом");
        if (t == seat) return ActResult.Fail("На себе не показують");

        var now = Ctx.Clock.UtcNow;
        _accused[seat] = true;
        _voting = true;
        _suspect = t;
        _accuser = seat;
        Array.Clear(_votes);
        _votes[seat] = 1;
        PauseClock(now);
        _endsAt = now.AddMilliseconds(VoteMs);
        // Фазу підозра міняє одразу, а не через тик: годинник раунду має стати тієї ж миті.
        SetPhase(SpyPhase.Vote);
        Speak(SpyGlek.Pick(Ctx.Rng, SpyGlek.Accuse, Name(seat), Name(t)));
        return ActResult.Accept("Підозра висунута — голосуємо");
    }

    ActResult Vote(int seat, JsonElement payload)
    {
        if (_phase != SpyPhase.Vote) return ActResult.Fail("Голосування зараз нема");
        if (seat == _suspect) return ActResult.Fail("Підозрюваний не голосує");
        if (Bool(payload) is not { } yes) return ActResult.Fail("Так чи ні?");
        _votes[seat] = (sbyte)(yes ? 1 : -1);
        _dirty = true;
        return ActResult.Accept(yes ? "Голос: це шпигун" : "Голос: не він");
    }

    ActResult Blame(int seat, JsonElement payload)
    {
        if (_phase != SpyPhase.Final) return ActResult.Fail("Ще не час — фінальне голосування буде, коли вийде час");
        if (Target(payload) is not { } t || !InMatch(t)) return ActResult.Fail("Не зрозумів, на кого показуєш");
        if (!_present[t]) return ActResult.Fail("Його вже нема за столом");
        if (t == seat) return ActResult.Fail("На себе не показують");
        _blame[seat] = t;
        _dirty = true;
        return ActResult.Done;
    }

    ActResult Guess(int seat, JsonElement payload)
    {
        if (seat != _spy) return ActResult.Fail("Ти не шпигун, тобі й так усе відомо");
        if (_phase == SpyPhase.Vote) return ActResult.Fail("Зачекай, іде голосування");
        if (_phase is not (SpyPhase.Play or SpyPhase.Final)) return ActResult.Fail("Зараз не час вгадувати");
        if (Text(payload) is not { } id || Array.FindIndex(_deck, l => l.Id == id) < 0) return ActResult.Fail("Такої локації в колоді нема");

        if (id == _loc!.Id) EndRound(SpyHow.Guessed, null, null, id);
        else EndRound(SpyHow.Misguess, null, null, id);
        return ActResult.Done;
    }

    ActResult Ready(int seat)
    {
        if (_phase != SpyPhase.Reveal) return ActResult.Fail("Готовність потрібна лише на розкритті");
        if (!_ready[seat])
        {
            _ready[seat] = true;
            _dirty = true;
        }
        return ActResult.Done;
    }

    /// <summary>Ціль: <c>{seat: 3}</c> або голе число.</summary>
    static int? Target(JsonElement payload) => payload.ValueKind switch
    {
        JsonValueKind.Number when payload.TryGetInt32(out var n) => n,
        JsonValueKind.Object when payload.TryGetProperty("seat", out var s)
            && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var n) => n,
        _ => null,
    };

    /// <summary>Голос: <c>{yes: true}</c> або голе <c>true/false</c>.</summary>
    static bool? Bool(JsonElement payload) => payload.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Object when payload.TryGetProperty("yes", out var y) && y.ValueKind is JsonValueKind.True or JsonValueKind.False
            => y.GetBoolean(),
        _ => null,
    };

    /// <summary>Локація: <c>{loc: "pasika"}</c> або голий рядок.</summary>
    static string? Text(JsonElement payload) => payload.ValueKind switch
    {
        JsonValueKind.String => payload.GetString(),
        JsonValueKind.Object when payload.TryGetProperty("loc", out var l) && l.ValueKind == JsonValueKind.String => l.GetString(),
        _ => null,
    };

    // =========================================================================================
    // Вихід з-за столу
    // =========================================================================================

    /// <summary>
    /// Устав посеред партії. Техпоразки нема: на десятьох партія не має вмирати від того, що комусь подзвонили.
    /// Шпигун пішов — раунд закрито на користь села; лишилось двоє — партію згорнуто з тим, що є.
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (_phase is SpyPhase.Lobby or SpyPhase.Done) return;
        if (seat < 0 || seat >= MaxSeats || !_inMatch[seat] || !_present[seat]) return;
        _present[seat] = false;
        _presentCount--;
        _dirty = true;
        var now = Ctx.Clock.UtcNow;
        // «Кожен по разу»: хто пішов, так і не побувавши шпигуном, свого раунду вже не отримає.
        if (_each && _spyTimes[seat] == 0 && _rounds > _round) _rounds--;

        var inRound = _phase is SpyPhase.Deal or SpyPhase.Play or SpyPhase.Vote or SpyPhase.Final;
        if (inRound && seat == _spy)
        {
            EndRound(SpyHow.Left, null, null, null);
        }
        else
        {
            if (inRound) MovePointer(seat, now);
            // Чий голос — того й нема. Голоси за того, хто пішов, теж викидаємо: його вже не засудиш.
            _votes[seat] = 0;
            _blame[seat] = -1;
            for (var s = 0; s < MaxSeats; s++) if (_blame[s] == seat) _blame[s] = -1;
            _ready[seat] = false;

            if (_phase == SpyPhase.Vote && seat == _suspect)
            {
                // Підозра обвинувача не згорає: раунд не має карати за чужий вихід.
                _accused[_accuser] = false;
                CloseVote();
                // Лишилось двоє — Fold нижче згорне партію одразу: без «граємо далі», за яким за мить іде «згорнуто».
                if (_presentCount >= MinSeats)
                {
                    if (_clockLeftMs <= 0) EnterFinal(now, SpyGlek.NotUnanimousFinal);
                    else
                    {
                        ResumePlay(now);
                        Speak(SpyGlek.Pick(Ctx.Rng, SpyGlek.VoteCancelled));
                    }
                }
            }
        }

        if (_presentCount < MinSeats) Fold();
    }

    /// <summary>Той, чия черга, пішов — слово переходить до того, хто питав його, або до першого за столом.</summary>
    void MovePointer(int gone, DateTimeOffset now)
    {
        if (_askedBy == gone) _askedBy = null;
        if (_asker != gone) return;
        var next = _askedBy is { } by && _present[by] ? by : -1;
        if (next < 0) foreach (var s in _seats) if (_present[s]) { next = s; break; }
        _asker = next < 0 ? null : next;
        _askedBy = null;
        _askedAt = now;
        _graceShown = false;
    }

    /// <summary>
    /// За столом менше трьох — партію згортаємо одразу. Недограний раунд очок не дає й у хроніку не йде, але його
    /// розкриваємо: партія скінчилась, таємницю берегти вже нема від кого, а людям цікаво, хто ж був шпигуном.
    /// </summary>
    void Fold()
    {
        if (_roundOpen && _loc is { } loc)
        {
            var zero = new Dictionary<int, long>();
            foreach (var s in _seats) if (_dealt[s]) zero[s] = 0;
            _reveal = new SpyRevealView(_spy, loc.Id, RolesMap(), Wire(SpyHow.Fold), zero, null, null, null,
                _phase == SpyPhase.Final ? BlameMap() : null);
        }
        _roundOpen = false;
        CloseVote();
        var played = _history.Count > 0;
        var leaders = played ? Leaders() : [];
        _result = new SpyResultView(leaders, true);
        SetPhase(SpyPhase.Done);
        Speak(SpyGlek.Pick(Ctx.Rng, SpyGlek.Fold));
        if (played) Ctx.Finish(leaders, $"{Info.Title}: за столом лишилось двоє — партію згорнули. {Standings()}", ScoreMap());
        else Ctx.Finish([], $"{Info.Title}: за столом лишилось двоє, партії не вийшло");
    }

    /// <summary>
    /// Хто мовчить у балачці столу. Поки йде раунд, говорять лише ті, хто грає цю партію: глядач локації не знає,
    /// зате суфлером за чужим столом бути може. У лобі, на розкритті й після партії балакають усі.
    /// </summary>
    public override string? TalkBlock(int? seat)
    {
        if (_phase is SpyPhase.Lobby or SpyPhase.Reveal or SpyPhase.Done) return null;
        return seat is { } s && InMatch(s) ? null : "Поки йде партія, глядачі мовчать";
    }

    // =========================================================================================
    // Види й кадр
    // =========================================================================================

    /// <summary>
    /// Дограний стіл каркас відкриває наново, і на вільне місце сідає хтось інший. Таке місце до цієї партії
    /// не має стосунку — ні карткою, ні очками.
    /// </summary>
    bool Newcomer(int x) => _phase is SpyPhase.Lobby or SpyPhase.Done
        && Ctx.NickOf(x) is { } now
        && !string.Equals(now, _nicks[x], StringComparison.Ordinal);

    bool InMatch(int x) => x >= 0 && x < MaxSeats && _inMatch[x] && !Newcomer(x);

    public override object View(int? seat)
    {
        var now = Ctx.Clock.UtcNow;
        var me = seat is { } s && InMatch(s) && _dealt[s] ? s : -1;

        var list = new List<SpyPlayerView>(MaxSeats);
        for (var x = 0; x < MaxSeats; x++)
        {
            if (InMatch(x))
            {
                list.Add(new SpyPlayerView(x, _nicks[x] ?? Ctx.NickOf(x), _present[x] && Ctx.Seated(x), _scores[x],
                    _accused[x], _ready[x]));
            }
            else if (Ctx.Seated(x))
            {
                // До першого старту — усі, хто сів; після партії — новачки, що підсіли до дограного столу.
                list.Add(new SpyPlayerView(x, Ctx.NickOf(x), true, 0, false, false));
            }
        }

        var running = _phase is SpyPhase.Deal or SpyPhase.Play or SpyPhase.Vote or SpyPhase.Final;
        var ends = _phase == SpyPhase.Play && _clockEndsAt is { } ce ? ce : _endsAt;
        return new
        {
            phase = Wire(_phase),
            round = _round,
            of = _rounds,
            endsAt = ends,
            phaseMs = PhaseMs(),
            // Скільки лишилось до кінця фази на момент виду: браузер рахує від нього, а не від свого годинника —
            // годинник телефона буває на кілька секунд «не той», а дуга мусить бити з сервером.
            phaseLeftMs = _phase is SpyPhase.Lobby or SpyPhase.Done ? 0 : Math.Max(0, (long)Math.Ceiling((ends - now).TotalMilliseconds)),
            clock = Clock(now),
            rules = _rules,
            players = list,
            asker = running ? _asker : null,
            askedBy = running ? _askedBy : null,
            askGrace = _phase == SpyPhase.Play && (now - _askedAt).TotalMilliseconds >= AskGraceMs,
            vote = _phase == SpyPhase.Vote && _voting ? VoteView() : null,
            blame = _phase == SpyPhase.Final ? BlameView() : null,
            deck = _deckView,
            me = me >= 0 ? new SpyMeView(me == _spy, me == _spy ? null : _loc?.Id, me == _spy ? null : _roles[me]) : null,
            reveal = _phase is SpyPhase.Reveal or SpyPhase.Done ? _reveal : null,
            history = _history.ToArray(),
            result = _phase == SpyPhase.Done ? _result : null,
            // Лише після партії: тоді на звільнене місце вже може сісти новачок (players показує його), а хроніка й
            // переможці мусять лишитись при іменах тих, хто грав.
            names = _phase == SpyPhase.Done ? _namesView : null,
        };
    }

    int PhaseMs() => _phase switch
    {
        SpyPhase.Deal => DealMs,
        SpyPhase.Play => (int)PlayMs,
        SpyPhase.Vote => VoteMs,
        SpyPhase.Final => FinalMs,
        SpyPhase.Reveal => RevealMs,
        _ => 0,
    };

    SpyClockView Clock(DateTimeOffset now)
    {
        if (_clockEndsAt is { } end)
            return new SpyClockView(end, Math.Max(0, (long)Math.Ceiling((end - now).TotalMilliseconds)), false, PlayMs);
        return new SpyClockView(null, _phase is SpyPhase.Lobby ? PlayMs : _clockLeftMs, true, PlayMs);
    }

    SpyVoteView VoteView()
    {
        var votes = new Dictionary<int, bool>();
        foreach (var s in _seats) if (_present[s] && _votes[s] != 0) votes[s] = _votes[s] > 0;
        return new SpyVoteView(_suspect, _accuser, votes, _presentCount - 1, _endsAt);
    }

    SpyBlameView BlameView()
    {
        var votes = new Dictionary<int, int>();
        foreach (var s in _seats) if (_present[s] && _blame[s] >= 0) votes[s] = _blame[s];
        return new SpyBlameView(votes, _presentCount / 2 + 1);
    }

    /// <summary>Кадр публічний (летить усій кімнаті) і лише на зміну фази — жодного секрету в ньому нема.</summary>
    public override object? Frame()
    {
        var running = _phase is SpyPhase.Deal or SpyPhase.Play or SpyPhase.Vote or SpyPhase.Final;
        var now = Ctx.Clock.UtcNow;
        return new
        {
            phase = Wire(_phase),
            round = _round,
            of = _rounds,
            endsAt = _phase == SpyPhase.Play && _clockEndsAt is { } ce ? ce : _endsAt,
            phaseMs = PhaseMs(),
            clockLeftMs = _clockEndsAt is { } end ? Math.Max(0, (long)Math.Ceiling((end - now).TotalMilliseconds)) : _clockLeftMs,
            paused = _clockEndsAt is null,
            asker = running ? _asker : null,
        };
    }

    // =========================================================================================
    // Дрібниці
    // =========================================================================================

    void Speak(string text)
    {
        if (!string.IsNullOrWhiteSpace(text)) Ctx.Say(text);
    }

    /// <summary>Ім'я місця: збережене на старті, щоб той, хто встав, не став «гравцем 5».</summary>
    string Name(int seat) =>
        (seat >= 0 && seat < MaxSeats ? _nicks[seat] ?? Ctx.NickOf(seat) : null) ?? $"гравець {seat + 1}";

    string TitleOf(string? id) => id is null ? "" : Array.Find(_deck, l => l.Id == id)?.Title ?? id;

    public static string Wire(SpyPhase phase) => phase switch
    {
        SpyPhase.Deal => "deal",
        SpyPhase.Play => "play",
        SpyPhase.Vote => "vote",
        SpyPhase.Final => "final",
        SpyPhase.Reveal => "reveal",
        SpyPhase.Done => "done",
        _ => "lobby",
    };

    public static string Wire(SpyHow how) => how switch
    {
        SpyHow.Caught => "caught",
        SpyHow.Wrong => "wrong",
        SpyHow.Guessed => "guessed",
        SpyHow.Misguess => "misguess",
        SpyHow.Timeout => "timeout",
        SpyHow.Fold => "fold",
        _ => "left",
    };
}
