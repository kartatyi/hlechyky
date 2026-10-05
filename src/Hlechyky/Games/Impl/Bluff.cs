using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Байкарі» — як Fibbage. Питання з пропуском і дивною, але справжньою відповіддю. Кожен вписує правдоподібну
/// брехню; потім усі бачать картки впереміш (брехні гравців, правду й заготовки Глека, коли гравців мало) і шукають
/// правду. Вгадав — <see cref="TruthPts"/>; за кожного, кого надурила твоя брехня, — <see cref="FooledPts"/>; ❤ під
/// твоєю брехнею — <see cref="LikePts"/>. Останнє питання подвійне (крім ❤).
/// <para>
/// Партія живе від тика (раз на пів секунди), як «Скільки?»: усі переходи фаз — у <see cref="Tick"/> від
/// <c>Ctx.Clock</c>, а <see cref="Act"/> лише міняє стан і ставить <c>_dirty</c>. Каркас після Act реалтайм-гри видів не
/// шле, тож гравець бачить підтвердження тостом, а решта — найближчим тиком.
/// </para>
/// <para>
/// Гра <c>Hidden</c>: чужу брехню до складання карток не бачить ніхто (лише галочку «написав»), а на столі до
/// розкриття не видно ні авторів, ні чужого вибору, ні де правда. Перевірка «ти випадково написав правду» —
/// <see cref="BluffText"/>, банк — <see cref="BluffBank"/>, пам'ять бачених — <see cref="BluffSeen"/>.
/// </para>
/// </summary>
public sealed class Bluff : Game
{
    public const int Seats = 8;
    /// <summary>Найдовша брехня, знаків після <see cref="BluffText.Clean"/>.</summary>
    public const int MaxLie = 40;
    /// <summary>Скільки карток намагаємось показати: правда + брехні + заготовки Глека.</summary>
    public const int MinOptions = 5;
    public const int ReadMs = 3000;
    public const int StepPickedMs = 3000;
    public const int StepEmptyMs = 1500;
    public const int StepTruthMs = 4000;
    public const int ScoreMs = 6000;
    public const int TruthPts = 1000;
    public const int FooledPts = 500;
    public const int LikePts = 100;
    /// <summary>Останнє питання: правда й жертви множаться, ❤ — ні.</summary>
    public const int FinalMult = 2;
    public const int DefaultQuestions = 7;

    public const string PhaseRead = "read";
    public const string PhaseWrite = "write";
    public const string PhasePick = "pick";
    public const string PhaseReveal = "reveal";
    public const string PhaseScore = "score";
    public const string PhaseDone = "done";
    /// <summary>Перед питанням: гравець по черзі обирає одну з двох тем (опція «Тему обирає: Гравці по черзі»).</summary>
    public const string PhaseTopic = "topic";

    /// <summary>Скільки чекаємо, поки гравець обере тему; не обрав — бере Глек (першу, свіжішу).</summary>
    public const int TopicMs = 8000;
    public const string TopicHlek = "hlek";
    public const string TopicTurn = "turn";

    /// <summary>Голос Глека: репліку кроку розкриття ще беремо, якщо кліп доспів за стільки після відкриття картки.</summary>
    public const int VoiceGraceMs = 1200;
    /// <summary>Питання Глек ще може зачитати, якщо кліп доспів за стільки від появи питання (далі — лише текстом).</summary>
    public const int QuestionGraceMs = 12_000;
    /// <summary>Пауза після репліки перед наступною карткою.</summary>
    public const int VoicePadMs = 400;
    /// <summary>Стеля кроку розкриття з голосом: довга репліка не тягне шоу.</summary>
    public const int MaxStepMs = 9000;

    /// <summary>Відмова, коли брехня збіглась із правдою. Одна фраза на всі випадки — звертання «ти», не розповідь.</summary>
    public const string Truthy = "Схоже, ти випадково написав правду — вигадай іншу 🙂";
    /// <summary>Відмова слову з двох абеток: «кисeнь» із латинською e на великій картці не відрізнити від чесного.</summary>
    public const string MixedAbc = "Пиши однією абеткою — кирилицею або латиницею 🙂";
    /// <summary>
    /// Відмова літерам інших письмен (черокі, лісу, капітель, розширена латиниця й кирилиця, «математичні»): на великій
    /// картці вони вдають наші: «МАШИНОЮ», де М і А — літери черокі (U+13B7, U+13AA), — та сама правда.
    /// </summary>
    public const string ForeignAbc = "Такі літери на картці вдають наші — пиши кирилицею або латиницею 🙂";
    /// <summary>
    /// Відмова знакові посеред слова («Д│СНЕЙЛЕНД», «МА✕ОРКА», «ЛЬВ◯ВІ»): на великій картці він вдає літеру. Можна лише
    /// дефіс, апостроф і звичайні розділові знаки (<see cref="BluffText.MarkInWord"/>).
    /// </summary>
    public const string InWordMark = "Такий знак посеред слова вдає літеру — пиши літерами 🙂";
    /// <summary>Повторний 🎲, коли нової заготовки Глек не дасть: решту він береже для столу й для тих, хто ще без брехні.</summary>
    public const string DiceHeld = "Інших Глек не дасть — решту береже для столу. Лиши цю або пиши сам 🙂";
    /// <summary>Відмова, коли спроби на це питання скінчились: однакова на будь-який текст, тож правди не видає.</summary>
    public const string TooManyTries = "Годі перебирати 🙂 Лиши, що є, або тисни 🎲";

    /// <summary>
    /// Скільки разів за питання сервер може сказати «ти випадково написав правду». Кожна така відмова — підказка, де
    /// правда, тож після п'ятої текстові брехні на це питання більше не приймаються (🎲 лишається).
    /// </summary>
    public const int MaxTruthy = 5;
    /// <summary>Скільки текстових брехень за питання можна надіслати (переписувати — можна, перебирати скриптом — ні).</summary>
    public const int MaxLieTries = 20;
    /// <summary>
    /// Скільки різних заготовок Глек показує одному гравцеві через 🎲 за питання. Бачену заготовку Глек уже не кладе на
    /// стіл як «чужу» картку (ти її знаєш), тож решту бережемо для столу й для інших.
    /// </summary>
    public const int DiceSeen = 2;
    /// <summary>
    /// Скільки карток 🎲 не дасть з'їсти: бачена через 🎲 заготовка на стіл уже не лягає, і за столом на двох три бачені
    /// лишали б три картки — кожен обирав би з двох, тобто 50 на 50. З чотирма кожен обирає щонайменше з трьох.
    /// </summary>
    public const int MinTable = 4;

    static readonly int[] QuestionChoices = [5, 7, 10];

    public override GameInfo Info { get; } = new(
        "bluff", "Байкарі", "байкарів", GameGroup.Party, 2, Seats,
        TickMs: 500, Start: StartMode.ByHost, Hidden: true, Private: false, Persistent: false, Rated: false, Score: ScoreOrder.None,
        Options:
        [
            new GameOption("questions", "Питань", [.. QuestionChoices.Select(n => (Str(n), Str(n)))], Str(DefaultQuestions)),
            new GameOption("pace", "Темп", BluffPace.All, BluffPace.Normal),
            new GameOption("cat", "Теми", BluffCats.All, BluffCats.Any, Multi: true),
            new GameOption("topic", "Тему обирає", [(TopicHlek, "Глек"), (TopicTurn, "Гравці по черзі")], TopicHlek),
            new GameOption("voice", "Голос Глека", [("ostap", "Остап"), ("polina", "Поліна"), ("none", "Без голосу")], "ostap"),
        ],
        Hint: "Питання з пропуском і дивна правда. Кожен вписує свою брехню, потім усі шукають правду серед брехень. Надурив друга — очки тобі");

