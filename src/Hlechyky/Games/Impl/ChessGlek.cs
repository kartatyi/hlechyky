using System.Text;
using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Шахи з Глеком»: коли суперника нема, навпроти сідає Дядько Глек 🤖 (<see cref="ChessEngine"/>). Соло-кімната,
/// тож ні рейтингу, ні черепків (Rewards не рахує партій, де за столом менше двох людей) — чиста розминка.
///
/// Налаштування (рівень, колір, варіант) — не опціями столу (соло відкривається без лобі), а дією <c>set</c>
/// просто за дошкою: будь-яка зміна починає нову партію. «Ще раз» лишає обране.
///
/// Глек «думає» у тику: спершу пауза ThinkMs (щоб людина встигла побачити свій хід), далі сам розрахунок —
/// бюджет вузлів тримає його в межах ~100 мс навіть під замком кімнати, а кімната соло, тож чекає лише той, хто грає.
/// </summary>
public sealed class ChessGlek : Game
{
    public const int TickMillis = 100;
    /// <summary>Скільки Глек «думає», перш ніж походити, — мс (плюс трохи випадково).</summary>
    public const int ThinkMs = 600;

    public override GameInfo Info { get; } = new(
        "chess-glek", "Шахи з Глеком", "шахи з Глеком", GameGroup.Solo, 1, 1,
        TickMs: TickMillis, Start: StartMode.Immediate, Private: true,
        Hint: "Нема з ким зіграти? Навпроти сяде Дядько Глек 🤖 — легкий або середній. Без рейтингу й черепків, для розминки",
        Client: "chess");

    ChessVariant _variant = ChessVariant.Classic;
    ChessEngine.Level _level = ChessEngine.Easy;
    /// <summary>Колір людини: "white", "black" або "turn" (по черзі: непарна партія — білі).</summary>
    string _color = "turn";
    bool _humanWhite = true;
    ChessCore _core = ChessCore.Initial();
    readonly List<string> _sans = [];
    readonly StringBuilder _lostWhite = new(), _lostBlack = new();
    Dictionary<string, int> _seen = new(StringComparer.Ordinal);
    (int From, int To)? _last;
    (int? Winner, string Reason)? _result;
    DateTimeOffset? _botAt;
    bool _dirty;
    int _games;

    public override string SeatName(int seat) => seat == 0 ? "ти" : "🤖 Дядько Глек";

    /// <summary>Місце, чия черга: 0 — людина, 1 — Глек (його місця в кімнаті нема, це лише позначка для виду).</summary>
    int Turn => _core.WhiteToMove == _humanWhite ? 0 : 1;

    public override void Start()
    {
        _games++;
        NewGame();
    }

