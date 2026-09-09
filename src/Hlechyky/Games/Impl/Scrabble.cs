using System.Text.Json;
using Hlechyky.Games.Economy;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Ерудит: дошка 15×15, стійка на сім фішок, слова з українських літер. Правила й увесь стан живуть
/// тут, на сервері; браузер лише малює дошку й каже, куди гравець тицьнув.
///
/// Дві речі, яких у класичному «Скребл» нема, а в нас мусять бути:
/// 1) великого словника на цій машині може не бути (<c>Words.FullLoaded == false</c>) — тоді гра йде в
///    режимі «малий словник»: приймається будь-яке слово, зате суперник може його оскаржити («Не слово»);
/// 2) гравець може встати з-за столу посеред партії — його фішки повертаються в мішок, а решта грає далі.
/// </summary>
public sealed class Scrabble : Game
{
    /// <summary>Скільки пасів/обмінів поспіль (усіма разом) закривають партію.</summary>
    public const int PassesToEnd = 6;
    /// <summary>Слово, від якого починається ачівка «Ерудит».</summary>
    public const int AchievementScore = 30;
    /// <summary>Скільки останніх ходів тримаємо у виді для журналу партії.</summary>
    const int JournalDepth = 20;

    public override GameInfo Info { get; } = new(
        "scrabble", "Ерудит", "ерудит", GameGroup.Board, 2, 4,
        Start: StartMode.ByHost, Hidden: true,
        Hint: "Складай слова з літер на дошці 15×15. Рідкісні літери дорожчі, кольорові клітинки множать очки");

    /// <summary>Рядок журналу ходів: хто і що зробив.</summary>
    sealed record Move(int Seat, string Text);

    /// <summary>Останній викладений хід — для підсвітки на дошці.</summary>
    sealed record LastPlay(int Seat, IReadOnlyList<ScrabbleWord> Words, int Total, int[] Cells);

    /// <summary>
    /// Знімок стану перед останнім ходом. Потрібен рівно для оскарження в режимі малого словника:
    /// повертати фішки поштучно не вийде, бо гравець уже добрав нові з мішка.
    /// </summary>
    sealed record Undo(int Seat, int[] Cells, string Rack, string Bag, int Score, int Passes, string[] Added);

    /// <summary>Підсумок партії у виді.</summary>
    sealed record Outcome(int? Winner, int[] Scores, string Reason);

    Words? _words;
    ScrabbleBoard _board = new();
    List<char> _bag = [];
    List<char>[] _racks = [];
    int[] _scores = [];
    /// <summary>Місця, які зараз грають: хто встав з-за столу, той із черги випадає.</summary>
    bool[] _active = [];
    readonly List<Move> _moves = [];
    /// <summary>Слова, які вже стояли на дошці: у режимі малого словника вони лишаються законними.</summary>
    readonly HashSet<string> _placed = new(StringComparer.Ordinal);
    int _turn;
    int _passes;
    bool _small;
    LastPlay? _last;
    Undo? _undo;
    Outcome? _result;

    public override string SeatName(int seat) => seat switch
    {
        0 => "перший",
        1 => "другий",
        2 => "третій",
        _ => "четвертий",
    };

    /// <summary>Словник беремо тут: ігри створюються без параметрів, тому залежності — із сервісів кімнати.</summary>
    public override void Configure(IReadOnlyDictionary<string, string> options) => _words = Ctx.Services.GetService<Words>();

    public override void Start()
    {
        _words ??= Ctx.Services.GetService<Words>();
        // FullLoaded може перемкнутись із false на true вже після старту сервера (словник збирається у
        // фоні). Режим фіксуємо на початку партії, щоб посеред неї не мінялись правила прийому слів.
        _small = _words is null || !_words.FullLoaded;

        _board = new ScrabbleBoard();
        _bag = ScrabbleBag.Fresh(Ctx.Rng);
        _racks = [.. Enumerable.Range(0, Info.MaxPlayers).Select(_ => new List<char>())];
        _scores = new int[Info.MaxPlayers];
        _active = [.. Enumerable.Range(0, Info.MaxPlayers).Select(Ctx.Seated)];
        _moves.Clear();
        _placed.Clear();
        _passes = 0;
        _last = null;
        _undo = null;
        _result = null;

        for (var seat = 0; seat < _active.Length; seat++)
            if (_active[seat]) Refill(seat);
        _turn = Math.Max(0, Array.FindIndex(_active, a => a));
    }

