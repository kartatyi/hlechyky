using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Піктіонарі на компанію (specs/pictionary.md). Художник по черзі обирає одне з трьох слів і малює його, решта
/// вгадує, пишучи здогадки. Хто вгадав раніше — більше очок; художник отримує частку від усіх, хто вгадав.
/// Партія — кожен за столом малює стільки разів, скільки обрав господар.
///
/// Малюнок — це список операцій (лінія або заливка), які художник шле через Input шматками по кілька точок.
/// Кадр раз на тик несе лише те, що додалось, плюс версію: undo і «очистити» міняють версію, і тоді кадр везе
/// малюнок цілком. Вид (per-seat, бо слово бачить лише художник) теж несе весь малюнок — з нього стартує той,
/// хто щойно відкрив картку.
/// </summary>
public sealed class Pictionary : Game
{
    /// <summary>Тик: штрих художника доходить до інших не пізніше ніж за стільки мілісекунд.</summary>
    public const int TickMs = 100;
    /// <summary>Час художникові обрати слово; не обрав — обираємо за нього.</summary>
    public const int PickMs = 15_000;
    /// <summary>Пауза після ходу: побачити слово і хто скільки взяв.</summary>
    public const int RevealMs = 6_000;
    /// <summary>Скільки слів на вибір.</summary>
    public const int Choices = 3;
    /// <summary>Мінімальний проміжок між здогадками одного гравця.</summary>
    public const int GuessEveryMs = 400;
    /// <summary>Як часто гравець може попросити повний малюнок.</summary>
    public const int SyncEveryMs = 2_000;

    /// <summary>Логічне полотно й межі штриха — спільні з іншими іграми, де малюють (<see cref="Sketch"/>).</summary>
    public const int CanvasW = Sketch.CanvasW, CanvasH = Sketch.CanvasH, MaxChunkPoints = Sketch.MaxChunkPoints;

    /// <summary>Очки вгадувача: від <see cref="MinGuessPoints"/> до <see cref="MaxGuessPoints"/> залежно від часу, плюс перший.</summary>
    public const int MaxGuessPoints = 100, MinGuessPoints = 10, FirstBonus = 20;
    /// <summary>Здогадок у стрічці, які тримаємо й показуємо.</summary>
    public const int FeedKeep = 40;
    const int FeedInFrame = 12;
    const int MaxGuessLength = 40;
    const int Seats = 10;

    // ---------- прохід №3 (29.09) ----------
    /// <summary>«Удвох: скільки встигнемо» — стільки на всю партію; і коротке «слово було» між словами.</summary>
    public const int DuoMs = 180_000, DuoRevealMs = 1_500;
    /// <summary>Галерея наприкінці: стільки на ❤ найкращому чужому малюнку (усі проголосували — раніше).</summary>
    public const int VoteMs = 30_000;
    /// <summary>Черепків «Митцю партії» — тому, чий малюнок зібрав найбільше ❤.</summary>
    public const int ArtistShards = 5;
    /// <summary>Реакція 😂🔥🤯 — не частіше з місця чи глядача.</summary>
    public const int ReactEveryMs = 500;
    public static readonly string[] Reactions = ["😂", "🔥", "🤯"];
    /// <summary>Слова компанії: по стільки на людину, стільки всього, такої довжини.</summary>
    public const int HomePerSeat = 3, HomeMax = 30, HomeMaxLength = 30;
    /// <summary>Скільки малюнків партії одна людина може закинути в публічний альбом.</summary>
    public const int PinsPerNick = 3;
    public const string Party = "party", Duo = "duo";

    public static readonly int[] RoundChoices = [1, 2, 3];
    public static readonly int[] SecondChoices = [60, 80, 100, 120];
    public const int DefaultRounds = 2, DefaultSeconds = 80;

    const string Pick = "pick", Draw = "draw", Reveal = "reveal", Vote = "vote", Done = "done";

    public override GameInfo Info { get; } = new(
        "pictionary", "Піктіонарі", "піктіонарі", GameGroup.Party, 2, Seats,
        TickMs: TickMs, Start: StartMode.ByHost, Hidden: true, Score: ScoreOrder.HigherIsBetter,
        Options:
        [
            new GameOption("mode", "Режим", [(Party, "Компанією"), (Duo, "Удвох: скільки встигнемо")], Party),
            new GameOption("rounds", "Кола", [.. RoundChoices.Select(n => (n.ToString(), n == 1 ? "1 коло" : $"{n} кола"))], DefaultRounds.ToString()),
            new GameOption("seconds", "Час на малюнок", [.. SecondChoices.Select(n => (n.ToString(), $"{n} с"))], DefaultSeconds.ToString()),
            new GameOption("topic", "Теми", PictionaryWords.Topics, PictionaryWords.AnyTopic, Multi: true),
        ],
        Hint: "Один малює слово, решта вгадує. Хто вгадав швидше — більше очок. Малюють по черзі");

    // ---------- налаштування ----------
    PictionaryWords _words = null!;
    IReadOnlySet<string>? _topics;
    int _rounds = DefaultRounds;
    int _drawMs = DefaultSeconds * 1000;
    bool _duo;
    PictionaryStore? _store;
    bool _started;

