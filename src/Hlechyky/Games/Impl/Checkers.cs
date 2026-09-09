using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>Один повний хід: поля шляху (перше — звідки), побиті шашки і чим фігура стане в кінці.</summary>
/// <param name="Path">Поля 0..63; для взяття це весь ланцюг, а не перший стрибок.</param>
/// <param name="Taken">Поля побитих; знімаються лише після завершення ланцюга (турецький удар).</param>
/// <param name="End">Символ фігури на останньому полі: 'w'/'W'/'b'/'B' — з урахуванням перетворення.</param>
public sealed record CheckersMove(int[] Path, int[] Taken, char End)
{
    public bool Capture => Taken.Length > 0;
}

/// <summary>
/// Дошка і правила російських шашок — без жодного слова про кімнати, ставки й чат. Поля нумеруємо так
/// само, як їх бачить браузер у полі <c>board</c>: 0 — a8, 7 — h8, 63 — h1, тобто
/// <c>index = (8 - ранг) * 8 + файл</c>. Темні поля — ті, де сума ряду й колонки непарна (a1 темне).
///
/// Три речі, через які ця гра складніша за хрестики:
/// - взяття обов'язкове, тож легальність простого ходу залежить від усієї дошки, а не від однієї шашки;
/// - ланцюг узять іде за один хід, і побиті лишаються на дошці до його кінця (турецький удар);
/// - проста, що дійшла до останньої лінії посеред ланцюга, добиває вже як дамка.
/// </summary>
public static class CheckersRules
{
    /// <summary>Стеля пошуку ланцюгів ОДНІЄЇ шашки. Практично недосяжна (найдовший перелік, який ми
    /// бачили, — два десятки), але не дає зациклитись на штучній дошці.</summary>
    public const int MaxChains = 500;

    /// <summary>Скільки ланцюгів віддаємо клієнтові з однієї шашки. Стеля саме на шашку, а не на весь
    /// список: спільна віддала б усі ланцюги перших шашок і жодного від решти, і тими рештою гравець
    /// не зміг би походити з UI, хоча сервер такий хід прийняв би.</summary>
    public const int ViewChains = 64;

    /// <summary>Чотири діагоналі: 0 і 1 — угору (бік білих), 2 і 3 — униз (бік чорних).</summary>
    static readonly (int Dr, int Dc)[] Dirs = [(-1, -1), (-1, 1), (1, -1), (1, 1)];

    public static bool Dark(int i) => (i / 8 + i % 8) % 2 == 1;

    /// <summary>«c3» для поля 42.</summary>
    public static string Name(int i) => $"{(char)('a' + i % 8)}{8 - i / 8}";

    public static int Index(int row, int col) => row * 8 + col;

    /// <summary>«c3» → 42; null, якщо це не темне поле дошки.</summary>
    public static int? Parse(string? s)
    {
        if (s is not { Length: 2 }) return null;
        var col = char.ToLowerInvariant(s[0]) - 'a';
        var rank = s[1] - '1';
        if (col is < 0 or > 7 || rank is < 0 or > 7) return null;
        var i = Index(7 - rank, col);
        return Dark(i) ? i : null;
    }

    /// <summary>Початкова розстановка: по 12 шашок на трьох крайніх рядах, світлі поля — пробіли.</summary>
    public static char[] Start()
    {
        var b = new char[64];
        for (var i = 0; i < 64; i++)
            b[i] = !Dark(i) ? ' ' : i / 8 <= 2 ? 'b' : i / 8 >= 5 ? 'w' : '.';
        return b;
    }

    public static bool White(char p) => p is 'w' or 'W';
    public static bool Black(char p) => p is 'b' or 'B';
    public static bool King(char p) => p is 'W' or 'B';
    /// <summary>Шашка сторони side (0 — білі, 1 — чорні).</summary>
    public static bool Own(char p, int side) => side == 0 ? White(p) : Black(p);
    public static bool Enemy(char p, int side) => side == 0 ? Black(p) : White(p);
    public static int Count(char[] b, int side) => b.Count(p => Own(p, side));

