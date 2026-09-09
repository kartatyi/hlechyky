using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>Точка сліду: де була голова і чи писався в цю мить слід (у дірці — ні).</summary>
public readonly record struct CurvePoint(double X, double Y, bool Gap);

/// <summary>
/// Одна кривуля: де голова, куди дивиться, чи жива і як у неї справи з дірками. Слід тримаємо
/// повністю (не лише растр): з нього будується ламана для клієнта, коли той перемальовує поле з нуля.
/// </summary>
public sealed class CurveHead
{
    /// <summary>Місце зайняте і бере участь у цьому раунді.</summary>
    public bool Present;
    public bool Alive;
    public double X, Y;
    /// <summary>Курс у радіанах: 0 — праворуч, кути ростуть за годинниковою (вісь Y дивиться вниз, як на канвасі).</summary>
    public double A;
    /// <summary>Що зараз тримає гравець: -1 ліворуч, 0 прямо, 1 праворуч.</summary>
    public int Turn;
    /// <summary>Цього тика слід не пишеться.</summary>
    public bool Gap;
    /// <summary>Скільки тиків дірки ще лишилось.</summary>
    public int GapLeft;
    /// <summary>Через скільки тиків почнеться наступна дірка.</summary>
    public int NextGapIn;
    public List<CurvePoint> Trail { get; } = [];
}

/// <summary>
/// Симуляція «Кривулі» без жодного слова про кімнати й очки: поле, чотири голови, растр зіткнень.
/// Растр — це і є правда про те, хто в що врізався: 300×200 байтів на кімнату дешевші за перевірку
/// голови проти тисяч відрізків, а на око різниці нема (одиниця поля = одна клітинка).
///
/// Найтонше місце — власний слід. Голова рухається на 1.6 од/тик, а сама завширшки 4, тож якби слід
/// малювався просто під нею, вона б умирала об себе на першому ж тику. Тому слід відстає на
/// <see cref="TrailLag"/> тиків, а зіткнення голова шукає лише на своїй передній півкулі: позаду
/// в неї завжди власний хвіст, і це не привід гинути. Найкрутіший поворот дає коло діаметром ~25 од,
/// тож догнати себе за ці 4.8 од відставання неможливо — «безсмертний» лише той шматок, якого й на
/// екрані ще не видно.
/// </summary>
/// <param name="rng">Сідований генератор кімнати: та сама партія з тим самим сідом повторюється точка в точку.</param>
/// <param name="gaps">Чи робити дірки самому. false — дірок нема (тести дивляться на суцільний слід).</param>
public sealed class CurveCore(Random rng, bool gaps = true)
{
    /// <summary>Поле в умовних одиницях; воно ж — растр зіткнень, одна одиниця на клітинку.</summary>
    public const int W = 300, H = 200;
    public const int TickMs = 40;
    /// <summary>Місць за столом (і довжина всіх масивів на дроті).</summary>
    public const int Seats = 4;
    /// <summary>40 од/с при 25 тиках на секунду.</summary>
    public const double Speed = 1.6;
    /// <summary>180°/с — за секунду кривуля розвертається рівно назад.</summary>
    public const double TurnStep = Math.PI * 7.2 / 180.0;
    /// <summary>Радіус голови (він же — половина товщини сліду).</summary>
    public const double R = 2;
    /// <summary>«Готуйсь» перед раундом — дві секунди.</summary>
    public const int ReadyTicks = 50;
    /// <summary>Пауза між раундами — три секунди.</summary>
    public const int BetweenTicks = 75;
    /// <summary>Дірка триває чверть секунди.</summary>
    public const int GapTicks = 6;
    /// <summary>Наступна дірка — через 2–4 с.</summary>
    public const int GapMinTicks = 50, GapMaxTicks = 100;
    /// <summary>На скільки тиків слід відстає від голови (див. коментар до класу).</summary>
    public const int TrailLag = 2;
    /// <summary>Ближче до стіни не народжуємось: інакше половина раунду — це смерть об стіну за секунду.</summary>
    public const int SpawnMargin = 40;
    /// <summary>І одне від одного теж не впритул.</summary>
    public const double SpawnApart = 50;
    /// <summary>Дві хвилини — і раунд закривається сам: поле все одно вже нікуди їхати, а вид не має рости вічно.</summary>
    public const int MaxRoundTicks = 3000;

    /// <summary>Кут, у якому кривуля може дивитись на старті: ±90° від напрямку на центр поля.</summary>
    const double SpawnSpread = Math.PI;

    readonly byte[] _grid = new byte[W * H];

