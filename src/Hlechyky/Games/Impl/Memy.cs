using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Мемологія» (specs/memy.md): картинка мему без підпису проявляється з великих пікселів — вгадай назву мему чи його
/// підпис-фразу. Спершу пишеш сам за повну ціну, під кінець з'являються чотири варіанти — відтоді всім пів ціни;
/// хибний варіант — і мем уже без тебе. Після раунду — розкриття: ціла картинка, назва, фраза, рік і звідки мем.
/// <para>
/// Картинки готує фон (<see cref="MemyImages"/>): три кадри на мем, кожен за своїм непрозорим токеном. До розкриття у
/// виді лише токен поточного кадру — чіткої картинки клієнт не має, поки вона не проявиться. Під замком кімнати —
/// лише пам'ять: множина готових, видача токенів, банк.
/// </para>
/// <para>Тренування з таблицею — <see cref="MemySolo"/> у тому самому модулі.</para>
/// </summary>
public class Memy : Game
{
    public const int TickMs = 500;
    /// <summary>Скільки місць: як у «Скільки?» і «Вгадай мелодію» — кожен вгадує сам, черги нема.</summary>
    public const int Seats = 12;
    public const int RevealMs = 6_000;
    /// <summary>Скільки стіл чекає на картинку (перший запуск — меми ще качаються), перш ніж здатись.</summary>
    public const int LoadTimeoutMs = 60_000;
    /// <summary>Як часто можна писати: спроби необмежені, але не перебором.</summary>
    public const int GuessEveryMs = 1_000;
    public const int MaxGuess = 120;
    /// <summary>Влучив до варіантів — <see cref="Full"/>; відколи варіанти відкрились — <see cref="Half"/> усім (і тицьнутим, і написаним).</summary>
    public const int Full = 100, Half = 50;
    /// <summary>Перший, хто влучив у раунді.</summary>
    public const int FirstBonus = 20;
    /// <summary>Коли (від початку раунду) з'являється кадр проявлення: 0 — великі пікселі, 1 — дрібніші, 2 — чітка.</summary>
    public static readonly int[] FrameAtMs = [0, 5_000, 10_000];
    /// <summary>Бонус швидкості за кадр, на якому влучив; без проявлення — до <see cref="FastMax"/> лінійно від часу.</summary>
    public static readonly int[] FrameBonus = [50, 25, 0];
    public const int FastMax = 50;
    public const int ChoicesCount = 4;
    /// <summary>Скільки очок партії коштує черепок (≈ 10–30 за партію).</summary>
    public const int PointsPerShard = 50;
    /// <summary>«З пікселів»: стільки влучань на першому кадрі за партію.</summary>
    public const int PixelHits = 5;
    /// <summary>Скільки мемів просимо в завантажувача понад раунди — на випадок мертвих посилань.</summary>
    const int Spare = 3;
    public const string AchWin = "memy-win", AchPixels = "memy-pixels";
    /// <summary>Ключ у спільній пам'яті баченого (<see cref="GameSeen"/>): стіл і тренування — одна пам'ять.</summary>
    public const string SeenGame = "memy";

    public static readonly int[] RoundChoices = [5, 10, 15];
    public static readonly int[] SecondChoices = [15, 20, 30];

    // значення опцій (specs/memy.md §3, «Як реалізовано»)
    public const string WhatName = "name", WhatPhrase = "phrase", WhatMix = "mix";
    public const string AnswerBoth = "both", AnswerWrite = "write", AnswerPick = "pick";
    public const string At8 = "8", At5 = "5", AtHalf = "half";
    public const string ShowPixels = "pixels", ShowWhole = "whole";
    public const string LevelAll = "all", LevelEasy = "easy", LevelHard = "hard";

    public const string PhaseLoading = "loading", PhasePlay = "play", PhaseReveal = "reveal", PhaseDone = "done";

    public const string NoMemes = "Мемів ще нема — банк порожній, грати нема в що";
    public const string NoPool = "Під ці опції мемів нема — обери інші категорії";
    public const string NoPhrases = "Мемів із підписом у цих категоріях нема — обери «Назву» чи «Упереміш»";

