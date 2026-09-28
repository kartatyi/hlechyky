using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Вгадай мелодію» (specs/melody.md). Звучить уривок треку, який уже грав на радіо, — хто перший назве виконавця
/// й назву, той бере більше очок. Треки й уривки — з кешу радіо через ffmpeg (<see cref="MelodyLibrary"/>).
///
/// Уривки ріжуться у фоні, поза замком кімнати: на старті партія чекає лише перший, решта готується, поки грають.
/// Браузер тягне уривок за випадковим токеном (<see cref="MelodyClips"/>) — ні в адресі, ні у файлі назви нема.
/// </summary>
public sealed class Melody : Game
{
    public const int TickMs = 250;
    /// <summary>Скільки часу на здогадки понад довжину уривка.</summary>
    public const int ExtraMs = 15_000;
    public const int RevealMs = 7_000;
    /// <summary>Скільки чекати, поки наріжуться уривки, перш ніж здатись.</summary>
    public const int LoadTimeoutMs = 60_000;
    public const int GuessEveryMs = 700;
    public const int ArtistPoints = 50, TitlePoints = 100, ArtistFirst = 20, TitleFirst = 30;
    /// <summary>
    /// Скільки зайвих треків беремо про запас — на випадок, коли уривок не наріжеться чи пісня з добірки не скачається
    /// (YouTube буває відповідає 403 на цілі серії): щонайменше стільки, а загалом — удвічі більше за раунди.
    /// </summary>
    const int Spare = 4;
    /// <summary>Скільки пісень із добірки качаються водночас, наперед: один невдалий 403 тоді не зупиняє стіл.</summary>
    const int FetchAhead = 2;
    /// <summary>
    /// За скільки до кінця розкриття стіл «поспішає»: наступний трек ще качається — хай замість нього звучить
    /// готовий із диска, а той докачається й прозвучить пізніше. Уривок ріжеться ~1 с, тож 3 с вистачає без паузи.
    /// </summary>
    public const int HurryLeadMs = 3_000;
    /// <summary>
    /// Скільки місць за столом. Було вісім; дванадцять — як у «Скільки?»: на велику компанію гра так само годиться
    /// (кожен вгадує сам, черги нема), а стіл на вісім лишав решту глядачами.
    /// </summary>
    public const int Seats = 12;
    const int MaxGuess = 80;

    // ---------- прохід №3 (29.09): швидкість, «з першої ноти», варіанти, «хто закинув», команди, дуель ----------
    /// <summary>Вгадав у перші 5 с треку — трішки більше очок (не множник: інакше одна вдала секунда вирішувала партію).</summary>
    public const int SpeedMs = 5_000, SpeedBonus = 20;
    /// <summary>«З першої ноти»: уривок росте — 3, 6, 10 с; між ними пауза подумати. Вгадав на коротшому — бонус.</summary>
    public static readonly int[] GrowSec = [3, 6, 10];
    public static readonly int[] GrowBonus = [20, 10, 0];
    public const int GrowPauseMs = 4_000;
    /// <summary>Варіанти виконавців з'являються в останні 5 секунд уривка; відтоді виконавець коштує вдвічі менше.</summary>
    public const int ChoicesLeadMs = 5_000, ChoicesCount = 4;
    /// <summary>«Хто закинув?»: скільки за правильного замовника і від скількох пісень категорія відкривається.</summary>
    public const int WhoPoints = 50, WhoMin = 5;
    /// <summary>Фінальна дуель: хто вгадав, котрий із двох лідерів візьме фінальний трек.</summary>
    public const int BetPoints = 50, DuelMin = 3;
    /// <summary>Команди — від шести за столом: менше — це вже не диван, а кожен сам.</summary>
    public const int TeamsMin = 6;
    public static readonly string[] TeamNames = ["🟠 Глечики", "🔵 Макітри"];

    public static readonly int[] RoundChoices = [5, 10, 15];
    public static readonly int[] ClipChoices = [10, 15, 20];

    const string Loading = "loading", Play = "play", Reveal = "reveal", Done = "done";

    public override GameInfo Info { get; } = new(
        "melody", "Вгадай мелодію", "«Вгадай мелодію»", GameGroup.Party, 1, Seats,
        TickMs: TickMs, Start: StartMode.ByHost, Hidden: true, Score: ScoreOrder.HigherIsBetter,
        Options:
        [
            new GameOption("rounds", "Треків", [.. RoundChoices.Select(n => (n.ToString(), n.ToString()))], "10"),
            new GameOption("clip", "Звучить", [.. ClipChoices.Select(n => (n.ToString(), $"{n} с"))], "15"),
            new GameOption("cat", "Пісні", MelodyCategories.Values(MelodyClassics.Default), MelodyCategories.All, Multi: true),
            new GameOption("grow", "Уривок", [("0", "звучить одразу весь"), ("1", "з першої ноти: 3 → 6 → 10 с")], "0"),
            new GameOption("choices", "Підказка", [("0", "без варіантів"), ("1", "4 виконавці в останні 5 с")], "0"),
            new GameOption("teams", "Грають", [("0", "кожен сам"), ("1", "дві команди (від 6)")], "0"),
            new GameOption("duel", "Останній трек", [("0", "для всіх"), ("1", "дуель двох лідерів")], "0"),
        ],
        Hint: "Звучить уривок пісні — з радіо або зі світової класики. Пиши виконавця й назву — хто перший, той бере більше");