    /// <summary>Голови за номерами місць; невзяті місця мають <c>Present == false</c>.</summary>
    public CurveHead[] Heads { get; } = [.. Enumerable.Range(0, Seats).Select(_ => new CurveHead())];

    /// <summary>Скільки тиків триває цей раунд.</summary>
    public int RoundTicks { get; private set; }

    public int AliveCount => Heads.Count(h => h.Present && h.Alive);

    /// <summary>Що лежить у клітинці растра: 0 — порожньо, інакше номер місця + 1.</summary>
    public byte Cell(int x, int y) => x < 0 || y < 0 || x >= W || y >= H ? (byte)0 : _grid[y * W + x];

    /// <summary>Новий раунд: чистий растр, нові точки старту для тих, хто сидить за столом.</summary>
    public void Reset(IReadOnlyList<bool> present)
    {
        Array.Clear(_grid);
        RoundTicks = 0;
        var placed = new List<(double X, double Y)>();
        for (var s = 0; s < Seats; s++)
        {
            var h = Heads[s];
            h.Trail.Clear();
            h.Present = s < present.Count && present[s];
            h.Alive = h.Present;
            h.Turn = 0;
            h.Gap = false;
            h.GapLeft = 0;
            h.NextGapIn = NextGap();
            if (!h.Present) continue;

            var (x, y) = Spawn(placed);
            placed.Add((x, y));
            h.X = x;
            h.Y = y;
            // Дивитись строго куди попало не можна: народитись за 40 од від стіни носом у неї — це
            // секунда гри. Тому курс випадковий, але в півплощині «на центр».
            var toCenter = Math.Atan2(H / 2.0 - y, W / 2.0 - x);
            h.A = toCenter + (rng.NextDouble() - 0.5) * SpawnSpread;
            h.Trail.Add(new CurvePoint(x, y, false));
        }
    }

    /// <summary>Гравець тримає поворот. Значення поза -1..1 підрізаємо: на дроті буває всяке.</summary>
    public void Turn(int seat, int dir)
    {
        if (seat < 0 || seat >= Seats) return;
        Heads[seat].Turn = Math.Sign(dir);
    }

    /// <summary>Один крок усіх кривуль. Повертає місця, що загинули саме цього тика.</summary>
    public List<int> Step()
    {
        RoundTicks++;
        var died = new List<int>();
        var nx = new double[Seats];
        var ny = new double[Seats];

        // Спершу рахуємо й перевіряємо всіх — і лише потім рухаємо й малюємо. Інакше той, хто
        // йде першим у циклі, встигав би домалювати слід під носом у другого.
        for (var s = 0; s < Seats; s++)
        {
            var h = Heads[s];
            if (!h.Present || !h.Alive) continue;
            h.A += h.Turn * TurnStep;
            nx[s] = h.X + Math.Cos(h.A) * Speed;
            ny[s] = h.Y + Math.Sin(h.A) * Speed;
            Breathe(h);
            if (Wall(nx[s], ny[s]) || Hits(nx[s], ny[s], h.A)) died.Add(s);
        }

        // Лоб у лоб растром не ловиться: у кожного попереду ще не намальований шматок власного сліду.
        for (var a = 0; a < Seats; a++)
            for (var b = a + 1; b < Seats; b++)
            {
                if (!Heads[a].Present || !Heads[a].Alive || !Heads[b].Present || !Heads[b].Alive) continue;
                var (dx, dy) = (nx[a] - nx[b], ny[a] - ny[b]);
                if (dx * dx + dy * dy >= 4 * R * R) continue;
                if (!died.Contains(a)) died.Add(a);
                if (!died.Contains(b)) died.Add(b);
            }

        for (var s = 0; s < Seats; s++)
        {
            var h = Heads[s];
            if (!h.Present || !h.Alive || died.Contains(s)) continue;
            h.X = nx[s];
            h.Y = ny[s];
            h.Trail.Add(new CurvePoint(h.X, h.Y, h.Gap));
        }
        for (var s = 0; s < Seats; s++)
        {
            var h = Heads[s];
            if (h.Present && h.Alive && !died.Contains(s)) Paint(s);
        }
        foreach (var s in died) Heads[s].Alive = false;
        died.Sort();
        return died;
    }

    /// <summary>Раунд затягнувся — гасимо всіх, хто ще їде. Повертає їхні місця.</summary>
    public List<int> StopAll()
    {
        var died = new List<int>();
        for (var s = 0; s < Seats; s++)
        {
            if (!Heads[s].Present || !Heads[s].Alive) continue;
            Heads[s].Alive = false;
            died.Add(s);
        }
        return died;
    }