    static readonly GameInfo TableInfo = new(
        "memy", "Мемологія", "«Мемологію»", GameGroup.Party, 1, Seats,
        TickMs: TickMs, Start: StartMode.ByHost, Hidden: true, Score: ScoreOrder.HigherIsBetter,
        Options:
        [
            new GameOption("rounds", "Раундів", [.. RoundChoices.Select(n => (Str(n), Str(n)))], "10"),
            new GameOption("seconds", "Час на раунд", [.. SecondChoices.Select(n => (Str(n), $"{n} с"))], "20"),
            new GameOption("what", "Що вгадуємо", [(WhatName, "Назву"), (WhatPhrase, "Підпис"), (WhatMix, "Упереміш")], WhatMix),
            new GameOption("answer", "Відповідь",
                [(AnswerBoth, "Спершу пиши, потім варіанти"), (AnswerWrite, "Лише писати"), (AnswerPick, "Лише варіанти")], AnswerBoth),
            new GameOption("choicesAt", "Варіанти з'являються",
                [(At8, "за 8 с до кінця"), (At5, "за 5 с до кінця"), (AtHalf, "з половини раунду")], At8),
            new GameOption("show", "Картинка", [(ShowPixels, "Проявлення"), (ShowWhole, "Одразу ціла")], ShowPixels),
            new GameOption("cat", "Меми", MemyCats.List, MemyCats.All, Multi: true),
            new GameOption("level", "Складність", [(LevelAll, "Усякі"), (LevelEasy, "Відомі"), (LevelHard, "Для знавців")], LevelAll),
        ],
        Hint: "Мем без підпису проявляється з пікселів — пиши назву чи фразу. Під кінець з'являться варіанти, але за пів ціни");

    static string Str(int n) => n.ToString(CultureInfo.InvariantCulture);

    public override GameInfo Info => TableInfo;

    /// <summary>Місця — номерами, як у «Скільки?»: на дванадцять місць «гравець сьомий» у чіп не влізе.</summary>
    public override string SeatName(int seat) => (seat + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>Раунд: мем, що вгадуємо (фразу чи назву), варіанти з номером правильного і токени трьох кадрів.</summary>
    sealed record Round(MemyMeme Meme, bool Phrase, string[] Choices, int Right, string[] Tokens);

    /// <summary>Що місце зробило в раунді: останнє написане чи тицьнуте, влучив, на якому кадрі, скільки взяв.</summary>
    sealed class Attempt
    {
        public string Text = "";
        /// <summary>«write» — написав, «pick» — тицьнув варіант.</summary>
        public string How = "";
        public bool Hit, Blocked;
        public int Points;
        /// <summary>Кадр, на якому влучив (0–2); -1 — не влучив або без проявлення.</summary>
        public int Frame = -1;
    }

    MemyImages? _images;
    GameSeen? _seenMemo;

    // опції
    int _rounds = 10, _seconds = 20;
    string _what = WhatMix, _answer = AnswerBoth, _choicesAt = At8, _show = ShowPixels, _level = LevelAll;
    IReadOnlySet<string> _cats = MemyCats.Parse(null);

    // партія
    string _phase = PhaseLoading;
    /// <summary>Черга id мемів партії: спершу під складність і небачене, далі решта. Мертві (Gone) випадають.</summary>
    readonly List<string> _queue = [];
    readonly List<Round> _played = [];
    Round? _cur;
    int _round, _planned;
    DateTimeOffset _until, _loadStarted, _roundStart, _lastPoke;
    int _totalMs;
    readonly int[] _scores = new int[Seats];
    readonly int[] _pixelHits = new int[Seats];
    readonly HashSet<int> _left = [];
    readonly HashSet<int> _skip = [];
    readonly Dictionary<int, DateTimeOffset> _lastGuess = [];
    Dictionary<int, Attempt> _att = [];
    int _frame;
    bool _choicesOpen, _firstTaken, _dirty;
    object? _result;
    string? _error, _note;

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _images = Ctx.Services.GetService<MemyImages>();
        // Пам'ять «уже було» фон читає з бази заздалегідь — щоб перше «Почати» вже її мало.
        (_seenMemo ??= GameSeen.Of(Ctx.Services)).Prefetch(SeenGame);
        int Int(string key, int[] allowed, int fallback) =>
            options.TryGetValue(key, out var v) && int.TryParse(v, CultureInfo.InvariantCulture, out var n) && allowed.Contains(n) ? n : fallback;
        string Of(string key, string fallback, params string[] allowed) =>
            options.TryGetValue(key, out var v) && allowed.Contains(v) ? v : fallback;
        _rounds = Int("rounds", RoundChoices, 10);
        _seconds = Int("seconds", SecondChoices, 20);
        _what = Of("what", WhatMix, WhatName, WhatPhrase, WhatMix);
        _answer = Of("answer", AnswerBoth, AnswerBoth, AnswerWrite, AnswerPick);
        _choicesAt = Of("choicesAt", At8, At8, At5, AtHalf);
        _show = Of("show", ShowPixels, ShowPixels, ShowWhole);
        _level = Of("level", LevelAll, LevelAll, LevelEasy, LevelHard);
        _cats = MemyCats.Parse(options.GetValueOrDefault("cat"));
    }

