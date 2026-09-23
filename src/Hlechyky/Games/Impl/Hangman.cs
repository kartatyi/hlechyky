using System.Text.Json;
using Hlechyky.Games.Economy;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Віселиця на компанію (specs/hangman.md). Слово одне на всіх, черги нема: хто перший назвав літеру,
/// той і забрав очки за неї, а шибениця — спільна, тож промах одного наближає програш усіх. Партія —
/// п'ять слів; між ними пауза, щоб устигнути прочитати, що ж там було.
///
/// Чому тик, а не чиста покроковість: пауза між словами має минати сама, без того, щоб хтось на неї
/// натиснув. Тик заразом розсилає види — каркас після Act реалтайм-кімнати їх не шле (Rooms.Act,
/// гілка counts), тож усе, що змінилось, ми позначаємо прапорцем і віддаємо найближчим тиком.
/// </summary>
public sealed class Hangman : Game
{
    /// <summary>Скільки слів у партії типово (опція <c>words</c>: 3, 5 або 7).</summary>
    public const int Rounds = 5;
    public static readonly int[] RoundChoices = [3, 5, 7];
    /// <summary>Стільки промахів — і шибениця готова, слово програне всіма разом (типова складність).</summary>
    public const int MaxErrors = 8;
    /// <summary>Промахів на слово за складністю: легко — 10 (і перша з останньою літерою вже відкриті), важко — 6.</summary>
    public const int EasyErrors = 10, HardErrors = 6;
    /// <summary>По черзі: стільки часу на хід, потім черга переходить сама.</summary>
    public const int TurnMs = 25_000;

    /// <summary>Режими: усі разом наввипередки (як було) або по черзі, як «Поле чудес».</summary>
    public const string Race = "race", Turns = "turns";
    /// <summary>
    /// Крок тика. У spec стояла секунда, але тоді чужу літеру видно було б аж через секунду після
    /// натиску (види після Act каркас реалтайм-кімнатам не шле) — на чверть секунди це вже не помітно.
    /// </summary>
    public const int TickMs = 250;
    /// <summary>Пауза між словами: встигнути побачити відгадане слово й приготуватись до наступного.</summary>
    public const int PauseMs = 4000;
    const int PauseTicks = PauseMs / TickMs;
    /// <summary>Одна дія на місце на секунду: інакше швидкі пальці перебирають абетку за півхвилини.</summary>
    public const int ActEveryMs = 1000;
    /// <summary>Очки за вгадане ціле слово — понад ті, що дають нерозкриті літери.</summary>
    public const int WordBonus = 3;

    const int MinLen = 5, MaxLen = 12;
    const int Seats = 6;

    /// <summary>Українська абетка: і, ї, й, ґ, є — окремі літери, як і в самих словах.</summary>
    const string Alphabet = "абвгґдеєжзиіїйклмнопрстуфхцчшщьюя";

    /// <summary>Фази раунду так, як їх бачить клієнт (spec «Вид»).</summary>
    const string Play = "play", Between = "between", Done = "done";

    public override GameInfo Info { get; } = new(
        "hangman", "Віселиця", "віселицю", GameGroup.Party, 1, Seats,
        TickMs: TickMs, Start: StartMode.ByHost, Score: ScoreOrder.HigherIsBetter,
        Options:
        [
            new GameOption("mode", "Як гадаємо", [(Race, "Усі разом, наввипередки"), (Turns, "По черзі: влучив — ходиш ще")], Race),
            new GameOption("level", "Складність",
                [("easy", "Легко: 10 промахів, крайні літери відкриті"), ("normal", "Звичайно: 8 промахів"), ("hard", "Важко: 6 промахів")], "normal"),
            new GameOption("words", "Слів у партії", [.. RoundChoices.Select(n => (n.ToString(), n == 3 ? "3 слова" : $"{n} слів"))], Rounds.ToString()),
        ],
        Hint: "Слово сховане рисками. Називаєш літери, за помилки домальовується шибениця. "
            + "Хто відгадав більше — той і виграв. Можна наввипередки або по черзі");

