using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Ультимативні хрестики-нолики (№214): дев'ять полів 3×3 у великому полі 3×3. Куди ти сходив у малому полі —
/// у те поле великого мусить іти суперник. Виграв мале поле (три в ряд у ньому) — воно твоє; партію бере той,
/// хто збере три виграні поля в ряд на великому. Поле вигране або повне — наступний ходить будь-де.
/// Окрема гра, а не опція «Хрестиків»: поле, вид і клієнт зовсім інші, а рейтинг — свій.
/// Клітинки — <c>поле*9 + клітинка</c> (обидві частини рядками зліва направо, згори вниз).
/// </summary>
public sealed class UltimateTicTacToe : Game
{
    public override GameInfo Info { get; } = new(
        "ttt9", "Ультимативні хрестики", "ультимативні хрестики", GameGroup.Board, 2, 2, Rated: true,
        Hint: "Дев'ять хрестиків-ноликів в одному. Куди сходив у малому полі — туди на великому йде суперник. "
            + "Виграй три малі поля в ряд.");

    static readonly string[] Letters = ["x", "o"];
    /// <summary>Вісім рядів поля 3×3 — і для малого поля, і для великого.</summary>
    static readonly int[][] Lines =
    [
        [0, 1, 2], [3, 4, 5], [6, 7, 8],
        [0, 3, 6], [1, 4, 7], [2, 5, 8],
        [0, 4, 8], [2, 4, 6],
    ];

    string?[] _cells = new string?[81];
    /// <summary>Хто взяв мале поле: «x», «o», «draw» (повне без ряду) або null, поки грають.</summary>
    string?[] _boards = new string?[9];
    /// <summary>У яке поле мусить іти той, чия черга; -1 — будь-куди.</summary>
    int _next = -1;
    int _turn;
    string? _winner;
    int[] _won = [];
    /// <summary>Виграшний ряд великого поля (номери малих полів) і ряди малих полів, що їх виграли.</summary>
    int[]? _line;
    readonly int[]?[] _small = new int[]?[9];
    int? _last;
    readonly Series _series = new();

    public override string SeatName(int seat) => seat == 0 ? "✕" : "◯";

    public override void Start()
    {
        _cells = new string?[81];
        _boards = new string?[9];
        Array.Clear(_small);
        _next = -1;
        _turn = 0;
        _winner = null;
        _won = [];
        _line = null;
        _last = null;
        _series.Begin(Ctx, 2);
    }

    /// <summary>Чи можна зараз ходити в цю клітинку.</summary>
    bool Legal(int cell) =>
        cell >= 0 && cell < 81 && _cells[cell] is null && _boards[cell / 9] is null && (_next < 0 || cell / 9 == _next);

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (_winner is not null) return ActResult.Fail("Партію зіграно, тисни «Ану ще раз»");
        if (action == "resign") return Resign(seat);
        if (action != "move") return ActResult.Fail("Тут так не ходять");
        if (seat != _turn) return ActResult.Fail("Не так швидко — зараз не твій хід");
        if (Cell(payload) is not { } cell) return ActResult.Fail("Не зрозумів, куди ходити");
        if (cell < 0 || cell >= 81 || _cells[cell] is not null) return ActResult.Fail("Ця клітинка вже зайнята");
        if (_boards[cell / 9] is not null) return ActResult.Fail("Це поле вже закрите — ходи в інше");
        if (_next >= 0 && cell / 9 != _next) return ActResult.Fail("Не те поле: ходити треба в підсвічене");

        var mark = Letters[seat];
        var board = cell / 9;
        _cells[cell] = mark;
        _last = cell;

        // Мале поле: ряд — поле твоє; усе заповнене — нічия, воно нічиє.
        if (Row(i => _cells[board * 9 + i] == mark) is { } small)
        {
            _boards[board] = mark;
            _small[board] = small;
        }
        else if (Full(board)) _boards[board] = "draw";

        if (Row(i => _boards[i] == mark) is { } big)
        {
            _winner = mark;
            _line = big;
            Finish([seat], Score(seat));
            return ActResult.Accept("Твоя взяла! Три поля в ряд");
        }
        if (_boards.All(b => b is not null))
        {
            _winner = "draw";
            Finish([], $"{Info.Title}: {Nick(0)} ✕ і {Nick(1)} ◯ зіграли внічию");
            return ActResult.Accept("Нічия! Ану ще раз?");
        }

        // Куди сходив у малому полі — туди на великому йде суперник; закрите поле відпускає будь-куди.
        var target = cell % 9;
        _next = _boards[target] is null ? target : -1;
        _turn = 1 - seat;
        return ActResult.Done;
    }

    /// <summary>Рядок Журналу: ніки чужі й не відмінюються, тож рахунок замість речення.</summary>
    string Score(int seat) => $"{Info.Title}: {Nick(seat)} {SeatName(seat)} 1:0 {Nick(1 - seat)} {SeatName(1 - seat)}";

    string Nick(int seat) => Ctx.NickOf(seat) ?? SeatName(seat);

    bool Full(int board)
    {
        for (var i = 0; i < 9; i++) if (_cells[board * 9 + i] is null) return false;
        return true;
    }

    static int[]? Row(Func<int, bool> mine)
    {
        foreach (var l in Lines)
            if (mine(l[0]) && mine(l[1]) && mine(l[2])) return l;
        return null;
    }

    ActResult Resign(int seat)
    {
        if (seat is < 0 or > 1) return ActResult.Fail("Ти не за столом — дивись, хто кого");
        _winner = Letters[1 - seat];
        Finish([1 - seat], $"{Info.Title}: {Nick(seat)} здається, {Nick(1 - seat)} {SeatName(1 - seat)} перемагає");
        return ActResult.Accept("Партію віддано — ану ще раз?");
    }

    public override void OnLeave(int seat)
    {
        if (_winner is not null) return;
        // Техпоразка — як у всіх іграх (Game.OnLeave); серію не чіпаємо: склад за столом однаково змінився.
        _winner = seat is 0 or 1 ? Letters[1 - seat] : "draw";
        base.OnLeave(seat);
    }

    void Finish(int[] winners, string text)
    {
        _won = winners;
        _series.Record(Ctx, winners);
        Ctx.Finish(winners, text);
    }

    static int? Cell(JsonElement payload) => payload.ValueKind switch
    {
        JsonValueKind.Number when payload.TryGetInt32(out var n) => n,
        JsonValueKind.Object when payload.TryGetProperty("cell", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) => n,
        _ => null,
    };

    public override object View(int? seat)
    {
        var legal = new List<int>();
        if (_winner is null)
            for (var c = 0; c < 81; c++) if (Legal(c)) legal.Add(c);
        return new
        {
            cells = (string?[])_cells.Clone(),
            boards = (string?[])_boards.Clone(),
            // Ряди у виграних малих полях (номери клітинок усередині поля) — щоб браузер їх перекреслив.
            small = _small.Select(l => l is null ? null : (int[])l.Clone()).ToArray(),
            next = _winner is null ? _next : -1,
            legal,
            turn = _winner is null ? _turn : (int?)null,
            last = _last,
            line = _line is null ? null : (int[])_line.Clone(),
            winner = _winner,
            won = (int[])_won.Clone(),
            series = _series.View(Ctx, 2),
        };
    }
}
