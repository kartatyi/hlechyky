using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Реверсі з Глеком»: соло, навпроти — Дядько Глек 🤖 (<see cref="ReversiEngine"/>). Без рейтингу й черепків.
/// Місце 0 — людина, 1 — Глек (його місця в кімнаті нема, це позначка для виду). Колір людини — у <c>me</c>.
/// Налаштування — дією <c>set {level, color}</c>: будь-яка зміна посеред партії починає нову, «Ще раз» лишає обране.
/// Глек ходить у тику після паузи (як <see cref="ChessGlek"/>).
/// </summary>
public sealed class ReversiGlek : Game
{
    public const int TickMillis = 100;
    public const int ThinkMs = 600;
    /// <summary>Репліки Глека — не частіше ніж раз на стільки ходів.</summary>
    public const int QuipEvery = 6;

    public override GameInfo Info { get; } = new(
        "reversi-glek", "Реверсі з Глеком", "реверсі з Глеком", GameGroup.Solo, 1, 1,
        TickMs: TickMillis, Start: StartMode.Immediate, Private: true,
        Hint: "Нема з ким? Навпроти сяде Дядько Глек 🤖 — легкий, звичайний чи сильний. Без рейтингу й черепків",
        Client: "reversi");

    ReversiEngine.Level _level = ReversiEngine.Easy;
    /// <summary>"black", "white" або "turn" (по черзі: непарна партія — чорні).</summary>
    string _color = "turn";
    int _humanSide = ReversiCore.Black;
    ReversiCore _core = ReversiCore.Start();
    int? _last;
    ulong _flipped;
    int? _pass;
    int _moves;
    (int? Winner, string Reason)? _result;
    DateTimeOffset? _botAt;
    bool _dirty;
    int _games;
    int _quipAt = -QuipEvery;

    public override string SeatName(int seat) => seat == 0 ? "ти" : "🤖 Дядько Глек";

    /// <summary>Місце, чия черга: 0 — людина, 1 — Глек.</summary>
    int Turn => _core.Side == _humanSide ? 0 : 1;

    public override void Start()
    {
        _games++;
        NewGame();
    }