    /// <summary>Поле за times кроків у напрямку dir; -1, якщо вийшли за дошку.</summary>
    static int Step(int i, int dir, int times = 1)
    {
        var (dr, dc) = Dirs[dir];
        var (r, c) = (i / 8 + dr * times, i % 8 + dc * times);
        return r is < 0 or > 7 || c is < 0 or > 7 ? -1 : Index(r, c);
    }

    /// <summary>Проста на останній лінії стає дамкою — і ходом, і посеред ланцюга взять.</summary>
    static char Crown(char p, int at) => p switch
    {
        'w' when at / 8 == 0 => 'W',
        'b' when at / 8 == 7 => 'B',
        _ => p,
    };

    /// <summary>
    /// Усі повні ланцюги взять із поля from. «Повний» означає, що з останнього поля бити вже нема кого:
    /// саме тому неповний ланцюг гра відхиляє, а не доробляє за гравця.
    /// </summary>
    public static List<CheckersMove> Captures(char[] board, int from, int cap = MaxChains)
    {
        var res = new List<CheckersMove>();
        var piece = board[from];
        if (piece is '.' or ' ' || cap <= 0) return res;
        // Шашка вже в дорозі: своє поле вона не займає, і стати на нього наприкінці ланцюга можна.
        var b = (char[])board.Clone();
        b[from] = '.';
        Walk(b, White(piece) ? 0 : 1, from, piece, [from], [], res, cap);
        return res;
    }

    static void Walk(char[] b, int side, int at, char piece, List<int> path, List<int> taken, List<CheckersMove> res, int cap)
    {
        if (res.Count >= cap) return;
        var any = false;
        for (var d = 0; d < 4; d++)
        {
            if (King(piece))
            {
                // Дамка йде по діагоналі до першої фігури. Своя — глухо; уже побита теж глухо:
                // через одну шашку двічі не б'ють, і вона стоїть на дошці як перешкода.
                var mid = Step(at, d);
                while (mid >= 0 && b[mid] == '.') mid = Step(mid, d);
                if (mid < 0 || !Enemy(b[mid], side) || taken.Contains(mid)) continue;
                for (var land = Step(mid, d); land >= 0 && b[land] == '.'; land = Step(land, d))
                {
                    any = true;
                    Descend(b, side, land, piece, mid, path, taken, res, cap);
                    if (res.Count >= cap) return;
                }
            }
            else
            {
                // Проста б'є і вперед, і назад — тому всі чотири напрямки.
                var (mid, land) = (Step(at, d), Step(at, d, 2));
                if (mid < 0 || land < 0 || !Enemy(b[mid], side) || taken.Contains(mid) || b[land] != '.') continue;
                any = true;
                Descend(b, side, land, piece, mid, path, taken, res, cap);
                if (res.Count >= cap) return;
            }
        }
        // Бити більше нема кого — ланцюг скінчився. Один-єдиний елемент у шляху означає, що ця шашка
        // не побила нічого: такий «ланцюг» нікому не потрібен.
        if (!any && path.Count > 1) res.Add(new CheckersMove([.. path], [.. taken], piece));
    }

    static void Descend(char[] b, int side, int land, char piece, int victim, List<int> path, List<int> taken, List<CheckersMove> res, int cap)
    {
        path.Add(land);
        taken.Add(victim);
        Walk(b, side, land, Crown(piece, land), path, taken, res, cap);
        path.RemoveAt(path.Count - 1);
        taken.RemoveAt(taken.Count - 1);
    }

    /// <summary>Ходи без взяття однією шашкою: проста — на крок уперед, дамка — на всю діагональ.</summary>
    public static List<CheckersMove> Quiet(char[] b, int from)
    {
        var res = new List<CheckersMove>();
        var piece = b[from];
        if (piece is '.' or ' ') return res;
        var side = White(piece) ? 0 : 1;
        if (King(piece))
        {
            for (var d = 0; d < 4; d++)
                for (var to = Step(from, d); to >= 0 && b[to] == '.'; to = Step(to, d))
                    res.Add(new CheckersMove([from, to], [], piece));
        }
        else
        {
            int[] forward = side == 0 ? [0, 1] : [2, 3];
            foreach (var d in forward)
            {
                var to = Step(from, d);
                if (to >= 0 && b[to] == '.') res.Add(new CheckersMove([from, to], [], Crown(piece, to)));
            }
        }
        return res;
    }