    MemyBank Bank => _images?.Bank ?? MemyBank.Empty;

    /// <summary>Меми під опції: обрані категорії, а для «Підпис» — лише ті, що мають фразу.</summary>
    List<MemyMeme> Pool() => [.. Bank.Memes.Where(m => _cats.Contains(m.Cat) && (_what != WhatPhrase || m.HasPhrase))];

    /// <summary>«Відомі» — складність 1, «Для знавців» — 2–3; чого бракує, добирається з сусідньої складності.</summary>
    bool LevelFits(MemyMeme m) => _level switch { LevelEasy => m.Difficulty == 1, LevelHard => m.Difficulty >= 2, _ => true };

    int LevelDistance(MemyMeme m) => _level switch { LevelEasy => m.Difficulty - 1, LevelHard => 2 - m.Difficulty, _ => 0 };

    string? Unplayable() => Bank.Memes.Count == 0 ? NoMemes : Pool().Count > 0 ? null : _what == WhatPhrase ? NoPhrases : NoPool;

    public override string? CanStart() => Unplayable();

    public override void Start()
    {
        Array.Clear(_scores);
        Array.Clear(_pixelHits);
        _left.Clear();
        _skip.Clear();
        _lastGuess.Clear();
        _att = [];
        _played.Clear();
        _queue.Clear();
        _cur = null;
        _round = 0;
        _frame = 0;
        _choicesOpen = _firstTaken = false;
        _result = null;
        _error = _note = null;
        _phase = PhaseLoading;
        _loadStarted = _lastPoke = Now;
        _totalMs = 0;
        _until = Now;
        _dirty = true;

        if (Unplayable() is { } why)
        {
            _phase = PhaseDone;
            _error = why;
            Ctx.Finish([], $"{Info.Title}: {why}");
            return;
        }
        var pool = Pool();
        for (var i = pool.Count - 1; i > 0; i--) { var j = Ctx.Rng.Next(i + 1); (pool[i], pool[j]) = (pool[j], pool[i]); }
        // Спершу те, що бачило найменше з тих, хто сидить (сортування стабільне — тасовано вище); у межах складності —
        // так само, а чого під складність забракне, добирається з найближчої.
        _seenMemo ??= GameSeen.Of(Ctx.Services);
        var seen = _seenMemo.Peek(SeenGame, GameSeen.Table(Ctx, Seats));
        var fits = GameSeen.Order(pool.Where(LevelFits), m => m.Id, seen);
        var rest = GameSeen.Order(pool.Where(m => !LevelFits(m)), m => m.Id, seen).OrderBy(LevelDistance);
        _queue.AddRange(fits.Concat(rest).Select(m => m.Id));
        _planned = Math.Min(_rounds, _queue.Count);
        if (_planned < _rounds) _note = $"Під ці опції мемів лише {_planned} — стільки й зіграємо";
        _images?.Want(_queue.Take(_rounds + Spare));
        // Перший мем уже на диску — грати одразу, без «Готую меми…».
        TryBegin();
    }

