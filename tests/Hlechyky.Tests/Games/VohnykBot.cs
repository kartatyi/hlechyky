using Hlechyky.Games.Impl;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Робот-гравець для «Вогника і Краплі»: крутить <see cref="VohnykWorld"/> крок за кроком і пише журнал вводу
/// так само, як його писала б людина (лише зміни k). Дії — ітератори «що тиснути цього кроку», двоє героїв
/// можуть діяти одночасно. З цього складено записані проходження всіх 15 рівнів (VohnykPlans), а тести
/// потім програють ці журнали наосліп — без робота.
/// </summary>
public sealed class VohnykBot(VohnykLevel level)
{
    public const int Px = VohnykWorld.Px;
    public const int L = VohnykWorld.KeyLeft, R = VohnykWorld.KeyRight, J = VohnykWorld.KeyJump;

    public readonly VohnykWorld W = new(level);
    public readonly List<int[]> Log = [];
    readonly int[] _k = new int[2];
    public int Steps { get; private set; }
    /// <summary>Скільки кроків максимум на одну дію — щоб завислий план падав, а не крутився вічно.</summary>
    public int Patience { get; set; } = 3000;

    public int CenterPx(int h) => (W.X[h] + VohnykWorld.HeroW / 2) / Px;
    public int FeetPx(int h) => (W.Y[h] + VohnykWorld.HeroH) / Px;
    public bool Ground(int h) => W.Grounded[h] != 0;

    void Press(int h, int keys)
    {
        if (_k[h] == keys) return;
        _k[h] = keys;
        Log.Add([Steps + 1, h, keys]);
    }

    /// <summary>Один крок світу з поточними клавішами. Смерть чи зайвий «пройдено» — одразу виняток.</summary>
    public void Step()
    {
        Steps++;
        W.Step(_k[0], _k[1]);
        if (W.AnyDied)
            throw new InvalidOperationException($"рівень {level.N}: загинув герой {(W.Died[0] != 0 ? 0 : 1)} на кроці {Steps} " +
                $"(Вогник {CenterPx(0)},{FeetPx(0)}; Крапля {CenterPx(1)},{FeetPx(1)})");
    }

    /// <summary>
    /// Дії для обох героїв разом, доки не скінчаться обидві (null — стоїть). Коли одна скінчилась раніше,
    /// той герой стоїть без клавіш.
    /// </summary>
    public VohnykBot Do(IEnumerable<int>? fire, IEnumerable<int>? water = null)
    {
        var a = fire?.GetEnumerator();
        var b = water?.GetEnumerator();
        var n = 0;
        while (true)
        {
            var more = false;
            if (a is not null)
            {
                if (a.MoveNext()) { Press(0, a.Current); more = true; }
                else { a = null; Press(0, 0); }
            }
            if (b is not null)
            {
                if (b.MoveNext()) { Press(1, b.Current); more = true; }
                else { b = null; Press(1, 0); }
            }
            if (a is null && b is null && !more) break;
            if (!more) continue;
            Step();
            if (++n > Patience) throw new InvalidOperationException($"рівень {level.N}: дія не скінчилась за {Patience} кроків (крок {Steps}; " +
                $"Вогник {CenterPx(0)},{FeetPx(0)}; Крапля {CenterPx(1)},{FeetPx(1)})");
        }
        return this;
    }

    /// <summary>Лише один герой діє, другий стоїть.</summary>
    public VohnykBot Only(int h, IEnumerable<int> act) => h == 0 ? Do(act, null) : Do(null, act);

    // ---------------------------------------------------------------------------------------------
    // Дії
    // ---------------------------------------------------------------------------------------------

    /// <summary>Стояти n кроків.</summary>
    public IEnumerable<int> Wait(int n)
    {
        for (var i = 0; i < n; i++) yield return 0;
    }

    /// <summary>Стояти, доки умова не справдиться.</summary>
    public IEnumerable<int> WaitFor(Func<bool> cond)
    {
        while (!cond()) yield return 0;
    }

