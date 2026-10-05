namespace Hlechyky.Games.Impl;

/// <summary>Гравець Тонкого льоду. Координати — у <see cref="ThiniceCore.Sub"/>-частках клітинки, (0,0) — лівий верхній кут ставка.</summary>
public sealed class ThiniceBody
{
    /// <summary>Грає цю партію (сидів на старті чи бот партії; встав посеред — false, тіло зникає).</summary>
    public bool Plays;
    /// <summary>Ще на льоду (будь-якого ярусу); false — у воді, вибув з раунду.</summary>
    public bool In;
    /// <summary>Ярус: 0 — верхній, 1 — нижній.</summary>
    public int Tier;
    public int X, Y;
    /// <summary>Намір: сектор 0..15 (0 — праворуч, 4 — вниз, за годинниковою) або −1 — стоїть.</summary>
    public int Want = -1;
    public int Face = 4;
    /// <summary>Тиків стрибка лишилось (0 — на льоду); летить у <see cref="JumpDir"/> незалежно від наміру.</summary>
    public int Air;
    public int JumpDir = -1;
    /// <summary>Тиків падіння з верхнього ярусу на нижній лишилось (без керування).</summary>
    public int Fall;
    /// <summary>Перезарядка стрибка, тиків.</summary>
    public int Cd;
    /// <summary>Стрибків у цьому раунді («Стрибунець»).</summary>
    public int Jumps;
    /// <summary>Падав на нижній ярус у цій партії («Фігурист»).</summary>
    public bool Dropped;
    /// <summary>Скільки тіл випало з раунду раніше за це (−1 — ще на льоду). Хто шубовснув в одному тику — рівні.</summary>
    public int OutRank = -1;
    /// <summary>Очки партії (сума місць за раунди) і виграні раунди (останній на льоду сам).</summary>
    public int Points, RoundWins;
}

/// <summary>
/// Світ Тонкого льоду: квадратний ставок N×N плиток у два яруси, тіла, тріщини, стрибки, падіння й відлига.
/// Плитка, на яку став центр тіла, тріщить <see cref="CrackTicks"/> тиків і провалюється; з верхнього ярусу
/// падаєш на нижній (та сама точка), з нижнього — у воду. Без штовхання: тіла проходять крізь одне одного.
/// Spec: <c>docs/games/specs/thinice.md</c>.
/// </summary>
public sealed class ThiniceCore
{
    /// <summary>25 Гц, як Танчики й Крижина.</summary>
    public const int TickMs = 40;
    public const int Seats = 8;
    /// <summary>Одиниць у клітинці. Швидкість 16/тик = 4 клітинки за секунду (Бомбер — 4,2).</summary>
    public const int Sub = 100, Speed = 16;
    /// <summary>Тріщина живе секунду: від кроку до дірки.</summary>
    public const int CrackTicks = 25;
    /// <summary>Стрибок: 12 тиків у повітрі (1,92 клітинки — рівно через одну дірку), перезарядка 3 с.</summary>
    public const int AirTicks = 12, JumpCd = 75;
    /// <summary>Падіння з верхнього ярусу на нижній — 0,4 с без керування.</summary>
    public const int FallTicks = 10;
    /// <summary>Центр тіла не ближче за це до берега: ставок обгороджений, за край не вийдеш.</summary>
    public const int Margin = 22;
    /// <summary>Стан плитки в <see cref="Ice"/>: 0 — ціла, <see cref="Gone"/> — дірка, 1..25 — тріщить (тиків до дірки).</summary>
    public const int Gone = -1;
    /// <summary>Події тика (<c>ev</c> у кадрі): [вид, місце].</summary>
    public const int EvFall = 1, EvSplash = 2, EvJump = 3, EvLand = 4, EvThaw = 5, EvRound = 6;

    /// <summary>Зсуви кроку за сектором (16 секторів по 22,5°), цілі — щоб світ не плив від округлень.</summary>
    public static readonly int[] Dx = [.. Enumerable.Range(0, 16).Select(a => (int)Math.Round(Math.Cos(a * Math.PI / 8) * Speed))];
    public static readonly int[] Dy = [.. Enumerable.Range(0, 16).Select(a => (int)Math.Round(Math.Sin(a * Math.PI / 8) * Speed))];