    static string Str(int n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>Картка на столі: брехня гравців (By — співавтори), правда або заготовка Глека.</summary>
    sealed class Card
    {
        public string Text = "";
        public readonly List<int> By = [];
        public bool Truth;
        public bool Decoy;
        /// <summary>Брехня гравців, але текст — Глека (усі співавтори взяли її через 🎲).</summary>
        public bool Auto;
        /// <summary>Уже відкрита на розкритті.</summary>
        public bool Open;
        /// <summary>Хто обрав (заповнюється на початку розкриття, у порядку місць).</summary>
        public readonly List<int> Picks = [];
        public readonly HashSet<int> LikedBy = [];
    }

    /// <summary>Зігране питання (правду вже відкрито) — для підсумку й найкращої брехні партії.</summary>
    sealed record Played(BluffQuestion Question, List<Card> Cards);

    // ---- налаштування столу ----
    int _count = DefaultQuestions;
    int _writeMs = BluffPace.Seconds(BluffPace.Normal).Write * 1000;
    int _pickMs = BluffPace.Seconds(BluffPace.Normal).Pick * 1000;
    IReadOnlySet<string>? _cats;
    bool _topicTurn;
    string _voiceName = "ostap";

    // ---- партія ----
    readonly List<BluffQuestion> _asked = [];
    readonly List<Played> _played = [];
    readonly string?[] _nicks = new string?[Seats];
    readonly bool[] _present = new bool[Seats];
    readonly long[] _scores = new long[Seats];
    /// <summary>Скільки разів місце вгадало правду за партію (ачівка «Нюх на правду»).</summary>
    readonly int[] _hits = new int[Seats];
    /// <summary>«Хитрого лиса» вже попросили цій партії.</summary>
    readonly bool[] _fox = new bool[Seats];

    // ---- поточне питання ----
    readonly string?[] _lie = new string?[Seats];
    readonly bool[] _auto = new bool[Seats];
    /// <summary>Які заготовки (біти за номером у банку) місце вже бачило через 🎲.</summary>
    readonly int[] _dice = new int[Seats];
    /// <summary>Текстових брехень на це питання і з них — «ти випадково написав правду».</summary>
    readonly int[] _tries = new int[Seats];
    readonly int[] _truthy = new int[Seats];
    readonly int[] _pick = new int[Seats];
    readonly long[] _delta = new long[Seats];
    /// <summary>Частина <see cref="_delta"/> за вгадану правду — щоб клієнт не відтворював очки сам.</summary>
    readonly long[] _truthDelta = new long[Seats];
    readonly long[] _likeDelta = new long[Seats];
    readonly int[] _victims = new int[Seats];
    List<Card> _cards = [];
    readonly List<int> _order = [];
    readonly List<int> _revealed = [];
    bool _truthOpen;
    string? _quip;

    int _q;
    string _phase = PhaseRead;
    DateTimeOffset _endsAt;
    int _phaseMs;
    /// <summary>Хтось щось зробив — найближчий тик розішле свіжі види.</summary>
    bool _dirty;
    object? _result;

    BluffSeen? _seen;

    // ---- тему обирає гравець ----
    /// <summary>Скільки питань у партії (у режимі тем питання беруться по одному, тож <see cref="_asked"/> росте).</summary>
    int _total;
    /// <summary>Решта питань партії від найсвіжішого — з них беремо питання обраної теми.</summary>
    readonly List<BluffQuestion> _pool = [];
    /// <summary>Хто обирав останнім (на старті — випадкове місце): черга йде від нього по колу.</summary>
    int _turnFrom;
    string[] _topics = [];
    int _chooser = -1;
    string? _topicPick;
    /// <summary>Хто обрав тему кожного питання (−1 — Глек).</summary>
    readonly List<int> _chosenBy = [];

    // ---- голос Глека (як у Додепах: гра ніколи не чекає на озвучку) ----
    sealed record Speech(int Id, string Text, string Url, double Seconds);
    IDotepyVoice _voice = DotepyNoVoice.Instance;
    Speech? _say;
    int _sayId;
    /// <summary>Репліка, яку Глек скаже, щойно кліп доспіє (до <see cref="_wantUntil"/>), інакше — мовчки текстом.</summary>
    string? _want;
    DateTimeOffset _wantUntil;
    bool _wantStretch;
    DateTimeOffset _stepFrom;
    /// <summary>Вердикти кроків розкриття (у порядку <see cref="_order"/>), null — крок без слів.</summary>
    string?[] _verdicts = [];

    // ---- лобі: скільки свіжих ----
    (string Sig, DateTimeOffset At, int Fresh, int Of)? _fresh;

    public Bluff() => Array.Fill(_pick, -1);

    /// <summary>Місця — числами: на столі їх до восьми, у чіп має влізти нік.</summary>
    public override string SeatName(int seat) => (seat + 1).ToString(CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------------------------------
    // стіл і старт
    // ---------------------------------------------------------------------------------------

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue("questions", out var q) && int.TryParse(q, CultureInfo.InvariantCulture, out var n)
            && QuestionChoices.Contains(n)) _count = n;
        if (options.TryGetValue("pace", out var p))
        {
            var (w, k) = BluffPace.Seconds(p);
            _writeMs = w * 1000;
            _pickMs = k * 1000;
        }
        if (options.TryGetValue("cat", out var c)) _cats = BluffCats.Parse(c);
        _topicTurn = options.GetValueOrDefault("topic") == TopicTurn;
        _voiceName = options.GetValueOrDefault("voice") is "polina" or "none" ? options["voice"] : "ostap";
    }

    /// <summary>Банк цього столу: справжній або підкладений тестом (<see cref="BluffBankSource"/>).</summary>
    IReadOnlyList<BluffQuestion> Bank => _bank ??= Ctx.Services.GetService<BluffBankSource>()?.Questions ?? BluffBank.All;
    IReadOnlyList<BluffQuestion>? _bank;

    public override string? CanStart() =>
        Bank.Any(q => BluffCats.Fits(q, _cats)) ? null : "У цих темах ще нема питань — обери інші теми";

    /// <summary>
    /// Пам'ять бачених. Db беремо не в конструкторі: гру створює реєстр без параметрів. Без бази пам'ять мовчить.
    /// </summary>
    BluffSeen Seen => _seen ??= new BluffSeen(Ctx.Services.GetService<Db>());

