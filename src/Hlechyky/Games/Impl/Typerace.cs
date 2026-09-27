using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Клавоперегони — стіл на 2–10: усі друкують той самий уривок класики (або низку прислів'їв чи скоромовок), і
/// трактор кожного їде своєю доріжкою рівно на стільки, скільки надруковано. Помилка висить червоним, доки не
/// зітреш. Місця — за порядком фінішу на сервері; після першого зарахованого фінішу решта має хвіст 30–90 с.
/// Spec: docs/games/specs/typerace.md.
/// </summary>
public sealed class Typerace : TyperaceRace
{
    public const int MaxSeats = 10;
    /// <summary>
    /// Хвіст після першого зарахованого фінішу: стільки ж, скільки їхав переможець, але не менше 45 с і не більше
    /// 2 хв. Перший варіант (0,75 × і 30 с) лишав друга з телефона (120–150 зн/хв проти 350 на ПК) «не дописав» раз у раз.
    /// </summary>
    public const int TailMinMs = 45_000, TailMaxMs = 120_000;
    public const double TailShare = 1.0;
    /// <summary>
    /// Дотяжка: хвіст скінчився, а хтось за столом уже на <see cref="NearPercent"/> % тексту й друкував за останні
    /// <see cref="MovingMs"/> мс — даємо ще <see cref="ExtraMs"/> (скільки завгодно разів, але не далі стелі партії).
    /// </summary>
    public const int ExtraMs = 10_000, NearPercent = 85, MovingMs = 3_000;
    /// <summary>
    /// Тиша за столом: від старту ніхто не натиснув жодної клавіші. 15 с — щоб такий «заїзд» закрився раніше за
    /// 20 с від «Почати» (Economy.MinRewardSeconds) і не платив нічийних черепків, яких більше, ніж за чесний програш.
    /// </summary>
    public const int IdleMs = 15_000;
    /// <summary>Реакції з фінішу: 📯 👏 🔥 🐌 — не частіше за раз на <see cref="CheerEveryMs"/> мс від гонщика.</summary>
    public const int CheerKinds = 4, CheerEveryMs = 700;
    public const string ActCheer = "cheer";
    public const string NoTexts = "Тексти кудись подівались — Клавоперегони відпочивають";

    public override GameInfo Info { get; } = new(
        "typerace", "Клавоперегони", "клавоперегони", GameGroup.Live, 2, MaxSeats,
        TickMs: TickMs, Start: StartMode.ByHost, Score: ScoreOrder.HigherIsBetter,
        Options: TyperaceOptions.All,
        Hint: "Усі друкують той самий уривок класики — чий трактор перший доїде до прапорця. Помилку виправ, доки не поїдеш далі. На Steam Deck незручно: краще з клавіатури");

    /// <summary>Раунд, у якому був останній Start(): каркас відкриває дограний стіл наново з Round+1 без Start().</summary>
    int _round = -1;

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        TakeBank();
        if (Bank.Empty) throw new GameError(NoTexts);
        Length = OptionOr(options, "length", TyperaceBank.Lengths, TyperaceBank.Medium);
        Source = OptionOr(options, "source", TyperaceBank.Sources, TyperaceBank.All);
    }

    protected override bool LobbyNow => Phase == PhaseLobby || (Phase == PhaseDone && Ctx.Round != _round);

    public override void Start()
    {
        _round = Ctx.Round;
        Stretches = 0;
        _pendingN = _cheerN = 0;
        if (!BeginReady()) throw new GameError(NoTexts);
        ViewDirty = false;   // вид після Start() розсилає сам каркас; тику дублювати нема чого
    }

    protected override void OnVerified(Racer r, DateTimeOffset now)
    {
        AwardAchievements(r);
        if (TailSet) return;
        // перший зарахований фініш ставить хвіст: 0,75 часу переможця, але не менше 30 с і не більше 90 с
        TailSet = true;
        var tail = Math.Clamp((long)Math.Round(TailShare * r.Fin!.Value), TailMinMs, TailMaxMs);
        var until = now.AddMilliseconds(tail);
        if (EndsAt is null || until < EndsAt) EndsAt = until;
    }

    protected override void OnRaceTick(DateTimeOffset now)
    {
        // реакції, що прийшли між тиками, — у кадр цього тику (і лише цього)
        _cheerN = _pendingN;
        if (_pendingN > 0) { Array.Copy(_pending, _cheers, 2 * _pendingN); _pendingN = 0; FrameDirty = true; }

        if (AllFinished())
        {
            End(now);
            return;
        }
        if (EndsAt is { } end && now >= end)
        {
            if (TailSet && Stretch(now)) return;
            End(now);
            return;
        }
        if (!Moved && GoAt is { } g && (now - g).TotalMilliseconds >= IdleMs)
            Idle(now);
    }

    /// <summary>
    /// Дотяжка хвоста: хтось, хто ще сидить, уже біля фінішу й досі друкує — ще <see cref="ExtraMs"/>, але не далі
    /// стелі. true — заїзд триває.
    /// </summary>
    bool Stretch(DateTimeOffset now)
    {
        var cap = GoAt!.Value.AddMilliseconds(HardCapMs(Len));
        if (now >= cap) return false;
        foreach (var r in Racers)
        {
            if (!r.In || r.Gone || r.Fin is not null || !Ctx.Seated(r.Seat)) continue;
            if ((long)r.C * 100 < (long)NearPercent * Len || (now - r.LastMove).TotalMilliseconds > MovingMs) continue;
            var until = now.AddMilliseconds(ExtraMs);
            EndsAt = until < cap ? until : cap;
            Stretches++;
            ViewDirty = true;
            return true;
        }
        return false;
    }

    /// <summary>Скільки разів хвіст дотягували (у вид: клієнт каже «ще 10 с — хтось біля фінішу»).</summary>
    int Stretches;

    // ---------------------------------------------------------------------------------------------
    // Реакції з фінішу: хто вже доїхав, гуде тим, хто ще їде. Сервер лише пересилає їх у кадрі (h: [місце, вид, …]).
    // ---------------------------------------------------------------------------------------------

    readonly int[] _pending = new int[2 * MaxSeats * 2], _cheers = new int[2 * MaxSeats * 2];
    int _pendingN, _cheerN;

    public override ActResult Act(int seat, string action, JsonElement payload) =>
        action == ActCheer ? Cheer(seat, payload) : base.Act(seat, action, payload);

    ActResult Cheer(int seat, JsonElement payload)
    {
        if (Phase != PhaseGo) return ActResult.Fail("Гудіти можна, поки їдуть");
        if (seat < 0 || seat >= Racers.Length) return ActResult.Fail("Ти в цих перегонах не їдеш");
        var r = Racers[seat];
        if (!r.In || r.Gone) return ActResult.Fail("Ти в цих перегонах не їдеш");
        if (r.Fin is null) return ActResult.Fail("Спершу доїдь — тоді й гуди");
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("r", out var el)
            || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var kind) || kind < 0 || kind >= CheerKinds)
            return ActResult.Fail("Не зрозумів, як гудіти");
        var now = Ctx.Clock.UtcNow;
        if ((now - r.LastCheer).TotalMilliseconds < CheerEveryMs || _pendingN * 2 >= _pending.Length) return ActResult.Done;
        r.LastCheer = now;
        _pending[2 * _pendingN] = seat;
        _pending[2 * _pendingN + 1] = kind;
        _pendingN++;
        return ActResult.Done;
    }

    /// <summary>Кадр: положення всіх і — якщо цього тику хтось гудів — пари «місце, реакція».</summary>
    public override object? Frame()
    {
        if (_cheerN == 0) return base.Frame();
        var p = Positions();
        var h = new int[2 * _cheerN];
        Array.Copy(_cheers, h, h.Length);
        return new { t = TickNo, p, h };
    }

    /// <summary>Заїзд скінчився (усі доїхали, хвіст чи стеля): підсумок, Журнал, каркасу — кінець партії.</summary>
    void End(DateTimeOffset now)
    {
        FillUnfinished(now);
        Phase = PhaseDone;
        var order = Standings();

        // переможець — перший зарахований серед тих, хто ще сидить (хто встав, перемогти не може)
        Racer? winner = null;
        foreach (var seat in order)
        {
            var r = Racers[seat];
            if (r.Place is null) break;
            if (!r.Gone && Ctx.Seated(seat)) { winner = r; break; }
        }
        var allDone = true;
        var others = 0;
        foreach (var r in Racers)
        {
            if (!r.In) continue;
            if (r.Fin is null) allDone = false;          // хто встав недописавши — теж «не всі»
            if (winner is not null && r != winner && !r.Gone) others++;
        }
        string say;
        if (winner is not null)
            say = TyperaceLines.ForWinner(Ctx.Rng, winner.Nick ?? "", winner.Cpm ?? 0, winner.Wrong == 0, alone: others == 0 && CountIn() > 1, allDone);
        else say = AnyFinished() ? TyperaceLines.Robots : TyperaceLines.ForNobody(Ctx.Rng);

        var winners = winner is null ? Array.Empty<int>() : [winner.Seat];
        var misses = new List<bool[]>();
        foreach (var r in Racers) if (r.In && r.Missed is not null) misses.Add(r.Missed);
        Result = new Summary(order, winners, say, allDone, TyperaceText.Trap(Text ?? "", misses));

        var scores = new Dictionary<int, long>();
        foreach (var r in Racers)
            if (r.In && r.Place is not null && !r.Gone && Ctx.Seated(r.Seat)) scores[r.Seat] = r.Cpm ?? 0;

        Ctx.Finish(winners, Journal(order), scores.Count > 0 ? scores : null);
        ViewDirty = false;   // Finish сам розсилає вид
    }

    int CountIn()
    {
        var n = 0;
        foreach (var r in Racers) if (r.In) n++;
        return n;
    }

    bool AnyFinished()
    {
        foreach (var r in Racers) if (r.In && r.Fin is not null) return true;
        return false;
    }

    /// <summary>Хвилина від старту, а ніхто й літери не натиснув — нічия, стіл закривається.</summary>
    void Idle(DateTimeOffset now)
    {
        FillUnfinished(now);
        Phase = PhaseDone;
        Result = new Summary(Standings(), [], TyperaceLines.Idle, false);
        Ctx.Finish([], "Клавоперегони: ніхто й пальцем не ворухнув");
        ViewDirty = false;
    }

    /// <summary>
    /// Рядок Журналу в порядку підсумку: «Клавоперегони: Оля 312 зн/хв, Петро 287, Ганна 190 (лише 63 %), Іван 🤖
    /// (не зараховано)»; без жодного фінішу — «Клавоперегони: ніхто не дописав — …».
    /// </summary>
    string Journal(int[] order)
    {
        var sb = new StringBuilder("Клавоперегони: ");
        if (!AnyFinished()) sb.Append("ніхто не дописав — ");
        var first = true;
        foreach (var seat in order)
        {
            var r = Racers[seat];
            if (!first) sb.Append(", ");
            sb.Append(r.Nick ?? SeatName(seat));
            if (r.Fin is not null && r.Flag is not null) sb.Append(" 🤖 (не зараховано)");
            else
            {
                sb.Append(' ').Append((r.Cpm ?? 0).ToString(CultureInfo.InvariantCulture));
                if (first) sb.Append(" зн/хв");
                if (r.Fin is null) sb.Append(" (лише ").Append(Percent(r)).Append(" %)");
            }
            if (r.Gone) sb.Append(" 🚪");
            first = false;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Утікач лишається в підсумку сірим трактором (зі своїм фінішем, якщо встиг), але перемогти вже не може.
    /// Нікого не лишилось — партію закриваємо самі, щоб у Журналі був рядок.
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (seat >= 0 && seat < Racers.Length && Racers[seat].In) Racers[seat].Gone = true;
        var anyone = false;
        for (var i = 0; i < Seats; i++)
            if (i != seat && Ctx.Seated(i)) { anyone = true; break; }
        if (!anyone)
        {
            Phase = PhaseDone;
            Result = new Summary(Standings(), [], "Усі встали з-за столу. Трактори стоять, Глек їх постереже.", false);
            Ctx.Finish([], "Клавоперегони: усі встали з-за столу, заїзд не дограли");
            return;
        }
        FrameDirty = true;
        ViewDirty = true;
    }

    public override object View(int? seat)
    {
        var v = BaseView(PhaseLobby);
        v["extra"] = LobbyNow ? 0 : Stretches;
        return v;
    }
}

/// <summary>Опції обох ігор (стіл і тренування мають однаковий паспорт опцій).</summary>
public static class TyperaceOptions
{
    public static readonly IReadOnlyList<GameOption> All =
    [
        new GameOption("length", "Довжина",
            [(TyperaceBank.Short, "Коротко (~150 знаків)"), (TyperaceBank.Medium, "Середньо (~300)"), (TyperaceBank.Long, "Довго (~600)")],
            TyperaceBank.Medium),
        new GameOption("source", "Тексти",
            [(TyperaceBank.All, "Усе разом"), (TyperaceBank.Classic, "Класика"), (TyperaceBank.Proverbs, "Прислів’я"), (TyperaceBank.Twisters, "Скоромовки")],
            TyperaceBank.All),
    ];
}
