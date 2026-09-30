using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Реверсі (Отелло) удвох: місце 0 — чорні (ходять першими), місце 1 — білі. «Ще раз» обертає місця, тож кольори
/// міняються самі. Пас автоматичний: кому нема куди ходити, того пропускають, а Журнал це пише.
/// Годинник необов'язковий (<see cref="BoardClock"/>), прапорець перевіряє кожна дія — як у шашках.
/// Spec — docs/games/specs/reversi.md.
/// </summary>
public sealed class Reversi : Game
{
    public override GameInfo Info { get; } = new(
        "reversi", "Реверсі", "реверсі", GameGroup.Board, 2, 2, Rated: true,
        Options: [BoardClock.Option],
        Hint: "Отелло 8×8: затисни чужі фішки між своїми — і вони твої. Кому нема куди ходити, той пасує; у кого більше фішок, той і виграв");

    ReversiCore _core = ReversiCore.Start();
    int? _last;
    ulong _flipped;
    /// <summary>Хто пасував щойно (його пропустили); null — ніхто.</summary>
    int? _pass;
    int _moves;
    bool _over;
    int? _winner;
    string? _reason;
    readonly BoardClock _clock = new();
    readonly Series _series = new();

    public override string SeatName(int seat) => seat == 0 ? "чорні" : "білі";

    public static string Emoji(int side) => side == 0 ? "⚫" : "⚪";

    public override void Configure(IReadOnlyDictionary<string, string> options) => _clock.Configure(options);