    public override void Start()
    {
        for (var s = 0; s < Seats; s++)
        {
            _present[s] = Ctx.Seated(s);
            // Знімок ніків: той, хто вийде посеред партії, у розкритті й підсумку лишається з іменем.
            _nicks[s] = _present[s] ? Ctx.NickOf(s) : null;
        }
        Array.Clear(_scores);
        Array.Clear(_hits);
        Array.Clear(_fox);
        _played.Clear();
        _result = null;
        _asked.Clear();
        _pool.Clear();
        _chosenBy.Clear();
        _fresh = null;
        if (_topicTurn)
        {
            // Тему обирають по черзі: уся колода від найсвіжішого, питання — по одному з обраної теми.
            _pool.AddRange(Pick(all: true));
            _total = Math.Min(_count, _pool.Count);
            _turnFrom = Ctx.Rng.Next(Seats);
        }
        else
        {
            _asked.AddRange(Pick(all: false));
            _total = _asked.Count;
        }
        _q = 0;
        _dirty = false;
        var now = Ctx.Clock.UtcNow;
        ClearQuestion();
        StartVoice();
        if (_total == 0)
        {
            _phase = PhaseDone;
            _endsAt = now;
            _phaseMs = 0;
            Ctx.Finish([], $"{Info.Title}: у цих темах не знайшлось питань, партії не буде");
            return;
        }
        NextQuestion(now);
    }