    /// <summary>Чи мусить ця сторона бити. Дешевше за повний перелік: спиняємось на першому ланцюзі.</summary>
    public static bool MustCapture(char[] b, int side)
    {
        for (var i = 0; i < 64; i++)
            if (Own(b[i], side) && Captures(b, i, cap: 1).Count > 0) return true;
        return false;
    }

    /// <summary>Чи є в сторони бодай один хід. Спиняємось на першому — це дешевше за повний перелік
    /// і, на відміну від <see cref="Legal"/>, не залежить від жодної стелі.</summary>
    public static bool HasMove(char[] b, int side)
    {
        for (var i = 0; i < 64; i++)
            if (Own(b[i], side) && (Captures(b, i, cap: 1).Count > 0 || Quiet(b, i).Count > 0)) return true;
        return false;
    }

    /// <summary>Усі легальні ходи сторони. Є взяття — у списку лише взяття (бити обов'язково).
    /// <paramref name="cap"/> — стеля на ОДНУ шашку, тож у переліку є ланцюги від кожної, що вміє бити.</summary>
    public static List<CheckersMove> Legal(char[] b, int side, int cap = ViewChains)
    {
        var caps = new List<CheckersMove>();
        for (var i = 0; i < 64; i++)
            if (Own(b[i], side)) caps.AddRange(Captures(b, i, cap));
        if (caps.Count > 0) return caps;
        var quiet = new List<CheckersMove>();
        for (var i = 0; i < 64; i++)
            if (Own(b[i], side)) quiet.AddRange(Quiet(b, i));
        return quiet;
    }

    /// <summary>Зіграти хід: побиті знімаються саме тут, коли ланцюг уже завершено.</summary>
    public static void Apply(char[] b, CheckersMove m)
    {
        b[m.Path[0]] = '.';
        foreach (var t in m.Taken) b[t] = '.';
        b[m.Path[^1]] = m.End;
    }
}

/// <summary>
/// Російські шашки на двох. Правила й стан живуть тільки тут: клієнт малює дошку і надсилає намір
/// («ось такий ланцюг»), а чи він законний — вирішує сервер.
///
/// Місце 0 — білі, місце 1 — чорні, завжди. «Ще раз» кольори не міняє: каркас сам обертає місця, тож
/// той, хто грав чорними, наступну партію починає білими — обертати ще й тут означало б обернути двічі.
/// </summary>
public sealed class Checkers : Game
{
    /// <summary>Скільки ПОВНИХ ходів поспіль самими дамками без взяття вважаємо нічиєю (спрощене
    /// правило зі spec). «Хід» тут — як за дошкою: хід білих і хід чорних разом, тому лічильник
    /// півходів порівнюємо з подвоєним порогом. Інакше виграш «три дамки проти однієї», якому
    /// саме й треба з десяток ходів маневрування, обривався б нічиєю на половині.</summary>
    public const int QuietLimit = 15;

    /// <summary>Довший шлях фізично неможливий: 12 узять плюс поле старту.</summary>
    const int MaxPathLength = 13;

    public override GameInfo Info { get; } = new(
        "checkers", "Шашки", "шашки", GameGroup.Board, 2, 2, Rated: true,
        Hint: "Російські шашки: бити обов'язково, дамка ходить на всю діагональ");

    char[] _b = CheckersRules.Start();
    int _turn;
    string[]? _last;
    /// <summary>Хто зараз пропонує нічию; null — ніхто.</summary>
    int? _offer;
    bool _over;
    /// <summary>Місце переможця; null — нічия (має сенс лише коли _over).</summary>
    int? _winner;
    string? _reason;
    /// <summary>Півходи поспіль самими дамками без взяття; поріг нічиєї — <see cref="QuietLimit"/> * 2.</summary>
    int _quiet;
    /// <summary>Скільки разів позиція вже траплялась: триразове повторення — нічия.</summary>
    readonly Dictionary<string, int> _seen = new(StringComparer.Ordinal);

    public override string SeatName(int seat) => seat == 0 ? "білі" : "чорні";

    public override void Start()
    {
        _b = CheckersRules.Start();
        _turn = 0;
        _last = null;
        _offer = null;
        _over = false;
        _winner = null;
        _reason = null;
        _quiet = 0;
        _seen.Clear();
    }

