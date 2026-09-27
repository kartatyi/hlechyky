using System.Numerics;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Один селянин на ковзанах. Бот і гравець — той самий клас і той самий <see cref="SkateCore.Step"/>: гравцеві
/// <see cref="Want"/> пише ввід, ботові — <see cref="SkateCore.Think"/>. Координати й швидкість — у сабах (1/256
/// одиниці світу), усе ціле: той самий ввід дає ту саму траєкторію на будь-якій машині.
/// </summary>
public sealed class SkateVillager
{
    /// <summary>Номер у кадрі; за раунд не міняється, між раундами тасується.</summary>
    public int Id;
    /// <summary>Центр у сабах (одиниця світу × 256).</summary>
    public int X, Y;
    /// <summary>Швидкість у сабах за тик.</summary>
    public int Vx, Vy;
    /// <summary>Куди востаннє відштовхувався: 0 праворуч, далі за годинниковою, 7 — вгору-праворуч.</summary>
    public int Dir;
    /// <summary>Що тримає: −1 нічого (котиться), 0…7 — відштовхується в той бік, 8 — гальмує «плугом».</summary>
    public int Want = -1;
    /// <summary>Місце гравця; −1 — бот.</summary>
    public int Owner = -1;
    /// <summary>Лежить ще стільки тиків (збили чи впав сам): ковзає, але не керує.</summary>
    public int Fallen;
    /// <summary>У воді ще стільки тиків.</summary>
    public int Water;
    /// <summary>Гравець шубовснув і вибув на раунд: сидить на краю ополонки, у кожусі на плечах.</summary>
    public bool Out;
    /// <summary>Цього тика відштовхувався / гальмував — для кадру.</summary>
    public bool Pushing, Braking;
    /// <summary>Хто востаннє в нього врізався і коли (тик ядра) — кому зарахувати ополонку.</summary>
    public int LastBy = -1, LastAt = int.MinValue / 2;
    /// <summary>Скільки разів за раунд падав (на ачівку «Твердо на ногах»).</summary>
    public int Falls;

    // ---- мозок бота ----
    public int Mode, Hold, Stand, ModeLeft, Alarm;
    /// <summary>Ціль у сабах.</summary>
    public int Tx, Ty;
    public int Cruise, Seg, Segs, Spin, Chase = -1, Item = -1;

    // ---- вигляд на раунд ----
    public int Hat, HatColor, Coat, Scarf, Name;

    /// <summary>Стан у кадрі: 0 котиться/стоїть, 1 відштовхується, 2 лежить, 3 у воді, 4 вибув (на березі), 5 гальмує.</summary>
    public int State => Out ? 4 : Water > 0 ? 3 : Fallen > 0 ? 2 : Braking ? 5 : Pushing ? 1 : 0;
    public bool OnIce => !Out && Water == 0;
    public bool Upright => OnIce && Fallen == 0;
}

/// <summary>Ополонка: коло на льоду. Поки <see cref="Warn"/> більше нуля — це лише тріщини, видно всім заздалегідь.</summary>
public sealed class SkateHole
{
    /// <summary>Центр і радіус в одиницях світу.</summary>
    public int X, Y, R;
    public int Warn;
    public bool Open => Warn == 0;
}

/// <summary>Ласощі на льоду: вид і місце; <see cref="Wait"/> більше нуля — підхопили, бабця ще не докинула нове.</summary>
public sealed class SkateItem
{
    public int Kind, X, Y, Wait;
    public bool Here => Wait == 0;
}

/// <summary>
/// Ставок без кімнат і очок: селяни, одна спільна фізика, зіткнення, ополонки й ласощі, мозок ботів, розстановка
/// раунду. Усе ціле (сабах, тиках), уся випадковість — з переданого <see cref="Random"/> у строгому порядку, тож той
/// самий сід і той самий ввід дають побайтно ті самі кадри. У гарячому циклі — жодної алокації, крім кадру.
/// </summary>
public sealed class SkateCore(Random rng)
{
    /// <summary>Сабів в одиниці світу.</summary>
    public const int Fp = 256;
    /// <summary>Радіус селянина (для зіткнень) і половина його коробки (для берега) — в одиницях.</summary>
    public const int R = 10;
    /// <summary>Відштовхнувся — +40 сабів/тик за тик (0,16 од./тик²): до найбільшої швидкості ≈ 1,5 с.</summary>
    public const int Accel = 40;
    /// <summary>Найбільша швидкість: 6 од./тик = 150 од./с ≈ 4,7 клітинки/с (удвічі швидше за ходу в Юрмі).</summary>
    public const int Vmax = 1536;
    /// <summary>Тертя льоду: мінус 1/48 швидкості й ще 2 саби за тик уздовж руху. З повного ходу котиться ≈ 9 клітинок.</summary>
    public const int Drag = 48, DragConst = 2;
    /// <summary>«Плуг»: мінус 96 сабів/тик — з повного ходу стоїш за ≈ 0,6 с.</summary>
    public const int BrakeDecel = 96;
    /// <summary>Лежачий ковзає на животі: тертя 1/14 і 6 сабів.</summary>
    public const int FallDrag = 14, FallConst = 6;
    /// <summary>Від снігового берега відскакують із половиною швидкості.</summary>
    public const int WallNum = 1, WallDen = 2;
    /// <summary>Пружність зіткнень селян: (1 + e) / 2 при e = 0,8.</summary>
    public const int RestNum = 9, RestDen = 10;
    /// <summary>Зближення від 900 сабів/тик (3,5 од./тик) — таран: повільніший падає, обидва однаково швидкі — обидва.</summary>
    public const int KnockSpeed = 900;
    /// <summary>Лежить 2 с.</summary>
    public const int FallTicks = 50;
    /// <summary>
    /// Різко розвернувся на швидкості (тисне проти руху, кут понад 135°, швидкість від 1000) — щотика 8 з 1000, що
    /// ковзани зачепляться й упадеш «на рівному місці». Однаково для всіх: так падають і боти, і люди.
    /// </summary>
    public const int TripSpeed = 1000, TripMilli = 8;
    /// <summary>У воді 4 с: бот вилазить і котить далі, гравець вибуває на раунд.</summary>
    public const int WaterTicks = 100;
    /// <summary>Нахилитись за ласощами можна, проїжджаючи не далі 16 од. і не швидше за 2,5 од./тик.</summary>
    public const int PickR = 16, PickSpeed = 640;
    /// <summary>12 ласощів на льоду, по двоє кожного виду; підхопили — нове за 4–8 с деінде.</summary>
    public const int Items = 12, RespawnMin = 100, RespawnMax = 200;
    /// <summary>Ополонок на старті раунду і їхні радіуси; тріщина стає ополонкою радіуса 26 за 5 с.</summary>
    public const int StartHoles = 3, HoleRMin = 22, HoleRMax = 34, CrackR = 26, CrackWarn = 125, HoleRCap = 46, MaxHoles = 7;
    /// <summary>Хто врізався останнім за 3 с до ополонки — той і зіпхнув.</summary>
    public const int CreditTicks = 75;
    /// <summary>Скільки бот може простояти на самому старті раунду (0…6 с) — як гравець, що роздивляється.</summary>
    public const int OpeningStandMax = 150;