    void NewGame()
    {
        _humanWhite = _color switch { "white" => true, "black" => false, _ => _games % 2 == 1 };
        _core = _variant == ChessVariant.Fischer ? ChessCore.Fischer(Ctx.Rng) : ChessCore.Initial(_variant);
        _sans.Clear();
        _lostWhite.Clear();
        _lostBlack.Clear();
        _seen = new Dictionary<string, int>(StringComparer.Ordinal) { [_core.PositionKey()] = 1 };
        _last = null;
        _result = null;
        _botAt = null;
        _dirty = true;
    }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action == "set") return Set(payload);
        if (_result is not null) return ActResult.Fail("Партію зіграно, тисни «Ану ще раз»");
        switch (action)
        {
            case "resign":
                Finish(1, "resign", "здається");
                return ActResult.Accept("Глек потискає руку — ану ще раз?");
            case "move":
                if (Turn != 0) return ActResult.Fail("Глек думає — зачекай хвильку");
                var legal = _core.Legal();
                if (Chess.Resolve(payload, legal) is not { } move)
                    return ActResult.Fail(_variant == ChessVariant.Anti && legal.Any(_core.IsCapture) ? "Тут взяття обов'язкове" : "Так не ходять");
                Play(move, legal);
                return ActResult.Done;
            default:
                return ActResult.Fail("Тут так не ходять");
        }
    }

    /// <summary>Змінити рівень, колір чи варіант — і почати нову партію з цим.</summary>
    ActResult Set(JsonElement payload)
    {
        string? Str(string name) => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (Str("level") is { } lv) _level = ChessEngine.LevelOf(lv);
        if (Str("color") is "white" or "black" or "turn") _color = Str("color")!;
        if (Str("variant") is { } vr) _variant = vr switch { "960" => ChessVariant.Fischer, "anti" => ChessVariant.Anti, _ => ChessVariant.Classic };
        if (_result is not null) return ActResult.Accept("Запам'ятав — так і зіграємо наступну партію");
        NewGame();
        return ActResult.Accept(_humanWhite ? "Нова партія — твої білі" : "Нова партія — ти чорними, Глек починає");
    }

    void Play(ChessMove move, List<ChessMove> legal)
    {
        var seat = Turn;
        var san = _core.San(move, legal);
        var captured = _core.CapturedPiece(move);
        var moveNo = _core.Fullmove;
        _core.Make(move, out _);
        if (captured != 0) (ChessCore.White(captured) ? _lostWhite : _lostBlack).Append(ChessCore.Letter(captured));
        _last = (move.From, move.To);
        var next = _core.Legal();
        if (_variant != ChessVariant.Anti)
        {
            var check = _core.InCheck();
            san += next.Count == 0 && check ? "#" : check ? "+" : "";
        }
        _sans.Add(san);
        var key = _core.PositionKey();
        _seen[key] = _seen.GetValueOrDefault(key) + 1;
        _dirty = true;
        _botAt = null;

        if (_variant == ChessVariant.Anti)
        {
            if (next.Count == 0) Finish(Turn, "anti-nomoves", "віддав усе");
        }
        else if (next.Count == 0)
        {
            if (_core.InCheck()) Finish(seat, "mate", $"мат на {moveNo}-му ході");
            else Finish(null, "stalemate", "пат");
        }
        else if (_core.InsufficientMaterial()) Finish(null, "material", "матувати нічим");
        if (_result is null && _core.Halfmove >= 100) Finish(null, "fifty", "50 ходів без взяття");
        if (_result is null && _seen[key] >= 3) Finish(null, "repetition", "тричі та сама позиція");
    }

    void Finish(int? winner, string reason, string why)
    {
        _result = (winner, reason);
        _dirty = true;
        var me = Ctx.NickOf(0) ?? "Гравець";
        var lvl = _level == ChessEngine.Medium ? "середній" : "легкий";
        var text = winner switch
        {
            0 => $"{Info.Title}: {me} обіграв Глека ({lvl}; {why})",
            1 when reason == "resign" => $"{Info.Title}: {me} здається — Глек ({lvl}) бере партію",
            1 => $"{Info.Title}: Глек ({lvl}) обіграв {me} ({why})",
            _ => $"{Info.Title}: {me} і Глек ({lvl}) — нічия ({why})",
        };
        Ctx.Finish(winner is { } w ? [w] : [], text);
    }

    public override TickResult Tick()
    {
        if (_result is null && Turn == 1)
        {
            var now = Ctx.Clock.UtcNow;
            if (_botAt is null) _botAt = now.AddMilliseconds(ThinkMs + Ctx.Rng.Next(400));
            else if (now >= _botAt)
            {
                var legal = _core.Legal();
                if (ChessEngine.Pick(_core, _level, Ctx.Rng) is { } move) Play(move, legal);
            }
        }
        if (!_dirty) return TickResult.None;
        _dirty = false;
        return TickResult.Both;
    }

    public override object View(int? seat) => new
    {
        variant = _variant switch { ChessVariant.Fischer => "960", ChessVariant.Anti => "anti", _ => "classic" },
        board = _core.BoardString(),
        fen = _core.Fen(),
        turn = Turn,
        toMove = _core.WhiteToMove ? "w" : "b",
        me = _humanWhite ? "w" : "b",
        legal = _result is null && Turn == 0 ? Chess.WireOf(_core) : [],
        lastMove = _last is { } l ? (object?)new { from = ChessCore.Name(l.From), to = ChessCore.Name(l.To) } : null,
        check = _core.InCheck(),
        captured = new { w = _lostBlack.ToString(), b = _lostWhite.ToString() },
        moves = _sans.ToArray(),
        halfmove = _core.Halfmove,
        fullmove = _core.Fullmove,
        drawOffer = (int?)null,
        result = _result is { } r ? (object?)new { winner = r.Winner, reason = r.Reason } : null,
        glek = new { level = _level.Key, color = _color, thinking = _result is null && Turn == 1 },
    };
}