    public override ActResult Act(int seat, string action, JsonElement payload) => action switch
    {
        "move" => Move(seat, payload),
        "resign" => Resign(seat),
        "draw" => Offer(seat),
        "decline" => Decline(seat),
        _ => ActResult.Fail("Тут так не ходять"),
    };

    // ---------- хід ----------

    ActResult Move(int seat, JsonElement payload)
    {
        if (_over) return ActResult.Fail("Партію зіграно, тисни «Ще раз»");
        if (seat != _turn) return ActResult.Fail("Зараз не твій хід");
        if (ReadPath(payload) is not { } path) return ActResult.Fail("Не зрозумів, куди ходити");

        var from = path[0];
        if (!CheckersRules.Own(_b[from], seat)) return ActResult.Fail("Це не твоя шашка");

        // Взяття обов'язкове — тому набір законних ходів цієї шашки залежить від усієї дошки.
        var must = CheckersRules.MustCapture(_b, seat);
        var quiet = CheckersRules.Quiet(_b, from);
        var chains = must ? CheckersRules.Captures(_b, from) : quiet;
        if (chains.FirstOrDefault(c => c.Path.SequenceEqual(path)) is not { } move) return Why(path, chains, quiet, must);

        var wasMan = !CheckersRules.King(_b[from]);
        CheckersRules.Apply(_b, move);
        _last = [.. move.Path.Select(CheckersRules.Name)];
        _offer = null;                       // будь-який хід знімає пропозицію нічиєї
        // Взяття і рух простої незворотні: після них ні «15 ходів дамками», ні повторення рахувати нема від чого.
        if (move.Capture || wasMan) { _quiet = 0; _seen.Clear(); }
        else _quiet++;
        _turn = seat == 0 ? 1 : 0;

        if (CheckersRules.Count(_b, _turn) == 0) return Won(seat, "nopieces");
        if (!CheckersRules.HasMove(_b, _turn)) return Won(seat, "nomoves");
        if (_quiet >= QuietLimit * 2) return Drawn("kings15");   // _quiet рахує півходи
        var key = $"{new string(_b)}{_turn}";
        _seen[key] = _seen.GetValueOrDefault(key) + 1;
        if (_seen[key] >= 3) return Drawn("repetition");
        return ActResult.Done;
    }

    /// <summary>Чому хід не пройшов. Тексти короткі: гравець бачить їх тостом над дошкою.</summary>
    static ActResult Why(int[] path, List<CheckersMove> chains, List<CheckersMove> quiet, bool must)
    {
        if (!must) return ActResult.Fail("Так не ходять");
        if (chains.Any(c => c.Path.Length > path.Length && c.Path.Take(path.Length).SequenceEqual(path)))
            return ActResult.Fail("Треба дібрати до кінця");
        // Ця шашка бити не вміє або гравець сунув її повз бій — і те, й те означає одне.
        if (chains.Count == 0 || quiet.Any(q => q.Path.SequenceEqual(path))) return ActResult.Fail("Бити обов'язково");
        return ActResult.Fail("Так не ходять");
    }

