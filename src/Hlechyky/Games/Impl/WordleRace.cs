using System.Globalization;
using System.Text.Json;
using Hlechyky.Games.Economy;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Глек-слово наввипередки: те саме п'ятилітерне слово для всіх за столом (2–6), шість спроб кожному,
/// кілька раундів. Свої літери бачиш лише ти; суперників видно самими кольорами — хто вже близько, а
/// хто досі в чорному. За вгадане слово — 7 мінус спроби, першому, хто вгадав, ще +1.
/// <para>
/// Прохід №3: хто вже вгадав (чи вичерпав спроби), бачить чужі дошки з літерами; опція «💡 Підказка за очко»;
/// режим «Спринт» — у кожного своя черга слів, хто перший вгадав <see cref="SprintTarget"/>, той і переміг;
/// довжина слова 4/5/6 (4 і 6 — зі списків <see cref="WordleLists"/>).
/// </para>
/// <para>
/// Щоденне соло (<see cref="Wordle"/>) лишається як було: тут не щоденна головоломка, а стіл у лобі, і
/// слова беруться зі списку відповідей випадково, але ніколи не слово дня й не слова найближчого тижня —
/// інакше гра наввипередки спойлерила б «Щоденний глек».
/// </para>
/// </summary>
public sealed class WordleRace : Game
{
    public const int Len = Wordle.Len;
    /// <summary>Спринт: стільки слів треба вгадати, щоб перемогти.</summary>
    public const int SprintTarget = 3;
    /// <summary>Скільки підказок можна взяти на одне слово.</summary>
    public const int MaxHints = 2;
    public const string ModeSame = "same", ModeSprint = "sprint";
    public const int MaxTries = Wordle.MaxTries;
    public const int MaxSeats = 6;
    /// <summary>Скільки секунд показуємо слово й чужі спроби між раундами.</summary>
    public const int RevealSeconds = 8;
    /// <summary>Бонус тому, хто вгадав слово раунду першим.</summary>
    public const int FirstBonus = 1;
    /// <summary>Скільки днів довкола сьогодні не чіпаємо: ці слова — «Щоденний глек» учора, сьогодні й завтра.</summary>
    const int DailyGuard = 7;

    public const string PhaseLobby = "lobby", PhasePlay = "play", PhaseReveal = "reveal", PhaseDone = "done";

    static readonly int[] RoundChoices = [1, 3, 5];
    static readonly int[] SecondChoices = [90, 180, 300];
    static readonly int[] LenChoices = [5, 4, 6];
    public const int DefaultRounds = 3, DefaultSeconds = 180;

    public override GameInfo Info { get; } = new(
        "wordle-race", "Глек-слово наввипередки", "Глек-слово наввипередки", GameGroup.Party, 2, MaxSeats,
        TickMs: 500, Start: StartMode.ByHost, Hidden: true, Rated: false,
        Options:
        [
            new GameOption("rounds", "Раундів", [.. RoundChoices.Select(n => (Str(n), Str(n)))], Str(DefaultRounds)),
            new GameOption("seconds", "Час на слово", [.. SecondChoices.Select(n => (Str(n), n % 60 == 0 ? $"{n / 60} хв" : $"{n / 60}½ хв"))], Str(DefaultSeconds)),
            new GameOption("mode", "Режим", [(ModeSame, "Одне слово на всіх"), (ModeSprint, $"Спринт: кожен своє, до {SprintTarget} слів")], ModeSame),
            new GameOption("len", "Довжина слова", [.. LenChoices.Select(n => (Str(n), LettersWord(n)))], Str(Len)),
            new GameOption("hint", "Підказки", [("off", "Без підказок"), ("on", "💡 Літера за очко")], "off"),
        ],
        Hint: "Одне слово на всіх, шість спроб кожному. Чужі спроби видно кольорами, але без літер. Менше спроб — більше очок, першому ще +1",
        Client: "wordle");

