namespace Hlechyky.Games.Impl;

/// <summary>
/// «Намисто» (specs/namysto.md): детермінована симуляція разка — слово в слово те саме, що web/games/namysto-sim.js.
/// Браузер крутить її, щоб усе рухалось миттєво, сервер — щоб судити: той самий журнал вводу дає ті самі очки до
/// біта. Лише цілі числа: ділення відкидає дріб (у JS — Math.trunc), корінь — цілий (<see cref="Isqrt"/>), випадковість
/// — свій mulberry32 (<see cref="NamystoRng"/>), жодної тригонометрії. Будь-яка правка тут — дослівно і в JS, інакше
/// впаде паритет (NamystoParityTests і docs/games/dev/namysto-parity.js).
/// <para>
/// Одиниці: субпікселі (su), 1 px = 16 su; поле 720×720 px; крок = 1/60 с. Разок — список намистин від голови (найближча
/// до криниці, найбільше <see cref="Bead.D"/>) до хвоста; <see cref="Bead.D"/> — відстань уздовж жолоба від початку.
/// </para>
/// </summary>
public sealed class NamystoSim
{
    // ---------- сталі (su, кроки по 1/60 с) ----------
    public const int Px = 16;
    public const int Field = 720 * Px;
    /// <summary>Діаметр намистини: 32 px. Сусіди в разку стоять рівно на діаметр одна від одної.</summary>
    public const int Dm = 32 * Px;
    public const int StepsPerSecond = 60;
    /// <summary>Наскільки за крок розсуваються намистини, що налізли одна на одну (вставка): 6 px.</summary>
    public const int PushMax = 6 * Px;
    /// <summary>Шматок разка, що відірвався й має з хвостом однаковий колір на краях, котиться назад: 5 px за крок.</summary>
    public const int Magnet = 5 * Px;
    public const int BackSpeed = 2 * Px;
    public const int IntroSpeed = 4 * Px;
    /// <summary>Разок вкочується швидко, поки голова не пройде стільки відсотків жолоба.</summary>
    public const int IntroPct = 26;
    public const int IntroMaxSteps = 900;
    /// <summary>Підкрок польоту намистини: 6 px; звичайний постріл — 2 підкроки за крок (720 px/с), з «Оком» — 3.</summary>
    public const int ShotSub = 6 * Px;
    public const int SubsNormal = 2, SubsEye = 3;
    /// <summary>Намистина вилітає з горлечка Глека — за 40 px від середини.</summary>
    public const int Mouth = 40 * Px;
    public const int Cooldown = 9;
    public const int MaxShots = 5;
    /// <summary>Ближче за 8 px від середини Глека прицілитись не можна — напрямку нема.</summary>
    public const int MinAim = 8 * Px;
    public const int OutMargin = Dm;
    public const int RunPct = 32;

    public const int BonusFirst = 420, BonusEvery = 720, BonusJitter = 360, BonusLife = 900;
    public const int SlowSteps = 360, BackSteps = 120, EyeSteps = 480;
    public const int BombR = 112 * Px;
    public const int BonusSlow = 1, BonusBack = 2, BonusBomb = 3, BonusEye = 4, BonusKinds = 4;

    public const int PtsBead = 10, PtsCombo = 100, PtsStreak = 30, PtsGap = 100, PtsClear = 1000;
    /// <summary>Скільки очок за кожну секунду, на яку пройшов трасу швидше за норму.</summary>
    public const int PtsSecond = 10;
    public const int MaxGaps = 3;

    public const int Playing = 0, Won = 1, Lost = 2;
    public const int EvFire = 1, EvSwap = 2;

    // ---------- стан ----------
    public readonly NamystoTrack Track;
    public readonly int Colors, Speed, ParSteps;
    public readonly List<Bead> Beads = [];
    public readonly List<Shot> Shots = [];
    public NamystoRng ChainRng, ShotRng, BonusRng;
    public int S, ToSpawn, LastColor = -1, Cur, Next, CooldownLeft, SlowLeft, BackLeft, EyeLeft;
    public bool Intro = true;
    public int BonusIn = BonusFirst;
    public int ChainCombo, Streak, MaxCombo, Misses, Fired, Popped, MaxHead, NextId = 1;
    public long Score;
    public int State = Playing, ClearBonus, TimeBonus;