    /// <summary>Шлях приходить масивом полів: <c>{ path: ["c3","e5"] }</c>.</summary>
    static int[]? ReadPath(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.Array) return null;
        var path = new List<int>();
        foreach (var e in p.EnumerateArray())
        {
            if (path.Count >= MaxPathLength) return null;
            if (e.ValueKind != JsonValueKind.String || CheckersRules.Parse(e.GetString()) is not { } sq) return null;
            path.Add(sq);
        }
        return path.Count >= 2 ? [.. path] : null;
    }

    // ---------- здатись, нічия ----------

    ActResult Resign(int seat)
    {
        if (_over) return ActResult.Fail("Партію зіграно, тисни «Ще раз»");
        var win = Other(seat);
        Over(win, "resign");
        Ctx.Finish([win], $"{Info.Title}: {Ctx.NickOf(seat)} здається — {Ctx.NickOf(win)} {SeatName(win)} 1:0 {Ctx.NickOf(seat)} {SeatName(seat)}");
        return ActResult.Accept("Здався");
    }

    ActResult Offer(int seat)
    {
        if (_over) return ActResult.Fail("Партію зіграно, тисни «Ще раз»");
        if (_offer == seat) return ActResult.Fail("Пропозиція вже висить");
        if (_offer == Other(seat)) return Drawn("agreed");
        _offer = seat;
        return ActResult.Accept("Запропонував нічию");
    }

    ActResult Decline(int seat)
    {
        if (_over) return ActResult.Fail("Партію зіграно, тисни «Ще раз»");
        if (_offer != Other(seat)) return ActResult.Fail("Нічиєї ніхто не пропонував");
        _offer = null;
        return ActResult.Accept("Пропозицію знято");
    }

    // ---------- кінець партії ----------

    static int Other(int seat) => seat == 0 ? 1 : 0;

    void Over(int? winner, string reason)
    {
        _over = true;
        _winner = winner;
        _reason = reason;
        _offer = null;
    }

    ActResult Won(int seat, string reason)
    {
        Over(seat, reason);
        var lost = Other(seat);
        // Ніки чужі, відмінювати їх нема як, тому рахунок замість речення з відмінками.
        Ctx.Finish([seat], $"{Info.Title}: {Ctx.NickOf(seat)} {SeatName(seat)} 1:0 {Ctx.NickOf(lost)} {SeatName(lost)}");
        return ActResult.Accept("Твоя взяла!");
    }

    ActResult Drawn(string reason)
    {
        Over(null, reason);
        Ctx.Finish([], $"{Info.Title}: {Ctx.NickOf(0)} {SeatName(0)} і {Ctx.NickOf(1)} {SeatName(1)} зіграли внічию ({Said(reason)})");
        return ActResult.Accept("Нічия");
    }

    static string Said(string reason) => reason switch
    {
        "kings15" => $"{QuietLimit} ходів дамками",
        "repetition" => "повторення позиції",
        _ => "за згодою",
    };

    public override void OnLeave(int seat)
    {
        if (_over) return;                   // партію вже дограли, вставати можна спокійно
        var win = Other(seat);
        int[] winners = Ctx.Seated(win) ? [win] : [];
        Over(winners.Length > 0 ? win : null, "left");
        Ctx.Finish(winners, $"{Info.Title}: {Ctx.NickOf(seat)} встав з-за столу, партію не дограли");
    }

    // ---------- вид ----------

    public override object View(int? seat)
    {
        // Партію зіграно — легальних ходів нема за визначенням, і клієнт не має підсвічувати дошку.
        List<CheckersMove> legal = _over ? [] : CheckersRules.Legal(_b, _turn);
        return new
        {
            board = new string(_b),
            turn = _over ? null : (int?)_turn,
            toMove = _turn == 0 ? "w" : "b",
            legal = legal.Select(m => m.Path.Select(CheckersRules.Name).ToArray()).ToArray(),
            mustCapture = legal.Count > 0 && legal[0].Capture,
            lastPath = _last is null ? null : (string[])_last.Clone(),
            count = new { w = CheckersRules.Count(_b, 0), b = CheckersRules.Count(_b, 1) },
            drawOffer = _offer,
            result = _over ? new { winner = _winner, reason = _reason } : null,
        };
    }

    // ---------- збереження ----------

    /// <summary>Знімок партії. Гра не Persistent, але стан у неї цілком серіалізовний — і тестам так видніше.</summary>
    public sealed record Snapshot(
        string Board, int Turn, string[]? Last, int? Offer, bool Over, int? Winner, string? Reason,
        int Quiet, Dictionary<string, int> Seen);

    public override string? Save() =>
        JsonSerializer.Serialize(new Snapshot(new string(_b), _turn, _last, _offer, _over, _winner, _reason, _quiet, new(_seen)));

    public override void Load(string json)
    {
        if (JsonSerializer.Deserialize<Snapshot>(json) is not { } s || s.Board.Length != 64) return;
        _b = s.Board.ToCharArray();
        _turn = s.Turn == 1 ? 1 : 0;
        _last = s.Last;
        _offer = s.Offer;
        _over = s.Over;
        _winner = s.Winner;
        _reason = s.Reason;
        _quiet = s.Quiet;
        _seen.Clear();
        foreach (var (k, v) in s.Seen) _seen[k] = v;
    }
}