    static string Str(int n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>«4 літери», «5 літер», «6 літер».</summary>
    public static string LettersWord(int n) => n is >= 2 and <= 4 ? $"{n} літери" : $"{n} літер";

    /// <summary>Для відмов: «рівно чотири літери».</summary>
    static string LettersSpelled(int n) => n switch { 4 => "чотири літери", 6 => "шість літер", _ => "п'ять літер" };

    /// <summary>Місця тут рівні — просто номери, як у «Скільки?».</summary>
    public override string SeatName(int seat) => Str(seat + 1);

    /// <summary>Стан одного гравця в поточному раунді плюс очки за партію.</summary>
    sealed class Player
    {
        public bool In;            // сидів на старті партії
        public string? Nick;       // нік на старті: хто встав, лишається на столі під своїм ім'ям, а не «—»
        public bool Gone;          // встав посеред партії
        public readonly List<string> Guesses = [];
        public bool Solved, Failed;
        public long? Ms;           // за скільки вгадав від початку раунду
        public bool First;         // вгадав першим у раунді
        public int Gained;         // очки за цей раунд
        public int Total;          // очки за партію
        public int Words;          // скільки слів вгадав за партію
        public readonly List<int> Hints = [];      // підказані місця в цьому слові
        // спринт: своє слово, зіграні слова і коли вгадано останнє (для нічиєї за словами)
        public string Answer = "";
        public readonly List<(string Word, bool Ok)> Played = [];
        public long LastSolveMs = long.MaxValue;

        public bool Done => Solved || Failed;
        public bool Active => In && !Gone;

        public void NewRound()
        {
            Guesses.Clear();
            Hints.Clear();
            Solved = Failed = First = false;
            Ms = null;
            Gained = 0;
        }
    }

    readonly Player[] _p = [.. Enumerable.Range(0, MaxSeats).Select(_ => new Player())];
    readonly List<string> _answers = [];

    readonly HashSet<string> _used = new(StringComparer.Ordinal);

    Words? _words;
    WordleLists? _lists;
    int _rounds = DefaultRounds;
    int _seconds = DefaultSeconds;
    int _len = Len;
    bool _sprint, _hint;
    /// <summary>Переможці спринту — клієнтові не вгадувати нічию за часом останнього слова.</summary>
    int[]? _winners;
    /// <summary>До «Почати» — лобі: вид має казати «чекаємо», а не «партію зіграно».</summary>
    string _phase = PhaseLobby;
    string _answer = "";
    DateTimeOffset _startedAt, _endsAt;
    /// <summary>
    /// Щось змінилось поза тиком (спроба, хтось встав). Гра з тиком — реалтайм для каркаса, і після Act він
    /// видів не шле: розсилає їх наступний тик, коли ми про це попросимо.
    /// </summary>
    bool _dirty;

    int Round => _answers.Count;

    /// <summary>Слово поточного раунду — для тестів і діагностики. У вид до кінця раунду не йде.</summary>
    public string Answer => _answer;

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _words = Ctx.Services.GetService<Words>();
        if (_words is null || _words.Stats.Small5 == 0) throw new GameError("Нема словника — Глек-слово відпочиває");
        _rounds = Pick(options, "rounds", RoundChoices, DefaultRounds);
        _seconds = Pick(options, "seconds", SecondChoices, DefaultSeconds);
        _len = Pick(options, "len", LenChoices, Len);
        _sprint = options.TryGetValue("mode", out var mode) && mode == ModeSprint;
        // підказка — лише для «одного слова на всіх»: у спринті невдале слово й так просто міняється на нове
        _hint = !_sprint && options.TryGetValue("hint", out var hint) && hint == "on";
        _lists = null;
        if (_len != Len)
        {
            _lists = Ctx.Services.GetService<WordleLists>();
            if (_lists is null || _lists.Answers(_len).Count < 20)
                throw new GameError($"Слів на {LettersWord(_len)} ще нема — зіграймо на п'ять");
        }
    }

    /// <summary>Режим спринту (для тестів).</summary>
    public bool Sprint => _sprint;

    /// <summary>Слово гравця (у спринті в кожного своє) — для тестів і діагностики.</summary>
    public string AnswerOf(int seat) => _sprint ? _p[seat].Answer : _answer;

    static int Pick(IReadOnlyDictionary<string, string> options, string key, int[] allowed, int fallback) =>
        options.TryGetValue(key, out var raw) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && allowed.Contains(n)
            ? n : fallback;