    DateTimeOffset Now => Ctx.Clock.UtcNow;

    // =========================================================================================
    // Хід часу
    // =========================================================================================

    public override TickResult Tick()
    {
        var now = Now;
        switch (_phase)
        {
            case PhaseLoading:
                if (TryBegin()) break;
                if (_queue.Count == 0) Over(_round == 0 ? "жоден мем не скачався — грати нема в що" : Short());
                else if ((now - _loadStarted).TotalMilliseconds > LoadTimeoutMs)
                    Over(_round == 0 ? "картинки мемів ще не докачались — спробуйте за хвилину" : Short());
                else if ((now - _lastPoke).TotalSeconds >= 5)
                {
                    _lastPoke = now;
                    _images?.Poke();
                }
                break;
            case PhasePlay:
                PlayClock(now);
                if (now >= _until || AllDone()) EndRound();
                break;
            case PhaseReveal:
                if (now < _until) break;
                if (_round >= _planned) { Over(null); break; }
                _phase = PhaseLoading;
                _loadStarted = now;
                _dirty = true;
                TryBegin();
                break;
        }
        if (_phase != PhaseDone && !AnyPresent()) Over(null);
        if (!_dirty) return TickResult.None;
        _dirty = false;
        return new TickResult(Frame: false, View: true);
    }

    /// <summary>Годинник раунду: черговий кадр проявлення, поява варіантів. Без алокацій — це тик.</summary>
    void PlayClock(DateTimeOffset now)
    {
        var ms = (now - _roundStart).TotalMilliseconds;
        if (_show == ShowPixels && FrameAt(ms) is var f && f != _frame) { _frame = f; _dirty = true; }
        if (!_choicesOpen && _cur is { Choices.Length: > 1 } && ms >= ChoicesAtMs) { _choicesOpen = true; _dirty = true; }
    }

    static int FrameAt(double ms)
    {
        for (var k = FrameAtMs.Length - 1; k > 0; k--) if (ms >= FrameAtMs[k]) return k;
        return 0;
    }

    /// <summary>Коли (від початку раунду) відкриваються варіанти: «Лише варіанти» — одразу, «Лише писати» — ніколи.</summary>
    int ChoicesAtMs => _answer switch
    {
        AnswerPick => 0,
        AnswerWrite => int.MaxValue,
        _ => _choicesAt switch { At5 => _seconds * 1000 - 5_000, AtHalf => _seconds * 500, _ => _seconds * 1000 - 8_000 },
    };

    /// <summary>
    /// Писати можна всюди, крім «Лише варіанти»; але раунд без варіантів (банк замалий на чотири) дає писати й там —
    /// інакше в нього не було б як влучити.
    /// </summary>
    bool CanWrite => _answer != AnswerPick || _cur is not { Choices.Length: > 1 };

    /// <summary>
    /// Наступний мем черги, у якого вже готові всі кадри; мертві (не скачались до рестарту) випадають. Не той, що
    /// першим у черзі, а перший готовий: стіл не чекає на одне повільне посилання, коли є з чого грати.
    /// </summary>
    bool TryBegin()
    {
        if (_images is null) return false;
        for (var i = 0; i < _queue.Count; i++)
        {
            var id = _queue[i];
            if (_images.Gone(id)) { _queue.RemoveAt(i--); continue; }
            if (!_images.Ready(id) || Bank.Find(id) is not { } m) continue;
            _queue.RemoveAt(i);
            BeginRound(m);
            return true;
        }
        return false;
    }