    // ---------- партія ----------
    int[] _order = [];
    int _turn = -1;
    int _turns;
    string _phase = Pick;
    int _drawer = -1;
    string[] _choices = [];
    string _word = "";
    /// <summary>Які позиції слова вже підказані (відкриті всім).</summary>
    readonly HashSet<int> _hinted = [];
    /// <summary>Позиції літер слова, скільки підказок можна і коли наступна — рахуємо раз на слово, а не щотика.</summary>
    int[] _letters = [];
    int _hintsAllowed;
    DateTimeOffset _nextHint = DateTimeOffset.MaxValue;
    /// <summary>«к_т» для тих, хто слова не знає; міняється лише з підказкою чи новим словом.</summary>
    string _mask = "";
    DateTimeOffset _phaseStart;
    DateTimeOffset _until;
    readonly int[] _scores = new int[Seats];
    readonly int[] _gained = new int[Seats];
    readonly List<int> _guessed = [];
    readonly HashSet<string> _used = new(StringComparer.Ordinal);
    readonly HashSet<int> _left = [];
    readonly Dictionary<int, DateTimeOffset> _lastGuess = [];
    readonly Dictionary<int, DateTimeOffset> _lastSync = [];
    object? _result;

    // ---------- малюнок ----------
    readonly Sketch _sketch = new();
    int _ver;
    /// <summary>Скільки операцій уже поїхало кадрами; кадр везе решту.</summary>
    int _sent;
    int _frameFrom;

    // ---------- стрічка ----------
    readonly List<FeedItem> _feed = [];
    int _feedId;
    /// <summary>
    /// Стрічку кадр везе лише тоді, коли в ній з'явилось нове, і лише нове (прохід 28.09): раніше кожен кадр —
    /// десять на секунду, поки художник малює, — віз дванадцять останніх здогадок наново, ~800 байт із ~1000.
    /// </summary>
    int _feedSent, _frameFeedFrom;

    bool _frameDirty, _viewDirty;

    sealed record FeedItem(int Id, int Seat, string Kind, string? Text);

    // ---------- пропустити слово (п. 28) ----------
    /// <summary>Хто вже брав три нові слова в цій партії (раз на партію).</summary>
    readonly HashSet<int> _rerolled = [];

    // ---------- слова компанії (п. 25) ----------
    sealed record HomeWord(string Key, string Nick, string Word);
    readonly List<HomeWord> _home = [];
    readonly HashSet<string> _homeUsed = new(StringComparer.Ordinal);
    bool[] _choiceHome = [];
    HomeWord? _homeNow;

    // ---------- удвох (п. 29) ----------
    int _duoCount;
    DateTimeOffset _duoUntil;
    int _pairBest;
    bool _pairRecord;

    // ---------- реакції (п. 27) ----------
    readonly int[] _reactRing = new int[32];
    int _reactId, _reactSent, _frameReactFrom;
    readonly int[] _turnReacts = new int[3];
    readonly Dictionary<int, DateTimeOffset> _lastReact = [];
    readonly Dictionary<string, DateTimeOffset> _fanReact = new(StringComparer.Ordinal);

    // ---------- галерея, ❤ і альбом (п. 26) ----------
    sealed record Art(int T, int Seat, string Nick, string Word, bool Home, string Z, int[] Re);
    readonly List<Art> _art = [];
    readonly Dictionary<int, int> _votes = [];
    int[] _hearts = [];
    int[] _artists = [];
    readonly HashSet<string> _roster = new(StringComparer.Ordinal);
    readonly HashSet<int> _pinned = [];
    readonly Dictionary<string, int> _pinsBy = new(StringComparer.Ordinal);

    // ---------- компактний малюнок (п. 32) ----------
    string _packed = "";
    int _packedVer = -1, _packedN = -1;