    public override void Start()
    {
        _answers.Clear();
        for (var s = 0; s < MaxSeats; s++)
        {
            var p = _p[s];
            p.NewRound();
            p.In = Ctx.Seated(s);
            p.Nick = Ctx.NickOf(s);
            p.Gone = false;
            p.Total = p.Words = 0;
            p.Played.Clear();
            p.Answer = "";
            p.LastSolveMs = long.MaxValue;
        }
        _used.Clear();
        _winners = null;
        if (_sprint) StartSprint(Ctx.Clock.UtcNow);
        else NewRound(Ctx.Clock.UtcNow);
        if (_sprint ? _p.Any(p => p.In && p.Answer.Length != _len) : _answer.Length != _len)
        {
            _phase = PhaseDone;
            Ctx.Finish([], $"{Info.Title}: словник кудись подівся, партії не буде");
        }
    }

    void NewRound(DateTimeOffset now)
    {
        _answer = PickWord();
        _answers.Add(_answer);
        foreach (var p in _p) p.NewRound();
        _phase = PhasePlay;
        _startedAt = now;
        _endsAt = now.AddSeconds(_seconds);
    }

    /// <summary>
    /// Слово раунду. Беремо ту саму перемішану чергу, з якої йдуть слова дня, але з дня, далекого від
    /// сьогоднішнього: так слово дня (і сусідні) тут не випаде ніколи, а решта списку — рівноймовірно.
    /// Уже зіграні в цій партії слова не повторюємо.
    /// </summary>
    string PickWord()
    {
        var word = _len == Len ? PickFive() : PickOther();
        _used.Add(word);
        return word;
    }

    /// <summary>4 чи 6 літер: будь-яке зі списку, аби не повторювалось у партії.</summary>
    string PickOther()
    {
        var list = _lists?.Answers(_len) ?? [];
        if (list.Count == 0) return "";
        var word = "";
        for (var tries = 0; tries < 50; tries++)
        {
            word = list[Ctx.Rng.Next(list.Count)];
            if (!_used.Contains(word)) break;
        }
        return word;
    }

    string PickFive()
    {
        var n = _words?.Stats.Small5 ?? 0;
        if (n == 0) return "";
        var today = DateOnly.ParseExact(Days.Today(Ctx.Clock), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var guard = n > 4 * DailyGuard ? DailyGuard : 0;
        var word = "";
        for (var tries = 0; tries < 50; tries++)
        {
            var k = guard == 0 ? Ctx.Rng.Next(n) : guard + 1 + Ctx.Rng.Next(n - 2 * guard - 1);
            word = _words!.Daily5ForDay(today.AddDays(k).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (!_used.Contains(word)) break;
        }
        return word;
    }

    // ------------------------------------------------------------------------------------- ходи

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action is not ("guess" or "hint")) return ActResult.Fail("Тут так не ходять");
        if (seat is < 0 or >= MaxSeats || !_p[seat].Active) return ActResult.Fail("Ти в цій партії не граєш");
        if (_phase == PhaseReveal) return ActResult.Fail("Мить — зараз буде нове слово");
        if (_phase != PhasePlay) return ActResult.Fail("Партію зіграно");
        var p = _p[seat];
        if (action == "hint") return Hint(p);
        if (p.Solved) return ActResult.Fail("Слово вже твоє — дивись, як мучаться інші");
        if (p.Failed) return ActResult.Fail("Спроби скінчились — чекай кінця раунду");

        if (Word(payload) is not { } raw) return ActResult.Fail("Не зрозумів, що за слово");
        var word = Words.Normalize(raw);
        if (word is null || word.Length != _len) return ActResult.Fail($"Треба рівно {LettersSpelled(_len)}");
        if (!Valid(word)) return ActResult.Fail("Такого слова не знаю");

        if (_sprint) return SprintGuess(p, word);

        var now = Ctx.Clock.UtcNow;
        p.Guesses.Add(word);
        _dirty = true;
        string reply = "";
        if (word == _answer)
        {
            p.Solved = true;
            p.Ms = (long)(now - _startedAt).TotalMilliseconds;
            p.First = !_p.Any(o => o != p && o.Solved);
            // підказка коштує очко з виграшу за слово, але в мінус не заводить
            p.Gained = Math.Max(0, MaxTries + 1 - p.Guesses.Count + (p.First ? FirstBonus : 0) - p.Hints.Count);
            p.Words++;
            reply = p.First ? $"Є! Раніше за всіх — лови +{p.Gained}" : $"Є! Лови +{p.Gained}";
        }
        else if (p.Guesses.Count >= MaxTries) p.Failed = true;

        if (_p.Where(x => x.Active).All(x => x.Done)) EndRound(now);
        else if (p.Done) reply = (reply.Length > 0 ? reply + ". " : "") + "Тепер тобі видно чужі літери";
        return reply.Length > 0 ? ActResult.Accept(reply) : ActResult.Done;
    }

