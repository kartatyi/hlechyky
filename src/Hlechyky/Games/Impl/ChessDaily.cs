using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «Шахова задача дня»: мат у 2–3 ходи, одна позиція на всіх на добу (<see cref="ChessPuzzles"/>). Суперника
/// грає сервер: після правильного ходу відповідає найупертішим захистом (<see cref="ChessEngine.Defend"/>).
/// Хибний хід — мінус спроба, дошка повертається на початок, годинник іде далі: табло дня — за спробами й часом,
/// як у Сапера дня (<see cref="DailyCard"/>).
///
/// Перевірка ходу дешева навіть під замком кімнати: перший хід звіряємо з ключем банку (його єдиність доведено
/// тестом), а далі лишається щонайбільше «чи будь-яка відповідь пропускає мат одним ходом» — сотні ходів, не мільйони.
/// </summary>
public sealed class ChessDaily : Game, IDailyGame
{
    public override GameInfo Info { get; } = new(
        "chess-daily", "Шахова задача дня", "шахову задачу дня", GameGroup.Solo, 1, 1,
        Start: StartMode.Immediate, Private: true, Persistent: true, Score: ScoreOrder.LowerIsBetter,
        Hint: "Мат у 2–3 ходи — одна задача на всіх на добу. Хто розв'яже швидше і з меншої кількості спроб?",
        Client: "chess");   // малює той самий web/games/chess.js

    string _day = "";
    ChessPuzzle _puzzle = ChessPuzzles.Bank[0];
    ChessCore _core = ChessCore.FromFen(ChessPuzzles.Bank[0].Fen);
    /// <summary>Скільки ходів нападу вже зроблено в цій спробі.</summary>
    int _step;
    readonly List<string> _sans = [];
    (int From, int To)? _last;
    DateTimeOffset _startedAt;
    int _attempts = 1;
    bool _solved, _gaveUp;
    long _ms;
    DailyCard? _card;

