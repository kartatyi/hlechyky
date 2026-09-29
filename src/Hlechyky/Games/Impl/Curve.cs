using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Точка сліду: де була голова і чи писався в цю мить слід (у дірці — ні). <c>Jump</c> — голова щойно
/// перескочила крізь край поля (тор або 🚪): лінію від попередньої точки не малюємо. <c>Fat</c> — ⬛ товстий слід.
/// </summary>
public readonly record struct CurvePoint(double X, double Y, bool Gap, bool Jump = false, bool Fat = false);

/// <summary>Кружечок бонуса на полі (прохід №3, опція «Бонуси»).</summary>
public sealed record CurveBonus(int Kind, double X, double Y);

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
    /// <summary>До якої точки сліду (включно) вона вже лягла в растр.</summary>
    public int Painted = -1;
    /// <summary>Скільки тиків ще діє бонус: ⚡ швидше, 🐢 повільніше, 🔄 кермо навпаки, 🚪 крізь стіни, ⬛ товстий слід.</summary>
    public int Fast, Slow, Inv, Through, Fat;
    /// <summary>Бонуси, що зараз діють, бітами (<see cref="CurveCore.FxFast"/>…) — для кадру.</summary>
    public int Fx => (Fast > 0 ? CurveCore.FxFast : 0) | (Slow > 0 ? CurveCore.FxSlow : 0) | (Inv > 0 ? CurveCore.FxInv : 0)
        | (Through > 0 ? CurveCore.FxThrough : 0) | (Fat > 0 ? CurveCore.FxFat : 0);
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
/// <param name="w">Ширина поля; типово — звичні 300 (див. <see cref="SizeFor"/>).</param>
/// <param name="h">Висота поля; типово — звичні 200.</param>
public sealed class CurveCore(Random rng, bool gaps = true, int w = CurveCore.SmallW, int h = CurveCore.SmallH)
{
    /// <summary>Звичне поле на двох–чотирьох: 300 × 200 умовних одиниць.</summary>
    public const int SmallW = 300, SmallH = 200;

    /// <summary>Поле в умовних одиницях; воно ж — растр зіткнень, одна одиниця на клітинку.</summary>
    public int W { get; } = w;
    public int H { get; } = h;

    /// <summary>
    /// Розмір поля під склад. До чотирьох — звичні 300×200 (відчуття гри не міняємо), на п'ятьох-шістьох —
    /// 360×240, на сімох-вісьмох — 420×280: на кожного лишається приблизно стільки ж місця, як і вчотирьох.
    /// </summary>
    public static (int W, int H) SizeFor(int players) => players switch
    {
        <= 4 => (SmallW, SmallH),
        <= 6 => (360, 240),
        _ => (420, 280),
    };

    public const int TickMs = 40;
    /// <summary>Місць за столом (і довжина всіх масивів на дроті): як у класичній Achtung, die Kurve! — до восьми.</summary>
    public const int Seats = 8;
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

    readonly byte[] _grid = new byte[w * h];

    /// <summary>Голови за номерами місць; невзяті місця мають <c>Present == false</c>.</summary>
    public CurveHead[] Heads { get; } = [.. Enumerable.Range(0, Seats).Select(_ => new CurveHead())];

    /// <summary>Скільки тиків триває цей раунд.</summary>
    public int RoundTicks { get; private set; }

    public int AliveCount => Heads.Count(h => h.Present && h.Alive);

    /// <summary>Що лежить у клітинці растра: 0 — порожньо, інакше номер місця + 1.</summary>
    public byte Cell(int x, int y) => x < 0 || y < 0 || x >= W || y >= H ? (byte)0 : _grid[y * W + x];

    // ---------- прохід №3: тор, бонуси, «хто кого» ----------

    /// <summary>Поле-тор: стін нема, вилетів справа — з'явився зліва (опція «Стіни»).</summary>
    public bool Wrap { get; init; }
    /// <summary>На полі з'являються бонуси класичної Achtung (опція «Бонуси»).</summary>
    public bool Bonuses { get; init; }

    /// <summary>Бонуси: ⚡ собі, ⚡ усім іншим, 🐢 собі, 🐢 іншим, 🔄 кермо навпаки іншим, 🧹 стерти всі сліди, 🚪 собі крізь стіни, ⬛ товстий слід іншим.</summary>
    public const int BFastMe = 0, BFastThem = 1, BSlowMe = 2, BSlowThem = 3, BInvert = 4, BClear = 5, BThrough = 6, BFat = 7, BonusKinds = 8;
    public const int FxFast = 1, FxSlow = 2, FxInv = 4, FxThrough = 8, FxFat = 16;
    /// <summary>Скільки діє бонус: ⚡🐢⬛ — 5 с, 🔄 — 4 с, 🚪 — 6 с.</summary>
    public const int EffectTicks = 125, InvertTicks = 100, ThroughTicks = 150;
    /// <summary>Радіус кружечка бонуса; взяв — коли голова його торкнулась.</summary>
    public const double BonusR = 5;
    /// <summary>Більше кружечків на полі не буває: інакше поле — ярмарок, а не гра.</summary>
    public const int MaxBonuses = 4;
    /// <summary>Новий бонус — кожні 2,4–5 с.</summary>
    public const int BonusMinTicks = 60, BonusMaxTicks = 125;
    public const double FastK = 1.75, SlowK = 0.55;
    /// <summary>⬛ Товстий слід — радіус 3,5 замість 2.</summary>
    public const double FatR = 3.5;