    bool Valid(string word) => _len == Len ? _words?.IsValid5(word) == true : _lists?.IsValid(word, _len) == true;

    /// <summary>
    /// 💡 Одна літера на своєму місці. Беремо місце, якого гравець ще не відкрив зеленим і не брав
    /// підказкою; ціна — очко з виграшу за це слово (див. <see cref="Act"/>).
    /// </summary>
    ActResult Hint(Player p)
    {
        if (!_hint) return ActResult.Fail("Підказок за цим столом нема — так домовились");
        if (p.Done) return ActResult.Fail(p.Solved ? "Слово вже твоє" : "Спроби скінчились — чекай кінця раунду");
        if (p.Hints.Count >= MaxHints) return ActResult.Fail($"Більше {MaxHints} підказок на слово не дають — далі сам");
        Span<int> free = stackalloc int[_len];
        var k = 0;
        for (var i = 0; i < _len; i++)
        {
            if (p.Hints.Contains(i)) continue;
            var known = false;
            foreach (var g in p.Guesses) if (g[i] == _answer[i]) { known = true; break; }
            if (!known) free[k++] = i;
        }
        if (k == 0) return ActResult.Fail("Усі літери вже на місцях — лишилось скласти");
        var at = free[Ctx.Rng.Next(k)];
        p.Hints.Add(at);
        _dirty = true;
        return ActResult.Accept($"💡 {at + 1}-а літера — «{char.ToUpperInvariant(_answer[at])}». Мінус очко з виграшу");
    }

    // ------------------------------------------------------------------------------------- спринт

    void StartSprint(DateTimeOffset now)
    {
        _phase = PhasePlay;
        _startedAt = now;
        // час на слово × скільки слів треба: «3 хв на слово» — це дев'ять хвилин на весь спринт
        _endsAt = now.AddSeconds(_seconds * SprintTarget);
        _answer = "";
        foreach (var p in _p.Where(x => x.In)) SprintNext(p);
    }

    void SprintNext(Player p)
    {
        p.NewRound();
        p.Answer = PickWord();
    }

    ActResult SprintGuess(Player p, string word)
    {
        var now = Ctx.Clock.UtcNow;
        p.Guesses.Add(word);
        _dirty = true;
        if (word == p.Answer)
        {
            p.Words++;
            p.Total = p.Words;
            p.LastSolveMs = (long)(now - _startedAt).TotalMilliseconds;
            p.Played.Add((p.Answer, true));
            if (p.Words >= SprintTarget)
            {
                CloseSprint();
                return ActResult.Accept($"Є! {SprintTarget} з {SprintTarget} — ти перший!");
            }
            SprintNext(p);
            return ActResult.Accept($"Є! {p.Words} з {SprintTarget} — ось наступне");
        }
        if (p.Guesses.Count >= MaxTries)
        {
            var was = p.Answer;
            p.Played.Add((was, false));
            SprintNext(p);
            return ActResult.Accept($"Не далось: це було «{was.ToUpperInvariant()}». Ось наступне");
        }
        return ActResult.Done;
    }