    /// <summary>Дірки: рахуємо тики до наступної й доживаємо поточну.</summary>
    void Breathe(CurveHead h)
    {
        h.Gap = false;
        if (h.GapLeft > 0)
        {
            h.Gap = true;
            if (--h.GapLeft == 0 && gaps) h.NextGapIn = NextGap();
            return;
        }
        if (!gaps) return;
        if (--h.NextGapIn > 0) return;
        h.Gap = true;
        h.GapLeft = GapTicks - 1;
        if (h.GapLeft == 0) h.NextGapIn = NextGap();
    }

    int NextGap() => rng.Next(GapMinTicks, GapMaxTicks + 1);

    /// <summary>Голова торкнулась стіни.</summary>
    public static bool Wall(double x, double y) => x < R || y < R || x > W - R || y > H - R;

    /// <summary>
    /// Чи є слід під передньою півкулею голови. Позаду завжди свій хвіст, тому дивимось лише туди,
    /// куди їдемо: скалярний добуток на курс має бути додатним.
    /// </summary>
    public bool Hits(double x, double y, double a)
    {
        var (cx, cy) = (Math.Cos(a), Math.Sin(a));
        var (hx, hy) = ((int)x, (int)y);
        for (var dy = -2; dy <= 2; dy++)
            for (var dx = -2; dx <= 2; dx++)
            {
                var (gx, gy) = (hx + dx, hy + dy);
                if (gx < 0 || gy < 0 || gx >= W || gy >= H || _grid[gy * W + gx] == 0) continue;
                var (vx, vy) = (gx + 0.5 - x, gy + 0.5 - y);
                if (vx * vx + vy * vy > R * R) continue;
                if (vx * cx + vy * cy <= 0) continue;
                return true;
            }
        return false;
    }

    /// <summary>Домалювати в растр те місце, де голова була TrailLag тиків тому.</summary>
    void Paint(int seat)
    {
        var trail = Heads[seat].Trail;
        var i = trail.Count - 1 - TrailLag;
        if (i < 0) return;
        var p = trail[i];
        if (p.Gap) return;
        var id = (byte)(seat + 1);
        var (hx, hy) = ((int)p.X, (int)p.Y);
        for (var dy = -2; dy <= 2; dy++)
            for (var dx = -2; dx <= 2; dx++)
            {
                var (gx, gy) = (hx + dx, hy + dy);
                if (gx < 0 || gy < 0 || gx >= W || gy >= H) continue;
                var (vx, vy) = (gx + 0.5 - p.X, gy + 0.5 - p.Y);
                if (vx * vx + vy * vy <= R * R) _grid[gy * W + gx] = id;
            }
    }

    (double X, double Y) Spawn(List<(double X, double Y)> placed)
    {
        foreach (var apart in (double[])[SpawnApart, 30, 20])
            for (var i = 0; i < 200; i++)
            {
                var x = SpawnMargin + rng.NextDouble() * (W - 2 * SpawnMargin);
                var y = SpawnMargin + rng.NextDouble() * (H - 2 * SpawnMargin);
                if (placed.All(p => (p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y) >= apart * apart)) return (x, y);
            }
        // Не пощастило (буває хіба що при зміні констант) — розводимо по кутах, аби раунд не завис.
        var corner = placed.Count % 4;
        return (corner % 2 == 0 ? W * 0.25 : W * 0.75, corner < 2 ? H * 0.25 : H * 0.75);
    }

    /// <summary>Максимум точок, які можна злити в один прямий пробіг: далі похибка вже помітна.</summary>
    const int MaxRun = 40;

    /// <summary>
    /// Ламана для клієнта: цілі координати (растр усе одно цілий) і викинуті точки, що лежать на
    /// прямій. Пряма ділянка з двохсот точок так стискається до двох, і хвилина раунду вкладається
    /// в кілька кілобайтів замість десятків.
    /// </summary>
    /// <returns>
    /// Pts — пари x,y підряд; Gaps — номери точок, у які слід НЕ веде (там дірка).
    /// </returns>
    public static (int[] Pts, int[] Gaps) Polyline(IReadOnlyList<CurvePoint> trail)
    {
        var pts = new List<int>(trail.Count * 2);
        var gapAt = new List<bool>(trail.Count);
        var run = 0;
        foreach (var p in trail)
        {
            var (x, y) = ((int)Math.Round(p.X), (int)Math.Round(p.Y));
            var n = gapAt.Count;
            if (n > 0 && !p.Gap && pts[2 * (n - 1)] == x && pts[2 * (n - 1) + 1] == y) continue;
            if (n >= 2 && !p.Gap && !gapAt[n - 1] && run < MaxRun
                && OnLine(pts[2 * (n - 2)], pts[2 * (n - 2) + 1], x, y, pts[2 * (n - 1)], pts[2 * (n - 1) + 1]))
            {
                pts[2 * (n - 1)] = x;
                pts[2 * (n - 1) + 1] = y;
                run++;
                continue;
            }
            pts.Add(x);
            pts.Add(y);
            gapAt.Add(p.Gap);
            run = 0;
        }
        var gaps = new List<int>();
        for (var i = 0; i < gapAt.Count; i++)
            if (gapAt[i]) gaps.Add(i);
        return ([.. pts], [.. gaps]);
    }