    public override string SeatName(int seat) => seat == 0 ? "ти" : "Глек";

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        // Табло дня живе в пам'яті сервісу; тут, поза замком, лише просимо дочитати день фоном.
        _card = Ctx.Services.GetService<DailyCard>();
        _card?.Warm(Info.Id);
    }

    public override string SoloKey(string nickKey, IClock clock) => $"daily:{Info.Id}:{Days.Today(clock)}:{nickKey}";

    public override void Start()
    {
        _day = Days.Today(Ctx.Clock);
        _puzzle = ChessPuzzles.ForDay(_day);
        _attempts = 1;
        _solved = _gaveUp = false;
        _ms = 0;
        _startedAt = Ctx.Clock.UtcNow;
        Reset();
    }

    void Reset()
    {
        _core = ChessCore.FromFen(_puzzle.Fen);
        _step = 0;
        _sans.Clear();
        _last = null;
    }

    bool Over => _solved || _gaveUp;

    /// <summary>Хід координатами (e2e4, e7e8q) — так записано ключ у банку.</summary>
    static string Uci(ChessMove m) => ChessCore.Name(m.From) + ChessCore.Name(m.To) + (Chess.PromoLetter(m.Promo) ?? "");

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (Over) return ActResult.Fail("Задачу дня вже закрито — нова завтра");
        if (action == "reveal") return GiveUp();
        if (action != "move") return ActResult.Fail("Тут так не ходять");

        var legal = _core.Legal();
        if (Chess.Resolve(payload, legal) is not { } move) return ActResult.Fail("Так не ходять");
        var remaining = _puzzle.N - _step;

        var san = _core.San(move, legal);
        if (_step == 0 && Uci(move) != _puzzle.Key) return Miss(san);
        _core.Make(move, out _);
        if (ChessEngine.IsMate(_core))
        {
            Record(move, san + "#");
            return Solved();
        }
        // Перший хід уже звірено з ключем (і доведено, що він веде до мату) — рахувати заново нема чого.
        if (_step > 0 && (remaining <= 1 || !ChessEngine.Defenceless(_core, remaining - 1))) return Miss(san);
        Record(move, san + (_core.InCheck() ? "+" : ""));
        _step++;

        // Відповідь захисту — одразу: задача не на швидкість суперника, а на твою.
        var replies = _core.Legal();
        if (ChessEngine.Defend(_core, Ctx.Rng) is { } reply)
        {
            var rsan = _core.San(reply, replies);
            _core.Make(reply, out _);
            Record(reply, rsan + (_core.InCheck() ? "+" : ""));
        }
        var left = _puzzle.N - _step;
        return ActResult.Accept(left == 1 ? "Так! Глек відповів — тепер мат одним ходом" : $"Так! Глек відповів — лишилось ходів: {left}");
    }

    void Record(ChessMove m, string san)
    {
        _sans.Add(san);
        _last = (m.From, m.To);
    }

    ActResult Miss(string san)
    {
        _attempts++;
        Reset();
        return ActResult.Accept($"{san} — ні, Глек викрутився. Дошка знову на початку, спроба {_attempts}");
    }

    ActResult Solved()
    {
        _solved = true;
        _ms = Math.Max(1000, (long)(Ctx.Clock.UtcNow - _startedAt).TotalMilliseconds);
        Ctx.Score(0, _ms, _attempts);
        Ctx.Award(0, 0, $"daily:{Info.Id}");
        _card?.Note(Info.Id, Ctx.NickOf(0) ?? "", _attempts, (int)Math.Min(int.MaxValue, _ms));
        Ctx.Finish([0], $"{Info.Title}: {Ctx.NickOf(0)} — мат за {Seconds(_ms)}" + (_attempts > 1 ? $" (спроба {_attempts})" : ""));
        return ActResult.Accept($"Мат! Задачу розв'язано за {Seconds(_ms)}");
    }

    ActResult GiveUp()
    {
        _gaveUp = true;
        Reset();
        Ctx.Finish([], $"{Info.Title}: {Ctx.NickOf(0)} підглянув розв'язок");
        return ActResult.Accept("Ключ — підсвічено на дошці. Завтра буде нова задача");
    }

    static string Seconds(long ms)
    {
        var s = (int)Math.Round(ms / 1000.0);
        return s < 60 ? $"{s} с" : $"{s / 60}:{s % 60:00}";
    }

    public override object View(int? seat)
    {
        var elapsed = _solved ? _ms : Math.Max(0, (long)(Ctx.Clock.UtcNow - _startedAt).TotalMilliseconds);
        var start = ChessCore.FromFen(_puzzle.Fen);
        var legal = start.Legal();
        var key = legal.FirstOrDefault(m => Uci(m) == _puzzle.Key);
        return new
        {
            variant = "classic",
            board = _core.BoardString(),
            fen = _core.Fen(),
            turn = Over ? (int?)null : 0,
            toMove = _core.WhiteToMove ? "w" : "b",
            me = _puzzle.WhiteToMove ? "w" : "b",
            legal = Over ? [] : Chess.WireOf(_core),
            lastMove = _last is { } l ? (object?)new { from = ChessCore.Name(l.From), to = ChessCore.Name(l.To) } : null,
            check = _core.InCheck(),
            captured = new { w = "", b = "" },
            moves = _sans.ToArray(),
            result = _solved ? new { winner = (int?)0, reason = "solved" } : _gaveUp ? new { winner = (int?)null, reason = "reveal" } : null,
            puzzle = new
            {
                n = _puzzle.N,
                no = ChessPuzzles.NumberOf(_puzzle),
                day = _day,
                attempts = _attempts,
                left = _puzzle.N - _step,
                startedAt = _startedAt,
                elapsedMs = elapsed,
                solved = _solved,
                gaveUp = _gaveUp,
                ms = _ms,
                // Розв'язок кажемо лише тоді, коли задачу закрито — розв'язав чи підглянув.
                key = Over ? new { from = ChessCore.Name(key.From), to = ChessCore.Name(key.To), san = start.San(key, legal) } : null,
            },
            daily = DailyCard.Wire(_card?.Get(Info.Id)),
            streak = _card is null || Ctx.NickOf(0) is not { } me ? 0 : _card.StreakOf(Info.Id, me),
        };
    }

    sealed record State(string Day, string Fen, int Step, string[] Moves, int LastFrom, int LastTo, DateTimeOffset StartedAt,
        int Attempts, bool Solved, bool GaveUp, long Ms);

    public override string? Save() => JsonSerializer.Serialize(new State(_day, _core.Fen(), _step, [.. _sans],
        _last?.From ?? -1, _last?.To ?? -1, _startedAt, _attempts, _solved, _gaveUp, _ms));

    public override void Load(string json)
    {
        if (JsonSerializer.Deserialize<State>(json) is not { } s) return;
        _day = s.Day;
        _puzzle = ChessPuzzles.ForDay(s.Day);
        _core = ChessCore.FromFen(s.Fen);
        _step = s.Step;
        _sans.Clear();
        _sans.AddRange(s.Moves);
        _last = s.LastFrom < 0 ? null : (s.LastFrom, s.LastTo);
        _startedAt = s.StartedAt;
        _attempts = s.Attempts;
        _solved = s.Solved;
        _gaveUp = s.GaveUp;
        _ms = s.Ms;
    }
}