    int[] _bx = new int[64], _by = new int[64];
    bool _posDirty = true;

    public NamystoSim(NamystoTrack track, int colors, int beads, int speed, int parSeconds, uint seed)
    {
        Track = track;
        Colors = Math.Clamp(colors, 2, 6);
        Speed = Math.Max(1, speed);
        ParSteps = parSeconds * StepsPerSecond;
        ToSpawn = Math.Max(1, beads);
        // три окремі потоки випадковості: кольори разка не залежать від того, як грали, — у «Намисті дня» він у всіх той самий
        ChainRng = new NamystoRng(seed);
        ShotRng = new NamystoRng(seed ^ 0x9E3779B9u);
        BonusRng = new NamystoRng(seed ^ 0x85EBCA6Bu);
        Cur = Pick();
        Next = Pick();
    }

    public int End => Track.End;

    // ---------- ввід ----------

    /// <summary>Постріл у точку (tx, ty) у пікселях поля. false — зараз не можна (перезарядка, повно в повітрі, ціль під Глеком).</summary>
    public bool Fire(int tx, int ty)
    {
        if (State != Playing || CooldownLeft > 0 || Shots.Count >= MaxShots) return false;
        long dx = (long)tx * Px - Track.Sx, dy = (long)ty * Px - Track.Sy;
        var len = Isqrt(dx * dx + dy * dy);
        if (len < MinAim) return false;
        var subs = EyeLeft > 0 ? SubsEye : SubsNormal;
        Shots.Add(new Shot
        {
            X = Track.Sx + (int)(dx * Mouth / len),
            Y = Track.Sy + (int)(dy * Mouth / len),
            Vx = (int)(dx * ShotSub / len),
            Vy = (int)(dy * ShotSub / len),
            C = Cur,
            Subs = subs,
            Id = NextId++,
        });
        Cur = Next;
        Next = Pick();
        CooldownLeft = Cooldown;
        Fired++;
        return true;
    }

    /// <summary>Поміняти намистину в горлечку з тією, що на черзі.</summary>
    public bool Swap()
    {
        if (State != Playing) return false;
        (Cur, Next) = (Next, Cur);
        return true;
    }

    /// <summary>Подія журналу: [крок, вид, x, y]. Крок — той, перед яким її застосовано.</summary>
    public bool Apply(int kind, int x, int y) => kind switch
    {
        EvFire => Fire(x, y),
        EvSwap => Swap(),
        _ => false,
    };

    // ---------- крок ----------

    public void Step()
    {
        if (State != Playing) return;
        if (CooldownLeft > 0) CooldownLeft--;
        if (SlowLeft > 0) SlowLeft--;
        if (BackLeft > 0) BackLeft--;
        if (EyeLeft > 0) EyeLeft--;
        Bonuses();
        MoveChain();
        Spawn();
        MoveShots();
        // разок міг спорожніти й наповнитись новими кольорами — Глек тримає лише ті, що є в разку
        FixShooter();
        if (Beads.Count > 0 && Beads[0].D > MaxHead) MaxHead = Beads[0].D;
        if (Beads.Count > 0 && Beads[0].D >= End) State = Lost;
        else if (ToSpawn == 0 && Beads.Count == 0)
        {
            State = Won;
            ClearBonus = PtsClear;
            TimeBonus = S < ParSteps ? (ParSteps - S) * PtsSecond / StepsPerSecond : 0;
            Score += ClearBonus + TimeBonus;
        }
        S++;
    }

    void Bonuses()
    {
        var any = false;
        for (var i = 0; i < Beads.Count; i++)
        {
            var b = Beads[i];
            if (b.Bonus == 0) continue;
            if (--b.BonusLeft <= 0) { b.Bonus = 0; b.BonusLeft = 0; }
            else any = true;
        }
        if (Intro) return;
        if (BonusIn > 0) { BonusIn--; return; }
        if (any) return;
        // бонус лягає на намистину, яку видно і яка ще не біля криниці
        var lo = Dm;
        var hi = End - Dm * 4;
        var count = 0;
        for (var i = 0; i < Beads.Count; i++) if (Beads[i].D >= lo && Beads[i].D <= hi) count++;
        if (count == 0) { BonusIn = 60; return; }
        var k = (int)(BonusRng.Next() % (uint)count);
        var kind = 1 + (int)(BonusRng.Next() % BonusKinds);
        for (var i = 0; i < Beads.Count; i++)
        {
            if (Beads[i].D < lo || Beads[i].D > hi) continue;
            if (k-- != 0) continue;
            Beads[i].Bonus = kind;
            Beads[i].BonusLeft = BonusLife;
            break;
        }
        BonusIn = BonusEvery + (int)(BonusRng.Next() % BonusJitter);
    }