    /// <summary>
    /// Питання партії: банк за темами, тасування Фішера — Єйтса на <c>Ctx.Rng</c>, потім найсвіжіші для цього столу
    /// (спершу ніким не бачені, далі — бачені найдавніше). Пам'ять — з того, що підтяглось фоном у лобі, і з позначок
    /// самого столу: Start кличуть під замком кімнати, тож у базу тут не ходимо.
    /// </summary>
    List<BluffQuestion> Pick(bool all)
    {
        var pool = new List<BluffQuestion>();
        foreach (var q in Bank) if (BluffCats.Fits(q, _cats)) pool.Add(q);
        for (var i = pool.Count - 1; i > 0; i--)
        {
            var j = Ctx.Rng.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        return BluffSeen.Freshest(pool, q => q.Key, Seen.LastSeen(PresentKeys()), all ? pool.Count : _count);
    }

    List<string> PresentKeys()
    {
        var keys = new List<string>(Seats);
        for (var s = 0; s < Seats; s++)
            if (_present[s] && _nicks[s] is { } nick && BluffSeen.NickKey(nick) is var key && !keys.Contains(key)) keys.Add(key);
        return keys;
    }

    /// <summary>
    /// Лобі (чи дограний стіл перед «Ще раз»): хто зараз сидить — тих і пам'ять підтягуємо фоном, щоб Start узяв готове.
    /// Ключі ніків, уже прочитані чи в дорозі, повторно не читаємо.
    /// </summary>
    void PrefetchSeated()
    {
        List<string>? keys = null;
        for (var s = 0; s < Seats; s++)
            if (Ctx.Seated(s) && Ctx.NickOf(s) is { Length: > 0 } nick) (keys ??= []).Add(BluffSeen.NickKey(nick));
        if (keys is not null) Seen.Prefetch(keys);
    }

    BluffQuestion? Current => _q >= 0 && _q < _asked.Count ? _asked[_q] : null;
    bool Final => _q == _total - 1;

    // ---------------------------------------------------------------------------------------
    // фази (усі переходи — лише з тика)
    // ---------------------------------------------------------------------------------------

    void SetTimer(DateTimeOffset now, int ms)
    {
        _stepFrom = now;
        _endsAt = now.AddMilliseconds(ms);
        _phaseMs = ms;
    }

    void ClearQuestion()
    {
        Array.Clear(_lie);
        Array.Clear(_auto);
        Array.Clear(_dice);
        Array.Clear(_tries);
        Array.Clear(_truthy);
        Array.Fill(_pick, -1);
        Array.Clear(_delta);
        Array.Clear(_truthDelta);
        Array.Clear(_likeDelta);
        Array.Clear(_victims);
        _cards = [];
        _order.Clear();
        _revealed.Clear();
        _truthOpen = false;
        _quip = null;
    }

    /// <summary>Наступне питання: одразу читаємо або спершу гравець обирає тему.</summary>
    void NextQuestion(DateTimeOffset now)
    {
        if (_topicTurn) BeginTopic(now);
        else BeginRead(now);
    }

    void BeginRead(DateTimeOffset now)
    {
        ClearQuestion();
        _phase = PhaseRead;
        SetTimer(now, ReadMs);
        // «Бачив» — з тієї миті, коли питання з'явилось на екрані; до недограних питань пам'ять не доходить.
        Seen.Mark(PresentKeys(), _asked[_q], now);
        Want(BluffLines.Question(_asked[_q].Q, Final), now, QuestionGraceMs, stretch: false);
    }

    /// <summary>
    /// Тему обирає гравець: дві різні теми з найсвіжіших питань, що лишились. Обирає наступний за столом по черзі;
    /// лишилась одна тема (чи нікому обирати) — питання одразу.
    /// </summary>
    void BeginTopic(DateTimeOffset now)
    {
        ClearQuestion();
        string? a = null, b = null;
        foreach (var q in _pool)
        {
            if (a is null) a = q.Cat;
            else if (q.Cat != a) { b = q.Cat; break; }
        }
        var chooser = NextChooser();
        if (b is null || chooser < 0)
        {
            TakeTopic(a!, -1);
            BeginRead(now);
            return;
        }
        _topics = [a!, b];
        _chooser = chooser;
        _turnFrom = chooser;
        _topicPick = null;
        _phase = PhaseTopic;
        SetTimer(now, TopicMs);
    }

    /// <summary>Черга обирати: наступний за столом після того, хто обирав востаннє.</summary>
    int NextChooser()
    {
        for (var k = 1; k <= Seats; k++)
        {
            var s = (_turnFrom + k) % Seats;
            if (_present[s]) return s;
        }
        return -1;
    }

    /// <summary>Найсвіжіше питання обраної теми стає наступним питанням партії.</summary>
    void TakeTopic(string cat, int by)
    {
        var i = _pool.FindIndex(q => q.Cat == cat);
        if (i < 0) i = 0;
        _asked.Add(_pool[i]);
        _pool.RemoveAt(i);
        _chosenBy.Add(by);
        _topics = [];
        _chooser = -1;
        _topicPick = null;
        // Кліп питання — терміново: читати його за мить.
        if (VoiceOn) Prepare([BluffLines.Question(_asked[^1].Q, _asked.Count == _total)], urgent: true);
    }

    void BeginWrite(DateTimeOffset now)
    {
        _phase = PhaseWrite;
        SetTimer(now, _writeMs);
    }

    void BeginPick(DateTimeOffset now)
    {
        BuildOptions();
        _phase = PhasePick;
        SetTimer(now, _pickMs);
    }

    /// <summary>
    /// Картки питання, детерміновано: брехні присутніх у порядку місць (однакові — одна спільна картка на всіх
    /// співавторів), правда, заготовки Глека до <see cref="MinOptions"/> (крім тих, що вже є на столі, і тих, що хтось
    /// бачив через 🎲 — він би знав, що це Глек), і тасування на <c>Ctx.Rng</c> — один порядок для всіх, щоб за
    /// столом можна було сказати «третя — точно правда».
    /// </summary>
    void BuildOptions()
    {
        var q = _asked[_q];
        var cards = new List<Card>(Seats + 4);
        for (var s = 0; s < Seats; s++)
        {
            if (!_present[s] || _lie[s] is not { } lie) continue;
            var same = cards.Find(c => BluffText.LooksSame(c.Text, lie));
            if (same is not null)
            {
                same.By.Add(s);
                same.Auto &= _auto[s];
            }
            else
            {
                var card = new Card { Text = lie, Auto = _auto[s] };
                card.By.Add(s);
                cards.Add(card);
            }
        }
        cards.Add(new Card { Text = q.Answer, Truth = true });
        var seen = 0;
        for (var s = 0; s < Seats; s++) seen |= _dice[s];
        for (var k = 0; k < q.Decoys.Count; k++)
        {
            if (cards.Count >= MinOptions) break;
            var d = q.Decoys[k];
            // Хтось бачив її через 🎲 (і, може, передумав) — для нього це вже не загадка.
            if ((seen & (1 << k)) != 0) continue;
            // Гравець написав те саме — його версія важливіша.
            if (cards.Exists(c => BluffText.LooksSame(c.Text, d))) continue;
            cards.Add(new Card { Text = d, Decoy = true });
        }
        for (var i = cards.Count - 1; i > 0; i--)
        {
            var j = Ctx.Rng.Next(i + 1);
            (cards[i], cards[j]) = (cards[j], cards[i]);
        }
        _cards = cards;
    }

    /// <summary>
    /// Розкриття: брехні за зростанням кількості голосів (рівні — за номером картки), правда — завжди остання.
    /// Одразу відкриваємо першу.
    /// </summary>
    void BeginReveal(DateTimeOffset now)
    {
        for (var s = 0; s < Seats; s++)
            if (_pick[s] >= 0 && _pick[s] < _cards.Count) _cards[_pick[s]].Picks.Add(s);
        _order.Clear();
        var truth = -1;
        for (var i = 0; i < _cards.Count; i++)
            if (_cards[i].Truth) truth = i;
            else _order.Add(i);
        _order.Sort((a, b) => _cards[a].Picks.Count != _cards[b].Picks.Count
            ? _cards[a].Picks.Count.CompareTo(_cards[b].Picks.Count) : a.CompareTo(b));
        _order.Add(truth);
        _revealed.Clear();
        _phase = PhaseReveal;
        PrepareVerdicts();
        OpenNext(now);
    }

    /// <summary>
    /// Вердикти кроків розкриття — з ніками: «На гачку — Петро і Ганна! Автор брехні — Оля». Карток Глек не читає
    /// (рішення користувача), лише хто купився, чия брехня і де правда. Картка, яку не обрав ніхто, — без слів.
    /// Усі — одразу в чергу озвучки, по порядку: поки відкриваються перші, решта доспіває.
    /// </summary>
    void PrepareVerdicts()
    {
        _verdicts = new string?[_order.Count];
        if (!VoiceOn) return;
        var present = PresentSeats().Count;
        for (var k = 0; k < _order.Count; k++)
        {
            var c = _cards[_order[k]];
            _verdicts[k] = c.Truth
                ? BluffLines.Truth(c.Text, Names(c.Picks), c.Picks.Count, present)
                : c.Picks.Count == 0 ? null
                : c.Decoy ? BluffLines.Decoy(Names(c.Picks))
                : BluffLines.Lie(Names(c.Picks), Names(c.By), c.By.Count);
        }
        var lines = new List<string>(_verdicts.Length);
        foreach (var v in _verdicts) if (v is not null) lines.Add(v);
        if (lines.Count > 0) Prepare(lines, urgent: true);
    }

    /// <summary>Відкрити наступну картку й нарахувати за неї: правда — тим, хто її обрав; брехня гравців — авторам.</summary>
    void OpenNext(DateTimeOffset now)
    {
        var step = _revealed.Count;
        var i = _order[step];
        var card = _cards[i];
        card.Open = true;
        _revealed.Add(i);
        var verdict = step < _verdicts.Length ? _verdicts[step] : null;
        var mult = Final ? FinalMult : 1;
        if (card.Truth)
        {
            var found = 0;
            foreach (var p in card.Picks)
            {
                if (!Credit(p, TruthPts * mult)) continue;
                _truthDelta[p] += TruthPts * mult;
                _hits[p]++;
                found++;
            }
            _truthOpen = true;
            // Слово Глека — під те, що сталось: ніхто не вгадав, усі вгадали чи частина.
            var pool = found == 0 ? QuipsNobody : found >= PresentSeats().Count ? QuipsAll : QuipsSome;
            _quip = pool[Ctx.Rng.Next(pool.Length)];
            _played.Add(new Played(_asked[_q], _cards));
            SetTimer(now, StepTruthMs);
            Want(verdict, now, VoiceGraceMs, stretch: true);
            return;
        }
        if (!card.Decoy && card.Picks.Count > 0)
            foreach (var a in card.By)
            {
                Credit(a, (long)FooledPts * mult * card.Picks.Count);
                _victims[a] += card.Picks.Count;
                // «Хитрий лис» — за свою брехню, а не за Глекову з 🎲.
                if (card.Picks.Count >= 2 && _present[a] && !_fox[a] && !_auto[a])
                {
                    _fox[a] = true;
                    Ctx.Award(a, 0, "ach:bluff-fox");
                }
            }
        SetTimer(now, card.Picks.Count > 0 ? StepPickedMs : StepEmptyMs);
        Want(verdict, now, VoiceGraceMs, stretch: true);
    }

    void BeginScore(DateTimeOffset now)
    {
        _phase = PhaseScore;
        SetTimer(now, ScoreMs);
    }

    /// <summary>Очки за питання — лише тим, хто досі за столом (той, хто пішов, не виграє й не набирає).</summary>
    bool Credit(int seat, long points)
    {
        if (!_present[seat]) return false;
        _scores[seat] += points;
        _delta[seat] += points;
        return true;
    }

    public override TickResult Tick()
    {
        if (_phase == PhaseDone) return TickResult.None;
        var now = Ctx.Clock.UtcNow;
        // Кліп доспів — Глек каже (і крок розкриття подовжується на репліку); не доспів вчасно — мовчки текстом.
        if (_want is not null) TrySay(now);

        // «Готово» в усіх — таймер не чекаємо; сам перехід — нижче, в одному місці з рештою.
        if (_phase == PhaseWrite && AllWrote()) _endsAt = now;
        else if (_phase == PhasePick && AllPicked()) _endsAt = now;
        else if (_phase == PhaseTopic && _topicPick is not null) _endsAt = now;

        if (now < _endsAt)
        {
            if (!_dirty) return TickResult.None;
            _dirty = false;
            return ViewOnly;
        }

        _dirty = false;
        switch (_phase)
        {
            case PhaseTopic:
                TakeTopic(_topicPick ?? _topics[0], _topicPick is null ? -1 : _chooser);
                BeginRead(now);
                break;
            case PhaseRead:
                BeginWrite(now);
                break;
            case PhaseWrite:
                BeginPick(now);
                break;
            case PhasePick:
                BeginReveal(now);
                break;
            case PhaseReveal:
                if (_revealed.Count < _order.Count) OpenNext(now);
                else if (Final) Done();
                else BeginScore(now);
                break;
            case PhaseScore:
                _q++;
                NextQuestion(now);
                break;
        }
        return ViewOnly;
    }

    /// <summary>
    /// Лише види, без кадру: модуль кадрів не читає (у нього нема frame-хука), а відлік веде сам від <c>endsAt</c>.
    /// Кадр на кожну подію був би ~200 байт і чотири масиви в пам'яті — задарма.
    /// </summary>
    static readonly TickResult ViewOnly = new(false, true);

    bool AllWrote()
    {
        var any = false;
        for (var s = 0; s < Seats; s++)
        {
            if (!_present[s]) continue;
            if (_lie[s] is null) return false;
            any = true;
        }
        return any;
    }

    bool AllPicked()
    {
        var any = false;
        for (var s = 0; s < Seats; s++)
        {
            if (!_present[s]) continue;
            if (_pick[s] < 0) return false;
            any = true;
        }
        return any;
    }

    // ---------------------------------------------------------------------------------------
    // ходи
    // ---------------------------------------------------------------------------------------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action is not ("lie" or "pick" or "like" or "topic")) return ActResult.Fail("Тут так не ходять");
        if (_phase == PhaseDone) return ActResult.Fail("Партію зіграно, тисни «Ану ще раз»");
        if (seat is < 0 or >= Seats) return ActResult.Fail("Ти вже не за столом");
        return action switch
        {
            "lie" => Lie(seat, payload),
            "pick" => PickCard(seat, payload),
            "topic" => Topic(seat, payload),
            _ => Like(seat, payload),
        };
    }