    /// <summary>Вісім напрямків (0 праворуч, за годинниковою, y донизу) у 1/256: діагональ — 181 ≈ 256/√2.</summary>
    public static readonly int[] DX8 = [256, 181, 0, -181, -256, -181, 0, 181];
    public static readonly int[] DY8 = [0, 181, 256, 181, 0, -181, -256, -181];

    // Коди подій кадру.
    public const int EvKnock = 1, EvSplash = 2, EvPick = 3, EvCrack = 5, EvOpen = 6, EvTrip = 7, EvClimb = 8;

    // Режими мозку бота.
    public const int ModeStand = 0, ModePoint = 1, ModeItem = 2, ModeLoop = 3, ModeChase = 4;

    readonly Random _rng = rng;
    readonly int[] _nameOrder = new int[SkateMap.Names.Length];
    readonly int[] _head = new int[SkateMap.Cells];
    int[] _next = [];
    readonly List<long> _pairs = [];

    public SkateVillager[] V { get; private set; } = [];
    public int N => V.Length;
    public readonly List<SkateHole> Holes = [];
    public readonly SkateItem[] Slots = [.. Enumerable.Range(0, Items).Select(i => new SkateItem { Kind = i / 2 })];
    /// <summary>Події цього тика ядра — гра забирає їх у кадр.</summary>
    public readonly List<int[]> Ev = [];
    /// <summary>Тик ядра (наскрізний за раунд) — для «хто врізався останнім».</summary>
    public int Clock;
    /// <summary>Лід відкрито (фаза «go»): лише тоді підбирають ласощі.</summary>
    public bool Trading { get; set; } = true;

    // ---------------------------------------------------------------------------------------------
    // Ціла арифметика
    // ---------------------------------------------------------------------------------------------

    /// <summary>Цілий корінь (підлога) — Ньютоном від степеня двійки, що завжди більший за корінь.</summary>
    public static long Isqrt(long n)
    {
        if (n < 2) return n < 0 ? 0 : n;
        var x = 1L << ((BitOperations.Log2((ulong)n) >> 1) + 1);
        while (true)
        {
            var y = (x + n / x) >> 1;
            if (y >= x) return x;
            x = y;
        }
    }

    /// <summary>Вектор → один із восьми напрямків (межі — під 22,5°, tan ≈ 106/256).</summary>
    public static int Dir8(long x, long y)
    {
        long ax = Math.Abs(x), ay = Math.Abs(y);
        if (ay * 256 <= ax * 106) return x >= 0 ? 0 : 4;
        if (ax * 256 <= ay * 106) return y >= 0 ? 2 : 6;
        return x >= 0 ? (y >= 0 ? 1 : 7) : (y >= 0 ? 3 : 5);
    }

    /// <summary>
    /// Тертя: мінус 1/<paramref name="div"/> швидкості й ще <paramref name="c"/> сабів уздовж руху (не по осях — інакше
    /// діагональ гальмувала б сильніше, ніж пряма). Через нуль не перескакує.
    /// </summary>
    static void Rub(SkateVillager v, int div, int c)
    {
        v.Vx -= v.Vx / div;
        v.Vy -= v.Vy / div;
        var sp = Isqrt(Speed2(v));
        if (sp <= c) { v.Vx = v.Vy = 0; return; }
        v.Vx -= (int)(v.Vx * (long)c / sp);
        v.Vy -= (int)(v.Vy * (long)c / sp);
    }

    public static long Speed2(SkateVillager v) => (long)v.Vx * v.Vx + (long)v.Vy * v.Vy;