    // ---------- разок ----------

    /// <summary>
    /// Хвіст (шматок разка з останньою намистиною) котиться вперед і штовхає все, що попереду впритул; шматок, що
    /// відірвався, стоїть — хіба що на краях розриву однаковий колір, тоді він котиться назад до хвоста. Зійшлись —
    /// перевіряємо стик: три й більше одного кольору — розсипаються (ланцюжок, комбо росте).
    /// </summary>
    void MoveChain()
    {
        var n = Beads.Count;
        if (n == 0) { ChainCombo = 0; return; }
        // розриви до руху: щоб знати, хто зійшовся саме зараз
        var gap = n > 1 ? new bool[n - 1] : [];
        for (var k = 0; k < n - 1; k++) gap[k] = Beads[k].D - Beads[k + 1].D > Dm;

        // шматки, що котяться назад (магніт): від голови до хвоста; кожен шматок рухається раз
        var segStart = 0;
        for (var k = 0; k < n - 1; k++)
        {
            if (!gap[k]) continue;
            // шматок segStart..k, позаду — розрив, за ним намистина k+1
            if (Beads[k].C == Beads[k + 1].C)
            {
                var room = Beads[k].D - Beads[k + 1].D - Dm;
                var m = room < Magnet ? room : Magnet;
                for (var i = segStart; i <= k; i++) Beads[i].D -= m;
            }
            segStart = k + 1;
        }
        // хвіст: segStart..n-1
        int v;
        if (BackLeft > 0) v = -BackSpeed;
        else if (Intro) v = IntroSpeed;
        else v = SlowLeft > 0 ? Math.Max(1, Speed / 2) : Speed;
        for (var i = segStart; i < n; i++) Beads[i].D += v;
        if (Intro && (Beads[0].D * 100L >= (long)End * IntroPct || S >= IntroMaxSteps)) Intro = false;

        // розсунути тих, хто налазить: від хвоста до голови, не більше PushMax за крок
        for (var k = n - 2; k >= 0; k--)
        {
            var need = Beads[k + 1].D + Dm - Beads[k].D;
            if (need > 0) Beads[k].D += need < PushMax ? need : PushMax;
        }
        // хто зійшовся — позначка на задній намистині стику (позначки їдуть разом із намистинами, коли щось розсиплеться)
        for (var k = 0; k < n - 1; k++)
            if (gap[k] && Beads[k].D - Beads[k + 1].D <= Dm) Beads[k + 1].J = true;
        _posDirty = true;

        while (true)
        {
            var j = -1;
            for (var i = 1; i < Beads.Count; i++) if (Beads[i].J) { j = i; break; }
            if (j < 0) break;
            Beads[j].J = false;
            if (Beads[j - 1].C != Beads[j].C) continue;
            var (a, b) = Run(j);
            if (b - a + 1 >= 3) Pop(a, b, shot: false, gaps: 0);
        }
        for (var i = 0; i < Beads.Count; i++) Beads[i].J = false;

        // ланцюжок живий, поки є куди котитись магнітом
        var pull = false;
        for (var k = 0; k < Beads.Count - 1; k++)
            if (Beads[k].D - Beads[k + 1].D > Dm && Beads[k].C == Beads[k + 1].C) { pull = true; break; }
        if (!pull) ChainCombo = 0;
    }

    /// <summary>Нові намистини з'являються на початку жолоба, щойно хвіст від нього відкотився.</summary>
    void Spawn()
    {
        while (ToSpawn > 0 && (Beads.Count == 0 || Beads[^1].D >= 0))
        {
            var r1 = ChainRng.Next();
            var r2 = ChainRng.Next();
            var c = LastColor >= 0 && r1 % 100 < RunPct ? LastColor : (int)(r2 % (uint)Colors);
            var d = Beads.Count == 0 ? 0 : Beads[^1].D - Dm;
            Beads.Add(new Bead { C = c, D = d, Id = NextId++ });
            LastColor = c;
            ToSpawn--;
            _posDirty = true;
        }
    }

