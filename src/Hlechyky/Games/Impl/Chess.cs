using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Шахи на трьох варіантах: класика, шахи Фішера (960) і піддавки. Правила — у <see cref="ChessCore"/>,
/// тут лише партія: чия черга, що показати гравцям, коли писати в Журнал і кликати Finish.
///
/// Місце 0 — завжди білі. Кольори чергуються не всередині гри, а «Ще разом»: каркас обертає місця, тож
/// хто грав білими, наступну партію грає чорними.
/// </summary>
public sealed class Chess : Game
{
    /// <summary>Причина завершення в тому вигляді, у якому її читає клієнт (spec, поле result.reason).</summary>
    sealed record Outcome(int? Winner, string Reason);

    /// <summary>Рядок легального ходу для браузера. Рокіровка приходить двома записами — див. <see cref="Wire"/>.</summary>
    sealed record LegalMove(string From, string To, string? Promo, string? Castle);

    public override GameInfo Info { get; } = new(
        "chess", "Шахи", "шахи", GameGroup.Board, 2, 2, Rated: true,
        Options: [new GameOption("variant", "Варіант",
            [("classic", "Класика"), ("960", "Шахи Фішера"), ("anti", "Піддавки")], "classic")],
        Hint: "Класика, шахи Фішера (фігури на першій лінії перетасовано) або піддавки (хто позбувся всіх фігур — виграв)");

    ChessVariant _variant = ChessVariant.Classic;
    ChessCore _core = ChessCore.Initial();
    /// <summary>Ходи людським записом — для списку під дошкою.</summary>
    readonly List<string> _sans = [];
    /// <summary>Збиті фігури: окремо білі, окремо чорні. Літери, як у FEN.</summary>
    readonly StringBuilder _lostWhite = new(), _lostBlack = new();
    /// <summary>Скільки разів уже була ця позиція: тричі — нічия. Ключ — FEN без лічильників.</summary>
    Dictionary<string, int> _seen = new(StringComparer.Ordinal);
    (int From, int To)? _last;
    int? _drawOffer;
    Outcome? _result;

    public override string SeatName(int seat) => seat == 0 ? "білі" : "чорні";

    /// <summary>Місце того, чия зараз черга: білі сидять на нулі завжди.</summary>
    int Turn => _core.WhiteToMove ? 0 : 1;

