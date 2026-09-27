using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Дотепи» — Quiplash по-нашому (specs/dotepy.md). Кожен отримує дурні завдання («Найгірша назва для
/// сільського радіо»), пише найсмішнішу відповідь, потім відповіді виходять на екран анонімно, стіл голосує, і
/// лише тоді розкривається, хто що написав. Дядько Глек зачитує завдання й відповіді голосом (edge-tts).
/// <para>
/// Склад вирішує режим раунду: п'ятеро й більше — дуелі (кожне завдання двом, решта судить «ліве чи праве»),
/// троє-четверо — «на всіх» (обидва завдання пише кожен, голосують за чуже). Останній раунд партії — завжди
/// «Останній дотеп»: одне завдання на всіх, кожен роздає 🥇🥈🥉.
/// </para>
/// <para>
/// Партія живе від тика (250 мс), як «Скільки?»: фази міняє лише <see cref="Tick"/>, а <see cref="Act"/> тільки
/// міняє стан і ставить <c>_dirty</c> — види доїдуть найближчим тиком. Кадрів нема взагалі: гра <c>Hidden</c>,
/// і все, що треба браузеру, — у видах, які летять лише тоді, коли щось змінилось.
/// </para>
/// </summary>
public sealed class Dotepy : Game
{
    // ---------------------------------------------------------------------------------------
    // константи (specs/dotepy.md §2.1)
    // ---------------------------------------------------------------------------------------

    public const int TickMs = 250;
    public const int MinSeats = 3, MaxSeats = 8;
    /// <summary>Від скількох присутніх раунди 1–2 — дуелі.</summary>
    public const int DuelFrom = 5;
    /// <summary>Символів у дотепі після чистки. Довше — не влізе на картку й у голос.</summary>
    public const int MaxAnswer = 80;
    /// <summary>Завдань на гравця в раундах 1–2.</summary>
    public const int TasksPerRound = 2;
    /// <summary>Фінал пише коротше: завдання одне.</summary>
    public static int FinalWriteMs(int writeMs) => writeMs * 2 / 3;
    public const int VoteMs = 15_000;
    public const int FinalVoteMs = 30_000;
    public const int SpeechCapMs = 15_000;
    public const int FinalSpeechCapMs = 25_000;
    public const int RevealMs = 5_000;
    public const int VerdictCapMs = 4_000;
    public const int FinalStepMs = 2_500;
    public const int FinalHoldMs = 3_000;
    public const int TableMs = 6_000;
    public const int VoiceWaitMs = 3_000;
    public const double CharsPerSec = 14;
    public const int RankCount = 3;
    public static readonly int[] RankPoints = [300, 200, 100];
    public const int JuryPrize = 100, FinalJuryPrize = 200;
    /// <summary>Раунд 1 — 100 за голос, раунд 2 — 200.</summary>
    public static int VoteValue(int round) => 100 * round;
    public static int SweepBonus(int round) => 2 * VoteValue(round);
    /// <summary>Скільки останніх завдань сервер пам'ятає, щоб не повторювати їх між столами.</summary>
    public const int SeenRing = 300;

    public const string PhaseLobby = "lobby", PhaseWrite = "write", PhaseVote = "vote", PhaseReveal = "reveal",
        PhaseTable = "table", PhaseDone = "done";
    public const string ModeDuel = "duel", ModeAll = "all";

    static readonly int[] WriteChoices = [60, 90, 120];
    static readonly TickResult ViewOnly = new(false, true);

    public override GameInfo Info { get; } = new(
        "dotepy", "Дотепи", "«Дотепи»", GameGroup.Party, MinSeats, MaxSeats,
        TickMs: TickMs, Start: StartMode.ByHost, Hidden: true, Private: false, Persistent: false, Rated: false,
        Score: ScoreOrder.HigherIsBetter,
        Options:
        [
            new GameOption("rounds", "Партія",
                [("full", "2 раунди + Останній дотеп"), ("short", "1 раунд + Останній дотеп"), ("blitz", "Лише Останній дотеп")], "full"),
            new GameOption("write", "Час на дотеп", [("60", "60 с"), ("90", "90 с"), ("120", "120 с")], "90"),
            new GameOption("voice", "Голос Глека", [("ostap", "Остап"), ("polina", "Поліна"), ("none", "Без голосу")], "ostap"),
        ],
        Hint: "Дурне завдання — смішна відповідь. Пишете анонімно, голосуєте за чуже, Дядько Глек усе зачитує. Троє й більше");

    /// <summary>Місця — числами: їх до восьми, і «гравець восьмий» у чіп не влізе.</summary>
    public override string SeatName(int seat) => (seat + 1).ToString(CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------------------------------
    // стан
    // ---------------------------------------------------------------------------------------

    /// <summary>Одне завдання раунду і все, що з ним сталось: хто пише, що написали, хто за що голосував.</summary>
    sealed class Card(DotepyPrompt prompt)
    {
        public DotepyPrompt Prompt { get; } = prompt;
        /// <summary>Автори в порядку роздачі — до кінця написання.</summary>
        public readonly List<Entry> Entries = [];
        /// <summary>
        /// Порядок відповідей на голосуванні (індекси <see cref="Entries"/>). Тасується ще на старті раунду: тоді,
        /// щойно всі автори картки здали, її читання вже відоме й іде озвучуватись, поки решта ще пише.
        /// </summary>
        public int[] Order = [];
        /// <summary>Відповіді на голосування в порядку <see cref="Order"/> — після <see cref="CloseWriting"/>.</summary>
        public Answer[] Answers = [];
        /// <summary>Усі відповіді підставні (автори пішли) — голосувати нема за що, картку пропускаємо.</summary>
        public bool Skipped;
        /// <summary>Що Глек читає на цій картці (одним кліпом).</summary>
        public string Line = "";
        /// <summary>Індекс відповіді з «Розгромом»; −1 — не було.</summary>
        public int Sweep = -1;

        public bool IsAuthor(int seat)
        {
            foreach (var e in Entries) if (e.Seat == seat) return true;
            return false;
        }
    }

    /// <summary>Місце автора на картці: чернетка, здане й чи здане.</summary>
    sealed class Entry(int seat)
    {
        public int Seat { get; } = seat;
        public string Draft = "";
        public string Text = "";
        public bool Done;
    }

    /// <summary>Відповідь на голосуванні. Голоси, очки й ранг рахуються на розкритті.</summary>
    sealed class Answer(int seat, string text, bool stock)
    {
        public int Seat { get; } = seat;
        public string Text { get; } = text;
        public bool Stock { get; } = stock;
        public readonly List<int> Voters = [];
        /// <summary>Фінал: ранг кожного голосу з <see cref="Voters"/> (1 — 🥇).</summary>
        public readonly List<int> Medals = [];
        public int Jury;
        public int Points;
        public int Rank;
        public bool Prize;
    }

    /// <summary>Найдотепніше: для «Дотепу раунду» й трійки партії.</summary>
    sealed record Best(string Prompt, string Text, int Seat, int Points, int Round);

    /// <summary>Репліка Глека. <see cref="Id"/> росте на кожну нову — браузер грає кожну рівно раз.</summary>
    sealed record Speech(int Id, string Text, string? Url, double Seconds);

    // налаштування столу
    int _roundsTotal = 3;
    int _writeMs = 90_000;
    string _voiceName = "ostap";

    // сервіси
    IDotepyVoice _voice = DotepyNoVoice.Instance;
    DotepySeen _seen = DotepySeen.Shared;
    IReadOnlyList<DotepyPrompt> _bank = [];
    /// <summary>Завдання, що вже грали за цим столом. Живе в екземплярі гри — тож переживає «Ще раз».</summary>
    readonly HashSet<string> _used = new(StringComparer.Ordinal);

    // склад
    readonly string[] _nicks = new string[MaxSeats];
    readonly bool[] _inGame = new bool[MaxSeats];
    readonly bool[] _left = new bool[MaxSeats];
    readonly long[] _score = new long[MaxSeats];
    readonly long[] _roundFrom = new long[MaxSeats];
    /// <summary>Порядок місць партії (тасується раз на старті) — від нього дуелі: зсув 1 у раунді 1, зсув 2 у раунді 2.</summary>
    int[] _order = [];
    int _startedWith;

    // фаза
    string _phase = PhaseLobby;
    int _round;
    bool _final;
    string _mode = ModeAll;
    DateTimeOffset? _endsAt;
    int _totalMs;
    bool _dirty;

    // раунд і картка
    readonly List<Card> _cards = [];
    int _at = -1;
    readonly int[]?[] _picks = new int[MaxSeats][];
    readonly bool[] _voter = new bool[MaxSeats];
    int _perVoter = 1;
    /// <summary>Голос публіки на поточній картці: ключ ніка глядача → індекс відповіді.</summary>
    readonly Dictionary<string, int> _jury = new(StringComparer.Ordinal);
    /// <summary>Коли Глек дочитає картку: усі проголосували раніше — розкриваємо не раніше за це (голос не рвемо).</summary>
    DateTimeOffset _speechEnd;

    // розкриття фіналу
    int _shown;
    DateTimeOffset _stepAt;
    int[] _revealOrder = [];
    readonly int[] _revealPos = new int[MaxSeats + 1];

    // підсумки
    Best? _roundBest;
    readonly List<Best> _bests = [];
    int _sweeps;
    int[]? _winners;

    // голос
    Speech? _say;
    int _sayId;
    string? _pending;
    DateTimeOffset _pendingUntil;

    DateTimeOffset Now => Ctx.Clock.UtcNow;
    bool VoiceOn => _voice.Enabled;

    // ---------------------------------------------------------------------------------------
    // партія
    // ---------------------------------------------------------------------------------------

    /// <summary>Опції столу. Каркас уже звів кожну до дозволеного значення, але береженого бог береже.</summary>
    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _roundsTotal = options.GetValueOrDefault("rounds") switch { "short" => 2, "blitz" => 1, _ => 3 };
        _writeMs = int.TryParse(options.GetValueOrDefault("write"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)
            && WriteChoices.Contains(w) ? w * 1000 : 90_000;
        _voiceName = options.GetValueOrDefault("voice") is "polina" or "none" ? options["voice"] : "ostap";
    }

    public override void Start()
    {
        // Сервіси беремо тут: гру створює реєстр без параметрів (INTEGRATION-NOTES §1).
        _voice = DotepyNoVoice.Instance;
        if (_voiceName != "none")
        {
            try
            {
                if (Ctx.Services.GetService<IDotepyVoice>() is { } v && v.Enabled) _voice = v;
            }
            catch (Exception) { /* голос — чужий код; не вийшло — граємо текстом */ }
        }
        _seen = Ctx.Services.GetService<DotepySeen>() ?? DotepySeen.Shared;
        _bank = Ctx.Services.GetService<DotepyPrompts>()?.List ?? DotepyBank.All;

        var seated = new List<int>();
        for (var s = 0; s < MaxSeats; s++)
        {
            _inGame[s] = Ctx.Seated(s);
            _nicks[s] = Ctx.NickOf(s) ?? "";
            _left[s] = false;
            _score[s] = 0;
            _roundFrom[s] = 0;
            _picks[s] = null;
            _voter[s] = false;
            if (_inGame[s]) seated.Add(s);
        }
        _startedWith = seated.Count;
        for (var i = seated.Count - 1; i > 0; i--)
        {
            var j = Ctx.Rng.Next(i + 1);
            (seated[i], seated[j]) = (seated[j], seated[i]);
        }
        _order = [.. seated];

        _cards.Clear();
        _at = -1;
        _jury.Clear();
        _bests.Clear();
        _roundBest = null;
        _sweeps = 0;
        _winners = null;
        _say = null;
        _pending = null;
        _endsAt = null;
        _totalMs = 0;
        _round = 0;
        _final = false;
        _shown = 0;
        _revealOrder = [];

        if (_bank.Count == 0)
        {
            // Без банку грати нема в що. Кажемо це один раз і чесно, а не мовчимо порожнім екраном.
            _phase = PhaseDone;
            _winners = [];
            _dirty = true;
            Ctx.Finish([], $"{Info.Title}: банк завдань не знайшовся, партії не буде");
            return;
        }

        if (VoiceOn)
        {
            // Вступ першого раунду потрібен уже зараз; решта — чисті репліки й вердикти з ніками цього столу —
            // у кінець черги, до них ще кілька хвилин.
            Prepare([DotepyLines.Intro(1, _roundsTotal == 1, Math.Min(RankCount, _startedWith - 1))], urgent: true);
            var lines = new List<string>(DotepyLines.Pure());
            foreach (var s in seated) lines.AddRange(DotepyLines.Named(Spoken(s)));
            Prepare(lines);
        }
        OpenRound(1, Now);
        // Види після старту каркас розсилає сам (StartByHost / Rematch) — першому тику лишається мовчати.
        _dirty = false;
    }

    bool Present(int seat) => seat >= 0 && seat < MaxSeats && _inGame[seat] && !_left[seat] && Ctx.Seated(seat);

    int PresentCount()
    {
        var n = 0;
        for (var s = 0; s < MaxSeats; s++) if (Present(s)) n++;
        return n;
    }

    /// <summary>Новий раунд: режим за кількістю присутніх, завдання, роздача, таймер написання.</summary>
    void OpenRound(int round, DateTimeOffset now)
    {
        _round = round;
        _final = round >= _roundsTotal;
        var present = new List<int>(MaxSeats);
        foreach (var s in _order) if (Present(s)) present.Add(s);
        var n = present.Count;
        _mode = !_final && n >= DuelFrom ? ModeDuel : ModeAll;

        var count = _final ? 1 : _mode == ModeDuel ? n : TasksPerRound;
        var prompts = PickPrompts(count, _final);
        _cards.Clear();
        for (var k = 0; k < prompts.Count; k++)
        {
            var card = new Card(prompts[k]);
            if (_mode == ModeDuel)
            {
                // кожен пише рівно два завдання, і жодне — сам із собою; зсув 2 у раунді 2 не повторює пар раунду 1
                var off = round == 1 ? 1 : 2;
                card.Entries.Add(new Entry(present[k]));
                card.Entries.Add(new Entry(present[(k + off) % n]));
            }
            else
            {
                foreach (var s in present) card.Entries.Add(new Entry(s));
            }
            card.Order = new int[card.Entries.Count];
            for (var i = 0; i < card.Order.Length; i++) card.Order[i] = i;
            for (var i = card.Order.Length - 1; i > 0; i--)
            {
                var j = Ctx.Rng.Next(i + 1);
                (card.Order[i], card.Order[j]) = (card.Order[j], card.Order[i]);
            }
            _cards.Add(card);
        }

        for (var s = 0; s < MaxSeats; s++)
        {
            _roundFrom[s] = _score[s];
            _picks[s] = null;
            _voter[s] = false;
        }
        _at = -1;
        _jury.Clear();
        _roundBest = null;
        _shown = 0;
        _revealOrder = [];
        _phase = PhaseWrite;
        _totalMs = _final ? FinalWriteMs(_writeMs) : _writeMs;
        _endsAt = now.AddMilliseconds(_totalMs);
        SayNow(DotepyLines.Intro(round, _final, Math.Min(RankCount, n - 1)));
        _dirty = true;
    }

    /// <summary>
    /// Завдання раунду (specs/dotepy.md §2.2): пул перетасовано <c>Ctx.Rng</c>; спершу ті, яких цей стіл не грав і
    /// сервер нещодавно не бачив, далі — бачені на сервері, далі — уже зіграні тут; лише тоді повтор. Фінал бере
    /// завдання з позначкою <c>final</c> (якщо такі є), а звичайні раунди такі завдання беруть останніми — щоб на
    /// фінал вистачило.
    /// </summary>
    List<DotepyPrompt> PickPrompts(int count, bool final)
    {
        var pool = new List<DotepyPrompt>(_bank.Count);
        foreach (var p in _bank) if (!final || p.Final) pool.Add(p);
        if (pool.Count == 0) pool.AddRange(_bank);
        for (var i = pool.Count - 1; i > 0; i--)
        {
            var j = Ctx.Rng.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        var seen = _seen.Snapshot();
        // ранг свіжості: 0 — нове, 1 — бачене сервером, 2 — зігране тут; усередині — звичайні раніше за фінальні
        int Rank(DotepyPrompt p) => (_used.Contains(p.Id) ? 4 : seen.Contains(p.Id) ? 2 : 0) + (!final && p.Final ? 1 : 0);
        var ordered = pool.Select((p, i) => (p, i)).OrderBy(x => Rank(x.p)).ThenBy(x => x.i).Select(x => x.p).ToList();
        var picked = new List<DotepyPrompt>(count);
        for (var i = 0; i < count && ordered.Count > 0; i++) picked.Add(ordered[i % ordered.Count]);
        foreach (var p in picked) _used.Add(p.Id);
        _seen.Mark(picked.Select(p => p.Id));
        return picked;
    }

    public override TickResult Tick()
    {
        if (_phase is PhaseDone or PhaseLobby) return TickResult.None;
        var now = Now;

        // Глек прокашлюється: кліп картки готовий або чекали вже досить — відкриваємо голосування насправді.
        if (_pending is { } pending)
        {
            var clip = Clip(pending);
            if (clip is not null || now >= _pendingUntil) StartVote(pending, clip, now);
        }

        switch (_phase)
        {
            case PhaseWrite:
                if (now >= _endsAt || AllWritten())
                {
                    CloseWriting();
                    OpenCard(0, now);
                }
                break;
            case PhaseVote:
                if (_pending is null && (now >= _endsAt || now >= _speechEnd && AllVoted())) Reveal(now);
                break;
            case PhaseReveal:
                if (_final) FinalStep(now);
                else if (now >= _endsAt) OpenCard(_at + 1, now);
                break;
            case PhaseTable:
                if (now >= _endsAt) OpenRound(_round + 1, now);
                break;
        }

        if (!_dirty) return TickResult.None;
        _dirty = false;
        return ViewOnly;
    }

    /// <summary>Усі присутні здали всі свої завдання. Без алокацій — кличеться щотика.</summary>
    bool AllWritten()
    {
        foreach (var card in _cards)
            foreach (var e in card.Entries)
                if (!e.Done && Present(e.Seat)) return false;
        return true;
    }

    /// <summary>Усі присутні, хто має голос на цій картці, проголосували (і такі взагалі є).</summary>
    bool AllVoted()
    {
        var voters = 0;
        for (var s = 0; s < MaxSeats; s++)
        {
            if (!_voter[s] || !Present(s)) continue;
            voters++;
            if (!Complete(s)) return false;
        }
        return voters > 0;
    }

    /// <summary>
    /// Голос місця віддано повністю: у раундах — один, у фіналі — усі медалі (<see cref="_perVoter"/>). Неповний
    /// фінальний бюлетень рахується на дедлайні, але голосування раніше часу не закриває: людина, що поставила 🥇,
    /// ще шукає, кому дати 🥈.
    /// </summary>
    bool Complete(int seat) => _picks[seat] is { } p && (!_final || p.Length >= _perVoter);

    /// <summary>
    /// Кінець написання: незданому — чернетка (якщо не порожня), інакше підставна; відповіді перетасовано;
    /// картки, де все підставне, пропускаються; усі читання карток — у чергу голосу, перша — найперша.
    /// </summary>
    void CloseWriting()
    {
        var lines = new List<string>();
        foreach (var card in _cards)
        {
            var taken = new List<string>(card.Entries.Count);
            var byEntry = new Answer[card.Entries.Count];
            for (var k = 0; k < byEntry.Length; k++)
            {
                var e = card.Entries[k];
                if (e.Done) byEntry[k] = new Answer(e.Seat, e.Text, false);
                else if (e.Draft.Length > 0) byEntry[k] = new Answer(e.Seat, e.Draft, false);
                else
                {
                    var stock = DotepyStock.Pick(Ctx.Rng, taken);
                    taken.Add(stock);
                    byEntry[k] = new Answer(e.Seat, stock, true);
                }
            }
            card.Answers = [.. card.Order.Select(k => byEntry[k])];
            card.Skipped = Array.TrueForAll(card.Answers, a => a.Stock);
            card.Line = DotepyLines.Card(card.Prompt.Text, [.. card.Answers.Select(a => a.Text)]);
            if (!card.Skipped) lines.Add(card.Line);
        }
        // Усі — терміново й по порядку: звичайна черга могла б стояти за чужими репліками, і тоді кожна картка
        // чекала б на Глека по три секунди.
        if (VoiceOn && lines.Count > 0) Prepare(lines, urgent: true);
    }

    /// <summary>Відкрити картку <paramref name="index"/> (пропущені — перескочити); після останньої — підсумок раунду.</summary>
    void OpenCard(int index, DateTimeOffset now)
    {
        while (index < _cards.Count && _cards[index].Skipped) index++;
        if (index >= _cards.Count)
        {
            if (_final) Done();
            else Table(now);
            return;
        }
        _at = index;
        var card = _cards[index];
        _jury.Clear();
        var voters = 0;
        for (var s = 0; s < MaxSeats; s++)
        {
            _picks[s] = null;
            _voter[s] = Present(s) && !card.IsAuthor(s);
            if (_voter[s]) voters++;
        }
        // «На всіх» і фінал: автори — усі, тож голосують усі присутні, але за себе не можна.
        if (voters == 0)
            for (var s = 0; s < MaxSeats; s++) _voter[s] = Present(s);
        _perVoter = _final ? Math.Max(1, Math.Min(RankCount, card.Answers.Length - 1)) : 1;
        _phase = PhaseVote;
        card.Sweep = -1;
        _dirty = true;

        if (!VoiceOn)
        {
            StartVote(card.Line, null, now);
            return;
        }
        var clip = Clip(card.Line);
        if (clip is not null)
        {
            StartVote(card.Line, clip, now);
            return;
        }
        // Кліп ще готується: чекаємо до VoiceWaitMs (голосувати вже можна), далі — без голосу.
        Prepare([card.Line], urgent: true);
        _pending = card.Line;
        _pendingUntil = now.AddMilliseconds(VoiceWaitMs);
        _endsAt = null;
        _totalMs = 0;
    }

    /// <summary>Голосування пішло насправді: Глек читає картку, таймер — голосування плюс читання.</summary>
    void StartVote(string text, DotepyClip? clip, DateTimeOffset now)
    {
        _pending = null;
        var cap = _final ? FinalSpeechCapMs : SpeechCapMs;
        var estimate = text.Length / CharsPerSec;
        var speech = (int)Math.Min(cap, (clip?.Seconds ?? estimate) * 1000);
        _say = new Speech(++_sayId, text, clip?.Url, Math.Round(clip?.Seconds ?? estimate, 2));
        _totalMs = (_final ? FinalVoteMs : VoteMs) + speech;
        _endsAt = now.AddMilliseconds(_totalMs);
        // без справжнього голосу читають очима — хто вже проголосував, тому чекати нема на що
        _speechEnd = clip is null ? now : now.AddMilliseconds(speech);
        _dirty = true;
    }

    /// <summary>Розкриття картки: голоси, очки, «Розгром», приз публіки, вердикт Глека.</summary>
    void Reveal(DateTimeOffset now)
    {
        var card = _cards[_at];
        var answers = card.Answers;
        foreach (var a in answers)
        {
            a.Voters.Clear();
            a.Medals.Clear();
            a.Jury = 0;
            a.Points = 0;
            a.Rank = 0;
            a.Prize = false;
        }
        var totalVotes = 0;
        for (var s = 0; s < MaxSeats; s++)
        {
            if (_picks[s] is not { } picks) continue;
            for (var r = 0; r < picks.Length; r++)
            {
                var a = answers[picks[r]];
                a.Voters.Add(s);
                if (_final) a.Medals.Add(r + 1);
                totalVotes++;
            }
        }
        foreach (var pick in _jury.Values) answers[pick].Jury++;

        foreach (var a in answers)
        {
            if (a.Stock) continue;
            if (_final) foreach (var m in a.Medals) a.Points += RankPoints[m - 1];
            else a.Points = a.Voters.Count * VoteValue(_round);
        }

        // «Розгром!»: усі, хто міг голосувати за цю відповідь (≥ 2), віддали голос саме їй, і ніхто, крім її
        // автора, не голосував за іншу. У «на всіх» автор за себе голосувати не може — його голос не рахується.
        card.Sweep = -1;
        if (!_final)
        {
            for (var i = 0; i < answers.Length; i++)
            {
                var a = answers[i];
                if (a.Stock || a.Voters.Count < 2 || !IsSweep(i, answers)) continue;
                card.Sweep = i;
                a.Points += SweepBonus(_round);
                _sweeps++;
                Ctx.Award(a.Seat, 0, "ach:dotepy-sweep");
                break;
            }
        }

        // Приз публіки — строго найпопулярнішій серед глядачів; порівну — нікому. Підставній — теж нікому.
        var bestJury = 0;
        var juryAt = -1;
        for (var i = 0; i < answers.Length; i++)
        {
            if (answers[i].Jury > bestJury) { bestJury = answers[i].Jury; juryAt = i; }
            else if (answers[i].Jury == bestJury && bestJury > 0) juryAt = -1;
        }
        if (juryAt >= 0)
        {
            answers[juryAt].Prize = true;
            if (!answers[juryAt].Stock) answers[juryAt].Points += _final ? FinalJuryPrize : JuryPrize;
        }

        foreach (var a in answers)
            if (!a.Stock && a.Points > 0)
            {
                var best = new Best(card.Prompt.Text, a.Text, a.Seat, a.Points, _round);
                _bests.Add(best);
                if (_roundBest is null || best.Points > _roundBest.Points) _roundBest = best;
            }

        _phase = PhaseReveal;
        _dirty = true;
        if (!_final)
        {
            foreach (var a in answers) _score[a.Seat] += a.Points;
            var verdict = Verdict(card, totalVotes);
            var ms = SayNow(verdict);
            _totalMs = RevealMs + (int)Math.Min(VerdictCapMs, ms);
            _endsAt = now.AddMilliseconds(_totalMs);
            return;
        }

        // Фінал розкривається поступово, від найгіршої до найкращої: очки ↑, голосів ↑, місце ↑.
        var order = new int[answers.Length];
        for (var i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (x, y) =>
        {
            var c = answers[x].Points.CompareTo(answers[y].Points);
            if (c == 0) c = answers[x].Voters.Count.CompareTo(answers[y].Voters.Count);
            return c != 0 ? c : answers[x].Seat.CompareTo(answers[y].Seat);
        });
        for (var p = 0; p < order.Length; p++)
        {
            answers[order[p]].Rank = order.Length - p;
            _revealPos[order[p]] = p;
        }
        _revealOrder = order;
        _shown = 0;
        _stepAt = now.AddMilliseconds(FinalStepMs);
        _totalMs = order.Length * FinalStepMs + FinalHoldMs;
        _endsAt = now.AddMilliseconds(_totalMs);
    }

    /// <summary>Чи відповідь <paramref name="i"/> — «Розгром» (див. коментар у <see cref="Reveal"/>).</summary>
    bool IsSweep(int i, Answer[] answers)
    {
        var author = answers[i].Seat;
        var eligible = 0;
        for (var s = 0; s < MaxSeats; s++)
        {
            if (!_voter[s] || !Present(s) || s == author) continue;
            eligible++;
            if (_picks[s] is not { Length: > 0 } p || p[0] != i) return false;
        }
        if (eligible < 2) return false;
        for (var j = 0; j < answers.Length; j++)
        {
            if (j == i) continue;
            foreach (var v in answers[j].Voters) if (v != author) return false;
        }
        return true;
    }

    /// <summary>Що каже Глек на розкритті картки раунду.</summary>
    string Verdict(Card card, int totalVotes)
    {
        if (totalVotes == 0) return DotepyLines.Silence;
        var answers = card.Answers;
        var top = -1;
        var max = -1;
        var tie = false;
        for (var i = 0; i < answers.Length; i++)
        {
            var v = answers[i].Voters.Count;
            if (v > max) { max = v; top = i; tie = false; }
            else if (v == max) tie = true;
        }
        if (tie) return DotepyLines.Tie;
        if (answers[top].Stock) return DotepyLines.StockWin;
        return card.Sweep == top ? DotepyLines.Sweep(Spoken(answers[top].Seat)) : DotepyLines.Win(Spoken(answers[top].Seat));
    }

    /// <summary>Фінал: ще одна відповідь розкривається, її очки — у рахунок саме зараз (таблиця не спойлерить).</summary>
    void FinalStep(DateTimeOffset now)
    {
        var card = _cards[_at];
        var k = card.Answers.Length;
        while (_shown < k && now >= _stepAt)
        {
            var a = card.Answers[_revealOrder[_shown]];
            _score[a.Seat] += a.Points;
            _shown++;
            _stepAt = _stepAt.AddMilliseconds(FinalStepMs);
            _dirty = true;
            if (_shown == k) SayNow(FinalVerdict(card));
        }
        if (_shown >= k && now >= _endsAt) Done();
    }

    string FinalVerdict(Card card)
    {
        var answers = card.Answers;
        var any = false;
        foreach (var a in answers) if (a.Voters.Count > 0) { any = true; break; }
        if (!any) return DotepyLines.Silence;
        var best = answers[_revealOrder[^1]];
        if (best.Stock) return DotepyLines.StockWin;
        var second = _revealOrder.Length > 1 ? answers[_revealOrder[^2]] : null;
        return second is not null && second.Points == best.Points ? DotepyLines.Tie : DotepyLines.FinalWin(Spoken(best.Seat));
    }

    /// <summary>Підсумок раунду між раундами: смужки рахунку й «Дотеп раунду».</summary>
    void Table(DateTimeOffset now)
    {
        _phase = PhaseTable;
        _at = -1;
        _totalMs = TableMs;
        _endsAt = now.AddMilliseconds(_totalMs);
        _dirty = true;
    }

    /// <summary>
    /// Кінець партії: переможці — найбільший рахунок серед присутніх (кілька — усі), усі по нулях — нічия.
    /// Кожному присутньому — рахунок у таблицю; королю дотепів за столом від п'ятьох — ачівка.
    /// </summary>
    void Done()
    {
        _phase = PhaseDone;
        _endsAt = null;
        _totalMs = 0;
        _pending = null;
        _at = -1;
        var present = new List<int>();
        for (var s = 0; s < MaxSeats; s++) if (Present(s)) present.Add(s);
        long best = 0;
        foreach (var s in present) best = Math.Max(best, _score[s]);
        _winners = best > 0 ? [.. present.Where(s => _score[s] == best)] : [];
        foreach (var s in present) Ctx.Score(s, _score[s]);
        if (_startedWith >= DuelFrom) foreach (var w in _winners) Ctx.Award(w, 0, "ach:dotepy-king");
        SayNow(_winners.Length == 1 ? DotepyLines.GameWin(Spoken(_winners[0])) : DotepyLines.GameTie);
        _dirty = true;
        Ctx.Finish(_winners, Summary(present), present.ToDictionary(s => s, s => _score[s]));
    }

    /// <summary>«Дотепи: Оля 3400, Петро 2100, Ганна 900 — розгромів: 2» — від більшого, бо ніки не відмінюємо.</summary>
    string Summary(List<int> seats)
    {
        if (seats.Count == 0) return $"{Info.Title}: за столом уже нікого";
        var line = string.Join(", ", seats.OrderByDescending(s => _score[s]).ThenBy(s => s)
            .Select(s => $"{NickOf(s)} {_score[s].ToString(CultureInfo.InvariantCulture)}"));
        var tail = _sweeps > 0 ? $" — розгромів: {_sweeps}" : "";
        if (_winners is { Length: 0 }) tail += " — нічия";
        return $"{Info.Title}: {line}{tail}";
    }

    string NickOf(int seat) => seat >= 0 && seat < MaxSeats && _nicks[seat] is { Length: > 0 } n ? n : SeatName(seat);

    /// <summary>
    /// Нік для голосу й вердикту: без приставки «гість » — «Розгром! Усі голоси — Оля!», а не «…— гість Оля!».
    /// У Журналі лишається повний нік: там важливо, хто саме.
    /// </summary>
    string Spoken(int seat) => Spoken(NickOf(seat));

    public static string Spoken(string nick) =>
        nick.StartsWith(Auth.GuestPrefix, StringComparison.OrdinalIgnoreCase) && nick.Length > Auth.GuestPrefix.Length
            ? nick[Auth.GuestPrefix.Length..].Trim() : nick;

    /// <summary>
    /// Хтось встав посеред партії: його незданe стане чернеткою/підставною, його голос більше не чекаємо, рахунок
    /// лишається в таблиці, але переможцем він не буде. Менше трьох присутніх — голосувати нема кому: кінець.
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (_phase is PhaseDone or PhaseLobby || seat < 0 || seat >= MaxSeats) return;
        // Види після виходу каркас розсилає сам (Rooms.Vacate), тож _dirty тут не треба.
        _left[seat] = true;
        if (PresentCount() >= MinSeats) return;

        _phase = PhaseDone;
        _endsAt = null;
        _totalMs = 0;
        _pending = null;
        _at = -1;
        var present = new List<int>();
        for (var s = 0; s < MaxSeats; s++) if (Present(s)) present.Add(s);
        long best = 0;
        foreach (var s in present) best = Math.Max(best, _score[s]);
        _winners = best > 0 ? [.. present.Where(s => _score[s] == best)] : [];
        // підсумок Глека теж: інакше на екрані кінця висіла б остання репліка партії («Раунд перший…»)
        SayNow(_winners.Length == 1 ? DotepyLines.GameWin(Spoken(_winners[0])) : DotepyLines.Gone);
        Ctx.Finish(_winners, _winners.Length > 0
            ? $"{Info.Title}: гравці розійшлись — попереду {string.Join(", ", _winners.Select(NickOf))}"
            : $"{Info.Title}: гравці розійшлись, партію не дограли");
    }

    // ---------------------------------------------------------------------------------------
    // ходи
    // ---------------------------------------------------------------------------------------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (_phase == PhaseDone) return ActResult.Fail("Партію зіграно, тисни «Ще раз»");
        return action switch
        {
            "draft" => Draft(seat, payload),
            "answer" => Submit(seat, payload),
            "edit" => Edit(seat, payload),
            "vote" => Vote(seat, payload),
            _ => ActResult.Fail("Тут так не ходять"),
        };
    }

    /// <summary>Моє місце на картці <paramref name="i"/> поточного раунду (або null, якщо картка не моя).</summary>
    Entry? Mine(int seat, int? i)
    {
        if (i is not { } k || k < 0 || k >= _cards.Count) return null;
        foreach (var e in _cards[k].Entries) if (e.Seat == seat) return e;
        return null;
    }

    /// <summary>Чернетка — мовчки, як усі Input: без розсилки видів; на F5 автор побачить свій текст.</summary>
    ActResult Draft(int seat, JsonElement payload)
    {
        if (_phase != PhaseWrite || !Present(seat)) return ActResult.Fail("Зараз не пишуть");
        if (Mine(seat, Int(payload, "i")) is not { Done: false } e) return ActResult.Fail("Це не твоє завдання");
        e.Draft = Cut(Clean(Str(payload, "text")), MaxAnswer);
        return ActResult.Done;
    }

    ActResult Submit(int seat, JsonElement payload)
    {
        if (_phase != PhaseWrite) return ActResult.Fail("Зараз не пишуть");
        if (Mine(seat, Int(payload, "i")) is not { } e) return ActResult.Fail("Це не твоє завдання");
        var text = Clean(Str(payload, "text"));
        if (text.Length == 0) return ActResult.Fail("Порожній дотеп — то ще не дотеп");
        if (text.Length > MaxAnswer) return ActResult.Fail($"Задовго: до {MaxAnswer} знаків");
        e.Text = text;
        e.Draft = text;
        e.Done = true;
        _dirty = true;
        // Усі автори цієї картки здали — читання вже відоме: хай Глек озвучує його, поки решта пише.
        if (VoiceOn && _cards[Int(payload, "i")!.Value] is var card && card.Entries.TrueForAll(x => x.Done))
            Prepare([DotepyLines.Card(card.Prompt.Text, [.. card.Order.Select(k => card.Entries[k].Text)])], urgent: true);
        return ActResult.Done;
    }

    ActResult Edit(int seat, JsonElement payload)
    {
        if (_phase != PhaseWrite) return ActResult.Fail("Зараз не пишуть");
        if (Mine(seat, Int(payload, "i")) is not { } e) return ActResult.Fail("Це не твоє завдання");
        if (!e.Done) return ActResult.Fail("Ще не здано");
        e.Done = false;
        e.Draft = e.Text;
        _dirty = true;
        return ActResult.Done;
    }

    /// <summary>Голос за відповіді поточної картки (фінал — ранжовано, 🥇 першим). Повторний замінює попередній.</summary>
    ActResult Vote(int seat, JsonElement payload)
    {
        if (_phase != PhaseVote) return ActResult.Fail("Зараз не голосують");
        if (Int(payload, "card") != _at) return ActResult.Fail("Ця картка вже пішла");
        if (!_voter[seat] || !Present(seat)) return ActResult.Fail("Це твій дотеп — за нього голосують інші");
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("picks", out var raw)
            || raw.ValueKind != JsonValueKind.Array || raw.GetArrayLength() == 0) return ActResult.Fail("Обери хоч один");
        if (raw.GetArrayLength() > _perVoter) return ActResult.Fail("Забагато голосів");
        var picks = new int[raw.GetArrayLength()];
        var n = 0;
        foreach (var x in raw.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Number || !x.TryGetInt32(out var v)) return ActResult.Fail("Обери хоч один");
            picks[n++] = v;
        }
        for (var i = 0; i < picks.Length; i++)
            for (var j = 0; j < i; j++)
                if (picks[i] == picks[j]) return ActResult.Fail("Один дотеп — один голос");
        var answers = _cards[_at].Answers;
        foreach (var p in picks) if (p < 0 || p >= answers.Length) return ActResult.Fail("Такої відповіді нема");
        foreach (var p in picks) if (answers[p].Seat == seat) return ActResult.Fail("За себе не голосують");
        _picks[seat] = picks;
        _dirty = true;
        return ActResult.Done;
    }

    /// <summary>
    /// Голос публіки (глядач, specs/dotepy.md §3.2). Кличе <see cref="DotepyJury"/> під <c>room.Sync</c>, поза
    /// <c>Collect</c> — тому жодного <c>Ctx.*</c> тут нема: види розішле наступний тик.
    /// </summary>
    public ActResult JuryVote(string nickKey, int card, int pick)
    {
        if (_phase != PhaseVote) return ActResult.Fail("Зараз не голосують");
        if (card != _at) return ActResult.Fail("Ця картка вже пішла");
        if (pick < 0 || pick >= _cards[_at].Answers.Length) return ActResult.Fail("Такої відповіді нема");
        _jury[nickKey] = pick;
        _dirty = true;
        return ActResult.Accept("Голос публіки прийнято");
    }

    static int? Int(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    static string? Str(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// Чистка дотепу: без керівних і невидимих службових символів, пробіли по одному, без пробілів по краях.
    /// Довжину не ріже — «задовго» вирішує той, хто кличе (здати — відмова, чернетка — обрізати).
    /// </summary>
    public static string Clean(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new StringBuilder(raw.Length);
        var space = false;
        foreach (var ch in raw)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                space = true;
                continue;
            }
            // невидимі службові (напрямок тексту, м'який перенос) — геть, але ZWJ/селектори емодзі лишаємо: без них 👨‍👩‍👧 розсиплеться
            if (char.GetUnicodeCategory(ch) == UnicodeCategory.Format && ch is not ('‍' or '️')) continue;
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>Обрізати до <paramref name="max"/>, не розрізаючи емодзі навпіл.</summary>
    static string Cut(string s, int max)
    {
        if (s.Length <= max) return s;
        var n = char.IsHighSurrogate(s[max - 1]) ? max - 1 : max;
        return s[..n].TrimEnd();
    }

    // ---------------------------------------------------------------------------------------
    // голос
    // ---------------------------------------------------------------------------------------

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

    /// <summary>Репліка без очікування (вступ, вердикт): з голосом, якщо кліп готовий, інакше текстом. Повертає, скільки мс звучить.</summary>
    double SayNow(string text)
    {
        var clip = VoiceOn ? Clip(text) : null;
        var seconds = clip?.Seconds ?? text.Length / CharsPerSec;
        _say = new Speech(++_sayId, text, clip?.Url, Math.Round(seconds, 2));
        _dirty = true;
        return seconds * 1000;
    }

    // ---------------------------------------------------------------------------------------
    // види
    // ---------------------------------------------------------------------------------------

    public override object View(int? seat)
    {
        var me = seat is { } s && s >= 0 && s < MaxSeats ? s : -1;
        return new
        {
            phase = _phase,
            round = _round,
            rounds = _roundsTotal,
            final = _final,
            mode = _mode,
            endsAt = _endsAt,
            totalMs = _totalMs,
            waiting = _pending is not null,
            voice = _voiceName,
            players = PlayersView(),
            // завдання раунду — лише поки пишуть: глядачам і тим, хто вже здав («на що пишуть інші»)
            prompts = _phase == PhaseWrite ? _cards.Select(c => c.Prompt.Text).ToArray() : [],
            me = seat is null ? null : MeView(me),
            card = _phase is PhaseVote or PhaseReveal && _at >= 0 && _at < _cards.Count ? CardView(_cards[_at]) : null,
            say = _say is { } l ? new { id = l.Id, text = l.Text, url = l.Url, seconds = l.Seconds } : null,
            table = _phase == PhaseTable ? TableView() : null,
            result = _phase == PhaseDone && _winners is not null ? ResultView() : null,
        };
    }

    object[] PlayersView()
    {
        var list = new List<object>(MaxSeats);
        for (var s = 0; s < MaxSeats; s++)
        {
            if (!_inGame[s]) continue;
            list.Add(new
            {
                seat = s,
                nick = _nicks[s],
                score = _score[s],
                ready = _phase == PhaseWrite && Ready(s),
                voted = _phase is PhaseVote or PhaseReveal && Complete(s),
                left = _left[s],
            });
        }
        return [.. list];
    }

    /// <summary>Усі свої завдання здано.</summary>
    bool Ready(int seat)
    {
        var any = false;
        foreach (var card in _cards)
            foreach (var e in card.Entries)
            {
                if (e.Seat != seat) continue;
                if (!e.Done) return false;
                any = true;
            }
        return any;
    }

    object MeView(int seat)
    {
        var tasks = new List<object>();
        var mine = new List<int>();
        if (seat >= 0 && _phase == PhaseWrite)
            for (var k = 0; k < _cards.Count; k++)
                foreach (var e in _cards[k].Entries)
                    if (e.Seat == seat)
                        tasks.Add(new { i = k, prompt = _cards[k].Prompt.Text, text = e.Done ? e.Text : e.Draft, done = e.Done });
        if (seat >= 0 && _phase is PhaseVote or PhaseReveal && _at >= 0 && _at < _cards.Count)
        {
            var answers = _cards[_at].Answers;
            for (var i = 0; i < answers.Length; i++) if (answers[i].Seat == seat) mine.Add(i);
        }
        var voting = seat >= 0 && _phase is PhaseVote or PhaseReveal;
        return new
        {
            tasks = tasks.ToArray(),
            mine = mine.ToArray(),
            voter = voting && _voter[seat] && Present(seat),
            picks = voting && _picks[seat] is { } p ? (int[])p.Clone() : [],
        };
    }

    object CardView(Card card)
    {
        var reveal = _phase == PhaseReveal;
        var answers = new object[card.Answers.Length];
        for (var i = 0; i < answers.Length; i++)
        {
            var a = card.Answers[i];
            // Поки голосують — анонімно для всіх (і глядача): ні автора, ні голосів. Фінал відкривається по одній.
            var open = reveal && (!_final || _revealPos[i] < _shown);
            answers[i] = new
            {
                text = a.Text,
                stock = a.Stock,
                seat = open ? a.Seat : (int?)null,
                votes = open ? a.Voters.ToArray() : null,
                medals = open && _final ? a.Medals.ToArray() : null,
                jury = open ? a.Jury : (int?)null,
                points = open ? a.Points : (int?)null,
                rank = open && _final ? a.Rank : (int?)null,
                prize = open ? a.Prize : (bool?)null,
            };
        }
        var voters = new List<int>(MaxSeats);
        var voted = new List<int>(MaxSeats);
        for (var s = 0; s < MaxSeats; s++)
        {
            if (_voter[s] && Present(s)) voters.Add(s);
            if (Complete(s)) voted.Add(s);
        }
        return new
        {
            i = _at,
            of = _cards.Count,
            prompt = card.Prompt.Text,
            answers,
            voters = voters.ToArray(),
            voted = voted.ToArray(),
            juryVotes = _jury.Count,
            perVoter = _perVoter,
            ranked = _final,
            sweep = reveal && card.Sweep >= 0 ? card.Sweep : (int?)null,
            shown = reveal && _final ? _shown : card.Answers.Length,
        };
    }

    object TableView() => new
    {
        rows = Enumerable.Range(0, MaxSeats).Where(s => _inGame[s])
            .OrderByDescending(s => _score[s]).ThenBy(s => s)
            .Select(s => new { seat = s, score = _score[s], delta = _score[s] - _roundFrom[s] }).ToArray(),
        best = _roundBest is { } b ? BestView(b) : null,
    };

    object ResultView() => new
    {
        winners = (int[])_winners!.Clone(),
        scores = (long[])_score.Clone(),
        best = _bests.OrderByDescending(b => b.Points).ThenBy(b => b.Round).Take(3).Select(BestView).ToArray(),
    };

    static object BestView(Best b) => new { prompt = b.Prompt, text = b.Text, seat = b.Seat, points = b.Points };
}