    // =========================================================================================
    // Налаштування і старт
    // =========================================================================================

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _words = Ctx.Services.GetService<PictionaryWords>() ?? PictionaryWords.Default;
        if (options.TryGetValue("rounds", out var r) && int.TryParse(r, out var rn) && RoundChoices.Contains(rn)) _rounds = rn;
        if (options.TryGetValue("seconds", out var s) && int.TryParse(s, out var sn) && SecondChoices.Contains(sn)) _drawMs = sn * 1000;
        if (options.TryGetValue("topic", out var t)) _topics = PictionaryWords.ParseTopics(t);
        _duo = options.TryGetValue("mode", out var m) && m == Duo;
        _store = Ctx.Services.GetService<PictionaryStore>();
        if (_words.Count == 0) throw new GameError("Нема словника, піктіонарі відпочиває");
    }

    public override void Start()
    {
        Array.Clear(_scores);
        Array.Clear(_gained);
        _used.Clear();
        _left.Clear();
        _lastGuess.Clear();
        _lastSync.Clear();
        _feed.Clear();
        _result = null;
        _started = true;
        _rerolled.Clear();
        _lastReact.Clear();
        _fanReact.Clear();
        _art.Clear();
        _votes.Clear();
        _hearts = [];
        _artists = [];
        _pinned.Clear();
        _pinsBy.Clear();
        _order = [.. Enumerable.Range(0, Seats).Where(Ctx.Seated)];
        _roster.Clear();
        foreach (var s in _order) _roster.Add(Auth.NickKey(Ctx.NickOf(s)));
        _turns = _duo ? int.MaxValue : _order.Length * _rounds;
        _duoCount = 0;
        _pairRecord = false;
        _pairBest = _duo && _store is not null ? _store.Pair(PictionaryStore.PairKey(_order.Select(NickOrEmpty)))?.Best ?? 0 : 0;
        _duoUntil = Now.AddMilliseconds(DuoMs);
        _turn = -1;
        NextTurn();
    }

    string NickOrEmpty(int seat) => Ctx.NickOf(seat) ?? "";

    public override bool ActsInLobby => true;

    public override string? CanStart()
    {
        if (!_duo) return null;
        var n = 0;
        for (var s = 0; s < Seats; s++) if (Ctx.Seated(s)) n++;
        return n == 2 ? null : "«Скільки встигнемо» — рівно на двох: хай зайві стануть глядачами або обери режим «Компанією»";
    }

    IEnumerable<int> Present() => Enumerable.Range(0, Seats).Where(s => Ctx.Seated(s) && !_left.Contains(s));

    /// <summary>Скільки людей за столом — те саме, що Present().Count(), але без LINQ: це кличе кожен тик.</summary>
    int PresentCount()
    {
        var n = 0;
        for (var s = 0; s < Seats; s++) if (Ctx.Seated(s) && !_left.Contains(s)) n++;
        return n;
    }

    /// <summary>Хто може вгадувати в цьому ході: усі присутні, крім художника.</summary>
    IEnumerable<int> Guessers() => Present().Where(s => s != _drawer);

    DateTimeOffset Now => Ctx.Clock.UtcNow;

    // =========================================================================================
    // Дії
    // =========================================================================================

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (_phase == Done) return ActResult.Fail("Партію зіграно, тисни «Ану ще раз»");
        return action switch
        {
            "pick" => PickWord(seat, payload),
            "guess" => Guess(seat, payload),
            "draw" => AddLine(seat, payload),
            "fill" => AddFill(seat, payload),
            "undo" => Undo(seat),
            "clear" => Clear(seat),
            "sync" => Sync(seat),
            "reroll" => Reroll(seat),
            "react" => React(seat, payload),
            "vote" => VoteFor(seat, payload),
            "home" => AddHome(seat, payload),
            "unhome" => RemoveHome(seat, payload),
            _ => ActResult.Fail("Тут так не ходять"),
        };
    }

    ActResult PickWord(int seat, JsonElement payload)
    {
        if (_phase != Pick) return ActResult.Fail("Слово вже обрано");
        if (seat != _drawer) return ActResult.Fail("Слово обирає художник");
        var i = Int(payload, "i", -1);
        if (i < 0 || i >= _choices.Length) return ActResult.Fail("Нема такого слова");
        BeginDraw(i);
        return ActResult.Done;
    }

    /// <summary>
    /// «Пропустити слово» (п. 28): раз за партію художник бере три нові слова замість трьох непосильних. Годинник
    /// вибору не стоїть. Удвох — інакше: пропустити слово посеред малювання можна скільки завгодно, бо час і так спільний.
    /// </summary>
    ActResult Reroll(int seat)
    {
        if (seat != _drawer) return ActResult.Fail("Слова міняє лише художник");
        if (_duo)
        {
            if (_phase != Draw) return ActResult.Fail("Зараз нема чого пропускати");
            AddFeed(-1, "pass", _word);
            _choices = Deal(1, []);
            if (_choices.Length == 0) return ActResult.Fail("Слова скінчились 🙂");
            WipeCanvas();
            _guessed.Clear();
            BeginDraw(0);
            return ActResult.Done;
        }
        if (_phase != Pick) return ActResult.Fail("Слово вже обрано");
        if (!_rerolled.Add(seat)) return ActResult.Fail("Нові слова — лише раз за партію");
        var was = _choices;
        for (var k = 0; k < was.Length; k++)
            if (k >= _choiceHome.Length || !_choiceHome[k]) _used.Add(PictionaryWords.Normalize(was[k]));
        _choices = Deal(Choices, was);
        _viewDirty = true;
        return ActResult.Accept("Тримай три нові — годинник іде");
    }

    /// <summary>Три (удвох — одне) слова на вибір; якщо є свіже слово компанії не від художника — одне з них 🏠.</summary>
    string[] Deal(int count, string[] skip)
    {
        var picked = _words.Pick(Ctx.Rng, _topics, _used, count);
        if (picked.Length == 0) picked = PictionaryWords.Default.Pick(Ctx.Rng, null, _used, count);
        _choiceHome = new bool[picked.Length];
        if (picked.Length > 0 && HomeFor(_drawer, skip) is { } hw)
        {
            var i = Ctx.Rng.Next(picked.Length);
            picked[i] = hw.Word;
            _choiceHome[i] = true;
        }
        return picked;
    }

    HomeWord? HomeFor(int drawer, string[] skip)
    {
        if (_home.Count == 0) return null;
        var key = Auth.NickKey(Ctx.NickOf(drawer));
        var fit = _home.Where(h => h.Key != key && !_homeUsed.Contains(PictionaryWords.Normalize(h.Word)) && !skip.Contains(h.Word)).ToList();
        return fit.Count == 0 ? null : fit[Ctx.Rng.Next(fit.Count)];
    }

    // ---------------------------------------------------------------- слова компанії (п. 25)

    /// <summary>Слово компанії: у лобі кожен докидає до трьох своїх — їх малюватимуть інші, не автор.</summary>
    ActResult AddHome(int seat, JsonElement payload)
    {
        if (_started) return ActResult.Fail("Свої слова докидають, поки стіл збирається");
        var word = CleanHome(Str(payload, "text"));
        if (word is null) return ActResult.Fail("Слово з літер, 2–30 знаків: «кумів трактор» підійде");
        var key = Auth.NickKey(Ctx.NickOf(seat));
        if (_home.Count(h => h.Key == key) >= HomePerSeat) return ActResult.Fail("Три своїх слова — досить, дай іншим");
        if (_home.Count >= HomeMax) return ActResult.Fail("Торба слів компанії вже повна");
        var norm = PictionaryWords.Normalize(word);
        if (_home.Any(h => PictionaryWords.Normalize(h.Word) == norm)) return ActResult.Fail("Таке слово вже в торбі");
        _home.Add(new HomeWord(key, Ctx.NickOf(seat) ?? "", word));
        return ActResult.Accept("🏠 Слово в торбі — малюватиме хтось інший");
    }

    ActResult RemoveHome(int seat, JsonElement payload)
    {
        if (_started) return ActResult.Fail("Партія вже йде — слова в грі");
        var key = Auth.NickKey(Ctx.NickOf(seat));
        var word = Str(payload, "text");
        var i = _home.FindIndex(h => h.Key == key && h.Word == word);
        if (i < 0) return ActResult.Fail("Нема такого твого слова");
        _home.RemoveAt(i);
        return ActResult.Done;
    }

    /// <summary>Літери (і цифри), пробіли, дефіс, апостроф; щонайменше дві літери. null — не годиться.</summary>
    public static string? CleanHome(string? raw)
    {
        var s = string.Join(' ', (raw ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)).Replace('’', '\'').Replace('ʼ', '\'');
        if (s.Length is < 2 or > HomeMaxLength) return null;
        var letters = 0;
        foreach (var ch in s)
        {
            if (char.IsLetter(ch)) letters++;
            else if (!char.IsDigit(ch) && ch is not (' ' or '-' or '\'')) return null;
        }
        return letters >= 2 ? s : null;
    }

    // ---------------------------------------------------------------- реакції (п. 27)

    /// <summary>😂🔥🤯 над полотном: хто вже вгадав (художник — ні), а на «слово було» — будь-хто за столом.</summary>
    ActResult React(int seat, JsonElement payload)
    {
        var e = Int(payload, "e", -1);
        if (e < 0 || e >= Reactions.Length) return ActResult.Fail("Нема такої реакції");
        if (_phase is not (Draw or Reveal)) return ActResult.Fail("Зараз нема на що реагувати");
        if (_phase == Draw && (seat == _drawer || !_guessed.Contains(seat))) return ActResult.Fail("Спершу вгадай 🙂");
        var now = Now;
        if (_lastReact.TryGetValue(seat, out var last) && (now - last).TotalMilliseconds < ReactEveryMs) return ActResult.Done;
        _lastReact[seat] = now;
        AddReact(e);
        return ActResult.Done;
    }

    /// <summary>Реакція глядача (HTTP, <see cref="PictionarySetup"/>): ті самі фази, своя квота на нік.</summary>
    public ActResult FanReact(string nickKey, int e)
    {
        if (e < 0 || e >= Reactions.Length) return ActResult.Fail("Нема такої реакції");
        if (_phase is not (Draw or Reveal)) return ActResult.Fail("Зараз нема на що реагувати");
        var now = Now;
        if (_fanReact.TryGetValue(nickKey, out var last) && (now - last).TotalMilliseconds < ReactEveryMs) return ActResult.Done;
        if (_fanReact.Count > 256) _fanReact.Clear();
        _fanReact[nickKey] = now;
        AddReact(e);
        return ActResult.Done;
    }

    void AddReact(int e)
    {
        _reactRing[_reactId % _reactRing.Length] = e;
        _reactId++;
        _turnReacts[e]++;
        _frameDirty = true;
    }

    // ---------------------------------------------------------------- галерея й ❤ (п. 26)

    /// <summary>❤ найкращому чужому малюнку партії; передумав — голос переїжджає.</summary>
    ActResult VoteFor(int seat, JsonElement payload)
    {
        if (_phase != Vote) return ActResult.Fail("Голосують після останнього малюнка");
        if (_left.Contains(seat)) return ActResult.Fail("Ти вже не за столом");
        var t = Int(payload, "t", -1);
        var art = _art.Find(a => a.T == t);
        if (art is null) return ActResult.Fail("Нема такого малюнка");
        if (art.Seat == seat) return ActResult.Fail("За свій не можна — вибери чужий шедевр 🙂");
        _votes[seat] = t;
        _viewDirty = true;
        if (AllVoted()) Over();
        return ActResult.Done;
    }

    bool CanVote(int s)
    {
        foreach (var a in _art) if (a.Seat != s) return true;
        return false;
    }

    bool AllVoted()
    {
        for (var s = 0; s < Seats; s++)
            if (Ctx.Seated(s) && !_left.Contains(s) && CanVote(s) && !_votes.ContainsKey(s)) return false;
        return true;
    }

    void StartVote()
    {
        var voters = 0;
        for (var s = 0; s < Seats; s++) if (Ctx.Seated(s) && !_left.Contains(s) && CanVote(s)) voters++;
        if (_duo || _art.Count < 2 || voters < 2 || PresentCount() < 2) { Over(); return; }
        _phase = Vote;
        _phaseStart = Now;
        _until = _phaseStart.AddMilliseconds(VoteMs);
        _votes.Clear();
        _frameDirty = _viewDirty = true;
    }

    /// <summary>Малюнок партії для публічного альбому (HTTP): лише після партії, лише тим, хто грав, до трьох на людину.</summary>
    public (PictionaryArt? Art, string? Error) Pin(string nick, int t)
    {
        var key = Auth.NickKey(nick);
        if (_phase != Done) return (null, "Закидати в альбом можна після партії");
        if (!_roster.Contains(key)) return (null, "Закидають ті, хто грав");
        var i = _art.FindIndex(a => a.T == t);
        if (i < 0) return (null, "Нема такого малюнка");
        if (_pinned.Contains(t)) return (null, "Цей уже в альбомі 📌");
        if (_pinsBy.TryGetValue(key, out var n) && n >= PinsPerNick) return (null, $"Ти вже закинув {PinsPerNick} — хай інші теж оберуть");
        if (_art[i].Z.Length > PictionaryStore.MaxArtChars) return (null, "Цей малюнок завеликий для альбому");
        _pinned.Add(t);
        _pinsBy[key] = n + 1;
        var a = _art[i];
        var hearts = i < _hearts.Length ? _hearts[i] : 0;
        return (new PictionaryArt(0, a.Word, a.Nick, nick, hearts, a.Home, Now, a.Z), null);
    }

    /// <summary>Малюнок ходу <paramref name="t"/> (штрихами) — для галереї тих, хто цей хід не застав.</summary>
    public string? ArtZ(int t) => _art.Find(a => a.T == t)?.Z;

    ActResult Guess(int seat, JsonElement payload)
    {
        if (_phase != Draw) return ActResult.Fail(_phase == Pick ? "Мить — художник ще обирає слово" : "Зараз не вгадують");
        if (seat == _drawer) return ActResult.Fail("Ти малюєш — словами не підказуй 🙂");
        if (_left.Contains(seat)) return ActResult.Fail("Ти вже не за столом");
        if (_guessed.Contains(seat)) return ActResult.Fail("Слово вже твоє — дай іншим");

        var raw = Str(payload, "text").Trim();
        if (raw.Length == 0) return ActResult.Fail("Тяпни здогадку");
        if (raw.Length > MaxGuessLength) raw = raw[..MaxGuessLength];

        var now = Now;
        if (_lastGuess.TryGetValue(seat, out var last) && (now - last).TotalMilliseconds < GuessEveryMs)
            return ActResult.Fail("Не так швидко");
        _lastGuess[seat] = now;

        if (Matches(raw, _word))
        {
            int points;
            if (_duo)
            {
                // удвох рахунок спільний: скільки слів угадали разом
                points = 1;
                _duoCount++;
                foreach (var s in _order) { _scores[s] = _duoCount; _gained[s] = 1; }
            }
            else
            {
                var left = Math.Max(0, (_until - now).TotalMilliseconds);
                points = MinGuessPoints + (int)Math.Round((MaxGuessPoints - MinGuessPoints) * left / _drawMs);
                if (_guessed.Count == 0) points += FirstBonus;
                _scores[seat] += points;
                _gained[seat] += points;
            }
            _guessed.Add(seat);
            AddFeed(seat, "ok", null);
            _viewDirty = true;
            if (!Guessers().Any(s => !_guessed.Contains(s))) EndTurn();
            return ActResult.Accept($"Є! Лови +{points}");
        }

        // Майже вгадав — кажемо лише йому, у стрічку не пишемо: інакше решта отримала б підказку задарма.
        if (Close(raw, _word)) return ActResult.Fail("Гаряче! Ще трошки");

        AddFeed(seat, "guess", raw);
        return ActResult.Done;
    }

    /// <summary>
    /// Здогадка збігається зі словом без огляду на регістр, дефіси, пробіли й апострофи — або одне з її
    /// слів збігається з однословною відповіддю («це кіт!» на «кіт»).
    /// </summary>
    public static bool Matches(string guess, string word)
    {
        var w = PictionaryWords.Normalize(word);
        if (w.Length == 0) return false;
        if (PictionaryWords.Normalize(guess) == w) return true;
        if (word.Contains(' ') || word.Contains('-')) return false;
        return guess.Split([' ', ',', '.', '!', '?', ';', ':'], StringSplitOptions.RemoveEmptyEntries)
            .Any(part => PictionaryWords.Normalize(part) == w);
    }

    /// <summary>«Гаряче»: різниця в одну літеру (для слів від 4 літер) або у дві (від 8).</summary>
    public static bool Close(string guess, string word)
    {
        var g = PictionaryWords.Normalize(guess);
        var w = PictionaryWords.Normalize(word);
        if (w.Length < 4 || g.Length == 0 || g == w) return false;
        var max = w.Length >= 8 ? 2 : 1;
        return Math.Abs(g.Length - w.Length) <= max && Distance(g, w, max) <= max;
    }

    /// <summary>
    /// Відстань Левенштейна, де переставлені сусідні літери — одна помилка, а не дві («кажна» — «кажан»:
    /// пальці на телефоні так помиляються найчастіше). З раннім виходом: далі за <paramref name="max"/>
    /// рахувати нема чого.
    /// </summary>
    static int Distance(string a, string b, int max)
    {
        var before = new int[b.Length + 1];
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var best = cur[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    cur[j] = Math.Min(cur[j], before[j - 2] + 1);
                best = Math.Min(best, cur[j]);
            }
            if (best > max) return best;
            (before, prev, cur) = (prev, cur, before);
        }
        return prev[b.Length];
    }

    bool CanDraw(int seat) => _phase == Draw && seat == _drawer;

    ActResult AddLine(int seat, JsonElement payload)
    {
        if (!CanDraw(seat)) return ActResult.Fail("Зараз малює не ти");
        if (_sketch.Line(payload) is { } error) return ActResult.Fail(error);
        _frameDirty = true;
        return ActResult.Done;
    }

    ActResult AddFill(int seat, JsonElement payload)
    {
        if (!CanDraw(seat)) return ActResult.Fail("Зараз малює не ти");
        if (_sketch.Fill(payload) is { } error) return ActResult.Fail(error);
        _frameDirty = true;
        return ActResult.Done;
    }

    /// <summary>Прибрати останній штрих цілком (усі його шматки).</summary>
    ActResult Undo(int seat)
    {
        if (!CanDraw(seat)) return ActResult.Fail("Зараз малює не ти");
        if (_sketch.Undo()) Rewrite();
        return ActResult.Done;
    }

    ActResult Clear(int seat)
    {
        if (!CanDraw(seat)) return ActResult.Fail("Зараз малює не ти");
        WipeCanvas();
        return ActResult.Done;
    }

    /// <summary>Гравець помітив дірку в кадрах — розішлемо всім повні види (не частіше за раз на пару секунд).</summary>
    ActResult Sync(int seat)
    {
        var now = Now;
        if (_lastSync.TryGetValue(seat, out var last) && (now - last).TotalMilliseconds < SyncEveryMs) return ActResult.Done;
        _lastSync[seat] = now;
        _viewDirty = true;
        return ActResult.Done;
    }

    /// <summary>Малюнок змінився не дописуванням: нова версія, і наступний кадр везе його цілком.</summary>
    void Rewrite()
    {
        _ver++;
        _sent = 0;
        _frameDirty = true;
    }

    void WipeCanvas()
    {
        _sketch.Clear();
        Rewrite();
    }

    // =========================================================================================
    // Хід часу
    // =========================================================================================

    public override TickResult Tick()
    {
        var now = Now;
        if (_phase != Done && PresentCount() < 2)
        {
            Over();
        }
        else if (_duo && _phase is Draw or Reveal && now >= _duoUntil)
        {
            if (_phase == Draw) EndTurn();
            Over();
        }
        else if (_phase == Pick)
        {
            if (_left.Contains(_drawer) || !Ctx.Seated(_drawer)) EndTurn();
            else if (now >= _until) BeginDraw(Ctx.Rng.Next(_choices.Length));
        }
        else if (_phase == Vote)
        {
            if (now >= _until || AllVoted()) Over();
        }
        else if (_phase == Draw)
        {
            if (_left.Contains(_drawer) || !Ctx.Seated(_drawer) || now >= _until) EndTurn();
            else if (now >= _nextHint) Hint();
        }
        else if (_phase == Reveal && now >= _until)
        {
            NextTurn();
        }

        if (_frameDirty)
        {
            _frameFrom = _sent;
            _sent = _sketch.Count;
            _frameFeedFrom = _feedSent;
            _feedSent = _feedId;
            _frameReactFrom = _reactSent;
            _reactSent = _reactId;
        }
        var result = new TickResult(_frameDirty, _viewDirty);
        _frameDirty = _viewDirty = false;
        return result;
    }

    /// <summary>Коли відкривати літери: частки часу на малюнок.</summary>
    static readonly double[] HintMarks = [0.5, 0.7, 0.85];

    /// <summary>
    /// Підказки: коли минула половина часу, 70% і 85% — відкриваємо по літері, але не більше третини слова
    /// і не більше трьох. Короткі слова (до 4 літер) без підказок: там одна літера — вже пів відповіді.
    /// Скільки й коли — рахуємо раз на слово (<see cref="PlanHints"/>), тик лише звіряє годинник.
    /// </summary>
    void Hint()
    {
        var closed = _letters.Where(i => !_hinted.Contains(i)).ToList();
        if (closed.Count <= 1) { _nextHint = DateTimeOffset.MaxValue; return; }
        _hinted.Add(closed[Ctx.Rng.Next(closed.Count)]);
        _mask = BuildMask();
        NextHintAt();
        // Маска їде кадром. Повні види (з усім малюнком, кожному місцю) заради однієї літери не шлемо.
        _frameDirty = true;
    }

    void PlanHints()
    {
        _letters = [.. Enumerable.Range(0, _word.Length).Where(i => char.IsLetter(_word[i]))];
        _hintsAllowed = _duo || _letters.Length < 5 ? 0 : Math.Min(3, _letters.Length / 3);
        NextHintAt();
    }

    void NextHintAt() => _nextHint = _hinted.Count < _hintsAllowed
        ? _phaseStart.AddMilliseconds(_drawMs * HintMarks[_hinted.Count])
        : DateTimeOffset.MaxValue;

    void BeginDraw(int choice)
    {
        var word = _choices[choice];
        _word = word;
        _homeNow = choice < _choiceHome.Length && _choiceHome[choice] ? _home.Find(h => h.Word == word) : null;
        if (_homeNow is not null) _homeUsed.Add(PictionaryWords.Normalize(word));
        else _used.Add(PictionaryWords.Normalize(word));
        _hinted.Clear();
        _phase = Draw;
        _phaseStart = Now;
        _until = _duo ? _duoUntil : _phaseStart.AddMilliseconds(_drawMs);
        _mask = BuildMask();
        PlanHints();
        _frameDirty = _viewDirty = true;
    }

    /// <summary>Хід скінчився: усі вгадали, минув час або художник пішов. Художник бере частку від вгадувачів.</summary>
    void EndTurn()
    {
        var possible = Math.Max(1, _order.Count(s => s != _drawer && Ctx.Seated(s) && !_left.Contains(s)));
        if (!_duo && _guessed.Count > 0 && Ctx.Seated(_drawer) && !_left.Contains(_drawer))
        {
            // половина середнього: хороший малюнок вигідний, але вгадувати все одно вигідніше
            var share = _guessed.Sum(s => _gained[s]) / (2 * possible);
            _scores[_drawer] += share;
            _gained[_drawer] += share;
        }
        if (_word.Length > 0) AddFeed(-1, "word", _word);
        else AddFeed(_drawer, "skip", null);
        // малюнок ходу — у галерею наприкінці й на «📌 в альбом» (штрихами, п. 26/32)
        if (_word.Length > 0 && _sketch.Count > 0)
            _art.Add(new Art(_turn + 1, _drawer, NickOrEmpty(_drawer), _word, _homeNow is not null, PackedAll(), (int[])_turnReacts.Clone()));
        _phase = Reveal;
        _phaseStart = Now;
        _until = _phaseStart.AddMilliseconds(_duo ? DuoRevealMs : RevealMs);
        _frameDirty = _viewDirty = true;
    }

    void NextTurn()
    {
        if (_duo && Now >= _duoUntil) { Over(); return; }
        _turn++;
        while (_turn < _turns && !Present().Contains(_order[_turn % _order.Length])) _turn++;
        if (_turn >= _turns) { StartVote(); return; }

        _drawer = _order[_turn % _order.Length];
        _choices = Deal(_duo ? 1 : Choices, []);
        if (_choices.Length == 0) { Over(); return; }
        _word = "";
        _mask = "";
        _homeNow = null;
        _hinted.Clear();
        _nextHint = DateTimeOffset.MaxValue;
        _guessed.Clear();
        Array.Clear(_gained);
        Array.Clear(_turnReacts);
        WipeCanvas();
        if (_duo)
        {
            // удвох без вибору: слово одразу, малюєте по черзі, годинник спільний
            BeginDraw(0);
            return;
        }
        _phase = Pick;
        _phaseStart = Now;
        _until = _phaseStart.AddMilliseconds(PickMs);
        _frameDirty = _viewDirty = true;
    }

    void Over()
    {
        if (_phase == Done) return;
        _phase = Done;
        _frameDirty = _viewDirty = true;
        var seats = Present().ToArray();
        if (_duo) { OverDuo(seats); return; }
        var best = seats.Length == 0 ? 0 : seats.Max(s => _scores[s]);
        int[] winners = best > 0 ? [.. seats.Where(s => _scores[s] == best)] : [];
        foreach (var s in seats) Ctx.Score(s, _scores[s]);
        var artist = CountHearts();
        _result = new { winners, scores = (int[])_scores.Clone() };
        Ctx.Finish(winners, Told(seats, winners) + artist, seats.ToDictionary(s => s, s => (long)_scores[s]));
    }

    /// <summary>❤ галереї → «Митець партії»: хто зібрав найбільше (нічия — усі), черепки й рядок у Журнал.</summary>
    string CountHearts()
    {
        _hearts = new int[_art.Count];
        foreach (var t in _votes.Values)
        {
            var i = _art.FindIndex(a => a.T == t);
            if (i >= 0) _hearts[i]++;
        }
        var max = _hearts.Length == 0 ? 0 : _hearts.Max();
        if (max == 0) { _artists = []; return ""; }
        _artists = [.. Enumerable.Range(0, _art.Count).Where(i => _hearts[i] == max).Select(i => _art[i].Seat).Distinct()];
        foreach (var s in _artists)
            if (Ctx.Seated(s) && !_left.Contains(s)) Ctx.Award(s, ArtistShards, "🎨 Митець партії в піктіонарі");
        return $" · 🎨 Митець партії — {string.Join(" і ", _artists.Select(NickOrEmpty))} ({max} ❤)";
    }

    /// <summary>Кінець «Скільки встигнемо»: спільний рахунок, рекорд пари — якщо обоє дограли.</summary>
    void OverDuo(int[] seats)
    {
        var both = seats.Length == 2;
        if (both && _store is not null) _pairRecord = _store.RecordPair([.. seats.Select(NickOrEmpty)], _duoCount, Now);
        foreach (var s in seats) Ctx.Score(s, _duoCount);
        int[] winners = both && _duoCount > 0 ? seats : [];
        _result = new { winners, scores = (int[])_scores.Clone(), duo = _duoCount, best = _pairBest, record = _pairRecord };
        var names = string.Join(" і ", _order.Select(NickOrEmpty));
        var tail = _pairRecord ? " — рекорд пари!" : _pairBest > 0 ? $" (рекорд пари — {_pairBest})" : "";
        Ctx.Finish(winners, $"{Info.Title} удвох: {names} встигли {_duoCount} {WordsOf(_duoCount)} за три хвилини{tail}",
            seats.ToDictionary(s => s, s => (long)_duoCount));
    }

    static string WordsOf(int n) => (n % 10, n % 100) switch
    {
        (1, not 11) => "слово",
        (2 or 3 or 4, not (12 or 13 or 14)) => "слова",
        _ => "слів",
    };

    string Told(int[] seats, int[] winners)
    {
        var parts = seats.OrderByDescending(s => _scores[s]).Select(s => $"{Ctx.NickOf(s)} {_scores[s]}");
        var tail = winners.Length == 0 ? "ніхто нічого не вгадав" : "попереду " + string.Join(" і ", winners.Select(Ctx.NickOf));
        return $"{Info.Title}: {string.Join(", ", parts)} — {tail}";
    }

    /// <summary>Компанійна гра: хтось встав — решта грає далі. Пішов художник — його хід закінчується.</summary>
    public override void OnLeave(int seat)
    {
        _left.Add(seat);
        AddFeed(seat, "left", null);
        if (_phase is Pick or Draw && seat == _drawer) EndTurn();
        else if (_phase == Draw && Guessers().Any() && !Guessers().Any(s => !_guessed.Contains(s))) EndTurn();
        if (_phase != Done && Present().Count() < 2) Over();
        _viewDirty = _frameDirty = true;
    }

    void AddFeed(int seat, string kind, string? text)
    {
        _feed.Add(new FeedItem(++_feedId, seat, kind, text));
        if (_feed.Count > FeedKeep) _feed.RemoveRange(0, _feed.Count - FeedKeep);
        _frameDirty = true;
    }

    // =========================================================================================
    // Вид і кадр
    // =========================================================================================

    /// <summary>«к_т» для тих, хто слова не знає: відкриті підказки, пробіли й дефіси на своїх місцях.</summary>
    string Mask() => _mask;

    string BuildMask()
    {
        if (_word.Length == 0) return "";
        return string.Concat(_word.Select((c, i) => !char.IsLetter(c) || _hinted.Contains(i) ? c : '_'));
    }

    bool Knows(int? seat) =>
        _phase is Reveal or Vote or Done || seat is { } s && (s == _drawer || _guessed.Contains(s));

    object[] Feed(int take) => [.. _feed.TakeLast(take).Select(f => new { id = f.Id, seat = f.Seat, kind = f.Kind, text = f.Text })];

    public override object View(int? seat) => new
    {
        phase = _phase,
        turn = _phase is Pick or Draw ? _drawer : (int?)null,
        drawer = _drawer,
        turnNo = _turn + 1,
        turns = _turns,
        round = _order.Length == 0 ? 0 : Math.Min(_rounds, _turn / _order.Length + 1),
        rounds = _rounds,
        choices = _phase == Pick && seat == _drawer ? (string[])_choices.Clone() : null,
        word = Knows(seat) && _word.Length > 0 ? _word : null,
        mask = Mask(),
        until = _until,
        totalMs = _phase switch { Pick => PickMs, Draw => _drawMs, Reveal => RevealMs, _ => 0 },
        scores = (int[])_scores.Clone(),
        gained = (int[])_gained.Clone(),
        guessed = _guessed.ToArray(),
        left = _left.Order().ToArray(),
        drawing = new { ver = _ver, n = _sketch.Count, z = PackedAll() },
        feed = Feed(FeedKeep),
        result = _result,
        mode = _duo ? Duo : Party,
        reroll = seat is { } me && me == _drawer && (_duo ? _phase == Draw : _phase == Pick && !_rerolled.Contains(me)),
        choicesHome = _phase == Pick && seat == _drawer ? (bool[])_choiceHome.Clone() : null,
        homeBy = Knows(seat) && _homeNow is not null && _word.Length > 0 ? _homeNow.Nick : null,
        home = new { n = _home.Count, mine = MyHome(seat) },
        duo = _duo ? new { count = _duoCount, until = _duoUntil, totalMs = DuoMs, best = _pairBest, record = _pairRecord } : null,
        reacts = (int[])_turnReacts.Clone(),
        gallery = _phase is Vote or Done && _art.Count > 0 ? Gallery() : null,
        myVote = seat is { } vs && _votes.TryGetValue(vs, out var vt) ? vt : (int?)null,
        votes = _votes.Count,
        artists = _phase == Done ? (int[])_artists.Clone() : [],
        pinned = _pinned.Order().ToArray(),
    };

    string[] MyHome(int? seat)
    {
        if (seat is not { } s || Ctx.NickOf(s) is not { } nick) return [];
        var key = Auth.NickKey(nick);
        return [.. _home.Where(h => h.Key == key).Select(h => h.Word)];
    }

    object[] Gallery() => [.. _art.Select((a, i) => new
    {
        t = a.T, seat = a.Seat, nick = a.Nick, word = a.Word, home = a.Home, re = a.Re,
        hearts = _phase == Done && i < _hearts.Length ? _hearts[i] : (int?)null,
    })];

    /// <summary>Увесь малюнок рядком — один раз на версію, а не на кожне місце (десять видів — одне пакування).</summary>
    string PackedAll()
    {
        if (_packedVer != _ver || _packedN != _sketch.Count)
        {
            _packed = SketchWire.Pack(_sketch.Ops());
            _packedVer = _ver;
            _packedN = _sketch.Count;
        }
        return _packed;
    }

    /// <summary>Публічний кадр: без слова. Дописані операції від <see cref="_frameFrom"/>, маска, таймер, стрічка.</summary>
    public override object? Frame() => new
    {
        ph = _phase,
        drawer = _drawer,
        ver = _ver,
        from = _frameFrom,
        n = _sketch.Count,
        z = SketchWire.Pack(_sketch.Ops(_frameFrom)),
        mask = Mask(),
        until = _until,
        guessed = _guessed.ToArray(),
        feed = _feedId > _frameFeedFrom ? Feed(Math.Min(FeedInFrame, _feedId - _frameFeedFrom)) : null,
        re = _reactId > _frameReactFrom ? NewReacts() : null,
        dc = _duo ? _duoCount : (int?)null,
    };

    /// <summary>Реакції, що з'явились після минулого кадру (не більше, ніж вміщає кільце).</summary>
    int[] NewReacts()
    {
        var n = Math.Min(_reactRing.Length, _reactId - _frameReactFrom);
        var list = new int[n];
        for (var k = 0; k < n; k++) list[k] = _reactRing[(_reactId - n + k) % _reactRing.Length];
        return list;
    }

    // =========================================================================================
    // Дрібниці
    // =========================================================================================

    static int Int(JsonElement payload, string field, int fallback) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(field, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && !double.IsNaN(d) && Math.Abs(d) < int.MaxValue
            ? (int)Math.Round(d) : fallback;

    static string Str(JsonElement payload, string field) => payload.ValueKind switch
    {
        JsonValueKind.String => payload.GetString() ?? "",
        JsonValueKind.Object when payload.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String => v.GetString() ?? "",
        _ => "",
    };
}