    /// <summary>Однаковий колір навколо намистини i, лише впритул: [a..b].</summary>
    (int A, int B) Run(int i)
    {
        var c = Beads[i].C;
        var a = i;
        while (a > 0 && Beads[a - 1].C == c && Beads[a - 1].D - Beads[a].D <= Dm) a--;
        var b = i;
        while (b < Beads.Count - 1 && Beads[b + 1].C == c && Beads[b].D - Beads[b + 1].D <= Dm) b++;
        return (a, b);
    }

    /// <summary>Розсипати намистини a..b: очки, бонуси, що були на них, Глекові — кольори, що лишились.</summary>
    void Pop(int a, int b, bool shot, int gaps)
    {
        var count = b - a + 1;
        bool slow = false, back = false, eye = false, bomb = false;
        int bombD = 0;
        for (var i = a; i <= b; i++)
        {
            switch (Beads[i].Bonus)
            {
                case BonusSlow: slow = true; break;
                case BonusBack: back = true; break;
                case BonusEye: eye = true; break;
                case BonusBomb: if (!bomb) { bomb = true; bombD = Beads[i].D; } break;
            }
        }
        long pts;
        if (shot)
        {
            Streak++;
            ChainCombo = 1;
            pts = (long)PtsBead * count + (Streak >= 3 ? PtsStreak * (Streak - 2) : 0) + PtsGap * gaps;
        }
        else
        {
            ChainCombo = ChainCombo > 0 ? ChainCombo + 1 : 1;
            pts = (long)PtsBead * count + PtsCombo * (ChainCombo - 1);
        }
        if (ChainCombo > MaxCombo) MaxCombo = ChainCombo;
        Score += pts;
        Popped += count;
        Beads.RemoveRange(a, count);
        _posDirty = true;
        if (bomb) Bomb(bombD);
        if (slow) SlowLeft = SlowSteps;
        if (back) BackLeft = BackSteps;
        if (eye) EyeLeft = EyeSteps;
        FixShooter();
    }

    /// <summary>«Горщик із жаром»: усе в радіусі від місця, де він лежав, — у черепки.</summary>
    void Bomb(int d)
    {
        var (x, y) = Track.At(d);
        var r2 = (long)BombR * BombR;
        for (var i = Beads.Count - 1; i >= 0; i--)
        {
            var (bx, by) = Track.At(Beads[i].D);
            long dx = bx - x, dy = by - y;
            if (dx * dx + dy * dy > r2) continue;
            Beads.RemoveAt(i);
            Score += PtsBead;
            Popped++;
        }
        _posDirty = true;
    }

    /// <summary>Колір, якого в разку вже нема, Глекові ні до чого — міняємо.</summary>
    void FixShooter()
    {
        if (Beads.Count == 0) return;
        if (!Has(Cur)) Cur = Pick();
        if (!Has(Next)) Next = Pick();
    }

    bool Has(int c)
    {
        for (var i = 0; i < Beads.Count; i++) if (Beads[i].C == c) return true;
        return false;
    }

    /// <summary>Наступна намистина Глекові — з кольорів, що є в разку (порожній разок — будь-який колір траси).</summary>
    int Pick()
    {
        var mask = 0;
        for (var i = 0; i < Beads.Count; i++) mask |= 1 << Beads[i].C;
        if (mask == 0) mask = (1 << Colors) - 1;
        var count = 0;
        for (var c = 0; c < 6; c++) if ((mask & (1 << c)) != 0) count++;
        var k = (int)(ShotRng.Next() % (uint)count);
        for (var c = 0; c < 6; c++)
        {
            if ((mask & (1 << c)) == 0) continue;
            if (k-- == 0) return c;
        }
        return 0;
    }

    // ---------- постріли ----------

    void Positions()
    {
        if (!_posDirty) return;
        var n = Beads.Count;
        if (_bx.Length < n) { _bx = new int[n * 2]; _by = new int[n * 2]; }
        for (var i = 0; i < n; i++) (_bx[i], _by[i]) = Track.At(Beads[i].D);
        _posDirty = false;
    }