    // ---------------------------------------------------------------------------------------------
    // Фізика — одна на всіх
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Крок селянина. Ця функція одна для ботів і гравців: вона читає лише <see cref="SkateVillager.Want"/> і стан тіла.
    /// Відштовхнувся — прискорення в той бік (діагональ того ж модуля); тисне проти руху на швидкості — може зачепитись
    /// і впасти (<paramref name="rng"/>, лише тоді); гальмо — мінус <see cref="BrakeDecel"/> уздовж руху; далі тертя,
    /// стеля швидкості й рух по осях із відскоком від берега. Повертає true, якщо впав сам.
    /// </summary>
    public static bool Step(SkateVillager v, Random rng)
    {
        v.Pushing = v.Braking = false;
        if (!v.OnIce)
        {
            v.Vx = v.Vy = 0;
            return false;
        }
        var tripped = false;
        if (v.Fallen > 0)
        {
            Rub(v, FallDrag, FallConst);
        }
        else
        {
            var w = v.Want;
            if (w is >= 0 and < 8)
            {
                var sp2 = Speed2(v);
                var dot = (long)v.Vx * DX8[w] + (long)v.Vy * DY8[w];
                // проти руху під кутом понад 135°: cos² > 1/2 і косинус від'ємний
                if (sp2 >= (long)TripSpeed * TripSpeed && dot < 0 && dot * dot * 2 >= sp2 * 65536 && rng.Next(1000) < TripMilli)
                {
                    v.Fallen = FallTicks;
                    v.Falls++;
                    tripped = true;
                }
                else
                {
                    v.Vx += Accel * DX8[w] / 256;
                    v.Vy += Accel * DY8[w] / 256;
                    v.Dir = w;
                    v.Pushing = true;
                }
            }
            else if (w == 8)
            {
                var sp = Isqrt(Speed2(v));
                if (sp <= BrakeDecel) v.Vx = v.Vy = 0;
                else
                {
                    v.Vx -= (int)(v.Vx * (long)BrakeDecel / sp);
                    v.Vy -= (int)(v.Vy * (long)BrakeDecel / sp);
                }
                v.Braking = true;
            }
            Rub(v, Drag, DragConst);
            var s2 = Speed2(v);
            if (s2 > (long)Vmax * Vmax)
            {
                var sp = Isqrt(s2);
                v.Vx = (int)(v.Vx * (long)Vmax / sp);
                v.Vy = (int)(v.Vy * (long)Vmax / sp);
            }
        }
        Move(v);
        return tripped;
    }

    /// <summary>Рух по осях: коробка не влазить на лід — лишаємось, а швидкість по цій осі відскакує вдвічі слабша.</summary>
    static void Move(SkateVillager v)
    {
        var nx = v.X + v.Vx;
        if (SkateMap.BoxFits(nx / Fp, v.Y / Fp, R)) v.X = nx;
        else v.Vx = -v.Vx * WallNum / WallDen;
        var ny = v.Y + v.Vy;
        if (SkateMap.BoxFits(v.X / Fp, ny / Fp, R)) v.Y = ny;
        else v.Vy = -v.Vy * WallNum / WallDen;
    }

    /// <summary>Усі кроком, за зростанням id; хто впав сам — подія в кадр.</summary>
    public void StepAll()
    {
        var v = V;
        for (var i = 0; i < v.Length; i++)
            if (Step(v[i], _rng)) Ev.Add([EvTrip, i]);
    }

    // ---------------------------------------------------------------------------------------------
    // Зіткнення: сітка клітинок → пари → пружний удар
    // ---------------------------------------------------------------------------------------------

    static bool Solid(SkateVillager v) => v.OnIce;

    /// <summary>
    /// Пари, що торкаються (відстань центрів менша за 2R), за зростанням (i, j), i &lt; j. Широка фаза — клітинки мапи
    /// (32 од. більше за діаметр 20): кожного кладемо в його клітинку, сусідів шукаємо в 3×3. Список живе з ядром.
    /// </summary>
    public List<long> Pairs()
    {
        var v = V;
        var n = v.Length;
        if (_next.Length < n) _next = new int[n];
        Array.Fill(_head, -1);
        // у зворотному порядку — щоб у кожній клітинці список ішов за зростанням id
        for (var i = n - 1; i >= 0; i--)
        {
            if (!Solid(v[i])) continue;
            var c = CellOf(v[i]);
            _next[i] = _head[c];
            _head[c] = i;
        }
        _pairs.Clear();
        const long d2max = (long)(2 * R * Fp) * (2 * R * Fp);
        for (var i = 0; i < n; i++)
        {
            var a = v[i];
            if (!Solid(a)) continue;
            int cx = a.X / Fp / SkateMap.Cell, cy = a.Y / Fp / SkateMap.Cell;
            for (var yy = cy - 1; yy <= cy + 1; yy++)
            {
                if (yy < 0 || yy >= SkateMap.H) continue;
                for (var xx = cx - 1; xx <= cx + 1; xx++)
                {
                    if (xx < 0 || xx >= SkateMap.W) continue;
                    for (var j = _head[yy * SkateMap.W + xx]; j >= 0; j = _next[j])
                    {
                        if (j <= i) continue;
                        long dx = v[j].X - a.X, dy = v[j].Y - a.Y;
                        if (dx * dx + dy * dy < d2max) _pairs.Add((long)i << 32 | (uint)j);
                    }
                }
            }
        }
        // за i вони вже йдуть по порядку; усередині одного i — дочищаємо вставками (їх одиниці)
        for (var k = 1; k < _pairs.Count; k++)
        {
            var p = _pairs[k];
            var m = k - 1;
            while (m >= 0 && _pairs[m] > p) { _pairs[m + 1] = _pairs[m]; m--; }
            _pairs[m + 1] = p;
        }
        return _pairs;
    }

    /// <summary>Те саме, але перебором усіх пар — для тесту, що сітка нічого не губить.</summary>
    public List<long> PairsBrute()
    {
        var v = V;
        var list = new List<long>();
        const long d2max = (long)(2 * R * Fp) * (2 * R * Fp);
        for (var i = 0; i < v.Length; i++)
            for (var j = i + 1; j < v.Length; j++)
            {
                if (!Solid(v[i]) || !Solid(v[j])) continue;
                long dx = v[j].X - v[i].X, dy = v[j].Y - v[i].Y;
                if (dx * dx + dy * dy < d2max) list.Add((long)i << 32 | (uint)j);
            }
        return list;
    }

    static int CellOf(SkateVillager v) =>
        Math.Clamp(v.Y / Fp / SkateMap.Cell, 0, SkateMap.H - 1) * SkateMap.W + Math.Clamp(v.X / Fp / SkateMap.Cell, 0, SkateMap.W - 1);

    /// <summary>Усі зіткнення тика: пари за зростанням (i, j), кожну — <see cref="Bump"/> з поточними координатами.</summary>
    public void CollideAll()
    {
        var pairs = Pairs();
        for (var k = 0; k < pairs.Count; k++)
        {
            var p = pairs[k];
            Bump(V[(int)(p >> 32)], V[(int)(uint)p]);
        }
    }