    /// <summary>Чи лежить точка c на відрізку a→b з точністю до пів одиниці.</summary>
    static bool OnLine(int ax, int ay, int bx, int by, int cx, int cy)
    {
        double dx = bx - ax, dy = by - ay;
        var len2 = dx * dx + dy * dy;
        if (len2 == 0) return true;
        var cross = (cx - ax) * dy - (cy - ay) * dx;
        return cross * cross <= 0.25 * len2;
    }
}

/// <summary>
/// Кривуля на 2–4 гравців: їдеш уперед, лишаєш слід, повертати можна лише плавно. Партія — це низка
/// раундів у тій самій кімнаті: хто вибув, тому вже нема куди поспішати, а живі беруть по очку за
/// кожного вибулого. Дограли до <c>10 × (гравців − 1)</c> — партія скінчилась.
/// </summary>
public sealed class CurveGame : Game
{
    public override GameInfo Info { get; } = new(
        "curve", "Кривуля", "кривулю", GameGroup.Live, 2, CurveCore.Seats,
        TickMs: CurveCore.TickMs, Start: StartMode.ByHost,
        Hint: "Їдеш уперед і лишаєш слід. Повертати можна тільки плавно. Врізався — вибув. Останній живий бере очко");

    /// <summary>Скільки очок за партію треба на кожного суперника.</summary>
    public const int PerRival = 10;

    CurveCore? _core;
    int[] _scores = new int[CurveCore.Seats];
    bool[] _seats = new bool[CurveCore.Seats];
    int _target;
    int _round;
    string _phase = "ready";
    /// <summary>Тиків до кінця «Готуйсь» або паузи між раундами.</summary>
    int _phaseLeft;
    int[]? _winners;

    /// <summary>Поле партії: усі правила руху й зіткнень живуть там, а не тут.</summary>
    public CurveCore Field => Core;

    /// <summary>Поле є ще до старту: стіл, що чекає на гравців, має виглядати як поле, а не як діра.</summary>
    CurveCore Core
    {
        get
        {
            if (_core is not null) return _core;
            _core = new CurveCore(Ctx.Rng);
            _core.Reset(new bool[CurveCore.Seats]);
            return _core;
        }
    }

    public override string SeatName(int seat) => seat switch
    {
        0 => "жовта",
        1 => "зелена",
        2 => "глиняна",
        3 => "біла",
        _ => "кривуля",
    };

    public override void Start()
    {
        _core = new CurveCore(Ctx.Rng);
        _scores = new int[CurveCore.Seats];
        _seats = [.. Enumerable.Range(0, CurveCore.Seats).Select(Ctx.Seated)];
        _target = PerRival * Math.Max(1, _seats.Count(x => x) - 1);
        _round = 0;
        _winners = null;
        NewRound();
    }

    void NewRound()
    {
        _round++;
        Core.Reset(_seats);
        _phase = "ready";
        _phaseLeft = CurveCore.ReadyTicks;
    }

