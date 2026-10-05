namespace Hlechyky.Games.Impl;

/// <summary>
/// Мішень тиру: що (<see cref="TyrCore.Jug"/>…), коли вискочила (<paramref name="T0"/>, мс від старту стенду), скільки
/// живе (<paramref name="Dur"/>, мс), де (центр <paramref name="X"/>, <paramref name="Y"/> в одиницях поля 1000×600),
/// швидкість <paramref name="Vx"/> (одиниць за секунду: качки й діжки на воді; на полиці — 0) і радіус влучання.
/// </summary>
public sealed record TyrTarget(int Id, int Kind, int T0, int Dur, int X, int Y, int Vx, int R)
{
    public int End => T0 + Dur;
    public bool AliveAt(int t) => t >= T0 && t < End;
    public double XAt(int t) => X + Vx * (t - T0) / 1000.0;
}

/// <summary>
/// Своя копія тиру одного стрільця: рахунок, барабан, дим і які мішені він уже збив. Мішені для всіх однакові
/// (розклад стенду), а збиває кожен свої — тому пінг не вирішує, лише точність і реакція.
/// </summary>
public sealed class TyrShooter
{
    public long Score;
    public readonly long[] StandScore = new long[TyrCore.Stands];
    public int Ammo = TyrCore.Drum;
    /// <summary>Мс стенду, коли барабан знову повний (0 — не перезаряджає).</summary>
    public int ReloadAt;
    /// <summary>До якої мс стенду екран у диму (не стріляєш).</summary>
    public int SmokeUntil = int.MinValue;
    /// <summary>Мс останнього прийнятого пострілу — t тільки росте.</summary>
    public int LastT = int.MinValue;
    /// <summary>Скільки дій прийнято за стенд (постріли й перезарядки).</summary>
    public int N;
    /// <summary>
    /// Номер (<c>q</c>) останньої прийнятої дії від клієнта — за ним клієнт звіряє передбачення: відхилена дія посеред
    /// черги інакше зсувала б лічильник, і наступний постріл рахувався б двічі. Між стендами не скидається.
    /// </summary>
    public long LastQ;
    public int Shots, Hits, Misses, Kegs, Pots, Golds;
    public readonly int[] StandShots = new int[TyrCore.Stands], StandMisses = new int[TyrCore.Stands], StandBad = new int[TyrCore.Stands];
    public readonly int[] StandGolds = new int[TyrCore.Stands];
    bool[] _hit = [];
    readonly List<int> _hitList = [];
    /// <summary>Грає цю партію (людина зі старту чи бот).</summary>
    public bool Plays;

    public bool HasHit(int id) => id >= 0 && id < _hit.Length && _hit[id];
    public IReadOnlyList<int> HitIds => _hitList;

    /// <summary>Новий стенд: барабан повний, дим розвіявся, збитого нема (рахунок партії лишається).</summary>
    public void NewStand(int targets)
    {
        Ammo = TyrCore.Drum;
        ReloadAt = 0;
        SmokeUntil = int.MinValue;
        LastT = int.MinValue;
        N = 0;
        _hit = new bool[targets];
        _hitList.Clear();
    }

    public void Mark(int id)
    {
        _hit[id] = true;
        _hitList.Add(id);
    }

    /// <summary>Перезарядка скінчилась на мс <paramref name="t"/> — барабан повний.</summary>
    public bool Settle(int t)
    {
        if (ReloadAt <= 0 || t < ReloadAt) return false;
        Ammo = TyrCore.Drum;
        ReloadAt = 0;
        return true;
    }

    public bool Reloading(int t) => ReloadAt > 0 && t < ReloadAt;
    public bool Smoked(int t) => t < SmokeUntil;
}

/// <summary>Що вийшло з пострілу: збита мішень (−1 — промах), скільки очок, або що це була перезарядка.</summary>
public readonly record struct TyrShot(int Target, int Delta, bool Reload);