    readonly Random _rng;
    public int N { get; private set; } = 10;
    /// <summary>Два яруси плиток, рядками: <c>Ice[ярус][y * N + x]</c>.</summary>
    public int[][] Ice { get; private set; } = [new int[100], new int[100]];
    public ThiniceBody[] Bodies { get; } = [.. Enumerable.Range(0, Seats).Select(_ => new ThiniceBody())];
    /// <summary>Хто випав з раунду, по черзі.</summary>
    public List<int> Out { get; } = [];
    /// <summary>Тик світу (росте завжди) і тиків від початку гри раунду.</summary>
    public int T, Rt;
    /// <summary>З якого <see cref="Rt"/> лід тріскає сам (відлига); int.MaxValue — ніколи.</summary>
    public int ThawAt = int.MaxValue;
    double _thawAcc;
    readonly List<int[]> _ev = [];

    public ThiniceCore(Random rng) => _rng = rng;

    /// <summary>Сторона ставка за кількістю тіл: на 2–3 — 10, на 4–5 — 12, на 6–8 — 14.</summary>
    public static int SizeFor(int bodies) => bodies <= 3 ? 10 : bodies <= 5 ? 12 : 14;

    public bool Thawing => Rt >= ThawAt;

    public int AliveCount
    {
        get
        {
            var n = 0;
            foreach (var b in Bodies) if (b.Plays && b.In) n++;
            return n;
        }
    }

    public int CellOf(ThiniceBody b) => Math.Clamp(b.Y / Sub, 0, N - 1) * N + Math.Clamp(b.X / Sub, 0, N - 1);

    /// <summary>Нова партія: хто грає, очки з нуля.</summary>
    public void ResetParty(bool[] plays)
    {
        for (var i = 0; i < Seats; i++)
        {
            var b = Bodies[i];
            b.Plays = i < plays.Length && plays[i];
            b.Points = b.RoundWins = 0;
            b.Dropped = false;
        }
    }

    /// <summary>
    /// Новий раунд: цілий лід обох ярусів під <paramref name="bodies"/> тіл, тіла — по колу навколо центру
    /// (поворот кола — <paramref name="turn"/> у частках оберту; прев'ю лобі бере 0 і випадковості не питає).
    /// </summary>
    public void NewRound(int bodies, double turn)
    {
        N = SizeFor(bodies);
        Ice = [new int[N * N], new int[N * N]];
        Out.Clear();
        Rt = 0;
        _thawAcc = 0;
        _ev.Clear();
        var playing = Enumerable.Range(0, Seats).Where(i => Bodies[i].Plays).ToArray();
        var taken = new HashSet<int>();
        var mid = (N - 1) / 2.0;
        var r = N * 0.32;
        for (var k = 0; k < playing.Length; k++)
        {
            var b = Bodies[playing[k]];
            var ang = 2 * Math.PI * (k / (double)playing.Length + turn);
            var cx = (int)Math.Round(mid + r * Math.Cos(ang));
            var cy = (int)Math.Round(mid + r * Math.Sin(ang));
            // Двоє в одну клітинку не стають: шукаємо найближчу вільну по спіралі.
            for (var d = 0; !taken.Add(cy * N + cx) && d < N * N; d++)
            {
                cx = Math.Clamp(cx + (d % 2 == 0 ? 1 : 0), 0, N - 1);
                cy = Math.Clamp(cy + (d % 2 == 1 ? 1 : 0), 0, N - 1);
            }
            b.X = cx * Sub + Sub / 2;
            b.Y = cy * Sub + Sub / 2;
            b.In = true;
            b.Tier = 0;
            b.Want = -1;
            b.Air = b.Fall = b.Cd = b.Jumps = 0;
            b.JumpDir = -1;
            b.OutRank = -1;
            // Обличчям до центру — перший крок не в берег.
            b.Face = SectorOf(Math.Atan2(mid * Sub + Sub / 2 - b.Y, mid * Sub + Sub / 2 - b.X) * 180 / Math.PI);
        }
    }

    /// <summary>Кут у градусах (y донизу) → найближчий сектор 0..15.</summary>
    public static int SectorOf(double deg)
    {
        var s = (int)Math.Round(deg / 22.5, MidpointRounding.AwayFromZero) % 16;
        return s < 0 ? s + 16 : s;
    }

    // ---------- ввід ----------

    public void Move(int seat, int a)
    {
        var b = Bodies[seat];
        b.Want = a;
        if (a >= 0) b.Face = a;
    }

    /// <summary>Стрибок: текст відмови або null. Фазу перевіряє гра. Летить туди, куди йшов (стоячи — на місці).</summary>
    public string? Jump(int seat)
    {
        var b = Bodies[seat];
        if (!b.Plays || !b.In) return "Ти вже у воді";
        if (b.Fall > 0) return "Ти падаєш";
        if (b.Air > 0) return "Ти вже в повітрі";
        if (b.Cd > 0) return "Стрибок ще не готовий";
        b.Air = AirTicks;
        b.JumpDir = b.Want;
        b.Cd = JumpCd;
        b.Jumps++;
        Event(EvJump, seat);
        return null;
    }