    /// <summary>
    /// Місця — просто номерами, як у «Скільки?»: типове каркасне «перший», «другий», «гравець 3» на столі з
    /// дванадцяти місць давало різнобій, а довгі чіпи вільних місць з'їдали пів шапки.
    /// </summary>
    public override string SeatName(int seat) => (seat + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Готовий раунд: уривки (один; у «з першої ноти» — три, 3/6/10 с), варіанти виконавців (якщо увімкнено) і хто
    /// з-за столу закидав цю пісню на радіо (лише «Хто закинув?», інакше null).
    /// </summary>
    sealed record Prepared(MelodyTrack Track, string[] Tokens, string[] Choices, string[]? By);

    sealed class Found
    {
        public bool Artist, Title;
        public int Points;
        /// <summary>За скільки секунд узяв бонус швидкості (0 — не взяв).</summary>
        public int Fast;
        /// <summary>Помилився у варіантах — цей трек уже без нього.</summary>
        public bool Blocked;
        /// <summary>«Хто закинув?»: null — ще не пробував, true/false — вгадав чи ні.</summary>
        public bool? Who;
    }

    IMelodySource _source = null!;
    int _rounds = 10;
    int _clipSec = 15;
    /// <summary>Обрані категорії (<see cref="MelodyCategories"/>); порожньо не буває — «усе» розгортається в усі.</summary>
    IReadOnlyList<string> _categories = MelodyCategories.Radio;
    bool _grow, _choicesOn, _teamsOn, _duelOn;

    // ---------- підготовка (фон) ----------
    CancellationTokenSource? _cts;
    /// <summary>Номер раунду → готовий уривок. Пише фонова задача, читає тик.</summary>
    ConcurrentDictionary<int, Prepared> _ready = new();
    /// <summary>Скільки раундів точно буде (фон уже знає, скільки треків вдалось нарізати). -1 — ще невідомо.</summary>
    volatile int _available = -1;
    /// <summary>Більше раундів не буде точно: нарізане плюс ще не спробувані треки (для «Трек 3 з N», поки фон працює).</summary>
    volatile int _cap = int.MaxValue;
    /// <summary>На який раунд стіл уже чекає (чи от-от чекатиме) — фон тоді не жде пісню, що качається, а бере готову.</summary>
    volatile int _hurry;
    /// <summary>Будильник для фону: стіл почав поспішати. Новий на кожен раунд, продовження — не під замком кімнати.</summary>
    TaskCompletionSource _hurrySignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ---------- партія ----------
    string _phase = Loading;
    int _round;
    DateTimeOffset _until;
    DateTimeOffset _loadStarted;
    int _totalMs;
    readonly int[] _scores = new int[Seats];
    Dictionary<int, Found> _found = [];
    /// <summary>Хто в цьому раунді готовий пропустити трек.</summary>
    readonly HashSet<int> _skip = [];
    bool _artistTaken, _titleTaken;
    readonly HashSet<int> _left = [];
    readonly Dictionary<int, DateTimeOffset> _lastGuess = [];
    object? _result;
    string? _error;
    bool _dirty;
    /// <summary>Пояснення посеред партії (фон пише, вид читає): «Хто закинув?» пропустили, команд не буде тощо.</summary>
    volatile string? _note;
    /// <summary>Чому грати нема в що — коли фон це знає краще за «треків нема» («Хто закинув?» без замовлень).</summary>
    volatile string? _empty;
    /// <summary>Ніки за столом на старті — для «Хто закинув?» (фон їх читає, Ctx не чіпає).</summary>
    string[] _nicks = [];

    // раунд
    DateTimeOffset _roundStart;
    int _stage;
    bool _choicesOpen;

    // команди: місце → 0/1 (-1 — поза командами), очки команд і що команда вже вгадала в цьому раунді
    bool _teams;
    readonly int[] _team = new int[Seats];
    readonly int[] _teamScore = new int[2];
    readonly Found[] _teamFound = [new(), new()];

    // фінальна дуель: два лідери, на якому раунді, ставки «хто візьме» (місце → на кого), чи ставки ще приймаються
    int _duelA = -1, _duelB = -1, _duelRound;
    readonly Dictionary<int, int> _bets = [];
    bool _betsClosed;
    /// <summary>Хто взяв фінальний трек: -2 — ще не ясно, -1 — ніхто (нічия чи ніхто не вгадав).</summary>
    int _duelWinner = -2;

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _source = Ctx.Services.GetService<IMelodySource>()
            ?? new MelodyLibrary(Ctx.Services.GetService<Db>(), Ctx.Services.GetService<IOptionsMonitor<YtDlpOptions>>());
        if (options.TryGetValue("rounds", out var r) && int.TryParse(r, out var rn) && RoundChoices.Contains(rn)) _rounds = rn;
        _categories = MelodyCategories.Parse(options.GetValueOrDefault("cat"), MelodyClassics.Default);
        if (options.TryGetValue("clip", out var c) && int.TryParse(c, out var cn) && ClipChoices.Contains(cn)) _clipSec = cn;
        _grow = options.GetValueOrDefault("grow") == "1";
        _choicesOn = options.GetValueOrDefault("choices") == "1";
        _teamsOn = options.GetValueOrDefault("teams") == "1";
        _duelOn = options.GetValueOrDefault("duel") == "1";
    }

    /// <summary>Скільки звучить уривок: звичайно — як обрано, «з першої ноти» — найдовший із трьох.</summary>
    int ClipSec => _grow ? GrowSec[^1] : _clipSec;

    /// <summary>Коли (від початку раунду) починається кожен уривок «з першої ноти»: 0, 7, 17 с.</summary>
    static int GrowAt(int stage)
    {
        var at = 0;
        for (var i = 0; i < stage; i++) at += GrowSec[i] * 1000 + GrowPauseMs;
        return at;
    }

    /// <summary>Коли (від початку раунду) уривок скінчиться — від цього рахуються й варіанти.</summary>
    int ClipEndMs => _grow ? GrowAt(GrowSec.Length - 1) + GrowSec[^1] * 1000 : _clipSec * 1000;

