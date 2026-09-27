using System.Globalization;
using System.Text;

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
    public const int TailMinMs = 30_000, TailMaxMs = 90_000;
    public const double TailShare = 0.75;
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
        if (AllFinished() || (EndsAt is { } end && now >= end))
        {
            End(now);
            return;
        }
        if (!Moved && GoAt is { } g && (now - g).TotalMilliseconds >= IdleMs)
            Idle(now);
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
            if (r.Fin is null && !r.Gone) allDone = false;
            if (winner is not null && r != winner && !r.Gone) others++;
        }
        string say;
        if (winner is not null)
            say = TyperaceLines.ForWinner(Ctx.Rng, winner.Nick ?? "", winner.Cpm ?? 0, winner.Wrong == 0, alone: others == 0 && CountIn() > 1, allDone);
        else say = AnyFinished() ? TyperaceLines.Robots : TyperaceLines.ForNobody(Ctx.Rng);

        var winners = winner is null ? Array.Empty<int>() : [winner.Seat];
        Result = new Summary(order, winners, say, allDone);

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

    public override object View(int? seat) => BaseView(PhaseLobby);
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