    // ---------------------------------------------------------------------------------- ходи

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (_result is not null) return ActResult.Fail("Партію зіграно, тисни «Ще раз»");
        if (seat < 0 || seat >= _active.Length || !_active[seat]) return ActResult.Fail("Ти тут не граєш");
        if (seat != _turn) return ActResult.Fail("Зараз не твій хід");
        return action switch
        {
            "play" => Play(seat, payload),
            "pass" => Pass(seat),
            "swap" => Swap(seat, payload),
            "challenge" => Challenge(seat),
            _ => ActResult.Fail("Тут так не ходять"),
        };
    }

    ActResult Play(int seat, JsonElement payload)
    {
        var (tiles, bad) = ReadTiles(payload);
        if (tiles is null) return ActResult.Fail(bad ?? "Не зрозумів, що ти кладеш");

        var rack = _racks[seat];
        var need = tiles.Select(t => t.Blank ? ScrabbleBag.Blank : t.Letter).ToList();
        if (!Takeable(rack, need)) return ActResult.Fail("Таких фішок у тебе на стійці нема");

        var (play, error) = _board.Check(tiles);
        if (error is not null) return ActResult.Fail(error);

        // Великий словник — суддя: невідоме слово просто не лягає. Малий словник надто вузький, щоб
        // ним судити (у ньому нема навіть половини словоформ), тому там приймаємо все, а правду
        // встановлює суперник кнопкою «Не слово».
        if (!_small)
            foreach (var word in play!.Words)
                if (!Known(word.Text)) return ActResult.Fail($"Такого слова нема: {word.Text}");

        var added = play!.Words.Select(w => w.Text).Where(w => !_placed.Contains(w)).Distinct(StringComparer.Ordinal).ToArray();
        _undo = new Undo(seat, play.Cells, new string([.. rack]), new string([.. _bag]), _scores[seat], _passes, added);

        foreach (var c in need) rack.Remove(c);
        _board.Apply(tiles);
        _scores[seat] += play.Total;
        foreach (var w in added) _placed.Add(w);
        Refill(seat);
        _last = new LastPlay(seat, play.Words, play.Total, play.Cells);
        _passes = 0;
        Note(seat, $"{string.Join(", ", play.Words.Select(w => w.Text))} +{play.Total}");

        var best = play.Words.MaxBy(w => w.Score)!;
        if (best.Score >= AchievementScore)
        {
            // Платформа очок за слово не бачить — ачівку гра просить сама (нуль черепків: їх платить каталог).
            Ctx.Award(seat, 0, "ach:scrabble-30");
            Ctx.Log($"{Info.Title}: {Ctx.NickOf(seat)} виклав «{best.Text}» на {best.Score} очок");
        }

        var message = tiles.Count == ScrabbleBoard.RackSize ? $"Бінго! +{play.Total} очок" : $"+{play.Total} очок";
        if (_bag.Count == 0 && rack.Count == 0)
        {
            FinishOut(seat);
            return ActResult.Accept(message);
        }
        _turn = Next(seat);
        return ActResult.Accept(message);
    }

    ActResult Pass(int seat)
    {
        _passes++;
        _undo = null;
        Note(seat, "пас");
        if (_passes >= PassesToEnd)
        {
            FinishPasses();
            return ActResult.Accept("Пас");
        }
        _turn = Next(seat);
        return ActResult.Accept("Пас");
    }

    ActResult Swap(int seat, JsonElement payload)
    {
        if (_bag.Count < ScrabbleBoard.RackSize) return ActResult.Fail("У мішку замало фішок для обміну");
        var (letters, bad) = ReadLetters(payload);
        if (letters is null) return ActResult.Fail(bad ?? "Не зрозумів, що міняти");

        var rack = _racks[seat];
        if (!Takeable(rack, letters)) return ActResult.Fail("Таких фішок у тебе на стійці нема");

        foreach (var c in letters) rack.Remove(c);
        // Спершу добираємо, і лише потім кидаємо здані назад: інакше можна витягти те саме, що щойно віддав.
        Refill(seat);
        _bag.AddRange(letters);
        ScrabbleBag.Shuffle(_bag, Ctx.Rng);

        _passes++;
        _undo = null;
        Note(seat, $"обмін ({letters.Count})");
        if (_passes >= PassesToEnd)
        {
            FinishPasses();
            return ActResult.Accept("Обмін");
        }
        _turn = Next(seat);
        return ActResult.Accept("Обмін");
    }

    ActResult Challenge(int seat)
    {
        if (!_small) return ActResult.Fail("Тут повний словник — оскаржувати нема потреби");
        if (_words is null) return ActResult.Fail("Словника нема, оскаржувати нічим");
        if (_undo is not { } undo || undo.Seat == seat || _last is null) return ActResult.Fail("Нема чого оскаржувати");

        var bad = _last.Words.FirstOrDefault(w => !_words.IsWord(w.Text));
        if (bad is null) return ActResult.Fail("Таке слово в словнику є");

        // Знімаємо хід цілком: фішки з дошки, стійку, мішок і очки повертаємо в те, що було до нього.
        _board.Clear(undo.Cells);
        _racks[undo.Seat] = [.. undo.Rack];
        _bag = [.. undo.Bag];
        _scores[undo.Seat] = undo.Score;
        _passes = undo.Passes;
        foreach (var w in undo.Added) _placed.Remove(w);
        _last = null;
        _undo = null;
        Note(seat, $"оскаржив: «{bad.Text}» — не слово");
        // Хід лишається за тим, хто оскаржив: штрафу за оскарження в нас нема.
        return ActResult.Accept($"«{bad.Text}» знято з дошки");
    }

    // ---------------------------------------------------------------------------------- кінець

    /// <summary>Хтось виклав усі фішки при порожньому мішку: його стійки чужі, свої — мінусом.</summary>
    void FinishOut(int seat)
    {
        var final = (int[])_scores.Clone();
        var gain = 0;
        for (var s = 0; s < _active.Length; s++)
        {
            if (!_active[s] || s == seat) continue;
            var value = RackValue(s);
            final[s] -= value;
            gain += value;
        }
        final[seat] += gain;
        Close(final, "out");
    }

    /// <summary>Шість пасів поспіль: усім мінус власна стійка.</summary>
    void FinishPasses()
    {
        var final = (int[])_scores.Clone();
        for (var s = 0; s < _active.Length; s++)
            if (_active[s]) final[s] -= RackValue(s);
        Close(final, "passes");
    }

    void Close(int[] final, string reason)
    {
        _scores = final;
        var playing = Enumerable.Range(0, _active.Length).Where(s => _active[s]).ToArray();
        var best = playing.Length == 0 ? 0 : playing.Max(s => final[s]);
        var top = playing.Where(s => final[s] == best).ToArray();
        // «Перемагає найбільше очок; нічия при рівності» — рівність нагорі це нічия, а не двоє переможців.
        int[] winners = top.Length == 1 ? top : [];
        int? winner = top.Length == 1 ? top[0] : null;
        _result = new Outcome(winner, (int[])final.Clone(), reason);
        Ctx.Finish(winners, Summary(playing, final, winner), playing.ToDictionary(s => s, s => (long)final[s]));
    }

    string Summary(int[] playing, int[] final, int? winner)
    {
        var parts = playing.Select(s => $"{Ctx.NickOf(s) ?? SeatName(s)} {final[s]}");
        var tail = winner is { } w ? $" — перемога: {Ctx.NickOf(w) ?? SeatName(w)}" : " — нічия";
        return $"{Info.Title}: {string.Join(", ", parts)}{tail}";
    }

    /// <summary>
    /// Хтось встав з-за столу. Його фішки йдуть у мішок (інакше решті не буде чим дограти), а партія
    /// триває, поки за столом лишається щонайменше двоє.
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (_result is not null) return;
        if (seat < 0 || seat >= _active.Length || !_active[seat]) return;

        _bag.AddRange(_racks[seat]);
        ScrabbleBag.Shuffle(_bag, Ctx.Rng);
        _racks[seat].Clear();
        _active[seat] = false;
        if (_undo?.Seat == seat) _undo = null;
        Note(seat, "встав з-за столу");

        var left = Enumerable.Range(0, _active.Length).Where(s => _active[s]).ToArray();
        if (left.Length >= 2)
        {
            if (_turn == seat) _turn = Next(seat);
            return;
        }
        _result = new Outcome(left.Length == 1 ? left[0] : null, (int[])_scores.Clone(), "left");
        var text = left.Length == 1
            ? $"{Info.Title}: {Ctx.NickOf(seat)} встав з-за столу, партія лишилась за {Ctx.NickOf(left[0])}"
            : $"{Info.Title}: за столом нікого не лишилось";
        Ctx.Finish(left, text, left.ToDictionary(s => s, s => (long)_scores[s]));
    }

    // ---------------------------------------------------------------------------------- вид

    public override object View(int? seat) => new
    {
        board = _board.Text,
        bonuses = ScrabbleBoard.Bonuses,
        turn = _result is null ? _turn : (int?)null,
        players = _active.Count(a => a),
        scores = (int[])_scores.Clone(),
        racks = _racks.Select(r => r.Count).ToArray(),
        // Стійка — приватна: чужу не бачить ніхто, глядач не бачить жодної.
        rack = seat is { } s && s >= 0 && s < _racks.Length && _active[s]
            ? _racks[s].Select(c => c.ToString()).ToArray()
            : null,
        bag = _bag.Count,
        last = _last is null ? null : new
        {
            seat = _last.Seat,
            words = _last.Words.Select(w => new { word = w.Text, score = w.Score }).ToArray(),
            total = _last.Total,
            cells = (int[])_last.Cells.Clone(),
        },
        passes = _passes,
        smallDict = _small,
        // Журнал партії: spec його не описує полем, але без нього клієнту нема чого малювати збоку.
        moves = _moves.Select(m => new { seat = m.Seat, text = m.Text }).ToArray(),
        result = _result is null ? null : new
        {
            winner = _result.Winner,
            scores = (int[])_result.Scores.Clone(),
            reason = _result.Reason,
        },
    };

    // ---------------------------------------------------------------------------------- збереження

    /// <summary>
    /// Стан партії в JSON. Ерудит не Persistent, тож у базу це не лягає — але тримати повний знімок
    /// стану дешево, і саме він дає тестам просту перевірку «Load(Save()) — та сама партія».
    /// Вікно оскарження (знімок перед останнім ходом) навмисно не зберігаємо.
    /// </summary>
    sealed record Saved(string Board, string Bag, string[] Racks, int[] Scores, bool[] Active,
        int Turn, int Passes, bool Small, string[] Placed, int[] MoveSeats, string[] MoveTexts,
        int? LastSeat, string[] LastWords, int[] LastScores, int LastTotal, int[] LastCells,
        int? Winner, int[] ResultScores, string? Reason);

    public override string? Save()
    {
        IReadOnlyList<ScrabbleWord> words = _last?.Words ?? [];
        return JsonSerializer.Serialize(new Saved(
            _board.Text, new string([.. _bag]), [.. _racks.Select(r => new string([.. r]))],
            (int[])_scores.Clone(), (bool[])_active.Clone(), _turn, _passes, _small, [.. _placed],
            [.. _moves.Select(m => m.Seat)], [.. _moves.Select(m => m.Text)],
            _last?.Seat, [.. words.Select(w => w.Text)], [.. words.Select(w => w.Score)],
            _last?.Total ?? 0, _last?.Cells ?? [],
            _result?.Winner, _result?.Scores ?? [], _result?.Reason));
    }

    public override void Load(string json)
    {
        var s = JsonSerializer.Deserialize<Saved>(json);
        if (s is null) return;
        _board = new ScrabbleBoard(s.Board);
        _bag = [.. s.Bag];
        _racks = [.. s.Racks.Select(r => new List<char>(r))];
        _scores = (int[])s.Scores.Clone();
        _active = (bool[])s.Active.Clone();
        _turn = s.Turn;
        _passes = s.Passes;
        _small = s.Small;
        _placed.Clear();
        foreach (var w in s.Placed) _placed.Add(w);
        _moves.Clear();
        for (var i = 0; i < s.MoveSeats.Length && i < s.MoveTexts.Length; i++) _moves.Add(new Move(s.MoveSeats[i], s.MoveTexts[i]));
        _last = s.LastSeat is { } seat
            ? new LastPlay(seat, [.. s.LastWords.Zip(s.LastScores, (w, sc) => new ScrabbleWord(w, sc))], s.LastTotal, s.LastCells)
            : null;
        _result = s.Reason is null ? null : new Outcome(s.Winner, s.ResultScores, s.Reason);
        _undo = null;
    }

    // ---------------------------------------------------------------------------------- дрібниці

    /// <summary>Слово законне: або словник його знає, або воно вже стоїть на дошці з чийогось ходу.</summary>
    bool Known(string word) => _words?.IsWord(word) == true || _placed.Contains(word);

    /// <summary>Наступне зайняте місце по колу; якщо гравець лишився сам — він же.</summary>
    int Next(int seat)
    {
        for (var i = 1; i <= _active.Length; i++)
        {
            var s = (seat + i) % _active.Length;
            if (_active[s]) return s;
        }
        return seat;
    }

    void Refill(int seat)
    {
        var rack = _racks[seat];
        while (rack.Count < ScrabbleBoard.RackSize && _bag.Count > 0)
        {
            rack.Add(_bag[^1]);
            _bag.RemoveAt(_bag.Count - 1);
        }
    }

    int RackValue(int seat) => _racks[seat].Sum(ScrabbleBag.Value);

    void Note(int seat, string text)
    {
        _moves.Add(new Move(seat, text));
        if (_moves.Count > JournalDepth) _moves.RemoveAt(0);
    }

    /// <summary>Чи є на стійці всі потрібні фішки (з урахуванням повторів).</summary>
    static bool Takeable(List<char> rack, IReadOnlyList<char> need)
    {
        var pool = new List<char>(rack);
        foreach (var c in need)
        {
            var i = pool.IndexOf(c);
            if (i < 0) return false;
            pool.RemoveAt(i);
        }
        return true;
    }

    /// <summary>Розбирає <c>{ tiles: [{ cell, letter, blank }] }</c>. Помилка — коротким текстом, без винятків.</summary>
    static (List<ScrabbleTile>? Tiles, string? Error) ReadTiles(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("tiles", out var list) || list.ValueKind != JsonValueKind.Array)
            return (null, "Не зрозумів, що ти кладеш");
        var tiles = new List<ScrabbleTile>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) return (null, "Не зрозумів, що ти кладеш");
            if (!item.TryGetProperty("cell", out var cellRaw) || cellRaw.ValueKind != JsonValueKind.Number || !cellRaw.TryGetInt32(out var cell))
                return (null, "Не зрозумів, куди ти кладеш");
            var blank = item.TryGetProperty("blank", out var b) && b.ValueKind == JsonValueKind.True;
            var letter = item.TryGetProperty("letter", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
            if (Words.Normalize(letter) is not { Length: 1 } one)
                return (null, blank ? "Скажи, яка це літера на порожній фішці" : "Це не українська літера");
            tiles.Add(new ScrabbleTile(cell, one[0], blank));
        }
        return (tiles, null);
    }

    /// <summary>Розбирає <c>{ letters: ["а", "*"] }</c> для обміну.</summary>
    static (List<char>? Letters, string? Error) ReadLetters(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("letters", out var list) || list.ValueKind != JsonValueKind.Array)
            return (null, "Не зрозумів, що міняти");
        var letters = new List<char>();
        foreach (var item in list.EnumerateArray())
        {
            var text = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (text == ScrabbleBag.Blank.ToString()) { letters.Add(ScrabbleBag.Blank); continue; }
            if (Words.Normalize(text) is not { Length: 1 } one) return (null, "Не зрозумів, що міняти");
            letters.Add(one[0]);
        }
        if (letters.Count == 0) return (null, "Обери, які фішки міняти");
        if (letters.Count > ScrabbleBoard.RackSize) return (null, "На стійці всього сім фішок");
        return (letters, null);
    }
}