    Words? _words;
    string _mode = Race;
    bool _easy;
    int _maxErrors = MaxErrors;
    int _rounds = Rounds;
    /// <summary>По черзі: чий зараз хід (-1 — нічий) і до якої миті.</summary>
    int _turn = -1;
    DateTimeOffset _turnUntil;
    /// <summary>Остання подія слова — хто що назвав і чим це скінчилось. Клієнт малює рядок і анімацію.</summary>
    object? _lastEvent;
    int _eventNo;
    string _word = "";
    /// <summary>Літери, які вже відкриті у слові.</summary>
    readonly HashSet<char> _open = [];
    /// <summary>Названі влучно і названі повз — у порядку називання, щоб клієнт малював те саме.</summary>
    readonly List<char> _right = [];
    readonly List<char> _wrong = [];
    int _errors;
    int _round;
    string _phase = Play;
    int _pause;
    /// <summary>Скільки секунд паузи вже показано: поки число не змінилось, розсилати нема чого.</summary>
    int _shownIn;
    string? _revealed;
    readonly int[] _scores = new int[Seats];
    /// <summary>Хто вибув із цього слова (не вгадав його цілком) або встав з-за столу.</summary>
    readonly HashSet<int> _out = [];
    readonly Dictionary<int, DateTimeOffset> _last = [];
    object? _result;
    /// <summary>Чи змінилось щось видиме з минулого тика.</summary>
    bool _dirty;

