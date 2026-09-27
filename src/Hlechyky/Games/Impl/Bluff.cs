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

    /// <summary>Відмова, коли брехня збіглась із правдою. Одна фраза на всі випадки — звертання «ти», не розповідь.</summary>
    public const string Truthy = "Схоже, ти випадково написав правду — вигадай іншу 🙂";
    /// <summary>Відмова слову з двох абеток: «кисeнь» із латинською e на великій картці не відрізнити від чесного.</summary>
    public const string MixedAbc = "Пиши однією абеткою — кирилицею або латиницею 🙂";
    /// <summary>
    /// Відмова літерам інших письмен (черокі, лісу, капітель, розширена латиниця й кирилиця, «математичні»): на великій
    /// картці вони вдають наші: «МАШИНОЮ», де М і А — літери черокі (U+13B7, U+13AA), — та сама правда.
    /// </summary>
    public const string ForeignAbc = "Такі літери на картці вдають наші — пиши кирилицею або латиницею 🙂";
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
        _asked.AddRange(Pick());
        _q = 0;
        _dirty = false;
        var now = Ctx.Clock.UtcNow;
        ClearQuestion();
        if (_asked.Count == 0)
        {
            _phase = PhaseDone;
            _endsAt = now;
            _phaseMs = 0;
            Ctx.Finish([], $"{Info.Title}: у цих темах не знайшлось питань, партії не буде");
            return;
        }
        BeginRead(now);
    }

    /// <summary>
    /// Питання партії: банк за темами, тасування Фішера — Єйтса на <c>Ctx.Rng</c>, потім найсвіжіші для цього столу
    /// (спершу ніким не бачені, далі — бачені найдавніше). Пам'ять — з того, що підтяглось фоном у лобі, і з позначок
    /// самого столу: Start кличуть під замком кімнати, тож у базу тут не ходимо.
    /// </summary>
    List<BluffQuestion> Pick()
    {
        var pool = new List<BluffQuestion>();
        foreach (var q in Bank) if (BluffCats.Fits(q, _cats)) pool.Add(q);
        for (var i = pool.Count - 1; i > 0; i--)
        {
            var j = Ctx.Rng.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        return BluffSeen.Freshest(pool, q => q.Key, Seen.LastSeen(PresentKeys()), _count);
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
    bool Final => _q == _asked.Count - 1;

    // ---------------------------------------------------------------------------------------
    // фази (усі переходи — лише з тика)
    // ---------------------------------------------------------------------------------------

    void SetTimer(DateTimeOffset now, int ms)
    {
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

    void BeginRead(DateTimeOffset now)
    {
        ClearQuestion();
        _phase = PhaseRead;
        SetTimer(now, ReadMs);
        // «Бачив» — з тієї миті, коли питання з'явилось на екрані; до недограних питань пам'ять не доходить.
        Seen.Mark(PresentKeys(), _asked[_q], now);
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
        OpenNext(now);
    }

    /// <summary>Відкрити наступну картку й нарахувати за неї: правда — тим, хто її обрав; брехня гравців — авторам.</summary>
    void OpenNext(DateTimeOffset now)
    {
        var i = _order[_revealed.Count];
        var card = _cards[i];
        card.Open = true;
        _revealed.Add(i);
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

        // «Готово» в усіх — таймер не чекаємо; сам перехід — нижче, в одному місці з рештою.
        if (_phase == PhaseWrite && AllWrote()) _endsAt = now;
        else if (_phase == PhasePick && AllPicked()) _endsAt = now;

        if (now < _endsAt)
        {
            if (!_dirty) return TickResult.None;
            _dirty = false;
            return ViewOnly;
        }

        _dirty = false;
        switch (_phase)
        {
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
                BeginRead(now);
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
        if (action is not ("lie" or "pick" or "like")) return ActResult.Fail("Тут так не ходять");
        if (_phase == PhaseDone) return ActResult.Fail("Партію зіграно, тисни «Ану ще раз»");
        if (seat is < 0 or >= Seats) return ActResult.Fail("Ти вже не за столом");
        return action switch
        {
            "lie" => Lie(seat, payload),
            "pick" => PickCard(seat, payload),
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
    /// приймає: порожній, задовгий, літери інших письмен (<see cref="ForeignAbc"/>), слово з двох абеток
    /// (<see cref="MixedAbc"/>), правда (<see cref="Truthy"/>). Одна перевірка й для <see cref="Act"/>, і для тестів
    /// на всьому банку.
    /// </summary>
    public static string? Refuse(string text, BluffQuestion question)
    {
        if (text.Length == 0) return "Порожня брехня нікого не надурить";
        if (text.Length > MaxLie) return "Коротше — до 40 знаків";
        if (BluffText.ForeignLetters(text)) return ForeignAbc;
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
        if (_asked.Count >= 5 && seats.Count >= 2 && _played.Count == _asked.Count)
            foreach (var s in seats)
                if (_hits[s] == _asked.Count) Ctx.Award(s, 0, "ach:bluff-nose");
        _result = Result(winners, left: false);
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
        if (_phase == PhaseDone || PresentSeats().Count >= 2) return;
        // Недогране питання в підсумок не йде (там лише ті, де правду вже відкрили), тож картки не чіпаємо.
        _phase = PhaseDone;
        _result = Result([], left: true);
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
        if (_asked.Count == 0 || done) PrefetchSeated();
        return new
        {
            phase = _phase,
            q = _asked.Count == 0 ? 0 : _q + 1,
            of = _asked.Count,
            @final = _asked.Count > 0 && Final,
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
        };
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
