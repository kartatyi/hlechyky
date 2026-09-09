using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Бомбер на двох–чотирьох. Правила раунду живуть у <see cref="BomberCore"/>, а тут — усе, що знає про
/// кімнату: фази («готуйсь», гра, пауза на догорілому полі), рахунок раундів до трьох перемог, вид і
/// кадр. Кадр летить кожні 60 мс, тож усе в ньому — короткі числа: клієнт домальовує плавність сам.
/// </summary>
public sealed class Bomber : Game
{
    /// <summary>«Готуйсь» на свіжому полі — десь дві секунди.</summary>
    public const int StartTicks = 33;
    /// <summary>Пауза на догорілому полі, щоб побачити, хто взяв раунд — десь секунда.</summary>
    public const int PauseTicks = 17;
    /// <summary>Партія — до трьох виграних раундів.</summary>
    public const int WinsToTake = 3;
    /// <summary>Запобіжник проти вічної партії з самих нічиїх: після дев'ятого раунду рахуємо, хто попереду.</summary>
    public const int MaxRounds = 9;

    /// <summary>Фази, які бачить клієнт у полі <c>phase</c> кадра.</summary>
    public const string PhaseStart = "start", PhaseGo = "go", PhasePause = "pause", PhaseOver = "over";

    public override GameInfo Info { get; } = new(
        "bomber", "Бомбер", "бомбер", GameGroup.Live, 2, BomberCore.Seats, TickMs: BomberCore.TickMs,
        Start: StartMode.ByHost,
        Hint: "Ставиш бомби, ламаєш ящики, підриваєш суперників. Останній живий бере раунд");

    BomberCore? _core;
    readonly int[] _wins = new int[BomberCore.Seats];
    string _phase = PhaseStart;
    int _startIn = StartTicks;
    int _round = 1;
    /// <summary>Партія вже стартувала хоч раз: до того поле — це просто картинка для лобі.</summary>
    bool _started;

    /// <summary>
    /// Поле готове ще до старту: стіл, який чекає на гравців, має виглядати як поле, а не як порожнеча.
    /// Ящиків там нема свідомо — <see cref="BomberCore.Layout"/> не бере жодного числа з <c>Ctx.Rng</c>,
    /// і партія лишається такою самою, скільки б разів у лобі не перемальовували картку.
    /// </summary>
    BomberCore Core
    {
        get
        {
            if (_core is not null) return _core;
            _core = new BomberCore(Ctx.Rng);
            _core.Layout();
            return _core;
        }
    }

    public override string SeatName(int seat) => seat switch
    {
        0 => "жовтий",
        1 => "зелений",
        2 => "рудий",
        _ => "сірий",
    };

    public override void Start()
    {
        Array.Clear(_wins);
        _round = 1;
        _started = true;
        _phase = PhaseStart;
        _startIn = StartTicks;
        Core.Reset(Plays());
    }

    /// <summary>Хто цього раунду на полі. Місця, з яких устали, назад не повертаються.</summary>
    bool[] Plays() => [.. Enumerable.Range(0, BomberCore.Seats).Select(Ctx.Seated)];

    // ---------- ввід ----------

    /// <summary>
    /// Реалтайм-ввід із хабового <c>Input</c>: «тримаю напрямок» і «клади бомбу». Відповіді ніхто не
    /// побачить, але тексти все одно людські — той самий метод кличе і <c>Act</c> у тестах.
    /// </summary>
    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (!_started) return ActResult.Fail("Партія ще не почалась");
        if (seat < 0 || seat >= BomberCore.Seats) return ActResult.Fail("Ти тут не граєш");