    void BeginRound(MemyMeme m)
    {
        _round++;
        var phrase = _what switch { WhatPhrase => true, WhatMix => m.HasPhrase && Ctx.Rng.Next(2) == 0, _ => false };
        var (choices, right) = _answer == AnswerWrite ? ([], -1) : Choices(m, phrase, Bank, Ctx.Rng);
        var tokens = new string[MemyImages.Frames];
        for (var f = 0; f < tokens.Length; f++) tokens[f] = _images!.Issue(m.Id, f);
        _cur = new Round(m, phrase, choices, right, tokens);
        _played.Add(_cur);
        // Мем зараз побачать усі, хто сидить, — у пам'ять баченого (база — фоном).
        (_seenMemo ??= GameSeen.Of(Ctx.Services)).Remember(SeenGame, GameSeen.Table(Ctx, Seats), [m.Id], Now);
        _att = [];
        _skip.Clear();
        _firstTaken = false;
        _frame = _show == ShowPixels ? 0 : MemyImages.Clear;
        _phase = PhasePlay;
        _roundStart = Now;
        _totalMs = _seconds * 1000;
        _until = Now.AddMilliseconds(_totalMs);
        _choicesOpen = choices.Length > 1 && ChoicesAtMs <= 0;
        _dirty = true;
    }

    /// <summary>
    /// Чотири варіанти: правильний і троє схожих — спершу з тієї ж категорії зі спільним тегом (коти до котів), далі
    /// з тієї ж категорії, далі зі спільним тегом, далі будь-які. У режимі «Підпис» — фрази. Схожих на правильну
    /// відповідь не беремо (інакше два правильні), повторів теж. Порядок — випадковий. Повертає й номер правильного.
    /// </summary>
    public static (string[] Choices, int Right) Choices(MemyMeme m, bool phrase, MemyBank bank, Random rng)
    {
        string Text(MemyMeme x) => phrase ? x.Phrase! : x.Name;
        var right = phrase ? m.PhraseAnswers.ToList() : [m.Name, .. m.Names];
        var others = bank.Memes.Where(x => x.Id != m.Id && (!phrase || x.HasPhrase)).ToList();
        for (var i = others.Count - 1; i > 0; i--) { var j = rng.Next(i + 1); (others[i], others[j]) = (others[j], others[i]); }
        int Tier(MemyMeme x)
        {
            var cat = x.Cat == m.Cat;
            var tag = x.Tags.Any(t => m.Tags.Contains(t, StringComparer.OrdinalIgnoreCase));
            return cat && tag ? 0 : cat ? 1 : tag ? 2 : 3;
        }
        var answer = Text(m);
        var picked = new List<string> { answer };
        var keys = new HashSet<string>(StringComparer.Ordinal) { MelodyAnswer.Key(answer) };
        foreach (var x in others.OrderBy(Tier))
        {
            if (picked.Count >= ChoicesCount) break;
            var t = Text(x);
            if (SvoyaAnswer.Hits(t, right) || !keys.Add(MelodyAnswer.Key(t))) continue;
            picked.Add(t);
        }
        for (var i = picked.Count - 1; i > 0; i--) { var j = rng.Next(i + 1); (picked[i], picked[j]) = (picked[j], picked[i]); }
        return ([.. picked], picked.IndexOf(answer));
    }

    void EndRound()
    {
        _phase = PhaseReveal;
        _frame = MemyImages.Clear;
        _totalMs = RevealMs;
        _until = Now.AddMilliseconds(RevealMs);
        _dirty = true;
    }

    bool IsPresent(int s) => Ctx.Seated(s) && !_left.Contains(s);

    // Тик — двічі на секунду на кожен стіл: без LINQ і нових колекцій.
    bool AnyPresent()
    {
        for (var s = 0; s < Seats; s++) if (IsPresent(s)) return true;
        return false;
    }

    /// <summary>Цьому місцю в раунді вже нема чого робити: влучив або профукав на варіантах.</summary>
    bool Finished(int seat) => _att.TryGetValue(seat, out var a) && (a.Hit || a.Blocked);

    /// <summary>Кожен присутній або вже своє зробив, або готовий пропустити (і хоч хтось присутній).</summary>
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

    /// <summary>Скільки ще мемів у черзі не мертві — для «Мем 3 з N», поки фон качає.</summary>
    int Alive()
    {
        var n = 0;
        foreach (var id in _queue) if (_images is null || !_images.Gone(id)) n++;
        return n;
    }

    string Short() => $"Глек дістав лише {_round} з {_planned} мемів — решта картинок не скачалась. Зіграли, що було";