    /// <summary>Реалтайм-ввід: утримання повороту. Хиби нікого не цікавлять — наступний кадр усе перемалює.</summary>
    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action != "turn") return ActResult.Fail("Тут так не ходять");
        if (Dir(payload) is not { } d) return ActResult.Fail("Не зрозумів, куди повертати");
        Core.Turn(seat, d);
        return ActResult.Done;
    }

    /// <summary>Приймаємо і <c>{ d: 1 }</c>, і голе число — клієнтам так простіше.</summary>
    static int? Dir(JsonElement payload) => payload.ValueKind switch
    {
        JsonValueKind.Number when payload.TryGetInt32(out var n) => n,
        JsonValueKind.Object when payload.TryGetProperty("d", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var n) => n,
        _ => null,
    };

    public override TickResult Tick()
    {
        if (_winners is not null) return TickResult.None;
        switch (_phase)
        {
            case "ready":
                if (--_phaseLeft > 0) return TickResult.FrameOnly;
                _phase = "play";
                return TickResult.Both;

            case "between":
                if (--_phaseLeft > 0) return TickResult.FrameOnly;
                NewRound();
                return TickResult.Both;

            default:
                var dead = Core.RoundTicks >= CurveCore.MaxRoundTicks ? Core.StopAll() : Core.Step();
                if (dead.Count > 0)
                    for (var s = 0; s < CurveCore.Seats; s++)
                        if (_seats[s] && Core.Heads[s].Alive) _scores[s] += dead.Count;
                if (Core.AliveCount > 1) return dead.Count > 0 ? TickResult.Both : TickResult.FrameOnly;
                EndRound();
                return TickResult.Both;
        }
    }

    /// <summary>Раунд скінчився: або йдемо на наступний, або партію зіграно.</summary>
    void EndRound()
    {
        var best = Leaders();
        if (best.Length > 0 && _scores[best[0]] >= _target)
        {
            _winners = best;
            _phase = "done";
            Ctx.Finish(best, $"{Info.Title}: {Table()}");
            return;
        }
        _phase = "between";
        _phaseLeft = CurveCore.BetweenTicks;
    }

    /// <summary>Місця з найбільшим рахунком серед тих, хто ще за столом.</summary>
    int[] Leaders()
    {
        var playing = Enumerable.Range(0, CurveCore.Seats).Where(s => _seats[s]).ToArray();
        if (playing.Length == 0) return [];
        var best = playing.Max(s => _scores[s]);
        return [.. playing.Where(s => _scores[s] == best)];
    }

    /// <summary>«Оля жовта 10, Петро зелена 7» — рахунок читається і на двох, і на чотирьох.</summary>
    string Table() => string.Join(", ", Enumerable.Range(0, CurveCore.Seats)
        .Where(s => _seats[s] && Ctx.NickOf(s) is not null)
        .OrderByDescending(s => _scores[s])
        .Select(s => $"{Ctx.NickOf(s)} {SeatName(s)} {_scores[s]}"));

    /// <summary>
    /// Хтось встав. На двох це кінець партії, а в компанії — ні: кривуля того, хто пішов, просто гасне,
    /// а решта дограє. Каркас кличе це ще до того, як звільнить місце, тому решту рахуємо явно.
    /// </summary>
    public override void OnLeave(int seat)
    {
        _seats[seat] = false;
        Core.Heads[seat].Present = false;
        Core.Heads[seat].Alive = false;
        var rest = Enumerable.Range(0, CurveCore.Seats).Where(s => s != seat && Ctx.Seated(s)).ToArray();
        if (rest.Length >= 2) return;
        _winners = rest;
        _phase = "done";
        Ctx.Finish(rest, rest.Length == 1
            ? $"{Info.Title}: {Ctx.NickOf(seat)} встав з-за столу, {Ctx.NickOf(rest[0])} лишився сам"
            : $"{Info.Title}: партію не дограли");
    }

    /// <summary>Скільки мілісекунд лишилось до кінця фази; у грі — нуль.</summary>
    int StartIn => _phase is "ready" or "between" ? Math.Max(0, _phaseLeft) * CurveCore.TickMs : 0;

    static double R1(double v) => Math.Round(v, 1);

    /// <summary>Голови так, як їх малює клієнт; порожнє місце — null, щоб індекси збігались із місцями.</summary>
    object?[] HeadsWire() => [.. Core.Heads.Select(h => h.Present
        ? new
        {
            x = R1(h.X),
            y = R1(h.Y),
            a = (int)Math.Round(h.A * 180 / Math.PI),
            alive = h.Alive,
            gap = h.Gap,
        }
        : null)];

    public override object? Frame() => new
    {
        t = Core.RoundTicks,
        r = _round,
        heads = HeadsWire(),
        s = (int[])_scores.Clone(),
        phase = _phase,
        startIn = StartIn,
    };

    public override object View(int? seat) => new
    {
        width = CurveCore.W,
        height = CurveCore.H,
        turn = (int?)null,
        round = _round,
        target = _target,
        phase = _phase,
        startIn = StartIn,
        scores = (int[])_scores.Clone(),
        heads = HeadsWire(),
        // Растр цілком — це 60 КБ на кожне перемальовування, тому клієнтові їде ламана: пари x,y
        // і номери точок, у які слід не веде (дірки).
        segments = (object?[])[.. Core.Heads.Select(h =>
        {
            if (!h.Present) return null;
            var (pts, gaps) = CurveCore.Polyline(h.Trail);
            return (object)new { pts, gaps };
        })],
        winners = _winners,
    };
}
