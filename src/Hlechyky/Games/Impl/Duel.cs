using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Фаза раунду дуелі. На дроті — рядок у camelCase: клієнт малює сцену саме за нею, тож назви тут і
/// в <c>web/games/duel.js</c> мусять збігатись до літери.
/// </summary>
public enum DuelPhase
{
    /// <summary>«Готуйсь…» — сталі півтори секунди, щоб обидва встигли покласти палець.</summary>
    Ready,
    /// <summary>«Цілься…» — від півтори до п'яти секунд, і скільки саме, не знає ніхто, крім сервера.</summary>
    Aim,
    /// <summary>«ВОГОНЬ!» — вікно на три секунди; хто перший, той і взяв раунд.</summary>
    Fire,
    /// <summary>Показуємо, чим скінчився раунд, і даємо видихнути перед наступним.</summary>
    Result,
    /// <summary>Партію зіграно.</summary>
    Done,
}

/// <summary>
/// Дуель-вестерн на реакцію. Уся правда про раунд живе на сервері: браузер не знає ні коли гримне
/// «ВОГОНЬ!», ні скільки лишилось цілитись — інакше виграв би не той, у кого швидша рука, а той, хто
/// підглянув у кадр. Тому в кадрі нема ні <c>fireAt</c>, ні відліку у фазі «Цілься…».
/// </summary>
public sealed class Duel : Game
{
    /// <summary>«Готуйсь…»: стала пауза, щоб обидва встигли зібратись.</summary>
    public const int ReadyMs = 1500;
    /// <summary>Межі «Цілься…». Менше — не встигнеш зібратись, більше — рука сама тисне з нудьги.</summary>
    public const int AimMinMs = 1500, AimMaxMs = 5000;
    /// <summary>Скільки чекаємо пострілу після «ВОГОНЬ!». Не дочекались — обидва заснули.</summary>
    public const int FireWindowMs = 3000;
    /// <summary>Пауза між раундами: встигнути прочитати, хто швидший і на скільки.</summary>
    public const int ResultMs = 2500;
    /// <summary>До трьох виграних раундів, тобто best of 5.</summary>
    public const int WinsNeeded = 3;
    /// <summary>50 мс — крок, на якому різниця в реакції ще чесна, а кадрів не забагато.</summary>
    public const int TickMs = 50;
    /// <summary>
    /// Стільки раундів поспіль «обидва заснули» — і стіл закривається нічиєю. Інакше покинута дуель
    /// (вкладки відкриті, за клавіатурою нікого) крутила б раунди по колу, поки хтось не закриє браузер:
    /// кімнату в статусі «грають» прибиральник каркаса не чіпає.
    /// </summary>
    public const int IdleRounds = 3;
    /// <summary>
    /// Швидше за це людина не встигає — це вже не рука, а скрипт у консолі. Раунд такому стрільцеві
    /// зараховуємо (сервер напевно не знає, хто там тиснув), але в таблицю реакцій і до ачівки
    /// «Швидка рука» такий час не пускаємо.
    /// </summary>
    public const int HumanFloorMs = 80;

    public override GameInfo Info { get; } = new(
        "duel", "Дуель", "дуель", GameGroup.Live, 2, 2, TickMs: TickMs, Rated: true,
        Score: ScoreOrder.LowerIsBetter,
        Options: [DuelKit.BaitOption, DuelKit.SignalOption, DuelKit.PingOption],
        Hint: "Двоє на курній вулиці. «Готуйсь… цілься…» — і на слово ВОГОНЬ тисни першим. Поспішив — куля в небо");

    DuelBout? _bout;
    DuelKit? _kit;
    /// <summary>Поєдинок живе в <see cref="DuelBout"/> (той самий, що й у турнірі); тут — лише партія до трьох перемог.</summary>
    DuelBout Bout => _bout ??= new DuelBout(Ctx);
    DuelKit Kit => _kit ??= new DuelKit(this, 2);
    static readonly int[] PairSeats = [0, 1];

    public override string SeatName(int seat) => seat == 0 ? "шериф" : "бандит";

    /// <summary>Скільки триває «Цілься…». Випадковість — тільки з сідованого генератора кімнати.</summary>
    public static int AimMs(Random rng) => AimMinMs + rng.Next(AimMaxMs - AimMinMs + 1);

    public override void Configure(IReadOnlyDictionary<string, string> options) => Kit.Configure(options);

    public override void Start()
    {
        // Партія — це вся серія до трьох перемог, тож «Ще раз» починає нову дуель з чистого рахунку.
        Kit.Reset();
        Kit.Wire(Bout);
        Bout.Reset(Ctx.Clock.UtcNow);
    }

    /// <summary>
    /// Постріл (<c>shoot</c>) — клієнт шле його через <c>Input</c>, але <c>Act</c> теж приймаємо. <c>pong</c> — відлуння
    /// кадру для заміру пінгу (лише з опцією «з поправкою»).
    /// </summary>
    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action == "pong") { Kit.Pong(seat, payload); return ActResult.Done; }
        if (action != "shoot") return ActResult.Fail("Тут так не ходять");
        if (seat is < 0 or > 1) return ActResult.Fail("Ти тут не граєш");
        return Bout.Shoot(seat, Ctx.Clock.UtcNow, "Дуель зіграно, тисни «Ану ще раз»");
    }

    public override TickResult Tick()
    {
        var b = Bout;
        if (b.Phase == DuelPhase.Done) return TickResult.None;
        var now = Ctx.Clock.UtcNow;
        switch (b.Tick(now))
        {
            case BoutTick.Frame:
                return Kit.Mark(TickResult.FrameOnly, now);
            // Разом із кадром шлемо й види: у них рекорди, а вони щойно змінились.
            case BoutTick.Announced:
                return Kit.Mark(TickResult.Both, now);
            case BoutTick.PauseOver:
                return Kit.Mark(Next(now), now);
        }
        return Kit.PingTick(b.Phase, now);
    }

    /// <summary>Пауза скінчилась: або наступний раунд, або вся дуель.</summary>
    TickResult Next(DateTimeOffset now)
    {
        var b = Bout;
        if (b.Wins[0] >= WinsNeeded || b.Wins[1] >= WinsNeeded)
        {
            var won = b.Wins[0] > b.Wins[1] ? 0 : 1;
            var lost = 1 - won;
            b.Finish();
            // Ніки чужі, відмінювати їх нема як, тому рахунок замість речення з відмінками.
            Ctx.Finish([won], $"{Info.Title}: {Ctx.NickOf(won)} {SeatName(won)} {b.Wins[won]}:{b.Wins[lost]} {Ctx.NickOf(lost)} {SeatName(lost)}");
            return TickResult.Both;
        }
        if (b.Idle >= IdleRounds)
        {
            // За столом нікого: три раунди поспіль ніхто навіть не смикнувся. Нічия — ставки назад.
            b.Finish();
            Ctx.Finish([], $"{Info.Title}: {Ctx.NickOf(0)} і {Ctx.NickOf(1)} так і не вистрілили — дуель не відбулась");
            return TickResult.Both;
        }
        b.Next(now);
        return TickResult.Both;
    }

    public override object? Frame() => Wire(false);

    public override object View(int? seat) => Wire(true);

    Dictionary<string, object?> Wire(bool full)
    {
        var o = new Dictionary<string, object?>();
        Bout.Fill(o, Ctx.Clock.UtcNow);
        Kit.Fill(o, full, PairSeats);
        return o;
    }
}