    void NewGame()
    {
        _humanSide = _color switch { "black" => ReversiCore.Black, "white" => ReversiCore.White, _ => _games % 2 == 1 ? ReversiCore.Black : ReversiCore.White };
        _core = ReversiCore.Start();
        _last = null;
        _flipped = 0;
        _pass = null;
        _moves = 0;
        _result = null;
        _botAt = null;
        _quipAt = -QuipEvery;
        _dirty = true;
    }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action == "set") return Set(payload);
        if (_result is not null) return ActResult.Fail("Партію зіграно, тисни «Ану ще раз»");
        switch (action)
        {
            case "resign":
                Finish(1, "resign");
                return ActResult.Accept("Глек потискає руку — ану ще раз?");
            case "move":
                if (Turn != 0) return ActResult.Fail("Глек думає — зачекай хвильку");
                var cell = Reversi.ReadCell(payload);
                if (Reversi.Illegal(_core, cell) is { } why) return ActResult.Fail(why);
                Play(cell!.Value);
                return ActResult.Done;
            default:
                return ActResult.Fail("Тут так не ходять");
        }
    }

    ActResult Set(JsonElement payload)
    {
        string? Str(string name) => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var level = ReversiEngine.LevelOf(Str("level"));
        var color = Str("color") is "black" or "white" or "turn" ? Str("color") : null;
        if (level is null && color is null) return ActResult.Fail("Не зрозумів налаштування");
        if (level is not null) _level = level;
        if (color is not null) _color = color;
        if (_result is not null) return ActResult.Accept("Запам'ятав — так і зіграємо наступну партію");
        NewGame();
        return ActResult.Accept(_humanSide == ReversiCore.Black ? "Нова партія — ти чорними, твій хід" : "Нова партія — ти білими, Глек починає");
    }

    void Play(int cell)
    {
        var seat = Turn;
        var tookCorner = ReversiEngine.IsCorner(cell);
        _flipped = _core.Play(cell);
        _last = cell;
        _moves++;
        _pass = null;
        _botAt = null;
        _dirty = true;

        if (_core.Over) { Counted(); return; }
        if (tookCorner) Quip(seat == 1 ? ["Кут мій!", "О, кутик — дякую", "Цей кут я давно пас"] : ["Ой, віддав кут…", "Ех, кут проґавив", "Гарний кут, нічого не скажеш"]);
        if (_core.PassIfStuck())
        {
            var stuck = 1 - seat;
            _pass = stuck;
            Ctx.Log(stuck == 1 ? "🤖 Глек пасує" : $"{Reversi.Emoji(_humanSide)} ти пасуєш — ходити нікуди");
        }
    }

    /// <summary>Коротка репліка Глека в Журнал — не частіше ніж раз на <see cref="QuipEvery"/> ходів.</summary>
    void Quip(string[] lines)
    {
        if (_moves - _quipAt < QuipEvery) return;
        _quipAt = _moves;
        Ctx.Log($"🤖 {lines[Ctx.Rng.Next(lines.Length)]}");
    }

    void Counted()
    {
        int mine = _core.Count(_humanSide), his = _core.Count(1 - _humanSide);
        var reason = mine == 0 || his == 0 ? "wipe" : "count";
        int? winner = mine > his ? 0 : mine < his ? 1 : null;
        if (winner == 1 && his - mine >= 40) Ctx.Log("🤖 Та це розгром");
        else if (winner == 0 && mine - his >= 40) Ctx.Log("🤖 Оце так рознесли Глека…");
        Finish(winner, reason);
    }

    void Finish(int? winner, string reason)
    {
        _result = (winner, reason);
        _dirty = true;
        var me = Ctx.NickOf(0) ?? "Гравець";
        int b = _core.Count(0), w = _core.Count(1);
        var glek = $"Глек ({_level.Name})";
        var (black, white) = _humanSide == ReversiCore.Black ? (me, glek) : (glek, me);
        var score = $"{black} ⚫ {b} : {w} ⚪ {white}";
        var text = reason == "resign"
            ? $"{Info.Title}: {me} здається — {glek} бере партію"
            : $"{Info.Title}: {score}{(winner is null ? " — нічия" : "")}{(reason == "wipe" ? " — дошку витерто" : "")}";
        Ctx.Finish(winner is { } x ? [x] : [], text);
    }

    public override TickResult Tick()
    {
        if (_result is null && Turn == 1)
        {
            var now = Ctx.Clock.UtcNow;
            if (_botAt is null) _botAt = now.AddMilliseconds(ThinkMs + Ctx.Rng.Next(400));
            else if (now >= _botAt && ReversiEngine.Pick(_core, _level, Ctx.Rng) is var cell and >= 0) Play(cell);
        }
        if (!_dirty) return TickResult.None;
        _dirty = false;
        return TickResult.Both;
    }

    public override object View(int? seat) => new
    {
        board = _core.BoardString(),
        turn = _result is null ? (int?)Turn : null,
        toMove = _result is null ? _core.Side == 0 ? "b" : "w" : null,
        me = _humanSide == ReversiCore.Black ? "b" : "w",
        legal = _result is null && Turn == 0 ? _core.Legal(_core.Side) : [],
        last = _last,
        flipped = ReversiCore.Cells(_flipped).ToArray(),
        pass = _pass,
        count = new { b = _core.Count(0), w = _core.Count(1) },
        moves = _moves,
        clock = (object?)null,
        result = _result is { } r ? new { winner = r.Winner, reason = r.Reason, b = _core.Count(0), w = _core.Count(1) } : null,
        glek = new { level = _level.Key, color = _color, thinking = _result is null && Turn == 1 },
    };

    // ---------- для тестів: позиція тим самим шляхом, що в Reversi ----------

    public override string? Save() => JsonSerializer.Serialize(new Reversi.Position(_core.BoardString(), _core.Side, _moves));

    public override void Load(string json)
    {
        var s = JsonSerializer.Deserialize<Reversi.Position>(json) ?? throw new GameError("Кривий знімок реверсі");
        _core = ReversiCore.FromString(s.Board, s.Side) ?? throw new GameError("Кривий знімок реверсі");
        _moves = s.Moves;
        _last = null;
        _flipped = 0;
        _pass = null;
        _result = null;
        _botAt = null;
    }
}