/// <summary>
/// Правила тиру без столу: розклад мішеней стенду (детермінований за генератором), постріл і перезарядка однієї копії.
/// Перевірка влучання — тут, за розкладом і часом пострілу; час і його межі перевіряє гра (<see cref="Tyr"/>).
/// </summary>
public static class TyrCore
{
    public const int W = 1000, H = 600;
    public const int Stands = 3;
    public const int StandMs = 45_000, ReadyMs = 3_000;
    public const int Drum = 6, ReloadMs = 1_000, SmokeMs = 1_500;
    /// <summary>Найменший проміжок між діями одного стрільця, мс — щоб «кулемет» не сипав постріли пачкою.</summary>
    public const int MinGap = 90;
    /// <summary>Зазор до радіуса мішені: палець на телефоні товстий, а мішень рухається.</summary>
    public const int Slack = 6;
    /// <summary>
    /// Мішень, що сховалась, не влучається пострілом, який прийшов пізніше за це після її зникнення (мс): інакше
    /// змінений клієнт міг би «в минуле» добивати те, що вже зникло (до <c>MaxLag</c> = 0,8 с). Чесному гравцеві
    /// вистачає: це запізнення в один бік, а не пінг туди й назад.
    /// </summary>
    public const int RetroMs = 300;

    /// <summary>Що буває: глек +1, качка +2, золотий глек +3, діжка з порохом −3 (і дим), бабин горщик −2.</summary>
    public const int Jug = 0, Duck = 1, Gold = 2, Keg = 3, Pot = 4;
    public static readonly int[] Points = [1, 2, 3, -3, -2];
    public static readonly int[] Radius = [36, 36, 30, 38, 38];
    public static bool Bad(int kind) => kind is Keg or Pot;

    /// <summary>Стенди: 0 — полиця з глеками, 1 — качки на воді, 2 — усе разом із золотими.</summary>
    public const int Shelf = 0, Water = 1, Mix = 2;
    public static readonly string[] StandNames = ["Полиця з глеками", "Качки на воді", "Усе разом"];

    /// <summary>Полиці: два ряди по вісім гнізд. Вода: дві доріжки — верхня пливе вправо, нижня вліво.</summary>
    public static readonly int[] ShelfY = [150, 290];
    public static readonly int[] LaneY = [420, 520];
    public const int Slots = 8;
    public static int SlotX(int k) => 125 + 107 * k;

    const int JugMs = 1_200, GoldMs = 600, KegMs = 1_500, PotMs = 1_400;

    /// <summary>
    /// Розклад стенду: мішені за часом появи (Id = індекс). Гнізда полиці не накладаються в часі, качки на доріжці
    /// пливуть з однаковою швидкістю й не наздоганяють одна одну — тож дві мішені під одним пострілом рідкість.
    /// </summary>
    public static List<TyrTarget> Schedule(int stand, Random rng)
    {
        var raw = new List<(int Kind, int T0, int Dur, int X, int Y, int Vx, int R)>();
        var busy = new int[ShelfY.Length * Slots];
        var end = StandMs;

        void ShelfAt(int t, int kind)
        {
            var dur = kind switch { Gold => GoldMs, Keg => KegMs, Pot => PotMs, _ => JugMs };
            if (t + dur > end) return;
            // Вільне гніздо: кілька спроб навмання, далі — пропуск (не біда, наступна мішень за мить).
            for (var a = 0; a < 6; a++)
            {
                var s = rng.Next(busy.Length);
                if (busy[s] > t) continue;
                busy[s] = t + dur + 200;
                raw.Add((kind, t, dur, SlotX(s % Slots), ShelfY[s / Slots], 0, Radius[kind]));
                return;
            }
        }

        void Lane(int lane, int speed, int gapMin, int gapMax, int kegPct)
        {
            var dir = lane == 0 ? 1 : -1;
            var from = dir > 0 ? -40 : W + 40;
            var dur = (W + 80) * 1000 / speed;
            for (var t = 300 + rng.Next(gapMin); t + 1_500 <= end; t += gapMin + rng.Next(gapMax - gapMin))
            {
                var kind = rng.Next(100) < kegPct ? Keg : Duck;
                raw.Add((kind, t, Math.Min(dur, end - t), from, LaneY[lane], dir * speed, Radius[kind]));
            }
        }

        int Pick(int potPct, int kegPct)
        {
            var r = rng.Next(100);
            return r < potPct ? Pot : r < potPct + kegPct ? Keg : Jug;
        }

        switch (stand)
        {
            case Shelf:
                for (var t = 400; t < end; t += 380 + rng.Next(320)) ShelfAt(t, Pick(16, 12));
                break;
            case Water:
                Lane(0, 230, 900, 1_800, 18);
                Lane(1, 300, 900, 1_800, 18);
                break;
            default:
                for (var t = 500; t < end; t += 650 + rng.Next(450)) ShelfAt(t, Pick(20, 16));
                Lane(0, 260, 1_300, 2_400, 15);
                Lane(1, 330, 1_300, 2_400, 15);
                for (var t = 2_000 + rng.Next(1_500); t < end; t += 2_500 + rng.Next(2_000)) ShelfAt(t, Gold);
                break;
        }
        raw.Sort((a, b) => a.T0 != b.T0 ? a.T0.CompareTo(b.T0) : a.Y.CompareTo(b.Y));
        var list = new List<TyrTarget>(raw.Count);
        foreach (var r in raw) list.Add(new TyrTarget(list.Count, r.Kind, r.T0, r.Dur, r.X, r.Y, r.Vx, r.R));
        return list;
    }