    public override void Start()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ready = new ConcurrentDictionary<int, Prepared>();
        _available = -1;
        _cap = int.MaxValue;
        _hurry = 0;
        _hurrySignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Array.Clear(_scores);
        _left.Clear();
        _lastGuess.Clear();
        _found = [];
        _result = null;
        _error = null;
        _note = null;
        _empty = null;
        _round = 0;
        _bets.Clear();
        _duelA = _duelB = -1;
        _duelRound = 0;
        _duelWinner = -2;
        _betsClosed = false;
        SetupTeams();
        var nicks = new List<string>();
        for (var s = 0; s < Seats; s++) if (Ctx.NickOf(s) is { } n) nicks.Add(n);
        _nicks = [.. nicks];
        _by = null;
        _phase = Loading;
        _loadStarted = Now;
        _dirty = true;
        // Сід для фону беремо з Rng кімнати тут, під замком: сама фонова задача Ctx.Rng не чіпає.
        var seed = Ctx.Rng.Next();
        var ready = _ready;
        var ct = _cts.Token;
        // Без Task.Run: справжнє джерело віддає керування на першому ж await (вибір треків іде в Task.Run, ffmpeg —
        // окремий процес), тож під замком кімнати нічого важкого не робиться. А джерело, яке відповідає одразу
        // (підробка в тестах), наріже все ще тут — без гонки між фоном і тиком, яку повільний CI програвав.
        _ = PrepareAsync(ready, seed, ct);
    }

    DateTimeOffset Now => Ctx.Clock.UtcNow;

    async Task PrepareAsync(ConcurrentDictionary<int, Prepared> ready, int seed, CancellationToken ct)
    {
        try
        {
            var rng = new Random(seed);
            var need = Math.Max(_rounds + Spare, _rounds * 2);
            var picks = await Picks(need, rng, ct);
            if (picks is null) { _available = 0; return; }
            var by = _by;
            var artists = _choicesOn ? Artists(picks) : [];
            var fetch = new List<Task<MelodyTrack?>?>(picks.Select(_ => (Task<MelodyTrack?>?)null));
            var round = 0;
            for (var i = 0; i < picks.Count && round < _rounds; i++)
            {
                ct.ThrowIfCancellationRequested();
                _cap = round + picks.Count - i;
                // пісні з добірки, яких ще нема на диску, качаються наперед — уже під час гри, поки звучать попередні
                Ahead(picks, fetch, i, ct);
                var t = await Take(picks, fetch, i, round + 1, ct);
                if (t is null) continue;
                var len = t.DurationSec > 0 ? t.DurationSec : 180;
                // з першої чверті до 60% — там зазвичай куплет або приспів, а не тиша інтро
                var from = len * 0.25;
                var to = Math.Max(from, Math.Min(len * 0.6, len - ClipSec - 2));
                var start = Math.Max(0, from + rng.NextDouble() * (to - from));
                // «з першої ноти» — три уривки з того самого місця: 3, 6 і 10 с (ffmpeg тричі, але у фоні й наперед)
                var lengths = _grow ? GrowSec : [_clipSec];
                var tokens = new string[lengths.Length];
                var ok = true;
                for (var k = 0; k < lengths.Length && ok; k++)
                {
                    var data = await _source.ClipAsync(t, start, lengths[k], ct);
                    if (data is null) ok = false;
                    else tokens[k] = MelodyClips.Put(data);
                }
                if (!ok) continue;
                string[]? who = null;
                if (by is not null && by.TryGetValue(t.Id, out var w)) who = w;
                ready[++round] = new Prepared(t, tokens, _choicesOn ? Choices(t, artists, rng) : [], who);
            }
            if (!ct.IsCancellationRequested) _available = round;
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!ct.IsCancellationRequested) _available = ready.Count;
        }
    }

    /// <summary>Хто з-за столу закидав пісні «Хто закинув?»: id треку → ніки. Пише фон до першого раунду.</summary>
    volatile Dictionary<string, string[]>? _by;

    /// <summary>
    /// Треки партії. Звичайно — з джерела за категоріями. З «Хто закинув?» — ще й пісні, які закидали гравці столу,
    /// вперемішку з рештою обраних категорій (своя частка, як у кожної категорії). Замовлень замало — категорія
    /// випадає з поясненням; якщо вона була єдина — null, грати нема в що.
    /// </summary>
    async Task<List<MelodyTrack>?> Picks(int need, Random rng, CancellationToken ct)
    {
        var others = _categories.Where(c => c != MelodyCategories.Who).ToList();
        List<MelodyTrack>? who = null;
        if (others.Count < _categories.Count)
        {
            var asked = await _source.RequestedAsync(_nicks, ct);
            if (asked.Count >= WhoMin)
            {
                var list = asked.ToList();
                for (var i = list.Count - 1; i > 0; i--) { var j = rng.Next(i + 1); (list[i], list[j]) = (list[j], list[i]); }
                _by = list.ToDictionary(x => x.Track.Id, x => x.By.ToArray(), StringComparer.Ordinal);
                who = [.. list.Select(x => x.Track)];
            }
            else
            {
                var why = asked.Count == 0
                    ? "«Хто закинув?» — ви ще нічого не закидали на радіо"
                    : $"«Хто закинув?» — ви закинули на радіо лише {Songs(asked.Count)}, а треба хоч {WhoMin}";
                if (others.Count == 0) { _empty = why + ". Закидайте — і категорія відкриється"; return null; }
                _note = why + " — граємо без неї";
            }
        }
        if (who is null) return [.. await _source.PickAsync(need, others, rng, ct)];
        if (others.Count == 0) return who;
        var rest = (await _source.PickAsync(need, others, rng, ct)).ToList();
        var mine = new HashSet<string>(who.Select(t => SongKey.Of(t.Artist, t.Title)), StringComparer.Ordinal);
        rest.RemoveAll(t => mine.Contains(SongKey.Of(t.Artist, t.Title)));
        return Mix(who, rest, others.Count + 1);
    }

    static string Songs(int n) => n % 10 == 1 && n % 100 != 11 ? $"{n} пісню"
        : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? $"{n} пісні" : $"{n} пісень";

    /// <summary>Кожен <paramref name="every"/>-й трек — з «Хто закинув?», решта — з інших категорій; закінчився один список — далі другий.</summary>
    public static List<MelodyTrack> Mix(List<MelodyTrack> who, List<MelodyTrack> rest, int every)
    {
        var all = new List<MelodyTrack>(who.Count + rest.Count);
        int w = 0, r = 0;
        for (var k = 0; w < who.Count || r < rest.Count; k++)
        {
            var takeWho = k % every == 0 ? w < who.Count : r >= rest.Count;
            all.Add(takeWho ? who[w++] : rest[r++]);
        }
        return all;
    }

    /// <summary>Різні виконавці партії (з запасом) — з них беруться хибні варіанти.</summary>
    static List<string> Artists(List<MelodyTrack> picks)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var t in picks) if (t.Artist.Length > 0 && seen.Add(t.Artist)) list.Add(t.Artist);
        return list;
    }

    /// <summary>
    /// Чотири виконавці на вибір: справжній і троє інших із цієї ж партії (схожих на справжнього не беремо —
    /// «Океан Ельзи» і «Океан Ельзи feat. …» були б двома правильними). Порядок — випадковий.
    /// </summary>
    public static string[] Choices(MelodyTrack t, IReadOnlyList<string> artists, Random rng)
    {
        var right = MelodyAnswer.Artists(t);
        var fakes = artists.Where(a => !MelodyAnswer.Hits(a, right) && !right.Any(x => MelodyAnswer.Hits(x, [a]))).ToList();
        for (var i = fakes.Count - 1; i > 0; i--) { var j = rng.Next(i + 1); (fakes[i], fakes[j]) = (fakes[j], fakes[i]); }
        var all = new List<string> { t.Artist };
        all.AddRange(fakes.Take(ChoicesCount - 1));
        for (var i = all.Count - 1; i > 0; i--) { var j = rng.Next(i + 1); (all[i], all[j]) = (all[j], all[i]); }
        return [.. all];
    }

    /// <summary>Почати качати наперед ще не початі пісні з добірки, починаючи з <paramref name="from"/>, — щоб водночас качалось не більше <see cref="FetchAhead"/>.</summary>
    void Ahead(List<MelodyTrack> picks, List<Task<MelodyTrack?>?> fetch, int from, CancellationToken ct)
    {
        var busy = 0;
        foreach (var f in fetch) if (f is { IsCompleted: false }) busy++;
        for (var j = from; j < picks.Count && busy < FetchAhead; j++)
        {
            if (!picks[j].Pending || fetch[j] is not null) continue;
            fetch[j] = _source.ResolveAsync(picks[j], ct);
            if (!fetch[j]!.IsCompleted) busy++;
        }
    }

    /// <summary>
    /// Трек для раунду <paramref name="slot"/> з місця <paramref name="i"/>: з диска — одразу; з добірки — коли
    /// докачається (null — не скачалась: 403, не знайшлась, — і тоді береться наступний). Але якщо стіл уже
    /// поспішає саме на цей раунд, а пісня ще качається, то на її місце стає перша готова з решти списку, а вона
    /// сама зсувається на одне місце далі: докачається — прозвучить наступною, а стіл не стоїть.
    /// </summary>
    async Task<MelodyTrack?> Take(List<MelodyTrack> picks, List<Task<MelodyTrack?>?> fetch, int i, int slot, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!picks[i].Pending) return await _source.ResolveAsync(picks[i], ct);
            var f = fetch[i] ??= _source.ResolveAsync(picks[i], ct);
            if (f.IsCompleted) return await f;
            var alarm = _hurrySignal.Task;          // спершу будильник, потім умова: інакше можна проспати дзвінок
            if (_hurry >= slot && ReadyLater(picks, fetch, i) is { } j)
            {
                var (p, t) = (picks[j], fetch[j]);
                picks.RemoveAt(j);
                fetch.RemoveAt(j);
                picks.Insert(i, p);
                fetch.Insert(i, t);
                continue;
            }
            await Task.WhenAny(f, alarm).WaitAsync(ct);
        }
    }

    /// <summary>Перше місце після <paramref name="i"/>, де трек уже можна різати: з диска або вже докачаний.</summary>
    static int? ReadyLater(List<MelodyTrack> picks, List<Task<MelodyTrack?>?> fetch, int i)
    {
        for (var j = i + 1; j < picks.Count; j++)
            if (!picks[j].Pending || fetch[j] is { IsCompletedSuccessfully: true, Result: not null }) return j;
        return null;
    }

    /// <summary>Стіл чекає (чи от-от чекатиме) на раунд <paramref name="slot"/>: розбудити фон, якщо він жде повільну пісню.</summary>
    void Hurry(int slot)
    {
        if (_hurry >= slot) return;
        _hurry = slot;
        Interlocked.Exchange(ref _hurrySignal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    // =========================================================================================
    // Хід часу
    // =========================================================================================

    public override TickResult Tick()
    {
        var now = Now;
        switch (_phase)
        {
            case Loading:
                if (_ready.ContainsKey(_round + 1)) BeginRound();
                else if (_available >= 0 && _round >= _available) Over(_available == 0 ? _empty ?? NoTracks() : Short());
                else if ((now - _loadStarted).TotalMilliseconds > LoadTimeoutMs)
                    Over(_round == 0 ? "Ой-йой — пісні не встигли ні скачатись, ні нарізатись" : Short());
                else Hurry(_round + 1);
                break;
            case Play:
                PlayClock(now);
                // Раунд закінчується, коли кожен або вгадав усе, або готовий пропустити (а не всі вже вгадали — не чекати ж).
                if (now >= _until || AllDone()) EndRound();
                break;
            case Reveal:
                // наступний трек ще качається, а розкриття от-от скінчиться — хай фон бере готовий із диска
                if (_round < _rounds && (_until - now).TotalMilliseconds <= HurryLeadMs && !_ready.ContainsKey(_round + 1)) Hurry(_round + 1);
                if (now < _until) break;
                if (_round >= _rounds) { Over(null); break; }
                _phase = Loading;
                _loadStarted = now;
                _dirty = true;
                if (_ready.ContainsKey(_round + 1)) BeginRound();
                break;
        }
        if (_phase != Done && !AnyPresent()) Over(null);
        if (!_dirty) return TickResult.None;
        _dirty = false;
        return new TickResult(Frame: false, View: true);
    }

    /// <summary>Годинник раунду: наступний уривок «з першої ноти», поява варіантів. Без алокацій — це тик.</summary>
    void PlayClock(DateTimeOffset now)
    {
        var ms = (now - _roundStart).TotalMilliseconds;
        if (_grow)
        {
            var stage = 0;
            for (var k = GrowSec.Length - 1; k > 0; k--) if (ms >= GrowAt(k)) { stage = k; break; }
            if (stage != _stage) { _stage = stage; _dirty = true; }
        }
        if (_choicesOn && !_choicesOpen && ms >= ClipEndMs - ChoicesLeadMs && Current is { Choices.Length: > 1 })
        {
            _choicesOpen = true;
            _dirty = true;
        }
    }

    Prepared? Current => _round > 0 && _ready.TryGetValue(_round, out var p) ? p : null;

    bool DuelNow => _duelA >= 0 && _round == _duelRound;

    bool Duelist(int s) => s == _duelA || s == _duelB;

    bool IsPresent(int s) => Ctx.Seated(s) && !_left.Contains(s);

    IEnumerable<int> Present() => Enumerable.Range(0, Seats).Where(IsPresent);

    // Тик — чотири рази на секунду на кожен стіл: без LINQ і нових колекцій.
    bool AnyPresent()
    {
        for (var s = 0; s < Seats; s++) if (IsPresent(s)) return true;
        return false;
    }

    /// <summary>Кожен присутній або вгадав усе, або готовий пропустити (і хоч хтось присутній).</summary>
    bool AllDone()
    {
        var any = false;
        for (var s = 0; s < Seats; s++)
        {
            if (!IsPresent(s)) continue;
            if (!Finished(s) && !_skip.Contains(s)) return false;
            any = true;
        }
        return any;
    }

    /// <summary>Партія вийшла коротшою за обрану: частина пісень так і не скачалась (YouTube відмовив) — кажемо чесно.</summary>
    string Short() => $"Глек дістав лише {Tracks(_round)} з {_rounds} — решта пісень не скачалась. Зіграли, що було";

    static string Tracks(int n) => n % 10 == 1 && n % 100 != 11 ? $"{n} трек"
        : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? $"{n} треки" : $"{n} треків";

    string NoTracks() => _categories.Count == 1 && _categories[0] == MelodyCategories.Ua
        ? "Українських треків у кеші радіо ще нема — грати нема в що"
        : "Треків для цих категорій ще нема — грати нема в що";

    /// <summary>
    /// Цьому місцю в раунді вже нема чого робити: вгадав (чи вгадала команда) і виконавця, і назву, а в «Хто
    /// закинув?» — ще й спробував замовника; або помилився у варіантах; або це фінальна дуель, а він лише ставить.
    /// </summary>
    bool Finished(int seat)
    {
        if (DuelNow && !Duelist(seat)) return true;
        _found.TryGetValue(seat, out var mine);
        if (mine is { Blocked: true }) return true;
        var f = TeamOf(seat) is var t and >= 0 ? _teamFound[t] : mine;
        if (f is null || !f.Artist || !f.Title) return false;
        return Current?.By is null || f.Who is not null;
    }

    int TeamOf(int seat) => _teams ? _team[seat] : -1;

    /// <summary>Команди на старті: від шести за столом, непарні місця — одна команда, парні — друга; вийшло криво (усі на парних) — по черзі.</summary>
    void SetupTeams()
    {
        _teams = false;
        Array.Fill(_team, -1);
        Array.Clear(_teamScore);
        if (!_teamsOn) return;
        var seated = new List<int>();
        for (var s = 0; s < Seats; s++) if (Ctx.Seated(s)) seated.Add(s);
        if (seated.Count < TeamsMin)
        {
            _note = $"Команд не буде: на дві команди треба хоч {TeamsMin} за столом — граємо кожен сам";
            return;
        }
        _teams = true;
        foreach (var s in seated) _team[s] = s % 2;
        var odd = seated.Count(s => s % 2 == 1);
        if (Math.Abs(seated.Count - 2 * odd) > 2)
            for (var i = 0; i < seated.Count; i++) _team[seated[i]] = i % 2;
    }

    void BeginRound()
    {
        _skip.Clear();
        _round++;
        _found = [];
        _artistTaken = _titleTaken = false;
        _teamFound[0] = new Found();
        _teamFound[1] = new Found();
        _stage = 0;
        _choicesOpen = false;
        _phase = Play;
        _roundStart = Now;
        _totalMs = ClipEndMs + ExtraMs;
        _until = Now.AddMilliseconds(_totalMs);
        _dirty = true;
    }

    void EndRound()
    {
        _phase = Reveal;
        _totalMs = RevealMs;
        _until = Now.AddMilliseconds(RevealMs);
        _dirty = true;
        if (DuelNow) SettleDuel();
        else if (_duelOn && !_teams && _duelA < 0 && _round + 1 == LastRound) AnnounceDuel();
    }

    /// <summary>Останній раунд партії, наскільки вже відомо (фон може ще зменшити, якщо пісні не скачаються).</summary>
    int LastRound => Math.Min(_rounds, _available > 0 ? _available : _cap);

    /// <summary>Перед останнім треком: два лідери — у дуель, решта ставить, хто з них візьме фінальний трек.</summary>
    void AnnounceDuel()
    {
        int a = -1, b = -1, present = 0;
        for (var s = 0; s < Seats; s++)
        {
            if (!IsPresent(s)) continue;
            present++;
            if (a < 0 || _scores[s] > _scores[a]) { b = a; a = s; }
            else if (b < 0 || _scores[s] > _scores[b]) b = s;
        }
        if (present < DuelMin || b < 0) return;
        _duelA = a;
        _duelB = b;
        _duelRound = _round + 1;
        _bets.Clear();
        _betsClosed = false;
        _duelWinner = -2;
    }

    /// <summary>Фінальний трек зіграно: взяв той, хто набрав на ньому більше (порівну — ніхто); вгадали ставку — +<see cref="BetPoints"/>.</summary>
    void SettleDuel()
    {
        int Got(int s) => _found.TryGetValue(s, out var f) ? f.Points : 0;
        var (pa, pb) = (Got(_duelA), Got(_duelB));
        _duelWinner = pa == pb ? -1 : pa > pb ? _duelA : _duelB;
        _betsClosed = true;
        if (_duelWinner < 0) return;
        foreach (var (s, on) in _bets)
            if (on == _duelWinner && IsPresent(s)) Gain(s, BetPoints);
    }

    /// <summary>Очки місцю, а в командній грі — ще й його команді.</summary>
    void Gain(int seat, int points)
    {
        _scores[seat] += points;
        if (TeamOf(seat) is var t and >= 0) _teamScore[t] += points;
    }

    void Over(string? error)
    {
        if (_phase == Done) return;
        _cts?.Cancel();
        _phase = Done;
        _error = error;
        _dirty = true;
        var seats = Present().ToArray();
        if (error is not null && _round == 0)
        {
            Ctx.Finish([], $"{Info.Title}: {error}");
            return;
        }
        if (_teams) { OverTeams(seats); return; }
        var best = seats.Length == 0 ? 0 : seats.Max(s => _scores[s]);
        int[] winners = best > 0 ? [.. seats.Where(s => _scores[s] == best)] : [];
        foreach (var s in seats) Ctx.Score(s, _scores[s]);
        // ніки — на момент фінішу: хто встане з-за столу вже після партії, лишиться в підсумку (інакше переможець зникав з таблиці)
        var nicks = new string?[Seats];
        for (var s = 0; s < Seats; s++) if (Ctx.Seated(s)) nicks[s] = Ctx.NickOf(s);
        _result = new { winners, scores = (int[])_scores.Clone(), nicks };
        var parts = seats.OrderByDescending(s => _scores[s]).Select(s => $"{Ctx.NickOf(s)} {_scores[s]}");
        var tail = winners.Length == 0 ? "жодної пісні не впізнали" : "найкраще вухо в " + string.Join(" і ", winners.Select(s => NickCases.Genitive(Ctx.NickOf(s))));
        Ctx.Finish(winners, $"{Info.Title}: {string.Join(", ", parts)} — {tail}", seats.ToDictionary(s => s, s => (long)_scores[s]));
    }

    /// <summary>Кінець командної партії: перемагає команда з більшими очками — усі її гравці; порівну — нічия.</summary>
    void OverTeams(int[] seats)
    {
        var (a, b) = (_teamScore[0], _teamScore[1]);
        var win = a == b ? -1 : a > b ? 0 : 1;
        int[] winners = win < 0 ? [] : [.. seats.Where(s => _team[s] == win)];
        foreach (var s in seats) Ctx.Score(s, _team[s] >= 0 ? _teamScore[_team[s]] : _scores[s]);
        var nicks = new string?[Seats];
        for (var s = 0; s < Seats; s++) if (Ctx.Seated(s)) nicks[s] = Ctx.NickOf(s);
        _result = new { winners, scores = (int[])_scores.Clone(), nicks, teams = (int[])_teamScore.Clone(), team = win };
        var tail = win < 0 ? "нічия" : $"перемогли {TeamNames[win]}: " + string.Join(", ", winners.Select(s => Ctx.NickOf(s)));
        Ctx.Finish(winners, $"{Info.Title}: {TeamNames[0]} {a} : {b} {TeamNames[1]} — {tail}",
            seats.ToDictionary(s => s, s => (long)(_team[s] >= 0 ? _teamScore[_team[s]] : _scores[s])));
    }

    public override void OnLeave(int seat)
    {
        _left.Add(seat);
        _dirty = true;
        if (!AnyPresent()) Over(null);
    }

    // =========================================================================================
    // Здогадки
    // =========================================================================================

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        switch (action)
        {
            case "skip": return Skip(seat);
            case "bet": return Bet(seat, payload);
            case "guess": case "pick": case "who": break;
            default: return ActResult.Fail("Тут так не ходять");
        }
        if (_phase != Play) return ActResult.Fail(_phase == Done ? "Партію зіграно, тисни «Ану ще раз»" : "Зараз не вгадують");
        if (_left.Contains(seat)) return ActResult.Fail("Ти вже не за столом");
        if (DuelNow && !Duelist(seat))
            return ActResult.Fail($"Фінальна дуель — вгадують лише {Ctx.NickOf(_duelA)} і {Ctx.NickOf(_duelB)}. Став, хто візьме!");

        var now = Now;
        // ліміт — проти перебору здогадок; варіант і замовник — і так одна спроба на трек, їх не гальмуємо
        if (action == "guess" && _lastGuess.TryGetValue(seat, out var last) && (now - last).TotalMilliseconds < GuessEveryMs)
            return ActResult.Fail("Не так швидко");

        if (!_found.TryGetValue(seat, out var mine)) _found[seat] = mine = new Found();
        if (mine.Blocked) return ActResult.Fail("Цей трек ти вже профукав на варіантах — чекай наступного");
        if (action == "who") return Who(seat, mine, payload);

        string text;
        if (action == "pick")
        {
            if (!_choicesOpen) return ActResult.Fail("Варіантів ще нема");
            var choices = _ready[_round].Choices;
            var i = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("i", out var ie) && ie.TryGetInt32(out var iv) ? iv : -1;
            if (i < 0 || i >= choices.Length) return ActResult.Fail("Нема такого варіанта");
            if (mine.Artist || TeamGot(seat, artist: true)) return ActResult.Fail("Виконавця вже вгадано");
            text = choices[i];
        }
        else
        {
            text = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? "" : payload.ValueKind == JsonValueKind.String ? payload.GetString() ?? "" : "";
            text = text.Trim();
            if (text.Length == 0) return ActResult.Fail("Тяпни виконавця або назву");
            if (text.Length > MaxGuess) text = text[..MaxGuess];
        }
        _lastGuess[seat] = now;

        if (mine.Artist && mine.Title) return ActResult.Fail("Усе вже вгадано 🎉");
        var track = _ready[_round].Track;
        var team = TeamOf(seat);
        var said = new List<string>();
        var points = 0;
        var hitArtist = !mine.Artist && MelodyAnswer.Hits(text, MelodyAnswer.Artists(track));
        if (action == "pick" && !hitArtist)
        {
            // варіант — одна спроба: не той — трек далі без тебе (інакше тицяли б по черзі всі чотири)
            mine.Blocked = true;
            _dirty = true;
            return ActResult.Accept($"Ні, не {text} 🙈 Цей трек уже без тебе");
        }
        if (hitArtist)
        {
            mine.Artist = true;
            if (team < 0 || !_teamFound[team].Artist)
            {
                if (team >= 0) _teamFound[team].Artist = true;
                // з'явились варіанти — виконавець коштує вдвічі менше, хоч тицьнув, хоч написав
                var p = (ArtistPoints + (_artistTaken ? 0 : ArtistFirst)) / (_choicesOpen ? 2 : 1);
                _artistTaken = true;
                points += p;
                said.Add($"🎤 виконавець +{p}");
            }
            else said.Add("🎤 виконавець (команда вже має)");
        }
        if (!mine.Title && action != "pick" && MelodyAnswer.Hits(text, MelodyAnswer.Titles(track)))
        {
            mine.Title = true;
            if (team < 0 || !_teamFound[team].Title)
            {
                if (team >= 0) _teamFound[team].Title = true;
                var p = TitlePoints + (_titleTaken ? 0 : TitleFirst);
                _titleTaken = true;
                points += p;
                said.Add($"🎵 назва +{p}");
            }
            else said.Add("🎵 назва (команда вже має)");
        }
        if (said.Count == 0) return ActResult.Fail("Мимо");

        if (points > 0 && mine.Fast == 0 && Speed(now) is var (sec, bonus) && bonus > 0)
        {
            mine.Fast = sec;
            points += bonus;
            said.Add($"⚡ з {Seconds(sec)} +{bonus}");
        }
        if (points > 0 && DuelNow && !_betsClosed) _betsClosed = true;   // перше влучання в дуелі — ставки зачинено
        mine.Points += points;
        Gain(seat, points);
        _dirty = true;
        return ActResult.Accept("Є! " + string.Join(", ", said));
    }

    bool TeamGot(int seat, bool artist) => TeamOf(seat) is var t and >= 0 && (artist ? _teamFound[t].Artist : _teamFound[t].Title);

    /// <summary>
    /// Бонус швидкості: звичайно — вгадав за перші <see cref="SpeedMs"/> (+<see cref="SpeedBonus"/>); «з першої
    /// ноти» — за уривком, на якому вгадав: 3 с — +20, 6 с — +10, 10 с — нічого.
    /// </summary>
    (int Sec, int Bonus) Speed(DateTimeOffset now)
    {
        if (_grow) return (GrowSec[_stage], GrowBonus[_stage]);
        var ms = (now - _roundStart).TotalMilliseconds;
        return ms <= SpeedMs ? ((int)Math.Max(1, Math.Ceiling(ms / 1000)), SpeedBonus) : (0, 0);
    }

    static string Seconds(int n) => n == 1 ? "1 секунди" : $"{n} секунд";

    /// <summary>«Хто закинув?»: після виконавця чи назви — одна спроба назвати, хто з-за столу закинув пісню на радіо.</summary>
    ActResult Who(int seat, Found mine, JsonElement payload)
    {
        var by = _ready[_round].By;
        if (by is null) return ActResult.Fail("Цю пісню ніхто з вас не закидав");
        var team = TeamOf(seat);
        var f = team >= 0 ? _teamFound[team] : mine;
        if (!mine.Artist && !mine.Title && !(team >= 0 && (f.Artist || f.Title)))
            return ActResult.Fail("Спершу вгадай виконавця чи назву");
        if (f.Who is not null || mine.Who is not null) return ActResult.Fail("Замовника вже називали");
        var nick = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("nick", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString() ?? "" : "";
        if (nick.Length == 0) return ActResult.Fail("Кого називаєш?");
        var key = Auth.NickKey(nick);
        var hit = by.Any(x => Auth.NickKey(x) == key);
        mine.Who = hit;
        if (team >= 0) f.Who = hit;
        _dirty = true;
        if (!hit) return ActResult.Accept($"Ні, не {nick} 🙈");
        mine.Points += WhoPoints;
        Gain(seat, WhoPoints);
        return ActResult.Accept($"Є! 📻 закинув {nick} +{WhoPoints}");
    }

    /// <summary>Ставка у фінальній дуелі: хто з двох лідерів візьме фінальний трек. Лише не дуелянтам і лише до першого влучання.</summary>
    ActResult Bet(int seat, JsonElement payload)
    {
        if (_duelA < 0 || _round > _duelRound || _phase == Done) return ActResult.Fail("Дуелі нема — нема й ставок");
        if (Duelist(seat)) return ActResult.Fail("Ти сам у дуелі — вгадуй!");
        if (!IsPresent(seat)) return ActResult.Fail("Ти вже не за столом");
        if (_betsClosed) return ActResult.Fail("Ставки зачинено: хтось уже влучив");
        var on = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("seat", out var se) && se.TryGetInt32(out var sv) ? sv : -1;
        if (!Duelist(on)) return ActResult.Fail("Ставити можна лише на дуелянтів");
        _bets[seat] = on;
        _dirty = true;
        return ActResult.Accept($"Ставку прийнято: {Ctx.NickOf(on)} візьме");
    }

    /// <summary>«Готовий пропустити» — перемикач: натиснув ще раз — передумав.</summary>
    ActResult Skip(int seat)
    {
        if (_phase != Play) return ActResult.Fail("Зараз нема чого пропускати");
        if (_left.Contains(seat)) return ActResult.Fail("Ти вже не за столом");
        if (Finished(seat)) return ActResult.Fail("Усе вже вгадано — чекаємо інших");
        if (!_skip.Remove(seat)) _skip.Add(seat);
        _dirty = true;
        return ActResult.Done;
    }

    // =========================================================================================
    // Вид
    // =========================================================================================

    public override object View(int? seat)
    {
        var prepared = _round > 0 && _ready.TryGetValue(_round, out var p) ? p : null;
        var mine = seat is { } s && _found.TryGetValue(s, out var f) ? f : null;
        var open = _phase is Reveal or Done;
        return new
        {
            phase = _phase,
            round = _round,
            // «з N»: скільки раундів іще може бути — поки фон качає, стеля падає з кожною пісною, що не скачалась
            rounds = Math.Max(_round, Math.Min(_rounds, _available > 0 ? _available : _cap)),
            clipSec = _clipSec,
            until = _until,
            totalMs = _totalMs,
            // «з першої ноти» — уривок поточного етапу (3/6/10 с); після раунду «Ще раз» грає найдовший
            clip = prepared is null || _phase == Loading ? null
                : $"/api/games/melody/{prepared.Tokens[_phase == Play ? Math.Min(_stage, prepared.Tokens.Length - 1) : prepared.Tokens.Length - 1]}.mp3",
            me = mine is null ? null : new { artist = mine.Artist, title = mine.Title, points = mine.Points, fast = mine.Fast, blocked = mine.Blocked, who = mine.Who },
            found = _found.Where(kv => kv.Value.Artist || kv.Value.Title || kv.Value.Who is not null || kv.Value.Blocked)
                // «хто закинув» і «профукав на варіантах» — лише коли є: на дванадцятьох вид мусить лишатись меншим за 1,5 КБ
                .Select(kv => kv.Value.Who is null && !kv.Value.Blocked
                    ? (object)new { seat = kv.Key, artist = kv.Value.Artist, title = kv.Value.Title, points = kv.Value.Points, fast = kv.Value.Fast }
                    : new { seat = kv.Key, artist = kv.Value.Artist, title = kv.Value.Title, points = kv.Value.Points, fast = kv.Value.Fast,
                        who = kv.Value.Who, blocked = kv.Value.Blocked }).ToArray(),
            grow = _grow ? new { stage = _stage, sec = GrowSec, bonus = GrowBonus } : null,
            // варіанти — лише коли вже відкрились (раніше їх нема й у виді: не підгледиш)
            choices = _phase == Play && _choicesOpen && prepared is not null ? prepared.Choices : null,
            choicesAt = _choicesOn && _phase == Play && !_choicesOpen ? _roundStart.AddMilliseconds(ClipEndMs - ChoicesLeadMs) : (DateTimeOffset?)null,
            // «Хто закинув?»: пісня з замовлень столу — можна назвати замовника; хто саме — лише після раунду
            who = prepared?.By is null || _phase == Loading ? null
                : new { by = open ? prepared.By : null },
            teams = _teams ? new { of = (int[])_team.Clone(), scores = (int[])_teamScore.Clone(), names = TeamNames,
                found = _phase == Play ? _teamFound.Select(f => new { artist = f.Artist, title = f.Title }).ToArray() : null } : null,
            duel = _duelA < 0 ? null : new
            {
                a = _duelA, b = _duelB, round = _duelRound, closed = _betsClosed, winner = _duelWinner,
                bets = new[] { _bets.Count(kv => kv.Value == _duelA), _bets.Count(kv => kv.Value == _duelB) },
                mine = seat is { } bs && _bets.TryGetValue(bs, out var on) ? on : -1,
                // хто на кого ставив — лише після дуелі
                all = _duelWinner != -2 ? _bets.Select(kv => new { seat = kv.Key, on = kv.Value }).ToArray() : null,
            },
            note = _note,
            skip = _phase == Play ? _skip.Order().ToArray() : [],
            answer = open && prepared is not null ? new { id = prepared.Track.Id, artist = prepared.Track.Artist, title = prepared.Track.Title, thumb = prepared.Track.Thumb } : null,
            // після партії — усе, що звучало: браузер дає поставити 👎 будь-якому
            played = _phase == Done
                ? Enumerable.Range(1, _round).Where(_ready.ContainsKey).Select(i => _ready[i].Track)
                    .Select(t => new { id = t.Id, artist = t.Artist, title = t.Title, thumb = t.Thumb }).ToArray()
                : null,
            scores = (int[])_scores.Clone(),
            left = _left.Order().ToArray(),
            error = _error,
            result = _result,
        };
    }
}