    public List<CurveBonus> Items { get; } = [];
    int _nextBonusIn = BonusMinTicks;
    /// <summary>Скільки разів 🧹 стерло поле цього раунду: клієнт за цим числом чистить свій канвас.</summary>
    public int Cleared { get; private set; }

    /// <summary>Хто вибув цього тика і через кого: Killer — місце (чий слід чи чия голова), -1 — стіна.</summary>
    public List<(int Victim, int Killer, bool Head)> Deaths { get; } = [];
    /// <summary>Хто що взяв цього тика.</summary>
    public List<(int Seat, int Kind)> Picks { get; } = [];

    /// <summary>Новий раунд: чистий растр, нові точки старту для тих, хто сидить за столом.</summary>
    public void Reset(IReadOnlyList<bool> present)
    {
        Array.Clear(_grid);
        RoundTicks = 0;
        Items.Clear();
        Cleared = 0;
        Deaths.Clear();
        Picks.Clear();
        _nextBonusIn = BonusMinTicks;
        var placed = new List<(double X, double Y)>();
        for (var s = 0; s < Seats; s++)
        {
            var h = Heads[s];
            h.Trail.Clear();
            h.Painted = -1;
            h.Fast = h.Slow = h.Inv = h.Through = h.Fat = 0;
            h.Present = s < present.Count && present[s];
            h.Alive = h.Present;
            h.Turn = 0;
            h.Gap = false;
            h.GapLeft = 0;
            // Генератор смикаємо лише за тих, хто справді їде: інакше порожній стіл у лобі (його вид
            // теж просить поле) зсував би всю випадковість партії залежно від того, чи хтось глянув.
            if (!h.Present) continue;
            h.NextGapIn = NextGap();

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

    /// <summary>Швидкість кривулі з урахуванням ⚡ і 🐢 (обидва разом — майже звична).</summary>
    public static double SpeedOf(CurveHead h) => Speed * (h.Fast > 0 ? FastK : 1) * (h.Slow > 0 ? SlowK : 1);

    readonly double[] _nx = new double[Seats], _ny = new double[Seats];
    readonly bool[] _jump = new bool[Seats];
    readonly int[] _killer = new int[Seats];
    readonly bool[] _byHead = new bool[Seats];

    /// <summary>Один крок усіх кривуль. Повертає місця, що загинули саме цього тика.</summary>
    public List<int> Step()
    {
        RoundTicks++;
        Deaths.Clear();
        Picks.Clear();
        var died = new List<int>();
        var nx = _nx;
        var ny = _ny;

        // Спершу рахуємо й перевіряємо всіх — і лише потім рухаємо й малюємо. Інакше той, хто
        // йде першим у циклі, встигав би домалювати слід під носом у другого.
        for (var s = 0; s < Seats; s++)
        {
            var h = Heads[s];
            if (!h.Present || !h.Alive) continue;
            h.A += (h.Inv > 0 ? -h.Turn : h.Turn) * TurnStep;
            var sp = SpeedOf(h);
            nx[s] = h.X + Math.Cos(h.A) * sp;
            ny[s] = h.Y + Math.Sin(h.A) * sp;
            _jump[s] = false;
            var through = Wrap || h.Through > 0;
            if (through)
            {
                // Крізь край: з'являємось з протилежного боку, растр теж «загорнутий» (див. HitBy/Paint).
                if (nx[s] < 0) { nx[s] += W; _jump[s] = true; } else if (nx[s] >= W) { nx[s] -= W; _jump[s] = true; }
                if (ny[s] < 0) { ny[s] += H; _jump[s] = true; } else if (ny[s] >= H) { ny[s] -= H; _jump[s] = true; }
            }
            Breathe(h);
            _byHead[s] = false;
            if (!through && Wall(nx[s], ny[s])) { died.Add(s); _killer[s] = -1; }
            else if (HitBy(nx[s], ny[s], h.A) is var c && c > 0) { died.Add(s); _killer[s] = c - 1; }
        }

        // Лоб у лоб растром не ловиться: у кожного попереду ще не намальований шматок власного сліду.
        for (var a = 0; a < Seats; a++)
            for (var b = a + 1; b < Seats; b++)
            {
                if (!Heads[a].Present || !Heads[a].Alive || !Heads[b].Present || !Heads[b].Alive) continue;
                var (dx, dy) = (nx[a] - nx[b], ny[a] - ny[b]);
                if (Wrap)
                {
                    // на торі найкоротший шлях між головами може йти крізь край
                    if (dx > W / 2.0) dx -= W; else if (dx < -W / 2.0) dx += W;
                    if (dy > H / 2.0) dy -= H; else if (dy < -H / 2.0) dy += H;
                }
                if (dx * dx + dy * dy >= 4 * R * R) continue;
                // Гине лише той, у кого чужа голова спереду — те саме правило, що й у Hits(). Лоб у лоб
                // це обидва, а от наздогнати ззаду — біда лише заднього: лідер нічого не робив.
                if (-dx * Math.Cos(Heads[a].A) - dy * Math.Sin(Heads[a].A) > 0 && !died.Contains(a)) { died.Add(a); _killer[a] = b; _byHead[a] = true; }
                if (dx * Math.Cos(Heads[b].A) + dy * Math.Sin(Heads[b].A) > 0 && !died.Contains(b)) { died.Add(b); _killer[b] = a; _byHead[b] = true; }
            }

        for (var s = 0; s < Seats; s++)
        {
            var h = Heads[s];
            if (!h.Present || !h.Alive || died.Contains(s)) continue;
            h.X = nx[s];
            h.Y = ny[s];
            h.Trail.Add(new CurvePoint(h.X, h.Y, h.Gap, _jump[s], h.Fat > 0));
        }
        for (var s = 0; s < Seats; s++)
        {
            var h = Heads[s];
            if (h.Present && h.Alive && !died.Contains(s)) Paint(s);
        }
        foreach (var s in died) Heads[s].Alive = false;
        died.Sort();
        foreach (var s in died) Deaths.Add((s, _killer[s], _byHead[s]));
        if (Bonuses) BonusStep();
        return died;
    }

    /// <summary>Бонуси: таймери, підбір, нові кружечки. Кличеться лише з опцією «Бонуси», тож звичайна гра генератор не смикає.</summary>
    void BonusStep()
    {
        for (var s = 0; s < Seats; s++)
        {
            var h = Heads[s];
            if (!h.Present || !h.Alive) continue;
            if (h.Fast > 0) h.Fast--;
            if (h.Slow > 0) h.Slow--;
            if (h.Inv > 0) h.Inv--;
            if (h.Fat > 0) h.Fat--;
            // 🚪 скінчилась, поки голова ще в «стіні» біля краю, — не караємо, даємо доїхати в поле
            if (h.Through > 0 && --h.Through == 0 && Wall(h.X, h.Y)) h.Through = 1;
        }
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            var b = Items[i];
            for (var s = 0; s < Seats; s++)
            {
                var h = Heads[s];
                if (!h.Present || !h.Alive) continue;
                var (dx, dy) = (h.X - b.X, h.Y - b.Y);
                if (dx * dx + dy * dy > (BonusR + R) * (BonusR + R)) continue;
                Items.RemoveAt(i);
                Apply(s, b.Kind);
                Picks.Add((s, b.Kind));
                break;
            }
        }
        if (--_nextBonusIn > 0) return;
        _nextBonusIn = rng.Next(BonusMinTicks, BonusMaxTicks + 1);
        if (Items.Count < MaxBonuses) TrySpawnBonus();
    }

    /// <summary>Бонус спрацював: собі, усім іншим живим чи всьому полю.</summary>
    public void Apply(int seat, int kind)
    {
        var me = Heads[seat];
        switch (kind)
        {
            case BFastMe: me.Fast = EffectTicks; break;
            case BSlowMe: me.Slow = EffectTicks; break;
            case BThrough: me.Through = ThroughTicks; break;
            case BClear:
                Array.Clear(_grid);
                foreach (var h in Heads)
                {
                    if (!h.Present) continue;
                    h.Trail.Clear();
                    h.Painted = -1;
                    if (h.Alive) h.Trail.Add(new CurvePoint(h.X, h.Y, true));
                }
                Cleared++;
                break;
            default:
                for (var s = 0; s < Seats; s++)
                {
                    var h = Heads[s];
                    if (s == seat || !h.Present || !h.Alive) continue;
                    switch (kind)
                    {
                        case BFastThem: h.Fast = EffectTicks; break;
                        case BSlowThem: h.Slow = EffectTicks; break;
                        case BInvert: h.Inv = InvertTicks; break;
                        case BFat: h.Fat = EffectTicks; break;
                    }
                }
                break;
        }
    }

    /// <summary>Кружечок — на порожнє місце, не під носом у когось (≥ 25 од) і не на сліді.</summary>
    void TrySpawnBonus()
    {
        const int margin = 12;
        for (var i = 0; i < 20; i++)
        {
            var x = Math.Round(margin + rng.NextDouble() * (W - 2 * margin));
            var y = Math.Round(margin + rng.NextDouble() * (H - 2 * margin));
            var kind = rng.Next(BonusKinds);
            if (Wrap && kind == BThrough) kind = BClear;   // на торі стін і так нема
            var ok = true;
            foreach (var h in Heads)
                if (h.Present && h.Alive && (h.X - x) * (h.X - x) + (h.Y - y) * (h.Y - y) < 25 * 25) ok = false;
            foreach (var b in Items)
                if ((b.X - x) * (b.X - x) + (b.Y - y) * (b.Y - y) < 20 * 20) ok = false;
            for (var dy = -6; dy <= 6 && ok; dy += 3)
                for (var dx = -6; dx <= 6 && ok; dx += 3)
                    if (Cell((int)x + dx, (int)y + dy) != 0) ok = false;
            if (!ok) continue;
            Items.Add(new CurveBonus(kind, x, y));
            return;
        }
    }

    /// <summary>Раунд затягнувся — гасимо всіх, хто ще їде. Повертає їхні місця.</summary>
    public List<int> StopAll()
    {
        Deaths.Clear();
        Picks.Clear();
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
    public bool Wall(double x, double y) => x < R || y < R || x > W - R || y > H - R;

    /// <summary>
    /// Чи є слід під передньою півкулею голови. Позаду завжди свій хвіст, тому дивимось лише туди,
    /// куди їдемо: скалярний добуток на курс має бути додатним.
    /// </summary>
    public bool Hits(double x, double y, double a) => HitBy(x, y, a) != 0;

    /// <summary>
    /// Те саме, що <see cref="Hits"/>, але каже, чий слід: 0 — нічий, інакше номер місця + 1. Растр
    /// «загорнутий» по краях: без тора голова до краю не доїжджає (стіна раніше), тож звичайній грі це байдуже.
    /// </summary>
    public byte HitBy(double x, double y, double a)
    {
        var (cx, cy) = (Math.Cos(a), Math.Sin(a));
        var (hx, hy) = ((int)Math.Floor(x), (int)Math.Floor(y));
        for (var dy = -2; dy <= 2; dy++)
            for (var dx = -2; dx <= 2; dx++)
            {
                var (gx, gy) = (hx + dx, hy + dy);
                var (vx, vy) = (gx + 0.5 - x, gy + 0.5 - y);
                if (vx * vx + vy * vy > R * R) continue;
                if (vx * cx + vy * cy <= 0) continue;
                if (gx < 0) gx += W; else if (gx >= W) gx -= W;
                if (gy < 0) gy += H; else if (gy >= H) gy -= H;
                var c = _grid[gy * W + gx];
                if (c != 0) return c;
            }
        return 0;
    }

    /// <summary>
    /// Домалювати в растр точки сліду, що вже досить позаду голови. Звично це рівно одна точка TrailLag
    /// тиків тому; ⬛ товстий слід чи 🐢 повільна голова просять відстати більше — інакше голова
    /// заїжджала б передньою півкулею у власний щойно намальований слід.
    /// </summary>
    void Paint(int seat)
    {
        var h = Heads[seat];
        var trail = h.Trail;
        if (h.Painted >= trail.Count) h.Painted = trail.Count - 1;
        var sp = SpeedOf(h);
        var id = (byte)(seat + 1);
        while (h.Painted + 1 < trail.Count)
        {
            var i = h.Painted + 1;
            var p = trail[i];
            var r = p.Fat ? FatR : R;
            // на наступній перевірці точка буде (lag + 1)·sp позаду голови — це мусить бути більше за радіус сліду
            var lag = Math.Max(TrailLag, (int)Math.Ceiling((r + 0.6) / sp - 1e-9) - 1);
            if (i > trail.Count - 1 - lag) break;
            h.Painted = i;
            if (p.Gap) continue;
            var (hx, hy) = ((int)Math.Floor(p.X), (int)Math.Floor(p.Y));
            var ri = (int)Math.Ceiling(r);
            for (var dy = -ri; dy <= ri; dy++)
                for (var dx = -ri; dx <= ri; dx++)
                {
                    var (gx, gy) = (hx + dx, hy + dy);
                    var (vx, vy) = (gx + 0.5 - p.X, gy + 0.5 - p.Y);
                    if (vx * vx + vy * vy > r * r) continue;
                    if (!Wrap && (gx < 0 || gy < 0 || gx >= W || gy >= H)) continue;
                    if (gx < 0) gx += W; else if (gx >= W) gx -= W;
                    if (gy < 0) gy += H; else if (gy >= H) gy -= H;
                    _grid[gy * W + gx] = id;
                }
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
        // Не пощастило (буває хіба що при зміні констант) — розводимо по сітці 4×2, аби раунд не завис.
        var spot = placed.Count % Seats;
        return (W * (0.125 + 0.25 * (spot % 4)), spot < 4 ? H * 0.3 : H * 0.7);
    }

    /// <summary>Максимум точок, які можна злити в один прямий пробіг: далі похибка вже помітна.</summary>
    const int MaxRun = 40;

    /// <summary>
    /// Скільки точок ламаної віддаємо на одну кривулю. Стиснення прямих ділянок нічого не дає тому,
    /// хто в'ється всі <see cref="MaxRoundTicks"/> тиків: у нього лишається понад тисяча точок, і
    /// вчотирьох вид перевалив би за обіцяні 32 КБ. Чотири по стільки — це 14 КБ.
    /// </summary>
    public const int MaxPts = 500;

    /// <summary>
    /// Стеля точок на кривулю за столом на <paramref name="players"/>: весь вид тримаємо в тих самих ~2000
    /// точок, що й учотирьох, — на вісьмох кожна кривуля отримує 250.
    /// </summary>
    public static int PtsFor(int players) => MaxPts * 4 / Math.Max(4, players);

    /// <summary>
    /// Ламана для клієнта: цілі координати (растр усе одно цілий) і викинуті точки, що лежать на
    /// прямій. Пряма ділянка з двохсот точок так стискається до двох, і хвилина раунду вкладається
    /// в кілька кілобайтів замість десятків.
    /// </summary>
    /// <returns>
    /// Pts — пари x,y підряд; Gaps — номери точок, у які слід НЕ веде (там дірка).
    /// </returns>
    /// <param name="trail">Слід однієї кривулі.</param>
    /// <param name="maxPts">Стеля точок на кривулю; на повному столі її ділять на більше людей (<see cref="PtsFor"/>).</param>
    /// <param name="fat">Якщо дали — сюди складаються номери точок, у які слід веде ⬛ товстим (бонус).</param>
    public static (int[] Pts, int[] Gaps) Polyline(IReadOnlyList<CurvePoint> trail, int maxPts = MaxPts, List<int>? fat = null)
    {
        var pts = new List<int>(trail.Count * 2);
        var gapAt = new List<bool>(trail.Count);
        var fatAt = new List<bool>(trail.Count);
        var run = 0;
        foreach (var p in trail)
        {
            var (x, y) = ((int)Math.Round(p.X), (int)Math.Round(p.Y));
            // Перескок крізь край поля — як дірка: лінію через усе поле не тягнемо.
            var gap = p.Gap || p.Jump;
            var n = gapAt.Count;
            if (n > 0 && !gap && fatAt[n - 1] == p.Fat && pts[2 * (n - 1)] == x && pts[2 * (n - 1) + 1] == y) continue;
            if (n >= 2 && !gap && !gapAt[n - 1] && run < MaxRun && fatAt[n - 1] == p.Fat
                && OnLine(pts[2 * (n - 2)], pts[2 * (n - 2) + 1], x, y, pts[2 * (n - 1)], pts[2 * (n - 1) + 1]))
            {
                pts[2 * (n - 1)] = x;
                pts[2 * (n - 1) + 1] = y;
                run++;
                continue;
            }
            pts.Add(x);
            pts.Add(y);
            gapAt.Add(gap);
            fatAt.Add(p.Fat);
            run = 0;
        }
        if (gapAt.Count > maxPts) (pts, gapAt, fatAt) = Thin(pts, gapAt, fatAt, maxPts);
        var gaps = new List<int>();
        for (var i = 0; i < gapAt.Count; i++)
        {
            if (gapAt[i]) gaps.Add(i);
            if (fatAt[i]) fat?.Add(i);
        }
        return ([.. pts], [.. gaps]);
    }

    /// <summary>
    /// Занадто довгу ламану проріджуємо: лишаємо кожну k-ту точку. Дірку з викинутого шматка
    /// переносимо на ту точку, що лишилась, — краще не домалювати кілька одиниць сліду, ніж
    /// провести лінію крізь дірку, якої на полі нема.
    /// </summary>
    static (List<int> Pts, List<bool> GapAt, List<bool> FatAt) Thin(List<int> pts, List<bool> gapAt, List<bool> fatAt, int maxPts)
    {
        var k = (gapAt.Count + maxPts - 1) / maxPts;
        var thinPts = new List<int>(maxPts * 2 + 2);
        var thinGap = new List<bool>(maxPts + 1);
        var thinFat = new List<bool>(maxPts + 1);
        var gap = false;
        var fat = false;
        for (var i = 0; i < gapAt.Count; i++)
        {
            gap |= gapAt[i];
            fat |= fatAt[i];
            if (i % k != 0 && i != gapAt.Count - 1) continue;
            thinPts.Add(pts[2 * i]);
            thinPts.Add(pts[2 * i + 1]);
            thinGap.Add(gap);
            thinFat.Add(fat);
            gap = false;
            fat = false;
        }
        return (thinPts, thinGap, thinFat);
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
/// Кривуля на 2–8 гравців: їдеш уперед, лишаєш слід, повертати можна лише плавно. Партія — це низка
/// раундів у тій самій кімнаті: хто вибув, тому вже нема куди поспішати, а живі беруть по очку за
/// кожного вибулого. Дограли до <c>10 × (гравців − 1)</c> — партія скінчилась.
/// </summary>
public sealed class CurveGame : Game
{
    public override GameInfo Info { get; } = new(
        "curve", "Кривуля", "кривулю", GameGroup.Live, 1, CurveCore.Seats,
        TickMs: CurveCore.TickMs, Start: StartMode.ByHost,
        Options:
        [
            new GameOption("walls", "Стіни", [("0", "смертельні"), ("1", "🍩 нема — поле-тор")], "0"),
            new GameOption("bonus", "Бонуси", [("0", "без бонусів"), ("1", "⚡🐢🔄🧹🚪⬛ як в Achtung")], "0"),
            new GameOption("teams", "Грають", [("0", "кожен сам"), ("1", "команди 2×2 / 3×3 / 4×4")], "0"),
            LiveBots.LevelOption,
        ],
        Hint: "Їдеш уперед і лишаєш слід. Повертати можна тільки плавно. Врізався — вибув. Останній живий бере очко. До восьми за столом. Самому — з 🤖 ботами");

    /// <summary>
    /// Скільки ботів, коли людина сама: троє. «Останній живий» на двох — це дуель, де все вирішує одна помилка;
    /// учотирьох поле 300×200 (те саме, що й на двох, див. <see cref="CurveCore.SizeFor"/>) стає класичною Achtung:
    /// боти ріжуть і одне одного, можна пересидіти тісняву, а очки за кожного вибулого роблять і друге місце вартим.
    /// </summary>
    public const int SoloBots = 3;
    /// <summary>Імена ботів — у чоловічому роді кольору місця: «🤖 бот зелений» (як «🤖 бот рудий» у Крижині).</summary>
    static readonly string[] BotColours = ["жовтий", "зелений", "глиняний", "білий", "синій", "рожевий", "фіалковий", "червоний"];
    readonly SoloBot _solo = new();
    /// <summary>Місця ботів у цій партії (порожньо — партія людська).</summary>
    int[] _bots = [];
    readonly CurveBot?[] _brain = new CurveBot?[CurveCore.Seats];
    /// <summary>Партія вже стартувала хоч раз: до того стіл — лобі, де дія одна — покликати бота.</summary>
    bool _started;
    public IReadOnlyList<int> Bots => _bots;
    public LiveBots.Level BotLevel => _solo.Level;

    /// <summary>Скільки очок за партію треба на кожного суперника.</summary>
    public const int PerRival = 10;

    /// <summary>Команди: по черзі за тим, як сіли, — перше, третє, п'яте місце проти другого, четвертого…</summary>
    public static readonly string[] TeamNames = ["🐍 Вужі", "🦎 Ящірки"];

    CurveCore? _core;
    int[] _scores = new int[CurveCore.Seats];
    bool[] _seats = new bool[CurveCore.Seats];
    int _target;
    int _round;
    string _phase = "ready";
    /// <summary>Тиків до кінця «Готуйсь» або паузи між раундами.</summary>
    int _phaseLeft;
    int[]? _winners;

    // опції столу
    bool _wrap, _bonus, _teamsOn;
    /// <summary>Команда кожного місця на цю партію (0/1, -1 — не грає); null — кожен сам.</summary>
    int[]? _teams;
    readonly int[] _teamPts = new int[2];
    /// <summary>Чому команд не вийшло — рядок для статусу.</summary>
    string? _note;
    /// <summary>🕸 Скільки разів у слід цього місця врізались суперники за партію.</summary>
    readonly int[] _kills = new int[CurveCore.Seats];
    /// <summary>Смерті й бонуси цього тика — для кадру: [жертва, через кого (-1 стіна), 1 — лоб у лоб].</summary>
    readonly List<int[]> _ev = [];
    readonly List<int[]> _pk = [];

    /// <summary>Поле партії: усі правила руху й зіткнень живуть там, а не тут.</summary>
    public CurveCore Field => Core;

    /// <summary>Команди партії (для тестів і вида).</summary>
    public int[]? Teams => _teams;

    /// <summary>Поле є ще до старту: стіл, що чекає на гравців, має виглядати як поле, а не як діра.</summary>
    CurveCore Core
    {
        get
        {
            if (_core is not null) return _core;
            _core = new CurveCore(Ctx.Rng) { Wrap = _wrap, Bonuses = _bonus };
            _core.Reset(new bool[CurveCore.Seats]);
            return _core;
        }
    }

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        _wrap = options.GetValueOrDefault("walls") == "1";
        _bonus = options.GetValueOrDefault("bonus") == "1";
        _teamsOn = options.GetValueOrDefault("teams") == "1";
        _solo.Configure(options);
        _core = null;
    }

    public override bool ActsInLobby => true;

    public override string? CanStart() => _solo.CanStart(Ctx, CurveCore.Seats);

    /// <summary>Куди сядуть боти: перші вільні місця, якщо їх кликали й людина одна.</summary>
    int[] BotSeats() => _solo.Active(Ctx, CurveCore.Seats)
        ? [.. Enumerable.Range(0, CurveCore.Seats).Where(s => !Ctx.Seated(s)).Take(SoloBots)] : [];

    /// <summary>Троє ботів з однаковим ім'ям плутались би в рахунку — додаємо колір місця.</summary>
    public override string? SeatBot(int seat) =>
        seat is >= 0 and < CurveCore.Seats && !Ctx.Seated(seat) && Array.IndexOf(_started ? _bots : BotSeats(), seat) >= 0
            ? $"{LiveBots.Name} {BotColours[seat]}" : null;

    string? Name(int seat) => SeatBot(seat) ?? Ctx.NickOf(seat);

    public override string SeatName(int seat) => seat switch
    {
        0 => "жовта",
        1 => "зелена",
        2 => "глиняна",
        3 => "біла",
        4 => "синя",
        5 => "рожева",
        6 => "фіалкова",
        7 => "червона",
        _ => "кривуля",
    };

    public override void Start()
    {
        _started = true;
        _bots = BotSeats();
        Array.Clear(_brain);
        // Думають у різні тики, щоб не смикались хором.
        for (var i = 0; i < _bots.Length; i++) _brain[_bots[i]] = new CurveBot(_solo.Level, i);
        _seats = [.. Enumerable.Range(0, CurveCore.Seats).Select(s => Ctx.Seated(s) || _bots.Contains(s))];
        var n = _seats.Count(x => x);
        // Поле — під склад: до чотирьох звичне, більшому столу — ширше (CurveCore.SizeFor).
        var (w, h) = CurveCore.SizeFor(n);
        _core = new CurveCore(Ctx.Rng, true, w, h) { Wrap = _wrap, Bonuses = _bonus };
        _scores = new int[CurveCore.Seats];
        Array.Clear(_teamPts);
        Array.Clear(_kills);
        SetupTeams();
        _target = _teams is not null ? PerRival * (n / 2) : PerRival * Math.Max(1, n - 1);
        _round = 0;
        _winners = null;
        NewRound();
    }

    /// <summary>Команди — лише на парному столі від чотирьох: 2×2, 3×3 чи 4×4. Інакше — кожен сам, і про це рядок.</summary>
    void SetupTeams()
    {
        _teams = null;
        _note = null;
        if (!_teamsOn) return;
        if (_bots.Length > 0)
        {
            // Боти — кожен сам за себе: «людина з ботом проти двох ботів» була б не соло, а лотерея напарника.
            _note = "З ботами команд нема — кожен сам";
            return;
        }
        var seated = Enumerable.Range(0, CurveCore.Seats).Where(s => _seats[s]).ToArray();
        if (seated.Length is not (4 or 6 or 8))
        {
            _note = "Команд не буде: треба четверо, шестеро чи восьмеро — граємо кожен сам";
            return;
        }
        _teams = [.. Enumerable.Repeat(-1, CurveCore.Seats)];
        for (var i = 0; i < seated.Length; i++) _teams[seated[i]] = i % 2;
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
        if (action == LiveBots.Toggle)
            return _started && _winners is null ? ActResult.Fail("Партія вже йде") : _solo.Switch(Ctx, seat, payload, CurveCore.Seats);
        if (!_started) return ActResult.Fail("Чекаємо на гравців");   // як казав каркас, поки лобі було не наше
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
        _ev.Clear();
        _pk.Clear();
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
                var timeout = Core.RoundTicks >= CurveCore.MaxRoundTicks;
                // Боти кермують до кроку поля — їхній ввід лягає в цей тик, як людський між тиками.
                if (!timeout) BotsThink();
                var dead = timeout ? Core.StopAll() : Core.Step();
                if (!timeout) Note();
                if (dead.Count > 0)
                {
                    if (_teams is not null) TeamScore(dead);
                    else
                        for (var s = 0; s < CurveCore.Seats; s++)
                            if (_seats[s] && Core.Heads[s].Alive) _scores[s] += dead.Count;
                }
                if (!RoundOver()) return dead.Count > 0 || _pk.Count > 0 ? TickResult.Both : TickResult.FrameOnly;
                EndRound();
                return TickResult.Both;
        }
    }

    /// <summary>Хто кого і хто що взяв — у кадр, а слід-пастки — у лічильник 🕸.</summary>
    void Note()
    {
        foreach (var (v, k, head) in Core.Deaths)
        {
            _ev.Add([v, k, head ? 1 : 0]);
            if (!head && k >= 0 && k != v && (_teams is null || _teams[k] != _teams[v])) _kills[k]++;
        }
        foreach (var (s, kind) in Core.Picks) _pk.Add([s, kind]);
    }

    /// <summary>
    /// Боти кермують кожен у свій тик тим самим <see cref="CurveCore.Turn"/>, що й людський <c>turn</c>: та сама
    /// швидкість, той самий крок повороту. Місце, на яке сіла людина, бот уже не чіпає.
    /// </summary>
    void BotsThink()
    {
        foreach (var s in _bots)
        {
            if (Ctx.Seated(s) || _brain[s] is not { } bot || !Core.Heads[s].Alive || !bot.Due(Core.RoundTicks)) continue;
            Core.Turn(s, bot.Think(Core, s, Ctx.Rng));
        }
    }

    /// <summary>Команді — по очку за кожного вибулого суперника, поки в неї хтось ще їде.</summary>
    void TeamScore(List<int> dead)
    {
        foreach (var d in dead)
            for (var t = 0; t < 2; t++)
                if (t != _teams![d] && TeamAlive(t)) _teamPts[t]++;
        for (var s = 0; s < CurveCore.Seats; s++)
            if (_teams![s] >= 0) _scores[s] = _teamPts[_teams[s]];
    }

    bool TeamAlive(int t)
    {
        for (var s = 0; s < CurveCore.Seats; s++)
            if (_seats[s] && _teams![s] == t && Core.Heads[s].Alive) return true;
        return false;
    }

    /// <summary>Раунд скінчився: живих ≤ 1, а в командах — живі лише з однієї команди.</summary>
    bool RoundOver()
    {
        if (Core.AliveCount <= 1) return true;
        return _teams is not null && !(TeamAlive(0) && TeamAlive(1));
    }

    /// <summary>Раунд скінчився: або йдемо на наступний, або партію зіграно.</summary>
    void EndRound()
    {
        var best = Leaders();
        if (best.Length > 0 && _scores[best[0]] >= _target)
        {
            EndGame(best);
            return;
        }
        _phase = "between";
        _phaseLeft = CurveCore.BetweenTicks;
    }

    /// <summary>Партію зіграно: рахунок усіх за столом іде в Журнал одним рядком.</summary>
    void EndGame(int[] best)
    {
        _winners = best;
        _phase = "done";
        if (_bots.Length == 0)
        {
            Ctx.Finish(best, $"{Info.Title}: {Table()}");
            return;
        }
        // З ботами — без нагород: переміг бот — winners порожні й вердикт; людина — вердикт з рівнем.
        var people = best.Where(Ctx.Seated).ToArray();
        var verdict = people.Length == 0 ? $"🤖 Кривулю взяв {Name(best[0])}"
            : best.Length == 1 ? $"🏆 {Ctx.NickOf(people[0])} — перемога над {LiveBots.Of(_solo.Level)}и ботами"
            : $"🤝 {Ctx.NickOf(people[0])} нарівні з {LiveBots.Of(_solo.Level)}и ботами";
        Ctx.Finish(people, $"{Info.Title}: {Table()}", verdict: verdict);
    }

    /// <summary>Місця з найбільшим рахунком серед тих, хто ще за столом.</summary>
    int[] Leaders()
    {
        var playing = Enumerable.Range(0, CurveCore.Seats).Where(s => _seats[s]).ToArray();
        if (playing.Length == 0) return [];
        var best = playing.Max(s => _scores[s]);
        return [.. playing.Where(s => _scores[s] == best)];
    }

    /// <summary>«Оля жовта 10, Петро зелена 7» — рахунок читається і на двох, і на чотирьох; у командах — по командах.</summary>
    string Table()
    {
        if (_teams is not null)
            return string.Join(", ", Enumerable.Range(0, 2).OrderByDescending(t => _teamPts[t]).Select(t =>
                $"{TeamNames[t]} ({string.Join(", ", Enumerable.Range(0, CurveCore.Seats).Where(s => _seats[s] && _teams[s] == t).Select(Ctx.NickOf))}) {_teamPts[t]}"));
        return string.Join(", ", Enumerable.Range(0, CurveCore.Seats)
            .Where(s => _seats[s] && Name(s) is not null)
            .OrderByDescending(s => _scores[s])
            .Select(s => SeatBot(s) is { } bot ? $"{bot} {_scores[s]}" : $"{Ctx.NickOf(s)} {SeatName(s)} {_scores[s]}"));
    }

    /// <summary>
    /// Хтось встав. На двох це кінець партії, а в компанії — ні: кривуля того, хто пішов, просто гасне,
    /// а решта дограє. Каркас кличе це ще до того, як звільнить місце, тому решту рахуємо явно.
    /// </summary>
    public override void OnLeave(int seat)
    {
        _seats[seat] = false;
        // Present не чіпаємо: слід того, хто пішов, лишається в растрі до кінця раунду і далі вбиває,
        // тож нехай його й видно. Місце прибере наступний Reset(_seats).
        Core.Heads[seat].Alive = false;
        var rest = Enumerable.Range(0, CurveCore.Seats).Where(s => s != seat && Ctx.Seated(s)).ToArray();
        var oneTeam = _teams is not null && rest.Length > 0 && rest.All(s => _teams[s] == _teams[rest[0]]);
        if (rest.Length >= 2 && !oneTeam)
        {
            // Стіл поменшав — ліміт партії теж: «до 10 × (гравців − 1)» рахується від тих, хто лишився.
            // У командах ліміт лишається: суперників менше, але й очок на раунд менше.
            if (_teams is null) _target = PerRival * Math.Max(1, rest.Length - 1);
            var best = Leaders();
            if (best.Length > 0 && _scores[best[0]] >= _target) EndGame(best);
            return;
        }
        _winners = rest;
        _phase = "done";
        Ctx.Finish(rest, rest.Length == 1
            ? $"{Info.Title}: {Ctx.NickOf(seat)} встає з-за столу — {Ctx.NickOf(rest[0])} лишається наодинці"
            : oneTeam ? $"{Info.Title}: {Ctx.NickOf(seat)} встає з-за столу — {TeamNames[_teams![rest[0]]]} лишаються самі"
            : $"{Info.Title}: партію не дограли");
    }

    /// <summary>Скільки мілісекунд лишилось до кінця фази; у грі — нуль.</summary>
    int StartIn => _phase is "ready" or "between" ? Math.Max(0, _phaseLeft) * CurveCore.TickMs : 0;

    static double R1(double v) => Math.Round(v, 1);

    /// <summary>Голови так, як їх малює клієнт; порожнє місце — null, щоб індекси збігались із місцями.</summary>
    object?[] HeadsWire() => [.. Core.Heads.Select(h => !h.Present ? null
        : _bonus
            ? new { x = R1(h.X), y = R1(h.Y), a = (int)Math.Round(h.A * 180 / Math.PI), alive = h.Alive, gap = h.Gap, fx = h.Fx }
            : (object)new { x = R1(h.X), y = R1(h.Y), a = (int)Math.Round(h.A * 180 / Math.PI), alive = h.Alive, gap = h.Gap })];

    /// <summary>Бонуси на полі: [вид, x, y].</summary>
    int[][] BonusWire() => [.. Core.Items.Select(b => new[] { b.Kind, (int)b.X, (int)b.Y })];

    public override object? Frame()
    {
        var f = new Dictionary<string, object>
        {
            ["t"] = Core.RoundTicks,
            ["r"] = _round,
            ["heads"] = HeadsWire(),
            ["s"] = (int[])_scores.Clone(),
            ["phase"] = _phase,
            ["startIn"] = StartIn,
        };
        if (_bonus)
        {
            f["b"] = BonusWire();
            f["k"] = Core.Cleared;
        }
        if (_ev.Count > 0) f["ev"] = _ev.ToArray();
        if (_pk.Count > 0) f["pk"] = _pk.ToArray();
        return f;
    }

    public override object View(int? seat)
    {
        var n = Core.Heads.Count(x => x.Present);
        var v = new Dictionary<string, object?>
        {
            ["width"] = Core.W,
            ["height"] = Core.H,
            ["turn"] = null,
            ["round"] = _round,
            ["target"] = _target,
            ["phase"] = _phase,
            ["startIn"] = StartIn,
            ["scores"] = (int[])_scores.Clone(),
            ["heads"] = HeadsWire(),
            // Растр цілком — це 60 КБ на кожне перемальовування, тому клієнтові їде ламана: пари x,y
            // і номери точок, у які слід не веде (дірки).
            ["segments"] = (object?[])[.. Core.Heads.Select(h =>
            {
                if (!h.Present) return null;
                var fat = new List<int>();
                var (pts, gaps) = CurveCore.Polyline(h.Trail, CurveCore.PtsFor(n), fat);
                return fat.Count > 0 ? new { pts, gaps, fat = fat.ToArray() } : (object)new { pts, gaps };
            })],
            ["winners"] = _winners,
            ["kills"] = (int[])_kills.Clone(),
            ["botOffer"] = _solo.Offer(Ctx, CurveCore.Seats),
            ["botWanted"] = _solo.Wanted,
            ["botLvl"] = _solo.LevelKey,
            ["bot"] = !_started ? BotSeats() : _bots.Where(s => !Ctx.Seated(s)).ToArray(),
        };
        if (_wrap) v["wrap"] = true;
        if (_bonus)
        {
            v["b"] = BonusWire();
            v["k"] = Core.Cleared;
        }
        if (_teams is not null) v["teams"] = (int[])_teams.Clone();
        if (_note is not null) v["note"] = _note;
        return v;
    }
}