    /// <summary>
    /// Постріл копії <paramref name="me"/> у мить <paramref name="t"/> (мс стенду) у точку (x, y). Повертає null, якщо
    /// стріляти зараз не можна (дим, перезарядка, зачасто) — стан тоді не змінено. Порожній барабан — постріл стає
    /// перезарядкою (так і на телефоні зручніше: тиснеш далі — сам перезаряджає).
    /// </summary>
    /// <param name="now">Мс стенду за годинником сервера, коли постріл прийшов (null — та сама мить, що й t).</param>
    public static TyrShot? Shoot(TyrShooter me, IReadOnlyList<TyrTarget> targets, int stand, int t, double x, double y, int? now = null)
    {
        if (t < me.LastT + MinGap) return null;
        me.Settle(t);
        if (me.Reloading(t) || me.Smoked(t)) return null;
        me.LastT = t;
        me.N++;
        if (me.Ammo <= 0)
        {
            me.ReloadAt = t + ReloadMs;
            return new TyrShot(-1, 0, true);
        }
        me.Ammo--;
        me.Shots++;
        me.StandShots[stand]++;
        var hit = Find(me, targets, t, x, y, now);
        if (hit is null)
        {
            me.Misses++;
            me.StandMisses[stand]++;
            return new TyrShot(-1, 0, false);
        }
        var tg = hit;
        me.Mark(tg.Id);
        var d = Points[tg.Kind];
        me.Score += d;
        me.StandScore[stand] += d;
        me.Hits++;
        switch (tg.Kind)
        {
            case Keg:
                me.Kegs++;
                me.StandBad[stand]++;
                me.SmokeUntil = t + SmokeMs;
                break;
            case Pot:
                me.Pots++;
                me.StandBad[stand]++;
                break;
            case Gold:
                me.Golds++;
                me.StandGolds[stand]++;
                break;
        }
        return new TyrShot(tg.Id, d, false);
    }

    /// <summary>Перезарядка вручну (правий клік, «⟳», X на паді): лише коли барабан не повний і не в диму.</summary>
    public static bool Reload(TyrShooter me, int t)
    {
        if (t < me.LastT + MinGap) return false;
        me.Settle(t);
        if (me.Reloading(t) || me.Smoked(t) || me.Ammo >= Drum) return false;
        me.LastT = t;
        me.N++;
        me.ReloadAt = t + ReloadMs;
        return true;
    }

    /// <summary>
    /// Мішень під пострілом: жива в мить t, ще не збита цим стрільцем, найближча до центру (у радіусах) і не схована
    /// довше ніж <see cref="RetroMs"/> до <paramref name="now"/> — коли постріл дійшов до сервера.
    /// </summary>
    public static TyrTarget? Find(TyrShooter me, IReadOnlyList<TyrTarget> targets, int t, double x, double y, int? now = null)
    {
        TyrTarget? best = null;
        var bestK = double.MaxValue;
        // Розклад відсортовано за T0: усе, що вискочить пізніше t, — далі; живуть мішені не довше ~5 с.
        for (var i = 0; i < targets.Count; i++)
        {
            var tg = targets[i];
            if (tg.T0 > t) break;
            if (!tg.AliveAt(t) || me.HasHit(tg.Id) || now - tg.End > RetroMs) continue;
            double dx = tg.XAt(t) - x, dy = tg.Y - y, rr = tg.R + Slack;
            var d2 = dx * dx + dy * dy;
            if (d2 > rr * rr) continue;
            var k = d2 / (tg.R * tg.R);
            if (k < bestK) { bestK = k; best = tg; }
        }
        return best;
    }

    /// <summary>Скільки очок можна взяти за стенд, якщо збити все хороше (для підписів і бота).</summary>
    public static int Max(IReadOnlyList<TyrTarget> targets)
    {
        var s = 0;
        foreach (var t in targets) if (!Bad(t.Kind)) s += Points[t.Kind];
        return s;
    }
}