    /// <summary>Тримати клавіші n кроків.</summary>
    public IEnumerable<int> Hold(int keys, int n)
    {
        for (var i = 0; i < n; i++) yield return keys;
    }

    /// <summary>Гальмівний шлях від швидкості v на землі (su).</summary>
    static int BrakeDist(int v)
    {
        v = Math.Abs(v);
        var d = 0;
        while (v > 0) { v = Math.Max(0, v - VohnykWorld.Friction); d += v; }
        return d;
    }

    /// <summary>Дійти по землі до центру x (px) і стати (±2 px, швидкість 0).</summary>
    public IEnumerable<int> Go(int h, int px)
    {
        var target = px * Px;
        while (true)
        {
            var c = W.X[h] + VohnykWorld.HeroW / 2;
            var d = target - c;
            var v = W.Vx[h];
            if (Math.Abs(d) <= 2 * Px && v == 0) yield break;
            var dir = Math.Sign(d);
            if (Math.Abs(d) <= 2 * Px) { yield return 0; continue; }
            if (v * dir > 0 && BrakeDist(v) >= Math.Abs(d) - Px) { yield return 0; continue; }
            // щоб не проскочити: ближче за 6 px — лише поштовх
            if (Math.Abs(d) < 6 * Px && v != 0) { yield return 0; continue; }
            yield return dir > 0 ? R : L;
        }
    }

    /// <summary>Бігти в бік dir (±1), доки умова не справдиться (без гальмування).</summary>
    public IEnumerable<int> RunUntil(int dir, Func<bool> cond)
    {
        while (!cond()) yield return dir > 0 ? R : L;
    }

    /// <summary>
    /// Стрибок у бік dir (−1, 0, +1) з утриманням стрибка hold кроків; у повітрі кермує до landPx (px центру, якщо
    /// задано), приземлився — чекає зупинки. Відштовхується з того місця, де стоїть.
    /// </summary>
    public IEnumerable<int> Jump(int h, int dir, int hold = 30, int? landPx = null, int runUp = 0)
    {
        var dk = dir > 0 ? R : dir < 0 ? L : 0;
        for (var i = 0; i < runUp; i++) yield return dk;
        var age = 0;
        var left = false;
        while (true)
        {
            var keys = age < hold ? J : 0;
            if (landPx is { } lp)
            {
                var c = W.X[h] + VohnykWorld.HeroW / 2;
                var d = lp * Px - c;
                // у повітрі кермуємо: далеко — тиснемо, близько й летимо туди — відпускаємо (опір повітря сам пригальмує)
                if (Math.Abs(d) > 3 * Px && !(W.Vx[h] * Math.Sign(d) > 0 && Math.Abs(W.Vx[h]) * 6 > Math.Abs(d)))
                    keys |= d > 0 ? R : L;
                else if (W.Vx[h] * Math.Sign(d) < 0) keys |= d > 0 ? R : L;
            }
            else keys |= dk;
            yield return keys;
            age++;
            if (!Ground(h)) left = true;
            if (left && Ground(h)) break;
            if (age > 400) throw new InvalidOperationException($"рівень {level.N}: стрибок не приземлився");
        }
        // приземлились — гальмуємо до зупинки
        while (W.Vx[h] != 0) yield return 0;
    }

    /// <summary>Розбіг від поточного місця й стрибок, коли центр перетне takeoffPx; у повітрі тримає напрямок.</summary>
    public IEnumerable<int> RunJump(int h, int dir, int takeoffPx, int hold = 30, int? landPx = null)
    {
        var dk = dir > 0 ? R : L;
        while (dir > 0 ? CenterPx(h) < takeoffPx : CenterPx(h) > takeoffPx) yield return dk;
        foreach (var k in Jump(h, dir, hold, landPx)) yield return k;
    }

    /// <summary>Послідовність дій одного героя.</summary>
    public static IEnumerable<int> Seq(params IEnumerable<int>[] acts)
    {
        foreach (var a in acts)
            foreach (var k in a)
                yield return k;
    }
}