        switch (action)
        {
            case "move":
                // «Тримаю напрямок» — це лише намір, і приймаємо його в будь-якій фазі: людина тисне
                // стрілку ще на відліку, і бомбер має поїхати з першого тика раунду. Зрушити раніше
                // нікому — у фазах «готуйсь» і «пауза» Core.Step() не кличеться взагалі.
                if (_phase == PhaseOver) return ActResult.Fail("Партію вже зіграно");
                var dir = Dir(payload);
                if (dir is null or < -1 or > 3) return ActResult.Fail("Такого напрямку нема");
                Core.Turn(seat, dir.Value);
                return ActResult.Done;
            case "bomb":
                if (_phase != PhaseGo) return ActResult.Fail("Зачекай, зараз почнемо");
                if (!Core.Players[seat].Alive) return ActResult.Fail("Тебе вже підірвали, чекай наступного раунду");
                return Core.Bomb(seat) ? ActResult.Done : ActResult.Fail("Бомби скінчились");
            default:
                return ActResult.Fail("Тут так не ходять");
        }
    }

    /// <summary>Напрямок приймаємо і як <c>{dir:1}</c>, і як голе число — клієнтам так простіше.</summary>
    static int? Dir(JsonElement payload) => payload.ValueKind switch
    {
        JsonValueKind.Number when payload.TryGetInt32(out var n) => n,
        JsonValueKind.Object when payload.TryGetProperty("dir", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var n) => n,
        _ => null,
    };

    // ---------- тик ----------

    public override TickResult Tick()
    {
        switch (_phase)
        {
            case PhaseOver:
                return TickResult.None;

            case PhaseStart:
                if (--_startIn > 0) return TickResult.FrameOnly;
                _phase = PhaseGo;
                return TickResult.FrameOnly;

            case PhasePause:
                if (--_startIn > 0) return TickResult.FrameOnly;
                _round++;
                Core.Reset(Plays());
                _phase = PhaseStart;
                _startIn = StartTicks;
                return TickResult.Both;   // рахунок раундів змінився ще раніше, але вид з ним їде саме тут

            default:
                return Play();
        }
    }

    TickResult Play()
    {
        Core.Step();
        if (!Core.RoundOver) return TickResult.FrameOnly;

        var took = Core.LastStanding;    // -1 — усі полягли разом або вийшов час
        if (took >= 0) _wins[took]++;

        if (took >= 0 && _wins[took] >= WinsToTake) return Over([took]);
        if (_round >= MaxRounds) return Over(Leaders());
        _phase = PhasePause;
        _startIn = PauseTicks;
        return TickResult.Both;
    }

    /// <summary>Хто попереду за раундами; порожньо — якщо попереду всі одразу (тоді це нічия).</summary>
    int[] Leaders()
    {
        var playing = Enumerable.Range(0, BomberCore.Seats).Where(Ctx.Seated).ToArray();
        if (playing.Length == 0) return [];
        var best = playing.Max(s => _wins[s]);
        var leaders = playing.Where(s => _wins[s] == best).ToArray();
        return leaders.Length == playing.Length ? [] : leaders;
    }

    /// <summary>Кінець партії: рядок Журналу — рахунок раундів, переможець першим.</summary>
    TickResult Over(int[] winners)
    {
        _phase = PhaseOver;
        winners = [.. winners.Where(Ctx.Seated)];
        var rest = Enumerable.Range(0, BomberCore.Seats).Where(s => Ctx.Seated(s) && !winners.Contains(s));
        var score = string.Join(" : ", winners.Concat(rest).Select(s => $"{Ctx.NickOf(s)} {_wins[s]}"));
        Ctx.Finish(winners, winners.Length > 0 ? $"{Info.Title}: {score}" : $"{Info.Title}: {score} — нічия");
        return TickResult.Both;
    }

    /// <summary>
    /// Хтось устав. На чотирьох це не привід ламати партію решті: бомбер утікача просто зникає з поля.
    /// А коли за столом лишається один — грати вже нема з ким, і партія його.
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (_started && seat >= 0 && seat < BomberCore.Seats)
        {
            var p = Core.Players[seat];
            p.Alive = false;
            p.Plays = false;
            p.Want = -1;
        }
        var left = Enumerable.Range(0, BomberCore.Seats).Where(s => s != seat && Ctx.Seated(s)).ToArray();
        if (left.Length > 1) return;
        _phase = PhaseOver;
        Ctx.Finish(left, $"{Info.Title}: {Ctx.NickOf(seat)} встав з-за столу, партію не дограли");
    }

    // ---------- вид і кадр ----------

    /// <summary>
    /// Кадр на кожен тик. Координати бомберів — у дванадцятих частках клітинки (щоб клієнт міг плавно
    /// інтерполювати), координати бомб і бонусів — у цілих клітинках: вони й так стоять по центру.
    /// </summary>
    public override object? Frame() => new
    {
        t = Core.Ticks,
        p = Men(),
        b = Core.Bombs.Select(b => new { x = BomberCore.X(b.Cell), y = BomberCore.Y(b.Cell), fuse = b.Fuse }).ToArray(),
        f = Core.FlameCells(),
        boxes = Core.BoxCells(),
        pw = Core.Drops.Select(d => new { x = BomberCore.X(d.Cell), y = BomberCore.Y(d.Cell), kind = Kind(d.Kind) }).ToArray(),
        wins = (int[])_wins.Clone(),
        phase = _phase,
        startIn = _startIn,
    };

    public override object View(int? seat) => new
    {
        width = BomberCore.W,
        height = BomberCore.H,
        sub = BomberCore.Sub,
        turn = (int?)null,                 // бомбер не покроковий: «чия черга» тут не буває
        need = WinsToTake,
        round = _round,
        walls = Core.WallCells(),          // рамка й стовпи не міняються — клієнт малює їх раз
        t = Core.Ticks,
        p = Men(),
        b = Core.Bombs.Select(b => new { x = BomberCore.X(b.Cell), y = BomberCore.Y(b.Cell), fuse = b.Fuse }).ToArray(),
        f = Core.FlameCells(),
        boxes = Core.BoxCells(),
        pw = Core.Drops.Select(d => new { x = BomberCore.X(d.Cell), y = BomberCore.Y(d.Cell), kind = Kind(d.Kind) }).ToArray(),
        wins = (int[])_wins.Clone(),
        phase = _phase,
        startIn = _startIn,
    };

    /// <summary>
    /// Бомбери по місцях. Поки партія не почалась, «живий» означає «за цим місцем хтось сидить» — щоб
    /// стіл у лобі показував, кого вже чекати, а не порожні кути.
    /// </summary>
    object[] Men() =>
    [
        .. Core.Players.Select((p, i) => (object)new
        {
            x = BomberCore.PosX(p),
            y = BomberCore.PosY(p),
            alive = _started ? p.Alive : Ctx.Seated(i),
            bombs = p.Bombs,
            range = p.Range,
            boots = p.Boots,
        }),
    ];

    static string Kind(BomberBonus kind) => kind switch
    {
        BomberBonus.Range => "range",
        BomberBonus.Bomb => "bomb",
        _ => "boots",
    };
}
