namespace Hlechyky.Games.Impl;

/// <summary>
/// Один селянин ярмарку. Бот і гравець — той самий клас і той самий <see cref="CrowdCore.Step"/>: гравцеві
/// <see cref="Want"/> пише ввід, ботові — <see cref="CrowdCore.Think"/>. Решта полів мозку в гравця просто лежать.
/// </summary>
public sealed class CrowdVillager
{
    /// <summary>Номер у кадрі; за раунд не міняється, між раундами тасується.</summary>
    public int Id;
    /// <summary>Центр у одиницях світу (клітинка — 32).</summary>
    public int X, Y;
    /// <summary>Куди дивиться: 0 праворуч, 1 вниз, 2 ліворуч, 3 вгору.</summary>
    public int Dir;
    /// <summary>Куди хоче йти; -1 — стоїть.</summary>
    public int Want = -1;
    /// <summary>Цього тика зробив крок.</summary>
    public bool Moving;
    /// <summary>Хотів іти, але коробка вперлась у перешкоду.</summary>
    public bool Blocked;
    /// <summary>Місце гравця; -1 — бот.</summary>
    public int Owner = -1;
    /// <summary>Збитий гравець: лежить до кінця раунду.</summary>
    public bool Dead;
    /// <summary>Збитий бот лежить ще стільки тиків.</summary>
    public int Fallen;
    /// <summary>Гравець торгується ще стільки тиків (рух ігнорується).</summary>
    public int Haggle;
    public int HaggleStall = -1;

    // ---- мозок бота ----
    /// <summary>Клітинка цілі; -1 — треба обрати.</summary>
    public int Target = -1;
    /// <summary>Точка цілі в клітинці (центр ± 6) — вона ж «смуга», якою бот повертає на перехрестях.</summary>
    public int Tx, Ty;
    /// <summary>Ціль — прилавок цього лотка; -1 — просто кудись.</summary>
    public int TargetStall = -1;
    public int Stand;
    public int Wander;
    public bool WanderNext;

    // ---- вигляд на раунд ----
    public int Hat, HatColor, Shirt, Skin, Name;

    /// <summary>Стан у кадрі: 0 стоїть, 1 іде, 2 лежить бот, 3 мертвий гравець.</summary>
    public int State => Dead ? 3 : Fallen > 0 ? 2 : Moving ? 1 : 0;

    public bool Upright => !Dead && Fallen == 0;
}

/// <summary>
/// Юрма без кімнат і очок: селяни, один спільний крок, мозок ботів, конус рогатки, розстановка раунду.
/// Усе ціле, уся випадковість — з переданого <see cref="Random"/> у строгому порядку, тож той самий сід і
/// той самий ввід дають побайтно ті самі кадри. У гарячому циклі — жодної алокації, крім кадру.
/// </summary>
public sealed class CrowdCore(Random rng)
{
    /// <summary>Крок за тик — однаковий для всіх: 75 од/с ≈ 2,3 клітинки/с. Бігу нема.</summary>
    public const int Speed = 3;
    /// <summary>Половина коробки селянина 16×16 (для перешкод; один крізь одного селяни проходять).</summary>
    public const int Half = 8;
    /// <summary>Купують з такої відстані до точки прилавка.</summary>
    public const int BuyRange = 40;
    /// <summary>Конус рогатки: найближчий у ±35° на стільки одиниць.</summary>
    public const int ShotRange = 160;
    /// <summary>Явна ціль (клік по селянину) приймається до стількох: клієнт бачить кадр на ~100 мс старший.</summary>
    public const int ShotRangeMax = 190;
    /// <summary>cos²35° ≈ 0,671 — у тисячних, щоб рахувати конус на цілих.</summary>
    public const int ConeCos2Milli = 671;
    public const int StandMin = 12, StandMax = 75;
    /// <summary>Відсоток «тиняння» після стояння і скільки воно триває.</summary>
    public const int WanderChance = 15, WanderMin = 8, WanderMax = 24;
    /// <summary>Відсоток цілей-прилавків (решта — випадкова клітинка).</summary>
    public const int StallPick = 60;
    /// <summary>Збитий бот лежить 3 с.</summary>
    public const int FallTicks = 75;
    /// <summary>Скільки бот може простояти на самому старті раунду (щоб нерухомі гравці не виділялись).</summary>
    public const int OpeningStandMax = 75;

    public static readonly int[] DX = [1, 0, -1, 0];
    public static readonly int[] DY = [0, 1, 0, -1];

    readonly Random _rng = rng;
    readonly int[] _nameOrder = new int[CrowdMap.Names.Length];

    public CrowdVillager[] V { get; private set; } = [];
    public int N => V.Length;

