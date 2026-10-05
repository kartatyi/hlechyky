using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Китайські шашки на зірці (прохід №3, пункт 207) — «шашки на трьох». Дошка — шестикутна зірка на 121 лунку,
/// у кожного 10 фішок у своєму куті; мета — перевести всі в протилежний кут. Хід — крок на сусідню лунку АБО ланцюжок
/// стрибків через будь-яку фішку (свою чи чужу) на порожню лунку одразу за нею; бити нікого не треба й не можна.
///
/// Координати — кубічні (x + y + z = 0), зберігаємо як осьові (q = x, r = z). Зірка — об'єднання двох великих
/// трикутників: «усі координати ≤ 4» і «усі ≥ −4». Лунки нумеруємо рядками r = −8…8, у рядку — q = −8…8: той самий
/// порядок рахує і браузер (zirka.js), тож у вид іде лише рядок станів, а не координати.
///
/// Гравці: удвох — низ (z ≥ 5) проти верху; утрьох — через кут: z ≥ 5, x ≥ 5, y ≥ 5, кожен іде в протилежний.
/// Перемога — коли всі 10 лунок цілі зайняті й хоч одна з них твоя (так чужа фішка, що засіла в куті, не блокує
/// назавжди — класичне правило проти «сторожа»). Стеля партії — <see cref="MaxRounds"/> кіл: далі виграє той,
/// у кого в цілі більше фішок.
/// </summary>
public sealed class Zirka : Game
{
    public const int MaxRounds = 120;
    public const int Pegs = 10;

    public override GameInfo Info { get; } = new(
        "zirka", "Китайські шашки", "китайські шашки", GameGroup.Board, 2, 3, Start: StartMode.ByHost,
        Hint: "Зірка на двох чи трьох: переведи свої 10 фішок у протилежний кут. Крок на сусідню лунку або ланцюжок стрибків через будь-які фішки");