    /// <summary>Гравець устав посеред партії: тіло зникає без заліку.</summary>
    public void Drop(int seat)
    {
        var b = Bodies[seat];
        b.Plays = false;
        b.In = false;
        b.Want = -1;
    }

    // ---------- крок світу ----------

    /// <summary>Початок тика: події минулого тика вже в кадрі.</summary>
    public void BeginTick() => _ev.Clear();

    /// <summary>Крок гри: відлига, тріщини, рух, стрибки, падіння.</summary>
    public void Step()
    {
        T++;
        Rt++;
        if (Thawing) Thaw();
        for (var tier = 0; tier < 2; tier++)
        {
            var g = Ice[tier];
            for (var i = 0; i < g.Length; i++)
                if (g[i] > 0 && --g[i] == 0) g[i] = Gone;
        }
        var outBefore = Out.Count;
        for (var s = 0; s < Seats; s++)
        {
            var b = Bodies[s];
            if (!b.Plays || !b.In) continue;
            if (b.Cd > 0) b.Cd--;
            if (b.Fall > 0)
            {
                if (--b.Fall > 0) continue;
                Event(EvLand, s);
                Underfoot(s, b, outBefore);
                continue;
            }
            var dir = b.Air > 0 ? b.JumpDir : b.Want;
            if (dir is >= 0 and < 16)
            {
                b.X = Math.Clamp(b.X + Dx[dir], Margin, N * Sub - Margin);
                b.Y = Math.Clamp(b.Y + Dy[dir], Margin, N * Sub - Margin);
                b.Face = dir;
            }
            if (b.Air > 0 && --b.Air > 0) continue;
            Underfoot(s, b, outBefore);
        }
    }

    /// <summary>Тіло на льоду: ціла плитка під ним починає тріщати, дірка — падіння на нижній ярус або у воду.</summary>
    void Underfoot(int s, ThiniceBody b, int outBefore)
    {
        var cell = CellOf(b);
        var g = Ice[b.Tier];
        if (g[cell] == 0) { g[cell] = CrackTicks; return; }
        if (g[cell] != Gone) return;
        if (b.Tier == 0)
        {
            b.Tier = 1;
            b.Fall = FallTicks;
            b.Air = 0;
            b.Dropped = true;
            Event(EvFall, s);
            return;
        }
        b.In = false;
        b.Want = -1;
        b.OutRank = outBefore;
        Out.Add(s);
        Event(EvSplash, s);
    }

    /// <summary>
    /// Відлига: лід тріскає сам випадковими цілими плитками обох ярусів, щосекунди більше (2 + с/2 плиток за
    /// секунду на ярус) — за ~25 с від початку ставок порожній, і раунд неминуче кінчається.
    /// </summary>
    void Thaw()
    {
        if (Rt == ThawAt) Event(EvThaw, -1);
        var secs = (Rt - ThawAt) * TickMs / 1000.0;
        _thawAcc += (2 + secs / 2) * TickMs / 1000.0;
        while (_thawAcc >= 1)
        {
            _thawAcc -= 1;
            for (var tier = 0; tier < 2; tier++) CrackRandom(Ice[tier]);
        }
    }

    void CrackRandom(int[] g)
    {
        var intact = 0;
        foreach (var v in g) if (v == 0) intact++;
        if (intact == 0) return;
        var k = _rng.Next(intact);
        for (var i = 0; i < g.Length; i++)
            if (g[i] == 0 && k-- == 0) { g[i] = CrackTicks; return; }
    }

    public void Event(int kind, int seat) => _ev.Add([kind, seat]);

    /// <summary>Події поточного тика — новий масив (кадр серіалізують поза замком).</summary>
    public int[][] Events() => [.. _ev.Select(e => (int[])e.Clone())];

    /// <summary>Ярус рядком для кадру: '.' ціла, '#' дірка, 'a'+k — тріщить, k тиків до дірки ('b'..'z').</summary>
    public string Row(int tier)
    {
        var g = Ice[tier];
        return string.Create(g.Length, g, static (span, g) =>
        {
            for (var i = 0; i < g.Length; i++) span[i] = g[i] == 0 ? '.' : g[i] == Gone ? '#' : (char)('a' + g[i]);
        });
    }
}