    /// <summary>
    /// Кінець спринту: хтось дотяг до <see cref="SprintTarget"/> або вийшов час. Перемагає більше слів, за
    /// рівних — той, хто вгадав своє останнє раніше; нуль слів у всіх — нічия.
    /// </summary>
    void CloseSprint()
    {
        _phase = PhaseDone;
        var seats = Enumerable.Range(0, MaxSeats).Where(s => _p[s].Active && Ctx.Seated(s)).ToList();
        var best = seats.Count == 0 ? 0 : seats.Max(s => _p[s].Words);
        var fastest = best == 0 ? 0 : seats.Where(s => _p[s].Words == best).Min(s => _p[s].LastSolveMs);
        int[] winners = best > 0 ? [.. seats.Where(s => _p[s].Words == best && _p[s].LastSolveMs == fastest)] : [];
        _winners = winners;
        var table = string.Join(", ", seats.OrderByDescending(s => _p[s].Words).ThenBy(s => _p[s].LastSolveMs)
            .Select(s => $"{Ctx.NickOf(s)} — {_p[s].Words}"));
        var tail = winners.Length == 0 ? "Нічия — слова перемогли всіх" : "Найшвидший — 🏆 " + string.Join(" і ", winners.Select(Ctx.NickOf));
        Ctx.Finish(winners, $"{Info.Title}, спринт: {table}. {tail}", seats.ToDictionary(s => s, s => (long)_p[s].Words));
    }

    public override TickResult Tick()
    {
        var now = Ctx.Clock.UtcNow;
        if (_sprint && _phase == PhasePlay && now >= _endsAt)
        {
            CloseSprint();
            _dirty = false;
            return new TickResult(false, true);
        }
        if (_phase == PhasePlay && now >= _endsAt)
        {
            EndRound(now);
            _dirty = false;
            return new TickResult(false, true);
        }
        if (_phase == PhaseReveal && now >= _endsAt)
        {
            NewRound(now);
            _dirty = false;
            return new TickResult(false, true);
        }
        if (!_dirty) return TickResult.None;
        _dirty = false;
        return new TickResult(false, true);
    }

    /// <summary>Раунд скінчився: хто не встиг — без очок; останній раунд одразу закриває партію.</summary>
    void EndRound(DateTimeOffset now)
    {
        foreach (var p in _p.Where(x => x.Active && !x.Done)) p.Failed = true;
        foreach (var p in _p.Where(x => x.In)) p.Total += p.Gained;
        if (Round >= _rounds)
        {
            Close();
            return;
        }
        _phase = PhaseReveal;
        _endsAt = now.AddSeconds(RevealSeconds);
    }

    void Close()
    {
        _phase = PhaseDone;
        var seats = Enumerable.Range(0, MaxSeats).Where(s => _p[s].Active && Ctx.Seated(s)).ToList();
        var best = seats.Count == 0 ? 0 : seats.Max(s => _p[s].Total);
        int[] winners = best > 0 ? [.. seats.Where(s => _p[s].Total == best)] : [];
        var table = string.Join(", ", seats.OrderByDescending(s => _p[s].Total).Select(s => $"{Ctx.NickOf(s)} — {_p[s].Total}"));
        var tail = winners.Length == 0 ? "Нічия — слова перемогли всіх" : "Найкраще в цій партії — 🏆 " + string.Join(" і ", winners.Select(Ctx.NickOf));
        Ctx.Finish(winners, $"{Info.Title}: {table}. {tail}", seats.ToDictionary(s => s, s => (long)_p[s].Total));
    }