    ActResult Lie(int seat, JsonElement payload)
    {
        if (_phase != PhaseWrite) return ActResult.Fail("Зараз не час брехати");
        if (!_present[seat]) return ActResult.Fail("Ти вже не за столом");
        if (Ctx.Clock.UtcNow >= _endsAt) return ActResult.Fail("Час вийшов");
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("auto", out var auto) && auto.ValueKind == JsonValueKind.True)
            return AutoLie(seat);

        // Перебір: кожна відмова «це правда» — підказка, тож спроби на питання лічимо, а коли вони скінчились,
        // відповідь однакова на будь-який текст. Реалтайм-ввід (Rooms.Input) кличе той самий Act — лічиться так само.
        if (_tries[seat] >= MaxLieTries || _truthy[seat] >= MaxTruthy) return ActResult.Fail(TooManyTries);
        _tries[seat]++;
        var text = BluffText.Clean(payload.ValueKind switch
        {
            JsonValueKind.String => payload.GetString(),
            JsonValueKind.Object when payload.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String => t.GetString(),
            _ => null,
        });
        if (Refuse(text, _asked[_q]) is { } why)
        {
            if (why == Truthy) _truthy[seat]++;
            return ActResult.Fail(why);
        }

        _lie[seat] = text;
        _auto[seat] = false;
        _dirty = true;
        return ActResult.Accept($"Записано: «{text}»");
    }

    /// <summary>
    /// Чому сервер не приймає цей (уже <see cref="BluffText.Clean"/>) текст як брехню на питання — або <c>null</c>, якщо
    /// приймає: порожній, задовгий, літери інших письмен (<see cref="ForeignAbc"/>), знак посеред слова
    /// (<see cref="InWordMark"/>), слово з двох абеток (<see cref="MixedAbc"/>), правда (<see cref="Truthy"/>). Одна
    /// перевірка й для <see cref="Act"/>, і для тестів на всьому банку.
    /// </summary>
    public static string? Refuse(string text, BluffQuestion question)
    {
        if (text.Length == 0) return "Порожня брехня нікого не надурить";
        if (text.Length > MaxLie) return "Коротше — до 40 знаків";
        if (BluffText.ForeignLetters(text)) return ForeignAbc;
        if (BluffText.MarkInWord(text)) return InWordMark;
        if (BluffText.MixedScripts(text)) return MixedAbc;
        if (BluffText.LooksTrue(text, question)) return Truthy;
        return null;
    }

    /// <summary>
    /// «🎲 Хай Глек збреше»: заготовка з банку стає твоєю брехнею з усіма очками. Наступний натиск — наступна заготовка,
    /// але нових не більше <see cref="DiceSeen"/> на гравця (далі Глек крутить уже бачені): кожна бачена заготовка
    /// випадає зі столу, бо ти знав би, що це Глек. Тому нову Глек показує, лише коли стіл від цього не схудне нижче
    /// <see cref="MinTable"/> (<see cref="CanShowNew"/>). Уже взяту іншим, бачену іншим через 🎲 чи написану кимось слово
    /// в слово Глек не дає.
    /// </summary>
    ActResult AutoLie(int seat)
    {
        var decoys = _asked[_q].Decoys;
        var start = 0;
        if (_auto[seat] && _lie[seat] is { } mine)
            for (var k = 0; k < decoys.Count; k++)
                if (decoys[k] == mine) { start = k + 1; break; }
        var others = 0;
        for (var s = 0; s < Seats; s++) if (s != seat) others |= _dice[s];
        var fresh = CanShowNew(seat, decoys.Count);
        for (var n = 0; n < decoys.Count; n++)
        {
            var k = (start + n) % decoys.Count;
            var bit = 1 << k;
            if ((others & bit) != 0 || TakenByOther(seat, decoys[k])) continue;
            if ((_dice[seat] & bit) == 0 && !fresh) continue;
            var d = decoys[k];
            // Обійшли коло й прийшли до тієї самої: нової Глек не дасть, а «підказав те саме» звучало б як збій.
            if (_auto[seat] && _lie[seat] == d) return ActResult.Fail(DiceHeld);
            _dice[seat] |= bit;
            _lie[seat] = d;
            _auto[seat] = true;
            _dirty = true;
            // Останній за столом: фаза піде далі на найближчому тику, тож «можеш переписати» було б неправдою.
            return ActResult.Accept(AllWrote() ? $"Глек збрехав за тебе: «{d}»" : $"Глек підказав: «{d}». Можеш переписати");
        }
        return ActResult.Fail("Глек уже все вибрехав — пиши сам 🙂");
    }

    static int BitCount(int mask) => System.Numerics.BitOperations.PopCount((uint)mask);

    /// <summary>
    /// Чи можна показати місцю ще не бачену ним заготовку. Кожна показана вже не ляже на стіл як Глекова, тож Глек
    /// береже: на стіл — стільки, щоб разом із брехнями всіх присутніх і правдою карток було щонайменше
    /// <see cref="MinTable"/> (на двох — одну заготовку, від трьох гравців не треба жодної), а поки столу бракує, ще й
    /// першу 🎲 для кожного, хто досі без брехні. Перша заготовка місця йде без цієї черги.
    /// </summary>
    bool CanShowNew(int seat, int decoys)
    {
        var mine = BitCount(_dice[seat]);
        if (mine >= DiceSeen) return false;
        int seen = 0, present = 0, waiting = 0;
        for (var s = 0; s < Seats; s++)
        {
            seen |= _dice[s];
            if (!_present[s]) continue;
            present++;
            if (s != seat && _lie[s] is null && _dice[s] == 0) waiting++;
        }
        var unseen = 0;
        for (var k = 0; k < decoys; k++) if ((seen & (1 << k)) == 0) unseen++;
        var need = Math.Max(0, MinTable - 1 - present);
        return unseen - 1 >= need + (mine > 0 && need > 0 ? waiting : 0);
    }

    bool TakenByOther(int seat, string decoy)
    {
        for (var s = 0; s < Seats; s++)
            if (s != seat && _present[s] && _lie[s] is { } other && BluffText.LooksSame(other, decoy)) return true;
        return false;
    }

    ActResult PickCard(int seat, JsonElement payload)
    {
        if (_phase != PhasePick) return ActResult.Fail("Зараз не час обирати");
        if (!_present[seat]) return ActResult.Fail("Ти вже не за столом");
        if (Ctx.Clock.UtcNow >= _endsAt) return ActResult.Fail("Час вийшов");
        if (Index(payload) is not { } i || i < 0 || i >= _cards.Count) return ActResult.Fail("Нема такої картки");
        if (_cards[i].By.Contains(seat)) return ActResult.Fail("Свою брехню обирати не можна 🙂");
        _pick[seat] = i;
        _dirty = true;
        return ActResult.Done;
    }

    /// <summary>Гравець, чия черга, обирає тему наступного питання: <c>"food"</c> чи <c>{ k: "food" }</c>.</summary>
    ActResult Topic(int seat, JsonElement payload)
    {
        if (_phase != PhaseTopic) return ActResult.Fail("Зараз тему не обирають");
        if (seat != _chooser) return ActResult.Fail($"Тему зараз обирає {Nick(_chooser)}");
        var key = payload.ValueKind switch
        {
            JsonValueKind.String => payload.GetString(),
            JsonValueKind.Object when payload.TryGetProperty("k", out var k) && k.ValueKind == JsonValueKind.String => k.GetString(),
            _ => null,
        };
        if (key is null || Array.IndexOf(_topics, key) < 0) return ActResult.Fail("Нема такої теми");
        _topicPick = key;
        _dirty = true;
        return ActResult.Done;
    }

    ActResult Like(int seat, JsonElement payload)
    {
        if (_phase is not (PhaseReveal or PhaseScore)) return ActResult.Fail("❤ ставлять на розкритті");
        if (!_present[seat]) return ActResult.Fail("Ти вже не за столом");
        if (Index(payload) is not { } i || i < 0 || i >= _cards.Count) return ActResult.Fail("Нема такої картки");
        var card = _cards[i];
        if (!card.Open) return ActResult.Fail("Цього ще не показували");
        if (card.Truth || card.Decoy) return ActResult.Fail("❤ ставлять брехням гравців");
        if (card.By.Contains(seat)) return ActResult.Fail("Собі ❤ не ставлять 🙂");
        var sign = card.LikedBy.Remove(seat) ? -1 : 1;
        if (sign > 0) card.LikedBy.Add(seat);
        foreach (var a in card.By)
        {
            if (!_present[a]) continue;
            _scores[a] += sign * LikePts;
            _likeDelta[a] += sign * LikePts;
        }
        _dirty = true;
        return ActResult.Done;
    }

    /// <summary>Номер картки терпимо: <c>{ i: 3 }</c>, <c>3</c> чи <c>"3"</c>.</summary>
    static int? Index(JsonElement payload) => payload.ValueKind switch
    {
        JsonValueKind.Number when payload.TryGetInt32(out var n) => n,
        JsonValueKind.String when int.TryParse(payload.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
        JsonValueKind.Object when payload.TryGetProperty("i", out var v) && v.ValueKind != JsonValueKind.Object => Index(v),
        _ => null,
    };

    // ---------------------------------------------------------------------------------------
    // голос Глека
    // ---------------------------------------------------------------------------------------

    bool VoiceOn => _voice.Enabled;

    /// <summary>
    /// На старті: голос (якщо не «без голосу» і edge-tts є), у чергу озвучки — питання партії (перше терміново) і
    /// «Перемагає {нік}» для кожного за столом: на кінці партії чекати нема коли.
    /// </summary>
    void StartVoice()
    {
        _say = null;
        _want = null;
        _verdicts = [];
        _voice = DotepyNoVoice.Instance;
        if (_voiceName == "none") return;
        try
        {
            if (Ctx.Services.GetService<IDotepyVoice>() is { } v && v.Enabled) _voice = v;
        }
        catch (Exception) { /* голос — чужий код; не вийшло — граємо текстом */ }
        if (!VoiceOn) return;
        if (_asked.Count > 0) Prepare([BluffLines.Question(_asked[0].Q, _total == 1)], urgent: true);
        var lines = new List<string>();
        for (var i = 1; i < _asked.Count; i++) lines.Add(BluffLines.Question(_asked[i].Q, i == _total - 1));
        for (var s = 0; s < Seats; s++) if (_present[s] && _nicks[s] is { } nick) lines.Add(BluffLines.Win(nick));
        lines.Add(BluffLines.Draw);
        Prepare(lines);
    }

    // Голос — чужий код (черга, диск). Його збій не має валити партію: тоді гра просто йде текстом.
    void Prepare(IEnumerable<string> texts, bool urgent = false)
    {
        try { _voice.Prepare(_voiceName, texts, urgent); }
        catch (Exception) { /* без голосу */ }
    }

    DotepyClip? Clip(string text)
    {
        try { return _voice.Ready(_voiceName, text); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Глек скаже це, щойно кліп доспіє, але не пізніше ніж за <paramref name="graceMs"/>: запізнілий вердикт до
    /// іншої картки гірший за тишу. <paramref name="stretch"/> — крок розкриття подовжується на час репліки (до
    /// <see cref="MaxStepMs"/>). Кліпа ще нема — гра йде далі, як ішла: вона ніколи не чекає на озвучку.
    /// </summary>
    void Want(string? line, DateTimeOffset now, int graceMs, bool stretch)
    {
        _want = null;
        if (line is null || !VoiceOn) return;
        _want = line;
        _wantUntil = now.AddMilliseconds(graceMs);
        _wantStretch = stretch;
        TrySay(now);
    }

    void TrySay(DateTimeOffset now)
    {
        if (_want is not { } line) return;
        if (Clip(line) is not { } clip)
        {
            if (now >= _wantUntil) _want = null;
            return;
        }
        _want = null;
        Say(line, clip);
        if (!_wantStretch || _phase != PhaseReveal) return;
        var end = now.AddMilliseconds(clip.Seconds * 1000 + VoicePadMs);
        var cap = _stepFrom.AddMilliseconds(MaxStepMs);
        if (end > cap) end = cap;
        if (end <= _endsAt) return;
        _endsAt = end;
        _phaseMs = (int)(end - _stepFrom).TotalMilliseconds;
    }

    void Say(string line, DotepyClip clip)
    {
        _say = new Speech(++_sayId, line, clip.Url, Math.Round(clip.Seconds, 2));
        _dirty = true;
    }

    // ---------------------------------------------------------------------------------------
    // кінець
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Кінець партії: переможці — усі присутні з найбільшим рахунком; якщо найбільший нуль — нічия. «Нюх на правду» —
    /// тим, хто вгадав правду в кожному питанні партії від п'яти питань, за столом на двох і більше.
    /// </summary>
    void Done()
    {
        _phase = PhaseDone;
        var seats = PresentSeats();
        var best = 0L;
        foreach (var s in seats) best = Math.Max(best, _scores[s]);
        var winners = best > 0 ? seats.Where(s => _scores[s] == best).ToArray() : [];
        if (_total >= 5 && seats.Count >= 2 && _played.Count == _total)
            foreach (var s in seats)
                if (_hits[s] == _total) Ctx.Award(s, 0, "ach:bluff-nose");
        _result = Result(winners, left: false);
        // Переможця Глек оголошує, лише якщо кліп уже є (ніки озвучено на старті): партія скінчилась, чекати нема чого.
        _want = null;
        if (VoiceOn)
        {
            var line = winners.Length == 1 ? BluffLines.Win(Nick(winners[0]))
                : winners.Length > 1 ? BluffLines.Wins(Names(winners)) : BluffLines.Draw;
            if (Clip(line) is { } clip) Say(line, clip);
        }
        Ctx.Finish(winners, Summary(seats), seats.ToDictionary(s => s, s => _scores[s]));
    }

    /// <summary>
    /// Хтось встав. Його брехня (якщо картки вже на столі) лишається з ніком зі знімка, його голос — теж, але очок
    /// йому більше нема. Лишилось менше двох — нічия: за неявку перемог не дають, і дві вкладки одного ніка не
    /// фармлять «Першу перемогу».
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (seat is < 0 or >= Seats) return;
        _present[seat] = false;
        _dirty = true;
        // Обирав тему й пішов — Глек обере за нього на найближчому тику.
        if (_phase == PhaseTopic && seat == _chooser) _endsAt = Ctx.Clock.UtcNow;
        if (_phase == PhaseDone || PresentSeats().Count >= 2) return;
        // Недогране питання в підсумок не йде (там лише ті, де правду вже відкрили), тож картки не чіпаємо.
        _phase = PhaseDone;
        _result = Result([], left: true);
        _want = null;
        Ctx.Finish([], $"{Info.Title}: гравці розійшлись, партію не дограли");
    }

    List<int> PresentSeats()
    {
        var list = new List<int>(Seats);
        for (var s = 0; s < Seats; s++) if (_present[s]) list.Add(s);
        return list;
    }

    /// <summary>
    /// Картка брехні гравців, що надурила найбільше: жертви, далі — власноруч написана перед 🎲-брехнею (текст Глека —
    /// не твоя заслуга), далі ❤, далі — раніше питання й менший номер.
    /// </summary>
    static (int Q, Card Card)? BestOf(IEnumerable<(int Q, Card Card)> cards)
    {
        (int Q, Card Card)? best = null;
        foreach (var (q, c) in cards)
        {
            if (c.Truth || c.Decoy || c.Picks.Count == 0) continue;
            if (best is not { } b) { best = (q, c); continue; }
            var o = b.Card;
            var better = c.Picks.Count != o.Picks.Count ? c.Picks.Count > o.Picks.Count
                : c.Auto != o.Auto ? !c.Auto
                : c.LikedBy.Count > o.LikedBy.Count;
            if (better) best = (q, c);
        }
        return best;
    }

    IEnumerable<(int Q, Card Card)> AllCards()
    {
        for (var n = 0; n < _played.Count; n++)
            foreach (var c in _played[n].Cards) yield return (n, c);
    }

    /// <summary>Підсумок партії для виду. <paramref name="left"/> — скінчилась тому, що гравці розійшлись (нічия).</summary>
    object Result(int[] winners, bool left)
    {
        var best = BestOf(AllCards());
        return new
        {
            winners,
            left,
            scores = (long[])_scores.Clone(),
            titles = left ? [] : Titles(),
            best = best is not { } b ? null : new
            {
                q = b.Q + 1,
                text = b.Card.Text,
                by = b.Card.By.ToArray(),
                victims = b.Card.Picks.Count,
                likes = b.Card.LikedBy.Count,
                hlek = b.Card.Auto,
            },
            recap = _played.Select((p, n) =>
            {
                var top = BestOf(p.Cards.Select(c => (n, c)));
                return new
                {
                    q = n + 1,
                    text = p.Question.Q,
                    answer = p.Question.Answer,
                    note = string.IsNullOrEmpty(p.Question.Note) ? null : p.Question.Note,
                    best = top is not { } t ? null
                        : new { text = t.Card.Text, by = t.Card.By.ToArray(), victims = t.Card.Picks.Count, hlek = t.Card.Auto },
                };
            }).ToArray(),
        };
    }

    /// <summary>
    /// Звання партії з того, що вже пораховано на картках: 🦊 Головний брехун (найбільше жертв), 👃 Нюх (найбільше
    /// вгаданих правд), 🐑 Найдовірливіший (найчастіше вірив брехні, Глековій теж), ❤ Улюбленець залу (найбільше ❤).
    /// Лише ті, хто за столом на кінці; рівні — усі разом; звання, яке ділять усі (чи нуль), не даємо — це не звання.
    /// </summary>
    object[] Titles()
    {
        var seats = PresentSeats();
        if (seats.Count < 2) return [];
        var fox = new int[Seats];
        var nose = new int[Seats];
        var sheep = new int[Seats];
        var heart = new int[Seats];
        foreach (var p in _played)
            foreach (var c in p.Cards)
            {
                if (c.Truth) { foreach (var s in c.Picks) nose[s]++; continue; }
                foreach (var s in c.Picks) sheep[s]++;
                if (c.Decoy) continue;
                foreach (var a in c.By)
                {
                    fox[a] += c.Picks.Count;
                    heart[a] += c.LikedBy.Count;
                }
            }
        var list = new List<object>(4);
        void Add(string key, string icon, string label, int[] by, Func<int, string> what)
        {
            var best = 0;
            foreach (var s in seats) best = Math.Max(best, by[s]);
            if (best == 0) return;
            var who = seats.Where(s => by[s] == best).ToArray();
            if (who.Length == seats.Count) return;
            list.Add(new { key, icon, label, seats = who, n = best, text = what(best) });
        }
        Add("fox", "🦊", "Головний брехун", fox, Victims);
        Add("nose", "👃", "Нюх", nose, n => $"{Str(n)} {Plural(n, "правда", "правди", "правд")}");
        Add("sheep", "🐑", "Найдовірливіший", sheep, n => $"{Str(n)} {Plural(n, "раз", "рази", "разів")} на гачку");
        Add("heart", "❤️", "Улюбленець залу", heart, n => $"{Str(n)} ❤");
        return [.. list];
    }

    /// <summary>1 правда, 2 правди, 5 правд.</summary>
    static string Plural(int n, string one, string few, string many)
    {
        var d = n % 10;
        var h = n % 100;
        return d == 1 && h != 11 ? one : d is >= 2 and <= 4 && (h < 12 || h > 14) ? few : many;
    }

    /// <summary>
    /// Рядок Журналу: рахунок усіх від більшого й автор найкращої брехні партії. Ніки — завжди в називному (ми їх не
    /// відмінюємо): «Байкарі: Оля 6 500, Петро 4 000 · найкраща брехня — Оля, 2 жертви». Самого тексту брехні тут нема:
    /// Журнал читає весь сайт, а писали «для своїх» — текст лишається в підсумку столу.
    /// </summary>
    string Summary(List<int> seats)
    {
        var line = string.Join(", ", seats
            .OrderByDescending(s => _scores[s]).ThenBy(s => s)
            .Select(s => $"{Nick(s)} {Num(_scores[s])}"));
        var best = BestOf(AllCards());
        var tail = best is not { } b ? "нікого так і не надурили"
            : $"найкраща брехня — {Names(b.Card.By)}, {Victims(b.Card.Picks.Count)}";
        return seats.Count == 0 ? $"{Info.Title}: за столом уже нікого" : $"{Info.Title}: {line} · {tail}";
    }

    string Nick(int seat) => _nicks[seat] ?? SeatName(seat);

    /// <summary>«Оля», «Оля і Петро», «Оля, Петро і Ганна».</summary>
    string Names(IReadOnlyList<int> seats)
    {
        var names = seats.Select(Nick).ToList();
        return names.Count <= 1 ? string.Concat(names) : string.Join(", ", names.Take(names.Count - 1)) + " і " + names[^1];
    }

    /// <summary>1 жертва, 2 жертви, 5 жертв.</summary>
    public static string Victims(int n)
    {
        var d = n % 10;
        var h = n % 100;
        var word = d == 1 && h != 11 ? "жертва" : d is >= 2 and <= 4 && (h < 12 || h > 14) ? "жертви" : "жертв";
        return $"{Str(n)} {word}";
    }

    /// <summary>Число з пробілами між тисячами: 6500 → «6 500».</summary>
    public static string Num(long v) => v.ToString("#,##0", CultureInfo.InvariantCulture).Replace(",", " ");

    // ---------------------------------------------------------------------------------------
    // види
    // ---------------------------------------------------------------------------------------

    public override object View(int? seat)
    {
        var me = seat is { } s && s >= 0 && s < Seats ? s : -1;
        var q = Current;
        var done = _phase == PhaseDone;
        // Лобі чи дограний стіл: той, хто зараз сидить, скоро натисне «Почати» чи «Ще раз» — пам'ять підтягуємо фоном.
        var lobby = _total == 0 || done;
        if (lobby) PrefetchSeated();
        return new
        {
            phase = _phase,
            q = _total == 0 ? 0 : _q + 1,
            of = _total,
            @final = _total > 0 && Final,
            endsAt = _endsAt,
            phaseMs = _phaseMs,
            cat = q?.Cat ?? "",
            catLabel = q is null ? "" : BluffCats.Label(q.Cat),
            text = done || q is null ? "" : q.Q,
            nicks = (string?[])_nicks.Clone(),
            present = (bool[])_present.Clone(),
            wrote = Wrote(),
            picked = Picked(),
            my = me < 0 ? null : new
            {
                lie = _lie[me],
                auto = _auto[me],
                pick = _pick[me] < 0 ? (int?)null : _pick[me],
                likes = MyLikes(me),
            },
            options = Options(me, done),
            revealed = _revealed.ToArray(),
            note = _truthOpen && q is not null && q.Note.Length > 0 ? q.Note : null,
            quip = _truthOpen ? _quip : null,
            scores = (long[])_scores.Clone(),
            delta = (long[])_delta.Clone(),
            truthDelta = (long[])_truthDelta.Clone(),
            likeDelta = (long[])_likeDelta.Clone(),
            victims = (int[])_victims.Clone(),
            result = _result,
            topic = _phase == PhaseTopic
                ? new { by = _chooser, options = _topics.Select(k => new { key = k, label = BluffCats.Label(k) }).ToArray(), pick = _topicPick }
                : null,
            chooser = _topicTurn && q is not null && _q < _chosenBy.Count ? _chosenBy[_q] : (int?)null,
            voice = _voiceName,
            say = _say is { } l ? new { id = l.Id, text = l.Text, url = l.Url, seconds = l.Seconds } : null,
            fresh = lobby ? Fresh() : null,
        };
    }

    /// <summary>
    /// Лобі: скільки питань (за обраними темами) ще не бачив ніхто з тих, хто сидить за столом, — «свіжих для цього
    /// столу: 143 з 612». Лише з пам'яті (<see cref="BluffSeen.LastSeen"/> у базу не ходить), не частіше ніж раз на
    /// дві секунди на той самий склад столу: вид шлють кожному місцю.
    /// </summary>
    object Fresh()
    {
        var keys = new List<string>(Seats);
        for (var s = 0; s < Seats; s++)
            if (Ctx.Seated(s) && Ctx.NickOf(s) is { Length: > 0 } nick && BluffSeen.NickKey(nick) is var key && !keys.Contains(key)) keys.Add(key);
        var sig = string.Join('|', keys);
        var now = Ctx.Clock.UtcNow;
        if (_fresh is not { } f || f.Sig != sig || now - f.At > TimeSpan.FromSeconds(2))
        {
            var seen = Seen.LastSeen(keys);
            int n = 0, of = 0;
            foreach (var q in Bank)
            {
                if (!BluffCats.Fits(q, _cats)) continue;
                of++;
                if (!seen.ContainsKey(q.Key)) n++;
            }
            f = (sig, now, n, of);
            _fresh = f;
        }
        return new { n = f.Fresh, of = f.Of };
    }

    /// <summary>
    /// Картки на столі. До розкриття текст бачать усі однаково, але ні авторів, ні вибору, ні де правда — у жодному
    /// виді, зокрема у власному; «mine» — лише автору. Після кінця партії відкрито все.
    /// </summary>
    object[]? Options(int me, bool done)
    {
        if (_cards.Count == 0 || _phase is PhaseRead or PhaseWrite) return null;
        var list = new object[_cards.Count];
        for (var i = 0; i < _cards.Count; i++)
        {
            var c = _cards[i];
            var open = c.Open || done;
            list[i] = new
            {
                i,
                text = c.Text,
                mine = me >= 0 && c.By.Contains(me),
                by = open ? c.By.ToArray() : null,
                picks = open ? c.Picks.ToArray() : null,
                truth = open ? c.Truth : (bool?)null,
                decoy = open ? c.Decoy : (bool?)null,
                likes = open ? c.LikedBy.Count : 0,
            };
        }
        return list;
    }

    int[] MyLikes(int me)
    {
        var list = new List<int>();
        for (var i = 0; i < _cards.Count; i++) if (_cards[i].LikedBy.Contains(me)) list.Add(i);
        return [.. list];
    }

    bool[] Wrote()
    {
        var flags = new bool[Seats];
        for (var s = 0; s < Seats; s++) flags[s] = _lie[s] is not null;
        return flags;
    }

    bool[] Picked()
    {
        var flags = new bool[Seats];
        for (var s = 0; s < Seats; s++) flags[s] = _pick[s] >= 0;
        return flags;
    }

    // Кадру (Frame) нема: модуль його не читає, а тик шле лише види (ViewOnly).

    // ---------------------------------------------------------------------------------------
    // Дядько Глек
    // ---------------------------------------------------------------------------------------

    /// <summary>Фраза Глека до правди, коли її не знайшов ніхто. Ніків не підставляємо — відмінків нема.</summary>
    public static readonly string[] QuipsNobody =
    [
        "Правда стояла поруч і мовчала.",
        "Ніхто не повірив — а дарма.",
        "Це чиста правда, хоч і звучить як брехня.",
        "Не вірите? Загугліть після партії.",
        "Бідолашна правда: жодного голосу.",
        "Байкарі так набрехали, що правді й місця не лишилось.",
        "Так буває: правда — найдивніша картка на столі.",
    ];

    /// <summary>Фраза Глека, коли правду знайшла частина столу.</summary>
    public static readonly string[] QuipsSome =
    [
        "Хто вгадав — той сьогодні з нюхом.",
        "Отак-то. Правда буває дивнішою за брехню.",
        "І це не жарт — так і було.",
        "Байкарі старались, але правда — ось.",
        "Записуйте, на ярмарку розкажете.",
    ];

    /// <summary>Фраза Глека, коли правду знайшли всі.</summary>
    public static readonly string[] QuipsAll =
    [
        "Нюх у всіх — як у гончих!",
        "Цю правду не сховаєш і в глечику.",
        "Усі вгадали — брехунам сьогодні не щастить.",
        "Ось вона, справжня. Решта — байки.",
        "Надто чесне питання — правду видно здалеку.",
    ];

    /// <summary>Усі фрази Глека разом.</summary>
    public static readonly string[] Quips = [.. QuipsNobody, .. QuipsSome, .. QuipsAll];
}