    /// <summary>Осьові координати лунок у порядку нумерації.</summary>
    public static readonly (int Q, int R)[] Cells = Build();
    static readonly Dictionary<(int, int), int> IndexOf = Cells.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i);
    static readonly (int Dq, int Dr)[] Dirs = [(1, 0), (-1, 0), (0, 1), (0, -1), (1, -1), (-1, 1)];
    /// <summary>Сусід у кожному з 6 напрямків (−1 — край дошки).</summary>
    static readonly int[,] Near = BuildNear();

    static (int, int)[] Build()
    {
        var list = new List<(int, int)>();
        for (var r = -8; r <= 8; r++)
            for (var q = -8; q <= 8; q++)
            {
                int x = q, z = r, y = -q - r;
                var a = x <= 4 && y <= 4 && z <= 4;
                var b = x >= -4 && y >= -4 && z >= -4;
                if (a || b) list.Add((q, r));
            }
        return [.. list];
    }

    static int[,] BuildNear()
    {
        var n = new int[Cells.Length, 6];
        for (var i = 0; i < Cells.Length; i++)
            for (var d = 0; d < 6; d++)
                n[i, d] = IndexOf.TryGetValue((Cells[i].Q + Dirs[d].Dq, Cells[i].R + Dirs[d].Dr), out var j) ? j : -1;
        return n;
    }

    /// <summary>Кут: котра кубічна координата (0 — x, 1 — y, 2 — z) і з яким знаком виходить за 4.</summary>
    static bool InCorner(int cell, int axis, int sign)
    {
        var (q, r) = Cells[cell];
        var v = axis switch { 0 => q, 1 => -q - r, _ => r };
        return sign > 0 ? v >= 5 : v <= -5;
    }

    /// <summary>Домівки за кількістю гравців: вісь і знак. Ціль — та сама вісь з протилежним знаком.</summary>
    static readonly (int Axis, int Sign)[][] Homes =
    [
        [],
        [],
        [(2, 1), (2, -1)],
        [(2, 1), (0, 1), (1, 1)],
    ];

    char[] _b = Enumerable.Repeat('.', Cells.Length).ToArray();
    int _n = 2;
    int _turn;
    int _moves;
    int[]? _last;
    int? _winner;
    string? _reason;
    readonly bool[] _gone = new bool[3];
    /// <summary>Хто грає цю партію, по порядку кутів — фіксуємо на старті: хто встав, лишається в списку (з _gone).</summary>
    int[] _players = [0, 1];

    public override string SeatName(int seat) => seat switch { 0 => "жовті", 1 => "сині", 2 => "червоні", _ => base.SeatName(seat) };

    public override void Start()
    {
        _n = Math.Clamp(Enumerable.Range(0, 3).Count(Ctx.Seated), 2, 3);
        // Гравці можуть сісти на місця 0 і 2 — рахуємо місця підряд за тим, хто справді сидить.
        Array.Fill(_b, '.');
        Array.Clear(_gone);
        var seats = _players = Seats();
        for (var k = 0; k < seats.Length; k++)
        {
            var (axis, sign) = Homes[_n][k];
            for (var i = 0; i < Cells.Length; i++) if (InCorner(i, axis, sign)) _b[i] = (char)('0' + seats[k]);
        }
        _turn = seats[0];
        _moves = 0;
        _last = null;
        _winner = null;
        _reason = null;
    }

    /// <summary>Місця, що сидять зараз, по порядку (до трьох). Для самої партії — знімок <see cref="_players"/>.</summary>
    int[] Seats() => [.. Enumerable.Range(0, 3).Where(Ctx.Seated).Take(3)];

    int Slot(int seat) => Array.IndexOf(_players, seat);

    bool InTarget(int cell, int seat)
    {
        var slot = Slot(seat);
        if (slot < 0) return false;
        var (axis, sign) = Homes[_n][slot];
        return InCorner(cell, axis, -sign);
    }

    // ---------- ходи ----------

    /// <summary>Куди може дійти фішка з from: сусідні порожні лунки й усе, що досяжне ланцюжком стрибків. Значення — звідки прийшли (для шляху).</summary>
    public Dictionary<int, int> Reach(int from)
    {
        var came = new Dictionary<int, int>();
        for (var d = 0; d < 6; d++)
        {
            var to = Near[from, d];
            if (to >= 0 && _b[to] == '.') came[to] = from;
        }
        var queue = new Queue<int>();
        queue.Enqueue(from);
        var seen = new HashSet<int> { from };
        while (queue.Count > 0)
        {
            var at = queue.Dequeue();
            for (var d = 0; d < 6; d++)
            {
                var over = Near[at, d];
                if (over < 0 || over == from || _b[over] == '.') continue;
                var land = Near[over, d];
                if (land < 0 || (_b[land] != '.' && land != from) || !seen.Add(land)) continue;
                if (land != from) came[land] = at;   // стрибок кращий за крок лише для шляху — ціль та сама
                queue.Enqueue(land);
            }
        }
        came.Remove(from);
        return came;
    }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (_winner is not null || _reason is not null) return ActResult.Fail("Партію зіграно, тисни «Ану ще раз»");
        if (action != "move") return ActResult.Fail("Тут так не ходять");
        if (seat != _turn) return ActResult.Fail("Не так швидко — зараз не твій хід");
        if (!Int(payload, "from", out var from) || !Int(payload, "to", out var to)) return ActResult.Fail("Не зрозумів, куди ходити");
        if (_b[from] != (char)('0' + seat)) return ActResult.Fail("Це не твоя фішка");
        var reach = Reach(from);
        if (!reach.ContainsKey(to)) return ActResult.Fail("Туди не дострибнеш");

        var path = new List<int> { to };
        for (var at = to; at != from; at = reach[at]) path.Add(reach[at]);
        path.Reverse();
        _last = [.. path];
        _b[to] = _b[from];
        _b[from] = '.';
        _moves++;

        if (Won(seat)) { Win(seat, "home"); return ActResult.Accept("Усі вдома — перемога!"); }
        NextTurn();
        if (_moves >= MaxRounds * _players.Count(s => !_gone[s])) Timeout();
        return ActResult.Done;
    }

    static bool Int(JsonElement p, string name, out int value)
    {
        value = -1;
        return p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out value) && value >= 0 && value < Cells.Length;
    }

    bool Won(int seat)
    {
        var mine = 0;
        for (var i = 0; i < Cells.Length; i++)
        {
            if (!InTarget(i, seat)) continue;
            if (_b[i] == '.') return false;
            if (_b[i] == (char)('0' + seat)) mine++;
        }
        return mine > 0;
    }

    int Home(int seat)
    {
        var n = 0;
        for (var i = 0; i < Cells.Length; i++) if (InTarget(i, seat) && _b[i] == (char)('0' + seat)) n++;
        return n;
    }

    void NextTurn()
    {
        var seats = _players;
        var k = Array.IndexOf(seats, _turn);
        for (var step = 1; step <= seats.Length; step++)
        {
            var s = seats[(k + step) % seats.Length];
            if (!_gone[s]) { _turn = s; return; }
        }
    }

    void Win(int seat, string reason)
    {
        _winner = seat;
        _reason = reason;
        Ctx.Finish([seat], $"{Info.Title}: {Ctx.NickOf(seat)} ({SeatName(seat)}) перший привів усіх додому" + (reason == "home" ? "" : $" ({reason})"));
    }

    /// <summary>Стеля кіл: хто більше завів у ціль, той і виграв; порівну — нічия.</summary>
    void Timeout()
    {
        var live = _players.Where(s => !_gone[s]).ToArray();
        var best = live.Max(Home);
        var top = live.Where(s => Home(s) == best).ToArray();
        _reason = "rounds";
        if (top.Length == 1) _winner = top[0];
        Ctx.Finish(top.Length == 1 ? top : [],
            $"{Info.Title}: {MaxRounds} кіл минуло — " + (top.Length == 1 ? $"найбільше вдома в {Ctx.NickOf(top[0])}" : "нічия"));
    }

    public override void OnLeave(int seat)
    {
        if (_winner is not null || _reason is not null) return;
        _gone[seat] = true;
        // Фішки того, хто пішов, знімаємо з дошки: лишити — означало б мертві перешкоди до кінця партії.
        for (var i = 0; i < _b.Length; i++) if (_b[i] == (char)('0' + seat)) _b[i] = '.';
        var live = _players.Where(s => !_gone[s]).ToArray();
        if (live.Length <= 1)
        {
            _reason = "left";
            _winner = live.Length == 1 ? live[0] : null;
            Ctx.Finish(live, $"{Info.Title}: {Ctx.NickOf(seat)} встає з-за столу" + (live.Length == 1 ? $", {Ctx.NickOf(live[0])} виграє" : ""));
            return;
        }
        if (_turn == seat) NextTurn();
    }

    public override object View(int? seat)
    {
        var playing = _winner is null && _reason is null;
        var moves = new Dictionary<string, int[]>();
        if (playing)
            for (var i = 0; i < _b.Length; i++)
                if (_b[i] == (char)('0' + _turn) && Reach(i) is { Count: > 0 } r) moves[i.ToString()] = [.. r.Keys.Order()];
        var seats = _players;
        return new
        {
            cells = new string(_b),
            n = _n,
            turn = playing ? (int?)_turn : null,
            // Хто в якому куті: місце, домівка (вісь і знак) — браузер фарбує кути й повертає дошку домівкою до себе.
            homes = seats.Select((s, k) => new { seat = s, axis = Homes[_n][Math.Min(k, _n - 1)].Axis, sign = Homes[_n][Math.Min(k, _n - 1)].Sign }).ToArray(),
            home = seats.Select(Home).ToArray(),
            moves,
            last = _last is null ? null : (int[])_last.Clone(),
            count = _moves,
            result = _reason is null ? null : new { winner = _winner, reason = _reason },
        };
    }

    public sealed record State(string Board, int N, int Turn, int Moves, int[]? Last, int? Winner, string? Reason, bool[] Gone, int[]? Players = null);

    public override string? Save() => JsonSerializer.Serialize(new State(new string(_b), _n, _turn, _moves, _last, _winner, _reason, [.. _gone], [.. _players]));

    /// <summary>Save тримає всю партію (таймерів і ботів тут нема) — після перезапуску сервера грає далі.</summary>
    public override bool Resumable => true;

    public override void Load(string json)
    {
        if (JsonSerializer.Deserialize<State>(json) is not { } s || s.Board.Length != Cells.Length) return;
        _b = s.Board.ToCharArray();
        _n = s.N;
        _turn = s.Turn;
        _moves = s.Moves;
        _last = s.Last;
        _winner = s.Winner;
        _reason = s.Reason;
        Array.Copy(s.Gone, _gone, Math.Min(3, s.Gone.Length));
        _players = s.Players ?? [.. Enumerable.Range(0, _n)];
    }
}