    /// <summary>
    /// Хтось встав. Решта грає далі: його дошка лишається на столі сірою, а раунд більше на нього не чекає.
    /// Лишився один — партію закінчено, і перемога його (не дограли — не штраф тому, хто лишився).
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (seat is < 0 or >= MaxSeats) return;
        _p[seat].Gone = true;
        _dirty = true;
        if (_phase == PhaseDone) return;
        var left = Enumerable.Range(0, MaxSeats).Where(s => s != seat && _p[s].Active && Ctx.Seated(s)).ToList();
        if (left.Count < 2)
        {
            // очки недограного раунду — теж очки: хто вже вгадав, той вгадав
            if (_phase == PhasePlay && !_sprint) foreach (var p in _p.Where(x => x.In)) p.Total += p.Gained;
            _phase = PhaseDone;
            Ctx.Finish([.. left], $"{Info.Title}: {Ctx.NickOf(seat)} встає з-за столу, партію не дограли",
                left.ToDictionary(s => s, s => (long)_p[s].Total));
            return;
        }
        if (_phase == PhasePlay && !_sprint && _p.Where(x => x.Active).All(x => x.Done)) EndRound(Ctx.Clock.UtcNow);
    }

    // ------------------------------------------------------------------------------------- вид

    public override object View(int? seat)
    {
        var open = _phase is PhaseReveal or PhaseDone;
        var mine = seat is { } ms && ms >= 0 && ms < MaxSeats && _p[ms].In ? _p[ms] : null;
        // Хто вже вгадав чи вичерпав спроби, бачить чужі дошки з літерами: собі він уже нічим не допоможе, а
        // дивитись, як сусід утретє пише ГРОЗА по-різному, — половина веселощів. У спринті слова в кожного свої
        // й ніхто «не закінчив» до кінця партії — там чужі літери лише після неї.
        var peek = open || (!_sprint && mine is { Done: true });
        return new
        {
            phase = _phase,
            mode = _sprint ? ModeSprint : ModeSame,
            len = _len,
            target = _sprint ? SprintTarget : 0,
            hint = _hint,
            maxHints = _hint ? MaxHints : 0,
            winners = _sprint && _phase == PhaseDone ? _winners : null,
            round = Round,
            rounds = _rounds,
            seconds = _seconds,
            revealSeconds = RevealSeconds,
            endsAt = _phase is PhasePlay or PhaseReveal ? _endsAt : (DateTimeOffset?)null,
            max = MaxTries,
            turn = (int?)null,
            // слово раунду — лише коли раунд позаду; до того його нема навіть у консолі
            answer = open ? _answer : null,
            answers = open ? _answers.ToArray() : _answers.Take(Math.Max(0, _answers.Count - 1)).ToArray(),
            me = mine is null ? null : new
            {
                rows = mine.Guesses.Select(w => new { word = w, marks = Wordle.Marks(AnswerFor(mine), w) }).ToArray(),
                keys = Wordle.KeysOf(AnswerFor(mine), mine.Guesses),
                solved = mine.Solved,
                failed = mine.Failed,
                attempts = mine.Guesses.Count,
                hints = mine.Hints.Select(i => new { i, ch = AnswerFor(mine)[i].ToString() }).ToArray(),
                played = _sprint ? mine.Played.Select(x => new { w = x.Word, ok = x.Ok }).ToArray() : null,
            },
            players = Enumerable.Range(0, MaxSeats).Where(s => _p[s].In).Select(s =>
            {
                var p = _p[s];
                return new
                {
                    seat = s,
                    nick = Ctx.NickOf(s) ?? p.Nick,
                    marks = p.Guesses.Select(w => Wordle.Marks(AnswerFor(p), w)).ToArray(),
                    // чужі літери до кінця раунду — лише тим, хто своє вже відгадав (чи відмучився); глядачам — ні
                    words = peek || s == seat ? p.Guesses.ToArray() : null,
                    hints = p.Hints.Count,
                    played = _sprint && _phase == PhaseDone ? p.Played.Select(x => new { w = x.Word, ok = x.Ok }).ToArray() : null,
                    // спринт позаду — яке слово лишилось недогаданим
                    left = _sprint && _phase == PhaseDone && p.Guesses.Count > 0 && p.Guesses[^1] != p.Answer ? p.Answer : null,
                    attempts = p.Guesses.Count,
                    solved = p.Solved,
                    failed = p.Failed,
                    ms = p.Ms,
                    first = p.First,
                    gained = p.Gained,
                    total = p.Total,
                    solvedWords = p.Words,
                    gone = p.Gone,
                };
            }).ToArray(),
        };
    }


    string AnswerFor(Player p) => _sprint ? p.Answer : _answer;

    static string? Word(JsonElement payload) => payload.ValueKind switch
    {
        JsonValueKind.String => payload.GetString(),
        JsonValueKind.Object when payload.TryGetProperty("word", out var w) && w.ValueKind == JsonValueKind.String => w.GetString(),
        _ => null,
    };
}