    void Over(string? error)
    {
        if (_phase == PhaseDone) return;
        _phase = PhaseDone;
        _error = error;
        _dirty = true;
        var seats = new List<int>();
        for (var s = 0; s < Seats; s++) if (IsPresent(s)) seats.Add(s);
        if (_round == 0 || seats.Count == 0)
        {
            Ctx.Finish([], $"{Info.Title}: {error ?? "гравці розійшлись, партію не дограли"}");
            return;
        }
        var best = seats.Max(s => _scores[s]);
        int[] winners = best > 0 ? [.. seats.Where(s => _scores[s] == best)] : [];
        foreach (var s in seats)
        {
            Ctx.Score(s, _scores[s]);
            if (_scores[s] / PointsPerShard is > 0 and var shards)
                // Номер партії в причині: «Ще раз» за тим самим столом — нова виплата, а не повтор старої.
                Ctx.Award(s, shards, $"points:{Ctx.Round.ToString(CultureInfo.InvariantCulture)}");
        }
        // «Мемолог» — перемога саме за столом, де було з ким змагатись.
        if (!Info.Solo && seats.Count >= 2) foreach (var w in winners) Ctx.Award(w, 0, "ach:" + AchWin);
        var nicks = new string?[Seats];
        for (var s = 0; s < Seats; s++) if (Ctx.Seated(s)) nicks[s] = Ctx.NickOf(s);
        _result = new { winners, scores = (int[])_scores.Clone(), nicks };
        var parts = seats.OrderByDescending(s => _scores[s]).ThenBy(s => s).Select(s => $"{Ctx.NickOf(s)} {_scores[s]}");
        var tail = winners.Length == 0 ? "жодного мему не впізнали"
            : (winners.Length == 1 ? "головний мемолог — " : "головні мемологи — ") + string.Join(" і ", winners.Select(s => Ctx.NickOf(s)));
        Ctx.Finish(winners, $"{Info.Title}: {string.Join(", ", parts)} — {tail}", seats.ToDictionary(s => s, s => (long)_scores[s]));
    }

    public override void OnLeave(int seat)
    {
        _left.Add(seat);
        _dirty = true;
        if (!AnyPresent()) Over(null);
    }