    /// <summary>
    /// Пружний удар двох однакових селян. Імпульс <c>j = (1 + e)/2 · (Δv·d)/|d|² · d</c> — одному додаємо, другому
    /// віднімаємо ті самі цілі числа, тож сумарний імпульс зберігається точно. Зближення ≥ <see cref="KnockSpeed"/> —
    /// таран: падає повільніший (обидва, якщо повільніший має хоч 3/4 швидкості швидшого). Перекриття розсуваємо
    /// навпіл, якщо коробка на льоду вміщається. Повертає true, якщо удар був (вони зближувались).
    /// </summary>
    public bool Bump(SkateVillager a, SkateVillager b)
    {
        long dx = b.X - a.X, dy = b.Y - a.Y;
        var d2 = dx * dx + dy * dy;
        const long reach = 2 * R * Fp;
        if (d2 >= reach * reach) return false;
        if (d2 == 0)
        {
            // один на одному — розводимо вздовж осі x, меншого id ліворуч
            dx = Fp;
            dy = 0;
            d2 = (long)Fp * Fp;
        }
        var dist = Isqrt(d2);
        var hit = false;
        long dvx = b.Vx - a.Vx, dvy = b.Vy - a.Vy;
        var dot = dvx * dx + dvy * dy;
        if (dot < 0)
        {
            hit = true;
            var closing = -dot / Math.Max(1, dist);
            long sa = Speed2(a), sb = Speed2(b);
            var jx = (int)(dot * dx * RestNum / (d2 * RestDen));
            var jy = (int)(dot * dy * RestNum / (d2 * RestDen));
            a.Vx += jx;
            a.Vy += jy;
            b.Vx -= jx;
            b.Vy -= jy;
            a.LastBy = b.Id;
            b.LastBy = a.Id;
            a.LastAt = b.LastAt = Clock;
            if (closing >= KnockSpeed)
            {
                var (fast, slow) = sa >= sb ? (a, b) : (b, a);
                var both = Math.Min(sa, sb) * 16 >= Math.Max(sa, sb) * 9;
                var fell = false;
                if (slow.Fallen == 0 && slow.Upright) { slow.Fallen = FallTicks; slow.Falls++; fell = true; }
                if (both && fast.Fallen == 0 && fast.Upright) { fast.Fallen = FallTicks; fast.Falls++; }
                if (fell || both) Ev.Add([EvKnock, fast.Id, slow.Id, both ? 1 : 0]);
            }
        }
        var overlap = reach - dist;
        if (overlap > 0)
        {
            var half = overlap / 2 + 1;
            int sx = (int)(half * dx / Math.Max(1, dist)), sy = (int)(half * dy / Math.Max(1, dist));
            if (SkateMap.BoxFits((a.X - sx) / Fp, (a.Y - sy) / Fp, R)) { a.X -= sx; a.Y -= sy; }
            if (SkateMap.BoxFits((b.X + sx) / Fp, (b.Y + sy) / Fp, R)) { b.X += sx; b.Y += sy; }
        }
        return hit;
    }

    // ---------------------------------------------------------------------------------------------
    // Вода, годинники, ласощі, тріщини
    // ---------------------------------------------------------------------------------------------

    /// <summary>Чи центр селянина у відкритій ополонці (у сабах, на квадратах).</summary>
    public static bool InHole(SkateHole h, int x, int y)
    {
        long dx = x - (long)h.X * Fp, dy = y - (long)h.Y * Fp, r = (long)h.R * Fp;
        return h.Open && dx * dx + dy * dy < r * r;
    }

    /// <summary>Хто заїхав центром у відкриту ополонку — шубовсть: у воду на <see cref="WaterTicks"/>, подія з місцем (−1 для бота).</summary>
    public void WaterAll()
    {
        var v = V;
        for (var i = 0; i < v.Length; i++)
        {
            var q = v[i];
            if (!q.OnIce) continue;
            foreach (var h in Holes)
            {
                if (!InHole(h, q.X, q.Y)) continue;
                q.Water = WaterTicks;
                q.Fallen = 0;
                q.Vx = q.Vy = 0;
                q.Want = -1;
                q.Pushing = q.Braking = false;
                Ev.Add([EvSplash, i, q.Owner]);
                break;
            }
        }
    }

    /// <summary>
    /// Годинники селян: хто відлежав — встає; хто відсидів у воді — бот вилазить на край ополонки й думає з чистого
    /// аркуша, гравець (вибулий) сідає на краю, укутаний, до кінця раунду.
    /// </summary>
    public void TimersAll()
    {
        Clock++;
        var v = V;
        for (var i = 0; i < v.Length; i++)
        {
            var q = v[i];
            if (q.Fallen > 0 && --q.Fallen == 0 && q.Owner < 0) Forget(q, _rng.Next(6, 31));
            if (q.Water > 0 && --q.Water == 0)
            {
                Rim(q);
                if (q.Owner >= 0) q.Out = true;
                else
                {
                    Forget(q, _rng.Next(25, 76));
                    Ev.Add([EvClimb, i]);
                }
            }
        }
    }

    /// <summary>Поставити того, хто у воді, на край найближчої ополонки — туди, де коробка на льоду й не в іншій воді.</summary>
    void Rim(SkateVillager q)
    {
        SkateHole? hole = null;
        long best = long.MaxValue;
        foreach (var h in Holes)
        {
            long dx = q.X - (long)h.X * Fp, dy = q.Y - (long)h.Y * Fp;
            var d = dx * dx + dy * dy - (long)h.R * Fp * h.R * Fp;
            if (d < best) { best = d; hole = h; }
        }
        q.Vx = q.Vy = 0;
        if (hole is null) return;
        long ox = q.X - (long)hole.X * Fp, oy = q.Y - (long)hole.Y * Fp;
        var first = ox == 0 && oy == 0 ? 0 : Dir8(ox, oy);
        for (var k = 0; k < 8; k++)
        {
            var d = (first + (k % 2 == 0 ? k / 2 : 8 - (k + 1) / 2)) & 7;
            var r = hole.R + R + 6;
            int x = hole.X + r * DX8[d] / 256, y = hole.Y + r * DY8[d] / 256;
            if (!SkateMap.BoxFits(x, y, R) || Holes.Any(h => InHole(h, x * Fp, y * Fp) || (!h.Open && Near(h, x, y, h.R)))) continue;
            q.X = x * Fp;
            q.Y = y * Fp;
            return;
        }
        var (sx, sy) = FreeSpot(R, 20);
        q.X = sx * Fp;
        q.Y = sy * Fp;
    }