    static int Other(int seat) => seat == 0 ? 1 : 0;

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _variant = options.TryGetValue("variant", out var v) ? v switch
        {
            "960" => ChessVariant.Fischer,
            "anti" => ChessVariant.Anti,
            _ => ChessVariant.Classic,
        } : ChessVariant.Classic;
        // Дошку ставимо вже тут: кімната показує вид ще в лобі, до першого Start(), і порожнеча
        // замість фігур виглядала б як зламана гра.
        NewGame();
    }

    public override void Start() => NewGame();

    void NewGame()
    {
        _core = _variant == ChessVariant.Fischer ? ChessCore.Fischer(Ctx.Rng) : ChessCore.Initial(_variant);
        _sans.Clear();
        _lostWhite.Clear();
        _lostBlack.Clear();
        _seen = new Dictionary<string, int>(StringComparer.Ordinal) { [_core.PositionKey()] = 1 };
        _last = null;
        _drawOffer = null;
        _result = null;
    }

    // ------------------------------------------------------------------------------------------
    // Ходи
    // ------------------------------------------------------------------------------------------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (_result is not null) return ActResult.Fail("Партію зіграно, тисни «Ще раз»");
        return action switch
        {
            "move" => Move(seat, payload),
            "resign" => Resign(seat),
            "draw" => Draw(seat),
            "decline" => Decline(seat),
            _ => ActResult.Fail("Тут так не ходять"),
        };
    }

    ActResult Move(int seat, JsonElement payload)
    {
        if (seat != Turn) return ActResult.Fail("Зараз не твій хід");
        var legal = _core.Legal();
        if (Resolve(payload, legal) is not { } move)
        {
            // У піддавках найчастіша причина відмови — не «так не ходять», а «є чим бити».
            if (_variant == ChessVariant.Anti && Wanted(payload) is var (from, to) && from >= 0
                && _core.PseudoLegal().Any(m => m.From == from && m.To == to))
                return ActResult.Fail("Тут взяття обов'язкове");
            return Wanted(payload).From < 0 ? ActResult.Fail("Не зрозумів, куди ходити") : ActResult.Fail("Так не ходять");
        }

        var san = _core.San(move, legal);
        var captured = _core.CapturedPiece(move);
        var moveNo = _core.Fullmove;
        _core.Make(move, out _);
        if (captured != 0) (ChessCore.White(captured) ? _lostWhite : _lostBlack).Append(ChessCore.Letter(captured));
        _last = (move.From, move.To);
        _drawOffer = null;

        var next = _core.Legal();
        if (_variant != ChessVariant.Anti)
        {
            var check = _core.InCheck();
            san += next.Count == 0 && check ? "#" : check ? "+" : "";
        }
        _sans.Add(san);
        var key = _core.PositionKey();
        _seen[key] = _seen.GetValueOrDefault(key) + 1;

        Judge(seat, next, moveNo, _seen[key]);
        return ActResult.Done;
    }

    /// <summary>Чим скінчився хід: мат, пат, нічия за матеріалом, 50 ходів, повторення чи перемога в піддавках.</summary>
    void Judge(int seat, List<ChessMove> next, int moveNo, int repeats)
    {
        var mover = seat;
        if (_variant == ChessVariant.Anti)
        {
            // Виграє той, кому нічим і нема чим ходити: у піддавках позбутись усього — і є мета.
            if (!HasPieces(_core.WhiteToMove)) { Win(Turn, "anti-nopieces", "у піддавках фігур не лишилось"); return; }
            if (next.Count == 0) { Win(Turn, "anti-nomoves", "у піддавках ходити нічим"); return; }
        }
        else
        {
            if (next.Count == 0)
            {
                if (_core.InCheck()) Win(mover, "mate", $"мат на {moveNo}-му ході");
                else Draw("stalemate", "пат");
                return;
            }
            if (_core.InsufficientMaterial()) { Draw("material", "матеріалу не стало"); return; }
        }
        if (_core.Halfmove >= 100) { Draw("fifty", "50 ходів без взяття й без пішаків"); return; }
        if (repeats >= 3) Draw("repetition", "тричі та сама позиція");
    }

    bool HasPieces(bool white)
    {
        for (var sq = 0; sq < 64; sq++)
        {
            var p = _core.PieceAt(sq);
            if (p != 0 && ChessCore.White(p) == white) return true;
        }
        return false;
    }

    void Win(int seat, string reason, string why)
    {
        _result = new Outcome(seat, reason);
        var lost = Other(seat);
        Ctx.Finish([seat], $"{Info.Title}: {Ctx.NickOf(seat)} {SeatName(seat)} 1:0 {Ctx.NickOf(lost)} {SeatName(lost)} ({why})");
    }

    void Draw(string reason, string why)
    {
        _result = new Outcome(null, reason);
        Ctx.Finish([], $"{Info.Title}: {Ctx.NickOf(0)} і {Ctx.NickOf(1)} зіграли внічию ({why})");
    }

    // ------------------------------------------------------------------------------------------
    // Здатись, нічия
    // ------------------------------------------------------------------------------------------

    ActResult Resign(int seat)
    {
        var winner = Other(seat);
        _result = new Outcome(winner, "resign");
        Ctx.Finish([winner], $"{Info.Title}: {Ctx.NickOf(seat)} здався, {Ctx.NickOf(winner)} 1:0 {Ctx.NickOf(seat)}");
        return ActResult.Accept("Здався");
    }

    ActResult Draw(int seat)
    {
        if (_drawOffer == Other(seat))
        {
            Draw("agreed", "за згодою");
            return ActResult.Accept("Нічия");
        }
        if (_drawOffer == seat) return ActResult.Fail("Ти вже пропонував нічию");
        _drawOffer = seat;
        return ActResult.Accept("Запропонував нічию");
    }

    ActResult Decline(int seat)
    {
        if (_drawOffer != Other(seat)) return ActResult.Fail("Нічиєї ніхто не пропонував");
        _drawOffer = null;
        return ActResult.Accept("Відхилив нічию");
    }

    public override void OnLeave(int seat)
    {
        var other = Other(seat);
        if (!Ctx.Seated(other))
        {
            _result = new Outcome(null, "left");
            Ctx.Finish([], $"{Info.Title}: {Ctx.NickOf(seat)} встав з-за столу, партію не дограли");
            return;
        }
        _result = new Outcome(other, "left");
        Ctx.Finish([other], $"{Info.Title}: {Ctx.NickOf(seat)} встав з-за столу, {Ctx.NickOf(other)} 1:0 {Ctx.NickOf(seat)}");
    }

    // ------------------------------------------------------------------------------------------
    // Розбір payload
    // ------------------------------------------------------------------------------------------

    static string? Str(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    /// <summary>Поля, які назвав гравець (-1 — не назвав або назвав дурницю).</summary>
    static (int From, int To) Wanted(JsonElement payload) =>
        (ChessCore.Parse(Str(payload, "from")), ChessCore.Parse(Str(payload, "to")));

    /// <summary>Куди в цьому ході стане король (рокіровку всередині записано як «король бере туру»).</summary>
    static int KingTarget(ChessMove m) =>
        ChessCore.Sq(ChessCore.File(m.To) > ChessCore.File(m.From) ? 6 : 2, ChessCore.Row(m.From));

    /// <summary>
    /// Який саме легальний хід мав на увазі гравець. Рокіровку приймаємо трьома способами: <c>castle: "K"|"Q"</c>,
    /// «король бере свою туру» (стандарт 960) і звичне «король на g1/c1». Якщо (from, to) — це водночас
    /// і простий хід короля, і рокіровка (у 960 буває), виграє простий хід: рокіровку тоді просять полем castle.
    /// </summary>
    ChessMove? Resolve(JsonElement payload, List<ChessMove> legal)
    {
        if (Str(payload, "castle") is { Length: > 0 } side)
        {
            var wantShort = char.ToLowerInvariant(side[0]) == 'k';
            foreach (var m in legal)
                if (m.Kind == ChessMoveKind.Castle && ChessCore.File(m.To) > ChessCore.File(m.From) == wantShort) return m;
            return null;
        }
        var (from, to) = Wanted(payload);
        if (from < 0 || to < 0) return null;

        var direct = legal.FindAll(m => m.From == from && m.To == to && m.Kind != ChessMoveKind.Castle);
        if (direct.Count == 1 && direct[0].Promo == 0) return direct[0];
        if (direct.Count > 0)
        {
            // Перетворення без вказівки — ферзь: так домовлено в spec, помилки тут нема.
            var want = Promo(Str(payload, "promo")) is var p && p != 0 ? p : ChessCore.Queen;
            foreach (var m in direct) if (m.Promo == want) return m;
            return direct[0];
        }
        foreach (var m in legal)
            if (m.Kind == ChessMoveKind.Castle && m.From == from && (m.To == to || KingTarget(m) == to)) return m;
        return null;
    }

    static sbyte Promo(string? s) => string.IsNullOrEmpty(s) ? (sbyte)0 : char.ToLowerInvariant(s[0]) switch
    {
        'q' => (sbyte)ChessCore.Queen, 'r' => (sbyte)ChessCore.Rook, 'b' => (sbyte)ChessCore.Bishop,
        'n' => (sbyte)ChessCore.Knight, 'k' => (sbyte)ChessCore.King, _ => (sbyte)0,
    };

    // ------------------------------------------------------------------------------------------
    // Вид
    // ------------------------------------------------------------------------------------------

    public override object View(int? seat) => new
    {
        variant = _variant switch { ChessVariant.Fischer => "960", ChessVariant.Anti => "anti", _ => "classic" },
        board = _core.BoardString(),
        fen = _core.Fen(),
        turn = Turn,
        toMove = _core.WhiteToMove ? "w" : "b",
        legal = Wire(),
        lastMove = _last is { } l ? (object?)new { from = ChessCore.Name(l.From), to = ChessCore.Name(l.To) } : null,
        // Шах лишається шахом і після кінця партії: саме на матовій позиції червоне поле короля
        // найпотрібніше — інакше мат виглядає як звичайний хід. Ходити все одно нема чим: legal порожній.
        check = _core.InCheck(),
        // «w» — те, що збили білі (а це чорні фігури), «b» — навпаки: рядок малюють біля того, хто взяв.
        captured = new { w = _lostBlack.ToString(), b = _lostWhite.ToString() },
        moves = _sans.ToArray(),
        halfmove = _core.Halfmove,
        fullmove = _core.Fullmove,
        drawOffer = _drawOffer,
        result = _result is { } r ? (object?)new { winner = r.Winner, reason = r.Reason } : null,
    };

    /// <summary>
    /// Легальні ходи для браузера. Рокіровку віддаємо двома записами — на поле тури (стандарт 960) і на
    /// звичне поле короля, — щоб клацнути можна було по обох; коли вони збігаються, запис один.
    /// </summary>
    LegalMove[] Wire()
    {
        if (_result is not null) return [];
        var moves = _core.Legal();
        var wire = new List<LegalMove>(moves.Count + 2);
        foreach (var m in moves)
        {
            if (m.Kind == ChessMoveKind.Castle)
            {
                var side = ChessCore.File(m.To) > ChessCore.File(m.From) ? "K" : "Q";
                var from = ChessCore.Name(m.From);
                wire.Add(new LegalMove(from, ChessCore.Name(m.To), null, side));
                var king = KingTarget(m);
                // Другий запис зайвий, коли поле те саме, коли король узагалі нікуди не їде (буває в 960)
                // або коли туди й так є простий хід короля — тоді (from, to) означало б два різні ходи.
                if (king != m.To && king != m.From
                    && !moves.Any(o => o.Kind != ChessMoveKind.Castle && o.From == m.From && o.To == king))
                    wire.Add(new LegalMove(from, ChessCore.Name(king), null, side));
                continue;
            }
            wire.Add(new LegalMove(ChessCore.Name(m.From), ChessCore.Name(m.To), PromoLetter(m.Promo), null));
        }
        return [.. wire];
    }

    static string? PromoLetter(sbyte promo) => promo == 0 ? null : " pnbrqk"[promo].ToString();

    // ------------------------------------------------------------------------------------------
    // Збереження (партія за столом його не потребує, але вид після Load має бути той самий)
    // ------------------------------------------------------------------------------------------

    sealed record Saved(string Variant, string Fen, string[] Moves, string LostWhite, string LostBlack,
        Dictionary<string, int> Seen, int LastFrom, int LastTo, int? DrawOffer, int? Winner, string? Reason);

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public override string? Save() => JsonSerializer.Serialize(new Saved(
        _variant.ToString(), _core.Fen(), [.. _sans], _lostWhite.ToString(), _lostBlack.ToString(),
        new Dictionary<string, int>(_seen, StringComparer.Ordinal),
        _last?.From ?? -1, _last?.To ?? -1, _drawOffer, _result?.Winner, _result?.Reason), Json);

    public override void Load(string json)
    {
        if (JsonSerializer.Deserialize<Saved>(json, Json) is not { } s) return;
        _variant = Enum.TryParse<ChessVariant>(s.Variant, out var v) ? v : ChessVariant.Classic;
        _core = ChessCore.FromFen(s.Fen, _variant);
        _sans.Clear();
        _sans.AddRange(s.Moves);
        _lostWhite.Clear().Append(s.LostWhite);
        _lostBlack.Clear().Append(s.LostBlack);
        _seen = new Dictionary<string, int>(s.Seen, StringComparer.Ordinal);
        _last = s.LastFrom < 0 ? null : (s.LastFrom, s.LastTo);
        _drawOffer = s.DrawOffer;
        _result = s.Reason is null ? null : new Outcome(s.Winner, s.Reason);
    }
}