    // =========================================================================================
    // Відповіді
    // =========================================================================================

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        switch (action)
        {
            case "skip": return Skip(seat);
            case "guess": case "pick": break;
            default: return ActResult.Fail("Тут так не ходять");
        }
        if (_phase != PhasePlay || _cur is null)
            return ActResult.Fail(_phase == PhaseDone ? "Партію зіграно, тисни «Ану ще раз»" : "Зараз не вгадують");
        if (!IsPresent(seat)) return ActResult.Fail("Ти вже не за столом");
        _att.TryGetValue(seat, out var mine);
        if (mine is { Hit: true }) return ActResult.Fail("Уже вгадано — чекаємо інших");
        if (mine is { Blocked: true }) return ActResult.Fail("Цей мем ти вже профукав на варіантах — чекай наступного");
        return action == "pick" ? Pick(seat, mine, payload) : Guess(seat, mine, payload);
    }

    ActResult Guess(int seat, Attempt? mine, JsonElement payload)
    {
        if (!CanWrite) return ActResult.Fail("Тут лише варіанти — тицяй кнопку");
        var text = (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() ?? "" : payload.ValueKind == JsonValueKind.String ? payload.GetString() ?? "" : "").Trim();
        if (text.Length == 0) return ActResult.Fail(_cur!.Phrase ? "Напиши підпис мему" : "Напиши назву мему");
        if (text.Length > MaxGuess) text = text[..MaxGuess];
        var now = Now;
        if (_lastGuess.TryGetValue(seat, out var last) && (now - last).TotalMilliseconds < GuessEveryMs)
            return ActResult.Fail("Не так швидко");
        _lastGuess[seat] = now;
        if (mine is null) _att[seat] = mine = new Attempt();
        // останнє написане — на розкриття («хто що писав»); до розкриття бачить лише сам
        mine.Text = text;
        mine.How = "write";
        _dirty = true;
        var answers = _cur!.Phrase ? _cur.Meme.PhraseAnswers : _cur.Meme.NameAnswers;
        return SvoyaAnswer.Hits(text, answers) ? Hit(seat, mine, now) : ActResult.Fail("Мимо");
    }

    ActResult Pick(int seat, Attempt? mine, JsonElement payload)
    {
        if (!_choicesOpen) return ActResult.Fail("Варіантів ще нема");
        var choices = _cur!.Choices;
        var i = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("i", out var ie) && ie.TryGetInt32(out var iv) ? iv : -1;
        if (i < 0 || i >= choices.Length) return ActResult.Fail("Нема такого варіанта");
        if (mine is null) _att[seat] = mine = new Attempt();
        mine.Text = choices[i];
        mine.How = "pick";
        _dirty = true;
        if (i == _cur.Right) return Hit(seat, mine, Now);
        // варіант — одна спроба: не той — мем далі без тебе (інакше тицяли б по черзі всі чотири)
        mine.Blocked = true;
        _skip.Remove(seat);
        return ActResult.Accept($"Ні, не «{choices[i]}» 🙈 Цей мем уже без тебе");
    }

    /// <summary>Влучання: база (повна чи пів), швидкість за кадром чи часом, перший у раунді — ще зверху.</summary>
    ActResult Hit(int seat, Attempt mine, DateTimeOffset now)
    {
        var ms = (now - _roundStart).TotalMilliseconds;
        var basePts = _choicesOpen ? Half : Full;
        var fast = _show == ShowPixels ? FrameBonus[_frame]
            : (int)Math.Round(FastMax * Math.Max(0, 1 - ms / (_seconds * 1000.0)), MidpointRounding.AwayFromZero);
        var first = _firstTaken ? 0 : FirstBonus;
        _firstTaken = true;
        var points = basePts + fast + first;
        mine.Hit = true;
        mine.Points = points;
        mine.Frame = _show == ShowPixels ? _frame : -1;
        _scores[seat] += points;
        _skip.Remove(seat);
        _dirty = true;
        if (_show == ShowPixels && _frame == 0 && ++_pixelHits[seat] == PixelHits) Ctx.Award(seat, 0, "ach:" + AchPixels);
        var said = new List<string> { $"Є! +{basePts}" };
        if (fast > 0) said.Add(_show == ShowPixels ? $"🧩 з пікселів +{fast}" : $"⚡ +{fast}");
        if (first > 0) said.Add($"🥇 перший +{first}");
        return ActResult.Accept(string.Join(", ", said));
    }

    /// <summary>«Не знаю — далі» — перемикач: натиснув ще раз — передумав. Коли всі своє зробили чи пропускають, раунд кінчається.</summary>
    ActResult Skip(int seat)
    {
        if (_phase != PhasePlay) return ActResult.Fail("Зараз нема чого пропускати");
        if (!IsPresent(seat)) return ActResult.Fail("Ти вже не за столом");
        if (Finished(seat)) return ActResult.Fail("Ти вже своє зробив — чекаємо інших");
        if (!_skip.Remove(seat)) _skip.Add(seat);
        _dirty = true;
        return ActResult.Done;
    }

    // =========================================================================================
    // Вид
    // =========================================================================================

    static string Url(string token) => $"/api/games/memy/{token}.jpg";

    public override object View(int? seat)
    {
        var cur = _cur;
        var play = _phase == PhasePlay && cur is not null;
        var open = _phase is PhaseReveal or PhaseDone && cur is not null;
        var mine = seat is { } s && _att.TryGetValue(s, out var a) ? a : null;
        var pixels = _show == ShowPixels;
        return new
        {
            phase = _phase,
            round = _round,
            // «з N»: скільки мемів іще може бути — мертві посилання з черги стелю знижують
            rounds = Math.Max(_round, Math.Min(_planned, _round + Alive())),
            seconds = _seconds,
            until = _until,
            totalMs = _totalMs,
            what = _what,
            answerMode = _answer,
            show = _show,
            // що вгадуємо в цьому раунді: «name» чи «phrase»
            mode = cur is null || _phase == PhaseLoading ? null : cur.Phrase ? WhatPhrase : WhatName,
            frame = play ? _frame : MemyImages.Clear,
            // до розкриття — лише токен поточного кадру; наступний з'явиться у виді, коли настане його час
            image = cur is null || _phase == PhaseLoading ? null : Url(cur.Tokens[play ? _frame : MemyImages.Clear]),
            nextFrameAt = play && pixels && _frame < MemyImages.Clear ? _roundStart.AddMilliseconds(FrameAtMs[_frame + 1]) : (DateTimeOffset?)null,
            canWrite = play && CanWrite,
            // варіанти — лише коли вже відкрились (раніше їх нема й у виді: не підгледиш)
            choices = play && _choicesOpen ? cur!.Choices : null,
            choicesAt = play && !_choicesOpen && cur!.Choices.Length > 1 ? _roundStart.AddMilliseconds(ChoicesAtMs) : (DateTimeOffset?)null,
            choicesOpen = play && _choicesOpen,
            me = mine is null ? null : new { text = mine.Text, how = mine.How, hit = mine.Hit, blocked = mine.Blocked, points = mine.Points, frame = mine.Frame },
            // хто вже влучив чи профукав — без тексту: що саме писали, видно лише на розкритті
            found = play
                ? _att.Where(kv => kv.Value.Hit || kv.Value.Blocked).OrderBy(kv => kv.Key)
                    .Select(kv => new { seat = kv.Key, hit = kv.Value.Hit, blocked = kv.Value.Blocked, points = kv.Value.Points, frame = kv.Value.Frame }).ToArray()
                : [],
            skip = play ? _skip.Order().ToArray() : [],
            answer = open ? new
            {
                id = cur!.Meme.Id,
                mode = cur.Phrase ? WhatPhrase : WhatName,
                name = cur.Meme.Name,
                phrase = cur.Meme.Phrase,
                year = cur.Meme.Year,
                about = cur.Meme.About,
                page = cur.Meme.ImagePage,
                sources = cur.Meme.Sources,
                image = Url(cur.Tokens[MemyImages.Clear]),
            } : null,
            // чужі відповіді — лише після раунду: що писав чи тицяв кожен, влучив чи ні, скільки взяв
            answers = open
                ? _att.Where(kv => kv.Value.Text.Length > 0).OrderBy(kv => kv.Key)
                    .Select(kv => new { seat = kv.Key, text = kv.Value.Text, how = kv.Value.How, hit = kv.Value.Hit, blocked = kv.Value.Blocked,
                        points = kv.Value.Points, frame = kv.Value.Frame }).ToArray()
                : null,
            // після партії — усе, що було: історія з картинками й посиланнями на джерела
            played = _phase == PhaseDone
                ? _played.Select(r => new { id = r.Meme.Id, name = r.Meme.Name, phrase = r.Meme.Phrase, year = r.Meme.Year, page = r.Meme.ImagePage,
                    image = Url(r.Tokens[MemyImages.Clear]) }).ToArray()
                : null,
            scores = (int[])_scores.Clone(),
            left = _left.Order().ToArray(),
            note = _note,
            error = _error,
            result = _result,
        };
    }
}

/// <summary>
/// «Мемологія: тренування» — та сама гра, фіксовані правила: десять мемів, типові опції (20 с, упереміш, спершу пиши,
/// проявлення, усі меми). Окремий паспорт потрібен заради таблиці: рекорд каркас показує лише для <c>MaxPlayers == 1</c>.
/// </summary>
public sealed class MemySolo : Memy
{
    static readonly GameInfo SoloInfo = new(
        "memy-solo", "Мемологія: тренування", "тренування «Мемології»", GameGroup.Solo, 1, 1,
        TickMs: TickMs, Start: StartMode.Immediate, Private: true, Rated: false, Score: ScoreOrder.HigherIsBetter,
        Hint: "Десять мемів, типові правила. Найкращий результат — у таблицю",
        Client: "memy");

    public override GameInfo Info => SoloInfo;

    public override string SeatName(int seat) => "1";

    /// <summary>Опцій нема — завжди типові, тож рекорди в таблиці порівнянні.</summary>
    public override void Configure(IReadOnlyDictionary<string, string> options) =>
        base.Configure(new Dictionary<string, string>());
}