    public override void Start()
    {
        _clock.Reset();
        _series.Begin(Ctx, 2);
        _core = ReversiCore.Start();
        _last = null;
        _flipped = 0;
        _pass = null;
        _moves = 0;
        _over = false;
        _winner = null;
        _reason = null;
    }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        // Прапорець — на кожній дії (як у шашках): хто просидів свій час, той уже не походить.
        if (!_over && _clock.Flagged(Ctx.Clock.UtcNow) is { } flagged)
        {
            var win = 1 - flagged;
            Over(win, "time");
            End([win], $"{Info.Title}: у {Ctx.NickOf(flagged)} скінчився час — {Ctx.NickOf(win)} {SeatName(win)} 1:0 {Ctx.NickOf(flagged)} {SeatName(flagged)}");
            return ActResult.Accept(flagged == seat ? "От халепа — твій час вийшов" : "Овва! У суперника впав прапорець");
        }
        return action switch
        {
            "flag" => ActResult.Fail(_over ? "Партію зіграно, тисни «Ану ще раз»" : "Час ще є"),
            "move" => Move(seat, payload),
            "resign" => Resign(seat),
            _ => ActResult.Fail("Тут так не ходять"),
        };
    }

    /// <summary>Поле з payload: <c>{ cell: 0..63 }</c> (або назвою, <c>{ cell: "f5" }</c>). null — кривий payload.</summary>
    public static int? ReadCell(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("cell", out var c)) return null;
        return c.ValueKind switch
        {
            JsonValueKind.Number when c.TryGetInt32(out var i) && i is >= 0 and < 64 => i,
            JsonValueKind.String => ReversiCore.Parse(c.GetString()),
            _ => null,
        };
    }

    /// <summary>Спільна перевірка ходу для обох режимів. null — хід законний.</summary>
    public static string? Illegal(ReversiCore core, int? cell)
    {
        if (cell is not { } c) return "Не зрозумів, куди ставити";
        if (((core.Discs[0] | core.Discs[1]) & ReversiCore.Bit(c)) != 0) return "Поле вже зайняте";
        if (core.Flips(core.Side, c) == 0) return "Так не можна — нічого не перевертається";
        return null;
    }

    ActResult Move(int seat, JsonElement payload)
    {
        if (_over) return ActResult.Fail("Партію зіграно, тисни «Ану ще раз»");
        if (seat != _core.Side) return ActResult.Fail("Не так швидко — зараз не твій хід");
        var cell = ReadCell(payload);
        if (Illegal(_core, cell) is { } why) return ActResult.Fail(why);

        _flipped = _core.Play(cell!.Value);
        _last = cell;
        _moves++;
        _pass = null;
        _clock.Moved(seat, Ctx.Clock.UtcNow);

        if (_core.Over) return Counted();
        if (_core.PassIfStuck())
        {
            var stuck = 1 - seat;
            _pass = stuck;
            // Годинник повертаємо тому, хто ходить знову (пасовому — хіба надбавка за «хід»).
            _clock.Moved(stuck, Ctx.Clock.UtcNow);
            Ctx.Log($"{Emoji(stuck)} {SeatName(stuck)} пасують — ходити нікуди");
        }
        return ActResult.Done;
    }

    ActResult Resign(int seat)
    {
        if (_over) return ActResult.Fail("Партію зіграно, тисни «Ану ще раз»");
        var win = 1 - seat;
        Over(win, "resign");
        End([win], $"{Info.Title}: {Ctx.NickOf(seat)} здається — {Ctx.NickOf(win)} {SeatName(win)} 1:0 {Ctx.NickOf(seat)} {SeatName(seat)}");
        return ActResult.Accept("Партію віддано — ану ще раз?");
    }

    /// <summary>Ходу нема ні в кого: рахуємо фішки. Порожні поля в рахунок не йдуть.</summary>
    ActResult Counted()
    {
        int b = _core.Count(0), w = _core.Count(1);
        var score = $"{Ctx.NickOf(0)} ⚫ {b} : {w} ⚪ {Ctx.NickOf(1)}";
        if (b == w)
        {
            Over(null, "count");
            End([], $"{Info.Title}: {score} — нічия");
            return ActResult.Accept("Нічия! Ану ще раз?");
        }
        var win = b > w ? 0 : 1;
        var wipe = _core.Count(1 - win) == 0;
        Over(win, wipe ? "wipe" : "count");
        if (wipe) Ctx.Award(win, 0, "ach:reversi-wipe");
        End([win], $"{Info.Title}: {score}{(wipe ? " — дошку витерто" : "")}");
        return ActResult.Accept("Партію дограно!");
    }

    void Over(int? winner, string reason)
    {
        _over = true;
        _winner = winner;
        _reason = reason;
    }

    void End(int[] winners, string text)
    {
        _clock.Stop(Ctx.Clock.UtcNow);
        _series.Record(Ctx, winners);
        Ctx.Finish(winners, text);
    }

    public override void OnLeave(int seat)
    {
        if (_over) return;
        var win = 1 - seat;
        int[] winners = Ctx.Seated(win) ? [win] : [];
        Over(winners.Length > 0 ? win : null, "left");
        End(winners, $"{Info.Title}: {Ctx.NickOf(seat)} встає з-за столу, партію не дограли");
    }

    public override object View(int? seat) => new
    {
        board = _core.BoardString(),
        turn = _over ? null : (int?)_core.Side,
        toMove = _over ? null : _core.Side == 0 ? "b" : "w",
        legal = _over ? [] : _core.Legal(_core.Side),
        last = _last,
        flipped = ReversiCore.Cells(_flipped).ToArray(),
        pass = _pass,
        count = new { b = _core.Count(0), w = _core.Count(1) },
        moves = _moves,
        clock = Ctx is null ? null : _clock.View(Ctx.Clock.UtcNow),
        result = _over ? new { winner = _winner, reason = _reason, b = _core.Count(0), w = _core.Count(1) } : null,
        series = Ctx is null ? null : _series.View(Ctx, 2),
    };

    // ---------- збереження: гра не Persistent, це для тестів (позиція тим самим шляхом, що в шашках) ----------

    public sealed record Position(string Board, int Side, int Moves = 0);

    public override string? Save() => JsonSerializer.Serialize(new Position(_core.BoardString(), _core.Side, _moves));

    public override void Load(string json)
    {
        var s = JsonSerializer.Deserialize<Position>(json) ?? throw new GameError("Кривий знімок реверсі");
        _core = ReversiCore.FromString(s.Board, s.Side) ?? throw new GameError("Кривий знімок реверсі");
        _moves = s.Moves;
        _last = null;
        _flipped = 0;
        _pass = null;
        _over = false;
        _winner = null;
        _reason = null;
    }
}
