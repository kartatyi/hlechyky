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

    public override GameInfo Info { get; } = new(
        "duel", "Дуель", "дуель", GameGroup.Live, 2, 2, TickMs: TickMs, Rated: true,
        Score: ScoreOrder.LowerIsBetter,
        Hint: "Двоє на курній вулиці. «Готуйсь… цілься…» — і на слово ВОГОНЬ тисни першим. Поспішив — куля в небо");

    DuelPhase _phase = DuelPhase.Ready;
    /// <summary>Номер раунду в партії. Перегравання (обидва поспішили або обидва заснули) його не рухає.</summary>
    int _round = 1;
    readonly int[] _wins = new int[2];
    /// <summary>Найшвидша реакція кожного за цю партію; null — ще жодного влучного пострілу.</summary>
    readonly long?[] _best = new long?[2];

    /// <summary>Коли «Готуйсь…» стає «Цілься…».</summary>
    DateTimeOffset _aimAt;
    /// <summary>Спершу — запланований момент «ВОГОНЬ!», потім — той, у який кадр справді пішов.</summary>
    DateTimeOffset _fireAt;
    /// <summary>Кінець вікна пострілу, а у фазі результату — кінець паузи між раундами.</summary>
    DateTimeOffset _deadline;

    // Підсумок раунду, поки він ще не оголошений кадром. Саме це вікно (від пострілу до наступного тика)
    // і є «одним тиком» зі spec: доки раунд не оголошено, другий гравець ще встигає долучитись до події.
    string? _reason;
    int? _winner;
    readonly long?[] _ms = new long?[2];

    // Останній оголошений підсумок — його клієнт малює в полі last.
    bool _hasLast;
    string? _lastReason;
    int? _lastWinner;
    readonly long?[] _lastMs = new long?[2];

    public override string SeatName(int seat) => seat == 0 ? "шериф" : "бандит";

    /// <summary>Скільки триває «Цілься…». Випадковість — тільки з сідованого генератора кімнати.</summary>
    public static int AimMs(Random rng) => AimMinMs + rng.Next(AimMaxMs - AimMinMs + 1);

    public override void Start()
    {
        // Партія — це вся серія до трьох перемог, тож «Ще раз» починає нову дуель з чистого рахунку.
        _wins[0] = _wins[1] = 0;
        _best[0] = _best[1] = null;
        _round = 1;
        _hasLast = false;
        _lastReason = null;
        _lastWinner = null;
        _lastMs[0] = _lastMs[1] = null;
        NewRound();
    }

    /// <summary>Новий раунд: «Готуйсь…», а за ним — невідомо скільки «Цілься…».</summary>
    void NewRound()
    {
        var now = Ctx.Clock.UtcNow;
        _phase = DuelPhase.Ready;
        _aimAt = now.AddMilliseconds(ReadyMs);
        _fireAt = _aimAt.AddMilliseconds(AimMs(Ctx.Rng));
        _deadline = _fireAt;
        _reason = null;
        _winner = null;
        _ms[0] = _ms[1] = null;
    }

    /// <summary>
    /// Постріл. Клієнт шле його через <c>Input</c> (щоб не чекати відповіді), але <c>Act</c> теж приймаємо:
    /// різниця лише в тому, чи побачить гравець тост.
    /// </summary>
    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action != "shoot") return ActResult.Fail("Тут так не ходять");
        if (_phase is DuelPhase.Result) return ActResult.Fail("Раунд уже скінчився, чекай наступного");
        if (_phase is DuelPhase.Done) return ActResult.Fail("Дуель зіграно, тисни «Ще раз»");

        if (_phase == DuelPhase.Fire) Shoot(seat, Ctx.Clock.UtcNow);
        else FalseStart(seat);
        return ActResult.Done;
    }

    /// <summary>
    /// Постріл після «ВОГОНЬ!». Перший, хто дійшов, бере раунд; другий устигає хіба записати свій час —
    /// показати його все одно чесно, бо програти на двадцять мілісекунд не соромно.
    /// </summary>
    void Shoot(int seat, DateTimeOffset now)
    {
        if (_ms[seat] is not null) return;   // двічі за раунд не стріляють
        _ms[seat] = (long)Math.Max(1, Math.Round((now - _fireAt).TotalMilliseconds));
        if (_reason is null)
        {
            _reason = "shot";
            _winner = seat;
        }
    }

    /// <summary>
    /// Постріл до слова «ВОГОНЬ!» — куля в небо, раунд суперникові. Якщо суперник устиг поспішити теж,
    /// поки раунд ще не оголошено, раунд не зараховується нікому й переграється.
    /// </summary>
    void FalseStart(int seat)
    {
        var other = seat == 0 ? 1 : 0;
        if (_reason is null)
        {
            _reason = "false";
            _winner = other;
            return;
        }
        if (_reason == "false" && _winner == seat)
        {
            _reason = "both-false";
            _winner = null;
        }
    }

    public override TickResult Tick()
    {
        if (_phase == DuelPhase.Done) return TickResult.None;
        var now = Ctx.Clock.UtcNow;

        if (_phase == DuelPhase.Result)
            return now >= _deadline ? Next(now) : TickResult.None;

        // Раунд міг скінчитись між тиками — пострілом або фальстартом. Оголошуємо його зараз.
        if (_reason is not null) return Announce(now);

        if (_phase == DuelPhase.Ready && now >= _aimAt)
        {
            _phase = DuelPhase.Aim;
            return TickResult.FrameOnly;
        }
        if (_phase == DuelPhase.Aim && now >= _fireAt)
        {
            // fireAt переставляємо на мить, коли кадр із «ВОГОНЬ!» справді пішов у браузер: запланована
            // точка лежить десь між тиками, і рахувати реакцію від часу, якого гравець ще не бачив, нечесно.
            _phase = DuelPhase.Fire;
            _fireAt = now;
            _deadline = now.AddMilliseconds(FireWindowMs);
            return TickResult.FrameOnly;
        }
        if (_phase == DuelPhase.Fire && now >= _deadline)
        {
            _reason = "sleep";
            _winner = null;
            return Announce(now);
        }
        return TickResult.None;
    }

    /// <summary>Оголосити підсумок раунду: рахунок, рекорди, пауза перед наступним.</summary>
    TickResult Announce(DateTimeOffset now)
    {
        if (_winner is { } w) _wins[w]++;
        for (var seat = 0; seat < 2; seat++)
        {
            if (_ms[seat] is not { } ms) continue;
            if (_best[seat] is not { } best || ms < best) _best[seat] = ms;
            // Найшвидша рука йде в таблицю реакцій (Info.Score = LowerIsBetter); звідси ж WP1 сам бачить
            // ачівку «Швидка рука», тож просити її через Ctx.Award не треба.
            Ctx.Score(seat, ms);
        }
        _hasLast = true;
        _lastReason = _reason;
        _lastWinner = _winner;
        _lastMs[0] = _ms[0];
        _lastMs[1] = _ms[1];
        _phase = DuelPhase.Result;
        _deadline = now.AddMilliseconds(ResultMs);
        // Разом із кадром шлемо й види: у них лежать рекорди, а вони щойно змінились.
        return TickResult.Both;
    }

    /// <summary>Пауза скінчилась: або наступний раунд, або вся дуель.</summary>
    TickResult Next(DateTimeOffset now)
    {
        if (_wins[0] >= WinsNeeded || _wins[1] >= WinsNeeded)
        {
            var won = _wins[0] > _wins[1] ? 0 : 1;
            var lost = won == 0 ? 1 : 0;
            _phase = DuelPhase.Done;
            // Ніки чужі, відмінювати їх нема як, тому рахунок замість речення з відмінками.
            Ctx.Finish([won], $"{Info.Title}: {Ctx.NickOf(won)} {SeatName(won)} {_wins[won]}:{_wins[lost]} {Ctx.NickOf(lost)} {SeatName(lost)}");
            return TickResult.Both;
        }
        // Перегравання номер раунду не рухає: у best of 5 «третій раунд» має бути справді третім.
        if (_lastWinner is not null) _round++;
        NewRound();
        return TickResult.Both;
    }

    public override object? Frame() => new
    {
        phase = Wire(_phase),
        round = _round,
        wins = (int[])_wins.Clone(),
        last = Last(),
        nextIn = NextIn(),
    };

    public override object View(int? seat) => new
    {
        phase = Wire(_phase),
        round = _round,
        wins = (int[])_wins.Clone(),
        last = Last(),
        nextIn = NextIn(),
        best = (long?[])_best.Clone(),
        // Дуель не покрокова, але поле каркас читає в кожної гри: без нього він писав би «Ходить X».
        turn = (int?)null,
    };

    object? Last() => _hasLast
        ? new { winner = _lastWinner, reason = _lastReason, ms = (long?[])_lastMs.Clone() }
        : null;

    /// <summary>
    /// Скільки лишилось до зміни фази. У «Цілься…» — навмисно null: будь-яке число там і є той самий
    /// <c>fireAt</c>, лише з іншого боку, а виграти має рука, а не очі.
    /// </summary>
    int? NextIn()
    {
        var until = _phase switch
        {
            DuelPhase.Ready => _aimAt,
            DuelPhase.Fire or DuelPhase.Result => _deadline,
            _ => (DateTimeOffset?)null,
        };
        if (until is not { } at) return null;
        var left = (at - Ctx.Clock.UtcNow).TotalMilliseconds;
        return (int)Math.Max(0, Math.Round(left));
    }

    static string Wire(DuelPhase phase) => phase switch
    {
        DuelPhase.Ready => "ready",
        DuelPhase.Aim => "aim",
        DuelPhase.Fire => "fire",
        DuelPhase.Result => "result",
        _ => "done",
    };
}