    static bool Near(SkateHole h, int x, int y, int r)
    {
        long dx = x - h.X, dy = y - h.Y;
        return dx * dx + dy * dy < (long)r * r;
    }

    /// <summary>
    /// Ласощі: хто повільно проїжджає над ними (не лежить, не у воді), той і підхопив — однаково бот чи гравець. Гра
    /// рахує лише гравцям зі списку. Що підхопили або що пішло під воду — за 4–8 с з'являється деінде.
    /// </summary>
    public void ItemsAll()
    {
        foreach (var s in Slots)
        {
            if (s.Wait > 0 && --s.Wait == 0) Drop(s);
            if (s.Here && Holes.Any(h => h.Open && Near(h, s.X, s.Y, h.R + 4))) s.Wait = _rng.Next(RespawnMin, RespawnMax + 1);
        }
        if (!Trading) return;
        var v = V;
        const long reach = (long)PickR * Fp * PickR * Fp;
        for (var i = 0; i < v.Length; i++)
        {
            var q = v[i];
            if (!q.Upright || Speed2(q) > (long)PickSpeed * PickSpeed) continue;
            for (var k = 0; k < Slots.Length; k++)
            {
                var s = Slots[k];
                if (!s.Here) continue;
                long dx = q.X - (long)s.X * Fp, dy = q.Y - (long)s.Y * Fp;
                if (dx * dx + dy * dy > reach) continue;
                s.Wait = _rng.Next(RespawnMin, RespawnMax + 1);
                Ev.Add([EvPick, i, k, s.Kind]);
                break;
            }
        }
    }