    /// <summary>
    /// Сервіси беремо тут: гру створює реєстр конструктором без параметрів (INTEGRATION-NOTES §1).
    /// Порожній словник — це не «крива гра», а причина не ставити стіл узагалі: GameError із Configure
    /// каркас покаже гравцеві текстом, і кімнати просто не буде.
    /// </summary>
    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _words = Ctx.Services.GetService<Words>();
        if (_words is null || _words.Stats.Hangman == 0) throw new GameError("Нема словника, віселиця відпочиває");
        if (options.TryGetValue("mode", out var m) && m == Turns) _mode = Turns;
        if (options.TryGetValue("level", out var l))
        {
            _easy = l == "easy";
            _maxErrors = l switch { "easy" => EasyErrors, "hard" => HardErrors, _ => MaxErrors };
        }
        if (options.TryGetValue("words", out var w) && int.TryParse(w, out var wn) && RoundChoices.Contains(wn)) _rounds = wn;
    }

    public override void Start()
    {
        Array.Clear(_scores);
        _last.Clear();
        _round = 0;
        _result = null;
        NewWord();
        // Словник міг зникнути між створенням столу і стартом (файл прибрали, база впала) — краще
        // чесно закрити партію, ніж показати п'ять порожніх рисок.
        if (_word.Length == 0) Ctx.Finish([], $"{Info.Title}: словник кудись подівся, партії не буде");
    }

    // ---------------------------------------------------------------------------------------
    // Ходи
    // ---------------------------------------------------------------------------------------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action is not ("guess" or "word")) return ActResult.Fail("Тут так не ходять");
        if (_phase == Done) return ActResult.Fail("Партію зіграно, тисни «Ще раз»");
        if (_phase == Between) return ActResult.Fail("Пауза. Зараз буде нове слово");
        if (_out.Contains(seat)) return ActResult.Fail("Це слово вже без тебе, чекай наступне");
        // По черзі ліміт швидкості не потрібен: чужий хід і так не пройде, а свій — хай хоч блискавкою.
        if (_mode == Turns) { if (seat != _turn) return ActResult.Fail("Зараз не твій хід"); }
        else if (!Ready(seat)) return ActResult.Fail("Не так швидко");
        return action == "guess"
            ? Guess(seat, Read(payload, "letter"))
            : Word(seat, Read(payload, "text"));
    }

    /// <summary>Ліміт швидкості. Позначку ставимо на вході, а не на вдалому ході: інакше спам відмовами не лічився б.</summary>
    bool Ready(int seat)
    {
        var now = Ctx.Clock.UtcNow;
        if (_last.TryGetValue(seat, out var last) && (now - last).TotalMilliseconds < ActEveryMs) return false;
        _last[seat] = now;
        return true;
    }

    /// <summary>Приймаємо і <c>{letter:"а"}</c>, і голий рядок — клієнтам так простіше.</summary>
    static string Read(JsonElement payload, string field) => payload.ValueKind switch
    {
        JsonValueKind.String => payload.GetString() ?? "",
        JsonValueKind.Object when payload.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String => v.GetString() ?? "",
        _ => "",
    };

    ActResult Guess(int seat, string raw)
    {
        if (Letter(raw) is not { } ch) return ActResult.Fail("Це не українська літера");
        if (_right.Contains(ch) || _wrong.Contains(ch)) return ActResult.Fail("Уже було");

        _dirty = true;
        var hits = _word.Count(c => c == ch);
        Event(seat, hits == 0 ? "miss" : "hit", ch.ToString(), hits);
        if (hits == 0)
        {
            _wrong.Add(ch);
            _errors++;
            if (_errors < _maxErrors) { PassTurn(); return ActResult.Done; }
            EndRound();
            // Мовчки: будь-який текст на вдалому ході каркас малює зеленим тостом «усе гаразд»
            // (core.js, call()), а дорисована шибениця з відкритим словом — новина не з тих.
            return ActResult.Done;
        }

        _right.Add(ch);
        _open.Add(ch);
        Add(seat, hits);
        // Влучив — ходиш ще, і годинник ходу заводиться наново.
        if (!Opened()) { if (_mode == Turns) _turnUntil = Ctx.Clock.UtcNow.AddMilliseconds(TurnMs); return ActResult.Done; }
        EndRound();
        return ActResult.Accept("Слово відкрите!");
    }

    ActResult Word(int seat, string raw)
    {
        var word = Words.Normalize(raw);
        if (word is null || word.Length < 2) return ActResult.Fail("Це не схоже на слово");

        _dirty = true;
        if (word != _word)
        {
            // Не вгадав — цим словом уже не грає; мінус очко, але не в борг.
            _out.Add(seat);
            Add(seat, -1);
            Event(seat, "wrong", word, 0);
            if (!Alive()) EndRound();
            else PassTurn();
            // Теж мовчки, і з тієї самої причини: зелений тост на «не вгадав» збивав би з пантелику.
            // Гравець і так бачить: клавіатура зникла, чіп рахунку потьмянів, статус це пояснює.
            return ActResult.Done;
        }

        // Вгадав ціле: три очки зверху і всі літери, яких на полі ще не було.
        var hidden = _word.Count(c => !_open.Contains(c));
        foreach (var c in _word) _open.Add(c);
        Add(seat, WordBonus + hidden);
        Event(seat, "word", _word, WordBonus + hidden);
        EndRound();
        return ActResult.Accept("Ціле слово! Твоя взяла");
    }

    /// <summary>Одна українська літера або нічого.</summary>
    static char? Letter(string raw)
    {
        var s = (raw ?? "").Trim().ToLowerInvariant();
        return s.Length == 1 && Alphabet.Contains(s[0]) ? s[0] : null;
    }

    /// <summary>Очки не падають нижче нуля: грати в мінус — то вже якесь інше почуття гумору.</summary>
    void Add(int seat, int points)
    {
        if (seat < 0 || seat >= Seats) return;
        _scores[seat] = Math.Max(0, _scores[seat] + points);
    }

    bool Opened() => _word.Length > 0 && _word.All(_open.Contains);

    /// <summary>Хто ще може називати літери в цьому слові.</summary>
    IEnumerable<int> Active() => Enumerable.Range(0, Seats).Where(s => Ctx.Seated(s) && !_out.Contains(s));

    bool Alive() => Active().Any();

    /// <summary>
    /// По черзі: хід переходить до наступного, хто ще грає це слово (по колу місць). Нікого — нічого:
    /// раунд закриє тик. У режимі наввипередки черги нема, і тут нема чого робити.
    /// </summary>
    void PassTurn()
    {
        if (_mode != Turns) return;
        var active = Active().ToArray();
        if (active.Length == 0) { _turn = -1; return; }
        var next = active.FirstOrDefault(s => s > _turn, active[0]);
        GiveTurn(next);
    }

    void GiveTurn(int seat)
    {
        _turn = seat;
        _turnUntil = Ctx.Clock.UtcNow.AddMilliseconds(TurnMs);
        _dirty = true;
    }

    void Event(int seat, string kind, string text, int n) =>
        _lastEvent = new { no = ++_eventNo, seat, kind, text, n };

    // ---------------------------------------------------------------------------------------
    // Хід часу
    // ---------------------------------------------------------------------------------------

    public override TickResult Tick()
    {
        if (_phase == Between)
        {
            _pause--;
            var left = SecondsLeft();
            if (left != _shownIn) { _shownIn = left; _dirty = true; }
            if (_pause <= 0) { if (_round >= _rounds) Over(); else NewWord(); }
        }
        else if (_phase == Play && !Alive())
        {
            // усі, хто міг гадати, вибули або встали з-за столу — слово так і лишиться нерозгаданим
            EndRound();
        }
        else if (_phase == Play && _mode == Turns && (!Active().Contains(_turn) || Ctx.Clock.UtcNow >= _turnUntil))
        {
            // задумався надовго або встав посеред свого ходу — черга йде далі
            if (Active().Contains(_turn)) Event(_turn, "timeout", "", 0);
            PassTurn();
        }
        return Flush();
    }

    /// <summary>
    /// Види шлемо лише тоді, коли на екрані справді щось змінилось. Кадри тут ні до чого: прихованого
    /// у виді нема, а на чотири тики за секунду повний вид дешевший за окрему форму кадра.
    /// </summary>
    TickResult Flush()
    {
        if (!_dirty) return TickResult.None;
        _dirty = false;
        return new TickResult(Frame: false, View: true);
    }

    int SecondsLeft() => (int)Math.Ceiling(Math.Max(0, _pause) * TickMs / 1000.0);

    /// <summary>Слово скінчилось — відкриваємо його всім і беремо паузу перед наступним.</summary>
    void EndRound()
    {
        _revealed = _word;
        _phase = Between;
        _turn = -1;
        _pause = PauseTicks;
        _shownIn = SecondsLeft();
        _dirty = true;
    }

    /// <summary>Наступне слово: чистий раунд, але очки партії лишаються.</summary>
    void NewWord()
    {
        _round++;
        _open.Clear();
        _right.Clear();
        _wrong.Clear();
        _out.Clear();
        _errors = 0;
        _revealed = null;
        _phase = Play;
        _pause = 0;
        _shownIn = 0;
        _word = _words?.RandomHangman(Ctx.Rng, MinLen, MaxLen) ?? "";
        _lastEvent = null;
        if (_easy && _word.Length > 0)
        {
            // Легко: перша й остання літери відкриті одразу (і всі їхні входження) — як у шкільній віселиці.
            foreach (var c in new[] { _word[0], _word[^1] })
                if (_open.Add(c)) _right.Add(c);
        }
        _dirty = true;
        if (_mode == Turns)
        {
            // Слово за словом першим ходить наступний: інакше перший за столом завжди мав би фору.
            var seats = Active().ToArray();
            if (seats.Length > 0) GiveTurn(seats[(_round - 1) % seats.Length]);
        }
    }

    /// <summary>П'ять слів позаду: рахуємо очки, роздаємо результати в таблицю й закриваємо партію.</summary>
    void Over()
    {
        _phase = Done;
        _revealed = _word;
        _dirty = true;

        var seats = Enumerable.Range(0, Seats).Where(Ctx.Seated).ToArray();
        var best = seats.Length == 0 ? 0 : seats.Max(s => _scores[s]);
        // Нуль у всіх — це не перемога, а спільна поразка від словника: нічия.
        int[] winners = best > 0 ? [.. seats.Where(s => _scores[s] == best)] : [];
        foreach (var s in seats) Ctx.Score(s, _scores[s]);
        _result = new { winners, scores = Line() };
        Ctx.Finish(winners, Told(seats, winners), seats.ToDictionary(s => s, s => (long)_scores[s]));
    }

    /// <summary>Рядок Журналу: рахунок усіх і хто попереду.</summary>
    string Told(int[] seats, int[] winners)
    {
        var parts = seats.OrderByDescending(s => _scores[s]).Select(s => $"{Ctx.NickOf(s)} {_scores[s]}");
        // Ніки чужі, відмінювати їх нема як, тому «попереду», а не «виграв/виграла».
        var tail = winners.Length == 0 ? "жодного слова так і не взяли"
            : "попереду " + string.Join(" і ", winners.Select(Ctx.NickOf));
        return $"{Info.Title}: {string.Join(", ", parts)} — {tail}";
    }

    /// <summary>Компанійна гра: хтось встав — решта дограє, партію це не валить.</summary>
    public override void OnLeave(int seat)
    {
        _out.Add(seat);
        _dirty = true;
        if (_phase == Play && seat == _turn) PassTurn();
    }

    // ---------------------------------------------------------------------------------------
    // Вид
    // ---------------------------------------------------------------------------------------

    /// <summary>«к_р__а»: відкриті літери на своїх місцях, решта — риски. Самого слова тут нема до кінця раунду.</summary>
    string Mask() => string.Concat(_word.Select(c => _open.Contains(c) ? c : '_'));

    int[] Line() => (int[])_scores.Clone();

    public override object View(int? seat) => new
    {
        round = _round,
        of = _rounds,
        mode = _mode,
        easy = _easy,
        mask = Mask(),
        wrong = _wrong.Select(c => c.ToString()).ToArray(),
        right = _right.Select(c => c.ToString()).ToArray(),
        errors = _errors,
        maxErrors = _maxErrors,
        scores = Line(),
        @out = _out.Order().ToArray(),
        phase = _phase,
        nextIn = SecondsLeft(),
        // слово показуємо лише тоді, коли раунд уже нічим не зіпсуєш
        revealed = _phase == Play ? null : _revealed,
        result = _result,
        // Наввипередки черги нема: гадають усі одразу, тож «Твій хід» каркас не малює. По черзі — є.
        turn = _mode == Turns && _phase == Play && _turn >= 0 ? _turn : (int?)null,
        turnUntil = _mode == Turns && _phase == Play && _turn >= 0 ? _turnUntil : (DateTimeOffset?)null,
        turnMs = TurnMs,
        last = _lastEvent,
    };
}