    // ---------------------------------------------------------------------------------------------
    // Крок — один на всіх
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Крок селянина. Хто торгується, лежить, мертвий чи нікуди не хоче — стоїть. Інакше повертається в бік
    /// <see cref="CrowdVillager.Want"/> (навіть коли далі стіна) і робить рівно <see cref="Speed"/> одиниць, якщо
    /// коробка там уміщається. Ця функція одна для ботів і гравців.
    /// </summary>
    public static void Step(CrowdVillager v)
    {
        if (v.Haggle > 0 || v.Fallen > 0 || v.Dead || v.Want < 0)
        {
            v.Moving = false;
            v.Blocked = false;
            return;
        }
        v.Dir = v.Want;
        int nx = v.X + DX[v.Dir] * Speed, ny = v.Y + DY[v.Dir] * Speed;
        if (CrowdMap.BoxFits(nx, ny))
        {
            v.X = nx;
            v.Y = ny;
            v.Moving = true;
            v.Blocked = false;
        }
        else
        {
            v.Moving = false;
            v.Blocked = true;
        }
    }

    /// <summary>Усі кроком, за зростанням id.</summary>
    public void StepAll()
    {
        var v = V;
        for (var i = 0; i < v.Length; i++) Step(v[i]);
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
    /// Бот виставляє <see cref="CrowdVillager.Want"/>: іде до цілі (прилавок чи випадкова клітинка), прийшов — стоїть
    /// 12–75 тиків обличчям до лотка, інколи потім тиняється. Між клітинками повертає лише тоді, коли
    /// вирівнявся на свою «смугу» (центр клітинки + зсув цілі): коробка тоді точно вміщається в коридор, а
    /// повороти не лягають на одну решітку.
    /// </summary>
    public void Think(CrowdVillager v)
    {
        if (v.Dead || v.Fallen > 0) return;
        if (v.Stand > 0)
        {
            v.Stand--;
            v.Want = -1;
            if (v.Stand == 0 && v.WanderNext)
            {
                v.WanderNext = false;
                v.Wander = _rng.Next(WanderMin, WanderMax + 1);
                v.Want = _rng.Next(4);
            }
            return;
        }
        if (v.Wander > 0)
        {
            v.Wander--;          // Want уже стоїть; біля стіни Step сам зупинить
            return;
        }
        if (v.Blocked)
        {
            // уперся (таке буває з колишнім гравцем, що стояв криво) — ціль геть, наступного тика нова
            v.Blocked = false;
            v.Target = -1;
            v.Want = -1;
            return;
        }
        if (v.Target < 0) PickTarget(v);

        var cell = CrowdMap.CellOf(v.X, v.Y);
        if (cell == v.Target)
        {
            int dx = v.Tx - v.X, dy = v.Ty - v.Y;
            if (Math.Abs(dx) <= 2 && Math.Abs(dy) <= 2)
            {
                v.Stand = _rng.Next(StandMin, StandMax + 1);
                v.Dir = v.TargetStall >= 0 ? CrowdMap.Stalls[v.TargetStall].Face : _rng.Next(4);
                v.WanderNext = _rng.Next(100) < WanderChance;
                v.Want = -1;
                v.Target = -1;
                v.TargetStall = -1;
                return;
            }
            v.Want = Math.Abs(dx) > Math.Abs(dy) ? (dx > 0 ? 0 : 2) : (dy > 0 ? 1 : 3);
            return;
        }

        var hop = CrowdMap.NextHop[v.Target * CrowdMap.Cells + cell];
        if (hop == CrowdMap.NoHop)
        {
            v.Target = -1;
            v.Want = -1;
            return;
        }
        // Вирівнятись на смугу поперек напрямку кроку, потім іти.
        if (hop is 0 or 2)
        {
            var lane = cell / CrowdMap.W * CrowdMap.Cell + CrowdMap.Cell / 2 + (v.Ty - CrowdMap.CenterY(v.Target));
            if (v.Y < lane - 1) { v.Want = 1; return; }
            if (v.Y > lane + 1) { v.Want = 3; return; }
        }
        else
        {
            var lane = cell % CrowdMap.W * CrowdMap.Cell + CrowdMap.Cell / 2 + (v.Tx - CrowdMap.CenterX(v.Target));
            if (v.X < lane - 1) { v.Want = 0; return; }
            if (v.X > lane + 1) { v.Want = 2; return; }
        }
        v.Want = hop;
    }

    void PickTarget(CrowdVillager v)
    {
        int cell;
        if (_rng.Next(100) < StallPick)
        {
            var k = _rng.Next(CrowdMap.Stalls.Length);
            var s = CrowdMap.Stalls[k];
            cell = _rng.Next(2) == 0 ? s.C0 : s.C1;
            v.TargetStall = k;
        }
        else
        {
            cell = CrowdMap.Walkable[_rng.Next(CrowdMap.Walkable.Length)];
            v.TargetStall = -1;
        }
        v.Target = cell;
        v.Tx = CrowdMap.CenterX(cell) + _rng.Next(-6, 7);
        v.Ty = CrowdMap.CenterY(cell) + _rng.Next(-6, 7);
    }

    /// <summary>Бот устав після падіння чи став ботом після виходу гравця: думає з чистого аркуша.</summary>
    public static void Forget(CrowdVillager v)
    {
        v.Target = -1;
        v.TargetStall = -1;
        v.Want = -1;
        v.Stand = 0;
        v.Wander = 0;
        v.WanderNext = false;
        v.Blocked = false;
    }

    // ---------------------------------------------------------------------------------------------
    // Рогатка
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Найближчий до стрільця селянин у конусі ±35° на <see cref="ShotRange"/> перед ним (стоїть чи йде); при
    /// рівній відстані — менший id. -1 — нікого. Той самий алгоритм малює підсвітку на клієнті.
    /// </summary>
    public int Cone(CrowdVillager p)
    {
        int best = -1;
        long bestD = long.MaxValue;
        int fx = DX[p.Dir], fy = DY[p.Dir];
        var v = V;
        for (var i = 0; i < v.Length; i++)
        {
            var q = v[i];
            if (q == p || !q.Upright) continue;
            long dx = q.X - p.X, dy = q.Y - p.Y;
            var d2 = dx * dx + dy * dy;
            if (d2 > (long)ShotRange * ShotRange) continue;
            var dot = dx * fx + dy * fy;
            if (dot <= 0 || dot * dot * 1000 < ConeCos2Milli * d2) continue;
            if (d2 < bestD)
            {
                bestD = d2;
                best = i;
            }
        }
        return best;
    }

    public static long Dist2(CrowdVillager a, int x, int y)
    {
        long dx = a.X - x, dy = a.Y - y;
        return dx * dx + dy * dy;
    }

    // ---------------------------------------------------------------------------------------------
    // Раунд
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Нова юрма: <paramref name="owners"/> — місця гравців (ідуть першими), далі <paramref name="bots"/> ботів.
    /// Порядок випадковості: розстановка (гравці — не ближче 3 клітинок один до одного) → тасування id →
    /// вигляд → імена → перше стояння ботів. Прилавки на старті вільні для всіх однаково.
    /// </summary>
    public void Deal(IReadOnlyList<int> owners, int bots)
    {
        var n = owners.Count + bots;
        var list = new CrowdVillager[n];
        var walk = CrowdMap.Walkable;
        for (var i = 0; i < n; i++)
        {
            var v = new CrowdVillager { Owner = i < owners.Count ? owners[i] : -1 };
            int cell = 0;
            for (var attempt = 0; attempt < 200; attempt++)
            {
                cell = walk[_rng.Next(walk.Length)];
                if (CrowdMap.CounterOf[cell] >= 0) continue;
                if (v.Owner >= 0 && TooClose(list, i, cell)) continue;
                break;
            }
            v.X = CrowdMap.CenterX(cell) + _rng.Next(-6, 7);
            v.Y = CrowdMap.CenterY(cell) + _rng.Next(-6, 7);
            v.Dir = _rng.Next(4);
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
            v.Shirt = _rng.Next(8);
            v.Skin = _rng.Next(4);
        }
        for (var i = 0; i < _nameOrder.Length; i++) _nameOrder[i] = i;
        for (var i = _nameOrder.Length - 1; i > 0; i--)
        {
            var j = _rng.Next(i + 1);
            (_nameOrder[i], _nameOrder[j]) = (_nameOrder[j], _nameOrder[i]);
        }
        for (var i = 0; i < n; i++) list[i].Name = _nameOrder[i % _nameOrder.Length];
        for (var i = 0; i < n; i++)
            if (list[i].Owner < 0) list[i].Stand = _rng.Next(0, OpeningStandMax + 1);
        V = list;
    }

    /// <summary>Гравця не ставимо ближче 3 клітинок (Чебишев) до вже поставлених гравців.</summary>
    static bool TooClose(CrowdVillager[] placed, int count, int cell)
    {
        int cx = cell % CrowdMap.W, cy = cell / CrowdMap.W;
        for (var i = 0; i < count; i++)
        {
            var p = placed[i];
            if (p.Owner < 0) continue;
            int px = p.X / CrowdMap.Cell, py = p.Y / CrowdMap.Cell;
            if (Math.Max(Math.Abs(px - cx), Math.Abs(py - cy)) < 3) return true;
        }
        return false;
    }

    /// <summary>Кадр: 4 числа на селянина за id — x, y, d, s. Новий масив щоразу: розсилка серіалізує його вже поза замком.</summary>
    public int[] Pack()
    {
        var v = V;
        var a = new int[v.Length * 4];
        for (var i = 0; i < v.Length; i++)
        {
            var q = v[i];
            a[i * 4] = q.X;
            a[i * 4 + 1] = q.Y;
            a[i * 4 + 2] = q.Dir;
            a[i * 4 + 3] = q.State;
        }
        return a;
    }

    /// <summary>Вигляд за id: hat, hatColor, shirt, skin.</summary>
    public int[] Looks()
    {
        var v = V;
        var a = new int[v.Length * 4];
        for (var i = 0; i < v.Length; i++)
        {
            a[i * 4] = v[i].Hat;
            a[i * 4 + 1] = v[i].HatColor;
            a[i * 4 + 2] = v[i].Shirt;
            a[i * 4 + 3] = v[i].Skin;
        }
        return a;
    }

    public string[] NamesNow()
    {
        var v = V;
        var a = new string[v.Length];
        for (var i = 0; i < v.Length; i++) a[i] = CrowdMap.Names[v[i].Name];
        return a;
    }
}