    void MoveShots()
    {
        for (var si = 0; si < Shots.Count;)
        {
            var s = Shots[si];
            var done = false;
            for (var k = 0; k < s.Subs && !done; k++)
            {
                Positions();
                int x0 = s.X, y0 = s.Y;
                s.X += s.Vx;
                s.Y += s.Vy;
                if (s.G < MaxGaps) s.G += Gaps(x0, y0, s.X, s.Y);
                var hit = Hit(s.X, s.Y);
                if (hit >= 0)
                {
                    Insert(s, hit);
                    done = true;
                }
                else if (s.X < -OutMargin || s.Y < -OutMargin || s.X > Field + OutMargin || s.Y > Field + OutMargin)
                {
                    Misses++;
                    Streak = 0;
                    done = true;
                }
            }
            if (done) Shots.RemoveAt(si);
            else si++;
        }
    }

    /// <summary>Найближча намистина, з якою зіткнулась летюча (центри ближче за діаметр); −1 — ні з чим.</summary>
    int Hit(int x, int y)
    {
        var best = -1;
        var bestD = (long)Dm * Dm;
        for (var i = 0; i < Beads.Count; i++)
        {
            var d = Beads[i].D;
            if (d < Dm / 2 || d >= End) continue;
            long dx = _bx[i] - x, dy = _by[i] - y;
            var q = dx * dx + dy * dy;
            if (q < bestD) { bestD = q; best = i; }
        }
        return best;
    }

    /// <summary>Скільки вузьких розривів разка перетнула летюча намистина за підкрок (постріл «у щілину»).</summary>
    int Gaps(int x0, int y0, int x1, int y1)
    {
        var n = 0;
        var lim = (long)Dm * 3;
        for (var i = 0; i < Beads.Count - 1; i++)
        {
            if (Beads[i].D - Beads[i + 1].D <= Dm) continue;
            if (Beads[i + 1].D < Dm / 2 || Beads[i].D >= End) continue;
            long ax = _bx[i], ay = _by[i], bx = _bx[i + 1], by = _by[i + 1];
            if ((ax - bx) * (ax - bx) + (ay - by) * (ay - by) >= lim * lim) continue;
            if (Cross(x0, y0, x1, y1, ax, ay, bx, by)) n++;
        }
        return n;
    }

    static bool Cross(long x0, long y0, long x1, long y1, long ax, long ay, long bx, long by)
    {
        var d1 = Orient(x0, y0, x1, y1, ax, ay);
        var d2 = Orient(x0, y0, x1, y1, bx, by);
        var d3 = Orient(ax, ay, bx, by, x0, y0);
        var d4 = Orient(ax, ay, bx, by, x1, y1);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    static long Orient(long ax, long ay, long bx, long by, long cx, long cy) => (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);

    /// <summary>
    /// Летюча намистина влучила в j: лягає перед нею (з боку криниці), якщо влучила в передню половину, інакше — на її
    /// місце, і все попереду розсувається. Три й більше одного кольору впритул — розсипаються.
    /// </summary>
    void Insert(Shot s, int j)
    {
        var (tx, ty) = Track.Tangent(Beads[j].D);
        long rx = s.X - _bx[j], ry = s.Y - _by[j];
        var ahead = rx * tx + ry * ty > 0;
        var idx = ahead ? j : j + 1;
        var d = ahead ? Beads[j].D + Dm : Beads[j].D;
        Beads.Insert(idx, new Bead { C = s.C, D = d, Id = s.Id });
        _posDirty = true;
        var (a, b) = Run(idx);
        if (b - a + 1 >= 3) Pop(a, b, shot: true, gaps: s.G);
        else Streak = 0;
    }

    // ---------- для перевірки ----------

    /// <summary>FNV-1a усього стану — для паритету C# ↔ JS.</summary>
    public uint Hash()
    {
        var h = 2166136261u;
        void Mix(int v) { h = (h ^ (uint)v) * 16777619u; }
        Mix(S); Mix((int)Score); Mix((int)(Score >> 32)); Mix((int)ChainRng.State); Mix((int)ShotRng.State); Mix((int)BonusRng.State);
        Mix(Cur); Mix(Next); Mix(ToSpawn); Mix(CooldownLeft); Mix(SlowLeft); Mix(BackLeft); Mix(EyeLeft); Mix(Intro ? 1 : 0);
        Mix(BonusIn); Mix(ChainCombo); Mix(Streak); Mix(State); Mix(Beads.Count); Mix(Shots.Count);
        foreach (var b in Beads) { Mix(b.C); Mix(b.D); Mix(b.Bonus); Mix(b.BonusLeft); }
        foreach (var s in Shots) { Mix(s.X); Mix(s.Y); Mix(s.Vx); Mix(s.Vy); Mix(s.C); Mix(s.G); Mix(s.Subs); }
        return h;
    }

    /// <summary>Цілий корінь (найбільше r, що r² ≤ n): Math.Sqrt дає наближення, цикли доводять до точного — однаково в C# і JS.</summary>
    public static long Isqrt(long n)
    {
        if (n <= 0) return 0;
        var r = (long)Math.Sqrt(n);
        while (r * r > n) r--;
        while ((r + 1) * (r + 1) <= n) r++;
        return r;
    }
}

public sealed class Bead
{
    /// <summary>Колір 0…5.</summary>
    public int C;
    /// <summary>Відстань уздовж жолоба (su).</summary>
    public int D;
    /// <summary>Бонус на намистині (0 — нема) і скільки кроків він ще протримається.</summary>
    public int Bonus, BonusLeft;
    public int Id;
    /// <summary>Позначка «щойно зійшлися з шматком спереду — перевірити стик».</summary>
    public bool J;
}

public sealed class Shot
{
    public int X, Y, Vx, Vy, C, G, Subs, Id;
}

/// <summary>mulberry32 — той самий у JS (Math.imul, &gt;&gt;&gt; 0).</summary>
public struct NamystoRng(uint seed)
{
    public uint State = seed;