    /// <summary>Нове місце для ласощів: на льоду, осторонь ополонок і тріщин, не ближче 40 од. до інших ласощів.</summary>
    void Drop(SkateItem s)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var (x, y) = FreeSpot(8, 24);
            var ok = true;
            foreach (var o in Slots)
            {
                if (o == s || !o.Here) continue;
                long dx = o.X - x, dy = o.Y - y;
                if (dx * dx + dy * dy < 40 * 40) { ok = false; break; }
            }
            if (!ok && attempt < 199) continue;
            s.X = x;
            s.Y = y;
            return;
        }
    }

    /// <summary>
    /// Випадкова точка льоду, де коробка з половиною <paramref name="half"/> вміщається і до ополонок (разом із
    /// тріщинами) не ближче <paramref name="margin"/> від краю. 200 спроб, далі — як вийшло.
    /// </summary>
    public (int X, int Y) FreeSpot(int half, int margin)
    {
        int x = 0, y = 0;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var cell = SkateMap.Ice[_rng.Next(SkateMap.Ice.Length)];
            x = SkateMap.CenterX(cell) + _rng.Next(-12, 13);
            y = SkateMap.CenterY(cell) + _rng.Next(-12, 13);
            if (!SkateMap.BoxFits(x, y, half)) continue;
            if (Holes.Any(h => Near(h, x, y, h.R + margin))) continue;
            return (x, y);
        }
        return (x, y);
    }

    /// <summary>Лід тріщить: нова тріщина там, де вміщається ополонка радіуса <see cref="CrackR"/> і не зачепить інших. −1 — ніде.</summary>
    public int Crack()
    {
        if (Holes.Count >= MaxHoles) return -1;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var cell = SkateMap.Ice[_rng.Next(SkateMap.Ice.Length)];
            int x = SkateMap.CenterX(cell) + _rng.Next(-12, 13), y = SkateMap.CenterY(cell) + _rng.Next(-12, 13);
            if (!SkateMap.DiskOnIce(x, y, CrackR + 8) || Holes.Any(h => Near(h, x, y, h.R + CrackR + 56))) continue;
            Holes.Add(new SkateHole { X = x, Y = y, R = CrackR, Warn = CrackWarn });
            Ev.Add([EvCrack, Holes.Count - 1]);
            return Holes.Count - 1;
        }
        return -1;
    }

    /// <summary>Тріщини доходять до води; <paramref name="grow"/> — відкриті ополонки ширшають на одиницю (до <see cref="HoleRCap"/>).</summary>
    public void IceAll(bool grow)
    {
        for (var k = 0; k < Holes.Count; k++)
        {
            var h = Holes[k];
            if (h.Warn > 0)
            {
                if (--h.Warn == 0) Ev.Add([EvOpen, k]);
            }
            else if (grow && h.R < HoleRCap && SkateMap.DiskOnIce(h.X, h.Y, h.R + 1)) h.R++;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Мозок бота
    // ---------------------------------------------------------------------------------------------

    /// <summary>Думають лише боти, за зростанням id.</summary>
    public void ThinkAll()
    {
        var v = V;
        for (var i = 0; i < v.Length; i++)
            if (v[i].Owner < 0) Think(v[i]);
    }

    /// <summary>
    /// Бот тисне «клавіші» так, як людина: тримає напрямок кілька тиків і довше, відпускає й котиться, гальмує,
    /// стоїть. Рішення — лише коли скінчилось попереднє натискання (<see cref="SkateVillager.Hold"/>), як у людини, що
    /// не перебирає клавіші щотика. Що робить: котить до точки чи до ласощів, кружляє колами й вісімками, грається в
    /// квача з кимось поруч, стоїть. Бачить ополонку й тріщину попереду — з запізненням 2–6 тиків відвертає.
    /// </summary>
    public void Think(SkateVillager v)
    {
        if (!v.OnIce) { v.Want = -1; return; }
        if (v.Fallen > 0) { v.Want = -1; v.Hold = 0; return; }
        if (v.Alarm > 0)
        {
            if (--v.Alarm == 0 && Danger(v, out var away))
            {
                v.Want = away;
                v.Hold = _rng.Next(5, 13);
                if (v.Mode != ModeStand) NewMode(v, false);
                return;
            }
        }
        else if (Danger(v, out _)) v.Alarm = _rng.Next(2, 7);
        if (v.Hold > 0) { v.Hold--; return; }
        Decide(v);
    }

    /// <summary>
    /// Попереду ополонка чи тріщина: котиться до неї й за 14 тиків буде в ній (з запасом) — або вже стоїть на самому
    /// краю. away — геть від її центру. Хто стоїть біля ополонки й нікуди не котиться, той не тікає: людина-мисливець
    /// теж так чатує, і бот тоді не виділяє її тим, що «боти біля води не стоять».
    /// </summary>
    bool Danger(SkateVillager v, out int away)
    {
        away = -1;
        long px = v.X + (long)v.Vx * 14, py = v.Y + (long)v.Vy * 14;
        foreach (var h in Holes)
        {
            long cx = (long)h.X * Fp, cy = (long)h.Y * Fp;
            long far = (long)(h.R + R + 14) * Fp, near = (long)(h.R + R + 2) * Fp;
            long ex = px - cx, ey = py - cy, nx = v.X - cx, ny = v.Y - cy;
            var ahead = ex * ex + ey * ey < far * far && nx * v.Vx + ny * v.Vy < 0;
            var edge = nx * nx + ny * ny < near * near;
            if (!ahead && !edge) continue;
            away = nx == 0 && ny == 0 ? _rng.Next(8) : Dir8(nx, ny);
            return true;
        }
        return false;
    }

    /// <summary>Скільки тримати напрямок: 35 % — мить (3–11 тиків), 45 % — 12–29, 20 % — 30–54.</summary>
    int HoldTicks()
    {
        var r = _rng.Next(100);
        return r < 35 ? _rng.Next(3, 12) : r < 80 ? _rng.Next(12, 30) : _rng.Next(30, 55);
    }

    /// <summary>Скільки стояти: 70 % — 0,5–3 с, 18 % — 3–6 с, 8 % — 6–12 с, 4 % — 12–20 с.</summary>
    int StandTicks()
    {
        var r = _rng.Next(100);
        return r < 70 ? _rng.Next(12, 76) : r < 88 ? _rng.Next(76, 151) : r < 96 ? _rng.Next(151, 301) : _rng.Next(301, 501);
    }

    void Decide(SkateVillager v)
    {
        switch (v.Mode)
        {
            case ModeStand:
                if (v.Stand > 0)
                {
                    // стоїть: докочується сам, інколи пригальмовує «плугом», зрідка переступає
                    var moving = Speed2(v) > 150L * 150;
                    var r = _rng.Next(100);
                    v.Want = moving && r < 45 ? 8 : !moving && r < 6 ? _rng.Next(8) : -1;
                    v.Hold = v.Want is >= 0 and < 8 ? _rng.Next(2, 5) : v.Want == 8 ? _rng.Next(3, 13) : Math.Min(v.Stand, _rng.Next(4, 40));
                    v.Stand -= v.Hold;
                    return;
                }
                NewMode(v, true);
                Decide(v);
                return;
            case ModePoint:
            case ModeItem:
            case ModeChase:
                Steer(v);
                return;
            case ModeLoop:
                if (v.Segs <= 0) { NewMode(v, true); Decide(v); return; }
                v.Segs--;
                if (v.Segs == 7 && v.Spin != 0 && v.ModeLeft > 0) { v.Spin = -v.Spin; v.ModeLeft = 0; }   // вісімка: друге коло — в інший бік
                v.Want = ((v.Want < 0 || v.Want > 7 ? v.Dir : v.Want) + v.Spin + 8) & 7;
                v.Hold = v.Seg + _rng.Next(0, 3);
                // кружляння теж кінчається, щойно попереду стіна: людина б відвернула
                if (!SkateMap.Clear(v.X / Fp, v.Y / Fp, (v.X + v.Vx * 20) / Fp, (v.Y + v.Vy * 20) / Fp)) v.Segs = 0;
                return;
        }
    }

    /// <summary>
    /// Нове заняття. <paramref name="rest"/> — щойно стояв чи докружляв (тоді можна й одразу знову стати). Ваги: стояти 12,
    /// до точки 42, по ласощі 18, колами й вісімками 14, у квача 14.
    /// </summary>
    void NewMode(SkateVillager v, bool rest)
    {
        v.Chase = -1;
        v.Item = -1;
        var r = _rng.Next(100);
        if (r < 12 && !rest)
        {
            v.Mode = ModeStand;
            v.Stand = StandTicks();
            return;
        }
        v.Cruise = _rng.Next(Vmax / 2, Vmax + 1);
        if (r < 54)
        {
            if (PickPoint(v)) return;
        }
        else if (r < 72)
        {
            if (PickItem(v)) return;
        }
        else if (r < 86)
        {
            v.Mode = ModeLoop;
            v.Seg = _rng.Next(4, 10);
            v.Segs = 8 * _rng.Next(1, 3);
            v.Spin = _rng.Next(2) == 0 ? 1 : -1;
            // кожна третя петля — вісімка: після першого кола напрямок обертання міняється
            v.ModeLeft = _rng.Next(3) == 0 ? 1 : 0;
            if (v.ModeLeft == 1) v.Segs = 16;
            if (v.Want is < 0 or > 7) v.Want = v.Dir;
            return;
        }
        else if (PickChase(v)) return;
        if (!PickPoint(v))
        {
            v.Mode = ModeStand;
            v.Stand = StandTicks();
        }
    }

    bool PickPoint(SkateVillager v)
    {
        for (var k = 0; k < 8; k++)
        {
            var (x, y) = FreeSpot(R, 36);
            if (!SkateMap.Clear(v.X / Fp, v.Y / Fp, x, y)) continue;
            v.Mode = ModePoint;
            v.ModeLeft = 40;
            v.Tx = x * Fp;
            v.Ty = y * Fp;
            return true;
        }
        return false;
    }

    bool PickItem(SkateVillager v)
    {
        var k = _rng.Next(Slots.Length);
        for (var i = 0; i < Slots.Length; i++)
        {
            var s = Slots[(k + i) % Slots.Length];
            if (!s.Here || !SkateMap.Clear(v.X / Fp, v.Y / Fp, s.X, s.Y)) continue;
            v.Mode = ModeItem;
            v.ModeLeft = 40;
            v.Item = (k + i) % Slots.Length;
            v.Tx = s.X * Fp;
            v.Ty = s.Y * Fp;
            v.Cruise = _rng.Next(Vmax / 3, Vmax * 3 / 4);
            return true;
        }
        return false;
    }

    /// <summary>Квач: хтось поруч (до 250 од.), на ногах, видно по льоду — за ним 2–6 с. Зіткнулись — гра скінчилась.</summary>
    bool PickChase(SkateVillager v)
    {
        var start = _rng.Next(V.Length);
        for (var i = 0; i < V.Length; i++)
        {
            var q = V[(start + i) % V.Length];
            if (q == v || !q.Upright) continue;
            long dx = q.X - v.X, dy = q.Y - v.Y;
            if (dx * dx + dy * dy > 250L * Fp * 250 * Fp || !SkateMap.Clear(v.X / Fp, v.Y / Fp, q.X / Fp, q.Y / Fp)) continue;
            v.Mode = ModeChase;
            v.Chase = q.Id;
            v.ModeLeft = _rng.Next(8, 26);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Рульове: бажана швидкість — на ціль, не більша за «круїз» і таку, з якої ще встигнеш загальмувати. Помилка
    /// мала — відпускає й котиться; треба скинути швидкість — гальмо або проти руху; інакше — напрямок, що найкраще
    /// виправляє помилку. Тримає його стільки, скільки тримала б людина.
    /// </summary>
    void Steer(SkateVillager v)
    {
        if (--v.ModeLeft <= 0)
        {
            Rest(v);
            return;
        }
        if (v.Mode == ModeChase)
        {
            var q = V[Math.Clamp(v.Chase, 0, V.Length - 1)];
            if (v.Chase < 0 || !q.Upright || !SkateMap.Clear(v.X / Fp, v.Y / Fp, q.X / Fp, q.Y / Fp))
            {
                Rest(v);
                return;
            }
            v.Tx = q.X + q.Vx * 8;
            v.Ty = q.Y + q.Vy * 8;
        }
        else if (v.Mode == ModeItem && (v.Item < 0 || !Slots[v.Item].Here))
        {
            NewMode(v, false);
            Decide(v);
            return;
        }
        long dx = v.Tx - v.X, dy = v.Ty - v.Y;
        var dist = Isqrt(dx * dx + dy * dy);
        var sp = Isqrt(Speed2(v));
        var arrive = v.Mode == ModeItem ? 6 * Fp : 12 * Fp;
        if (v.Mode != ModeChase && dist < arrive && sp < (v.Mode == ModeItem ? PickSpeed : 320))
        {
            Rest(v);
            return;
        }
        var vs = Math.Min(v.Cruise, Isqrt(BrakeDecel * dist * 3 / 2));
        if (v.Mode == ModeItem) vs = Math.Min(vs, Math.Max(PickSpeed * 3 / 4, dist / 20));
        long vdx = dist == 0 ? 0 : dx * vs / dist, vdy = dist == 0 ? 0 : dy * vs / dist;
        long ex = vdx - v.Vx, ey = vdy - v.Vy;
        var err = Isqrt(ex * ex + ey * ey);
        // Людина після поштовху частіше відпускає клавішу й котиться, ніж одразу тисне іншу: так і бот, коли не горить.
        if (err < 200 || (v.Want is >= 0 and < 8 && err < 700 && _rng.Next(100) < 60))
        {
            v.Want = -1;
            v.Hold = _rng.Next(2, 20);
        }
        else if (ex * v.Vx + ey * v.Vy < 0 && sp > vs + 250)
        {
            v.Want = _rng.Next(100) < 45 ? 8 : Dir8(ex, ey);
            v.Hold = _rng.Next(3, 11);
        }
        else
        {
            // людина не цілиться до градуса: якщо той самий напрямок, що й минулого разу, ще годиться (±45°) — тисне його
            var d = Dir8(ex, ey);
            if (((d - v.Dir + 9) & 7) <= 2 && _rng.Next(100) < 85) d = v.Dir;
            v.Want = d;
            v.Hold = dist > 160 * Fp ? HoldTicks() : _rng.Next(4, 17);
        }
    }

    /// <summary>Дійшов (чи набридло) — постояти, як людина, що роздивляється.</summary>
    void Rest(SkateVillager v)
    {
        v.Stand = v.Mode == ModeItem ? _rng.Next(5, 40) : StandTicks();
        v.Mode = ModeStand;
        v.Chase = -1;
        v.Item = -1;
        v.Want = Speed2(v) > 200L * 200 && _rng.Next(100) < 50 ? 8 : -1;
        v.Hold = _rng.Next(3, 11);
    }

    /// <summary>Бот устав, виліз із води чи став ботом після виходу гравця: думає з чистого аркуша, спершу трохи постоїть.</summary>
    public static void Forget(SkateVillager v, int stand = 0)
    {
        v.Mode = ModeStand;
        v.Stand = stand;
        v.Hold = 0;
        v.Alarm = 0;
        v.Want = -1;
        v.Chase = -1;
        v.Item = -1;
        v.ModeLeft = 0;
    }

    // ---------------------------------------------------------------------------------------------
    // Раунд
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Новий ставок: ополонки, селяни (<paramref name="owners"/> — місця гравців, далі <paramref name="bots"/> ботів),
    /// ласощі. Порядок випадковості: ополонки → розстановка (гравці не ближче 96 од. один до одного, усі осторонь води)
    /// → тасування id → вигляд → імена → перше стояння ботів → ласощі. Однаково для ботів і гравців.
    /// </summary>
    public void Deal(IReadOnlyList<int> owners, int bots)
    {
        Holes.Clear();
        Ev.Clear();
        Clock = 0;
        for (var k = 0; k < StartHoles; k++)
        {
            var r = _rng.Next(HoleRMin, HoleRMax + 1);
            for (var attempt = 0; attempt < 200; attempt++)
            {
                var cell = SkateMap.Ice[_rng.Next(SkateMap.Ice.Length)];
                int x = SkateMap.CenterX(cell) + _rng.Next(-12, 13), y = SkateMap.CenterY(cell) + _rng.Next(-12, 13);
                if (!SkateMap.DiskOnIce(x, y, r + 8) || Holes.Any(h => Near(h, x, y, h.R + r + 64))) continue;
                Holes.Add(new SkateHole { X = x, Y = y, R = r });
                break;
            }
        }

        var n = owners.Count + bots;
        var list = new SkateVillager[n];
        for (var i = 0; i < n; i++)
        {
            var v = new SkateVillager { Owner = i < owners.Count ? owners[i] : -1 };
            int x = 0, y = 0;
            for (var attempt = 0; attempt < 200; attempt++)
            {
                (x, y) = FreeSpot(R, 28);
                if (TooClose(list, i, x, y, v.Owner >= 0)) continue;
                break;
            }
            v.X = x * Fp;
            v.Y = y * Fp;
            v.Dir = _rng.Next(8);
            list[i] = v;
        }
        for (var i = n - 1; i > 0; i--)
        {
            var j = _rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        for (var i = 0; i < n; i++)
        {
            var v = list[i];
            v.Id = i;
            v.Hat = _rng.Next(4);
            v.HatColor = _rng.Next(8);
            v.Coat = _rng.Next(8);
            v.Scarf = _rng.Next(8);
        }
        for (var i = 0; i < _nameOrder.Length; i++) _nameOrder[i] = i;
        for (var i = _nameOrder.Length - 1; i > 0; i--)
        {
            var j = _rng.Next(i + 1);
            (_nameOrder[i], _nameOrder[j]) = (_nameOrder[j], _nameOrder[i]);
        }
        for (var i = 0; i < n; i++) list[i].Name = _nameOrder[i % _nameOrder.Length];
        for (var i = 0; i < n; i++)
            if (list[i].Owner < 0) Forget(list[i], _rng.Next(0, OpeningStandMax + 1));
        V = list;

        foreach (var s in Slots) s.Wait = 0;
        foreach (var s in Slots) { s.X = -1000; s.Y = -1000; }
        foreach (var s in Slots) Drop(s);
    }

    /// <summary>Нікого не ставимо впритул (24 од.), а гравця — не ближче 96 од. до вже поставлених гравців.</summary>
    static bool TooClose(SkateVillager[] placed, int count, int x, int y, bool player)
    {
        for (var i = 0; i < count; i++)
        {
            var p = placed[i];
            long dx = p.X / Fp - x, dy = p.Y / Fp - y;
            var d2 = dx * dx + dy * dy;
            if (d2 < 24 * 24) return true;
            if (player && p.Owner >= 0 && d2 < 96 * 96) return true;
        }
        return false;
    }

    // ---------------------------------------------------------------------------------------------
    // Кадр
    // ---------------------------------------------------------------------------------------------

    /// <summary>Кадр: 4 числа на селянина за id — x, y (одиниці), d (0…7), s (0…5). Новий масив щоразу: розсилка серіалізує поза замком.</summary>
    public int[] Pack()
    {
        var v = V;
        var a = new int[v.Length * 4];
        for (var i = 0; i < v.Length; i++)
        {
            var q = v[i];
            a[i * 4] = q.X / Fp;
            a[i * 4 + 1] = q.Y / Fp;
            a[i * 4 + 2] = q.Dir;
            a[i * 4 + 3] = q.State;
        }
        return a;
    }

    /// <summary>Ополонки: x, y, r, k (0 — ще тріщини, 1 — вода).</summary>
    public int[] PackHoles()
    {
        var a = new int[Holes.Count * 4];
        for (var i = 0; i < Holes.Count; i++)
        {
            var h = Holes[i];
            a[i * 4] = h.X;
            a[i * 4 + 1] = h.Y;
            a[i * 4 + 2] = h.R;
            a[i * 4 + 3] = h.Open ? 1 : 0;
        }
        return a;
    }

    /// <summary>Ласощі: x, y, вид (−1 — підхопили, нового ще нема; тоді й x = y = 0).</summary>
    public int[] PackItems()
    {
        var a = new int[Slots.Length * 3];
        for (var i = 0; i < Slots.Length; i++)
        {
            var s = Slots[i];
            if (!s.Here) { a[i * 3 + 2] = -1; continue; }
            a[i * 3] = s.X;
            a[i * 3 + 1] = s.Y;
            a[i * 3 + 2] = s.Kind;
        }
        return a;
    }

    /// <summary>Вигляд за id: шапка, її колір, кожушок, шарф.</summary>
    public int[] Looks()
    {
        var v = V;
        var a = new int[v.Length * 4];
        for (var i = 0; i < v.Length; i++)
        {
            a[i * 4] = v[i].Hat;
            a[i * 4 + 1] = v[i].HatColor;
            a[i * 4 + 2] = v[i].Coat;
            a[i * 4 + 3] = v[i].Scarf;
        }
        return a;
    }

    public string[] NamesNow()
    {
        var v = V;
        var a = new string[v.Length];
        for (var i = 0; i < v.Length; i++) a[i] = SkateMap.Names[v[i].Name];
        return a;
    }
}