    public uint Next()
    {
        unchecked
        {
            State += 0x6D2B79F5u;
            var t = State;
            t = (t ^ (t >> 15)) * (t | 1u);
            t ^= t + (t ^ (t >> 7)) * (t | 61u);
            return t ^ (t >> 14);
        }
    }
}

/// <summary>
/// Жолоб: ламана з точок у su (сервер будує їх раз — <see cref="NamystoTracks"/> — і віддає браузеру як є) і довжини
/// відрізків цілим коренем. Позиція на відстані d — лінійно між точками, цілим діленням.
/// </summary>
public sealed class NamystoTrack
{
    public readonly int[] X, Y, L;
    /// <summary>Де стоїть Глек (su).</summary>
    public readonly int Sx, Sy;
    public int End => L[^1];
    public int Count => X.Length;

    public NamystoTrack(int[] xy, int sx, int sy)
    {
        var n = xy.Length / 2;
        X = new int[n];
        Y = new int[n];
        L = new int[n];
        for (var i = 0; i < n; i++)
        {
            X[i] = xy[2 * i];
            Y[i] = xy[2 * i + 1];
            if (i == 0) continue;
            long dx = X[i] - X[i - 1], dy = Y[i] - Y[i - 1];
            L[i] = L[i - 1] + (int)NamystoSim.Isqrt(dx * dx + dy * dy);
        }
        Sx = sx;
        Sy = sy;
    }

    /// <summary>Номер відрізка, на якому лежить d: найбільше i з L[i] ≤ d (і i &lt; n − 1).</summary>
    int Seg(int d)
    {
        int lo = 0, hi = L.Length - 2;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >> 1;
            if (L[mid] <= d) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    public (int X, int Y) At(int d)
    {
        if (d <= 0) return (X[0], Y[0]);
        if (d >= End) return (X[^1], Y[^1]);
        var i = Seg(d);
        var len = L[i + 1] - L[i];
        if (len <= 0) return (X[i], Y[i]);
        long t = d - L[i];
        return (X[i] + (int)((X[i + 1] - X[i]) * t / len), Y[i] + (int)((Y[i + 1] - Y[i]) * t / len));
    }

    /// <summary>Напрям жолоба в точці d (до криниці) — вектор відрізка, не нормований.</summary>
    public (int X, int Y) Tangent(int d)
    {
        var i = d <= 0 ? 0 : d >= End ? L.Length - 2 : Seg(d);
        return (X[i + 1] - X[i], Y[i + 1] - Y[i]);
    }
}
