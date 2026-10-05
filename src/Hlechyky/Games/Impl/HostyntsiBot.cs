namespace Hlechyky.Games.Impl;

/// <summary>
/// Бот «Гостинців»: дивиться на гостинці, що запливають у його вікно, зважує кожен (що це й скільки місця лишилось
/// у кошику) і тисне «Хапай» тим самим <see cref="Hostyntsi.Act"/>, що й людина, — з затримкою реакції. Рівні:
/// легкий — жадібний (хапає й глеки, поки є місце), повільний і часом помиляється (хапне жар чи гарбуз);
/// звичайний — пропускає дрібноту, коли попереду вистачить кращого; сильний — рахує це точніше, швидко реагує,
/// а помиляється рідко. Випадковість — лише генератор столу.
/// </summary>
public sealed class HostyntsiBot(LiveBots.Level level)
{
    /// <summary>Рішення про гостинці: id → тик натиску (−1 — пропускаємо).</summary>
    readonly Dictionary<int, int> _plan = [];

    public LiveBots.Level Level => level;

    /// <summary>Реакція, тики по 50 мс: [від, до] включно.</summary>
    static readonly (int Lo, int Hi)[] React = [(4, 8), (2, 4), (1, 2)];
    /// <summary>Похибка: шанс схопити поганий гостинець (жар, гарбуз) чи прогавити добрий.</summary>
    static readonly double[] Slip = [0.12, 0.05, 0.015];

    public void Reset() => _plan.Clear();

    /// <summary>Тисне раз на тик щонайбільше: id гостинця, по який тягнеться, або null.</summary>
    public int? Think(Hostyntsi g, int seat, Random rng)
    {
        var me = g.P(seat);
        if (me.Basket.Count >= Hostyntsi.BasketMax || me.Burn > 0) return null;
        var c = Hostyntsi.Center(me.Station);
        var t = g.T;
        int? press = null;
        foreach (var x in g.Gifts)
        {
            if (_plan.TryGetValue(x.Id, out var at))
            {
                if (at >= 0 && t >= at && press is null && me.Lock == 0)
                {
                    press = x.Id;
                    _plan[x.Id] = -1;
                }
                continue;
            }
            // Рішення — коли гостинець запливає у вікно (раніше не дивимось: нижче можуть ще схопити інші).
            if (x.D < c - Hostyntsi.HalfWin || x.D > c + Hostyntsi.HalfWin) continue;
            var want = Want(g, me, x.Kind, rng);
            if (rng.NextDouble() < Slip[(int)level]) want = !want;
            var (lo, hi) = React[(int)level];
            _plan[x.Id] = want ? t + rng.Next(lo, hi + 1) : -1;
        }
        if (_plan.Count > 48)
        {
            var live = new HashSet<int>(g.Gifts.Select(x => x.Id));
            foreach (var id in _plan.Keys.Where(id => !live.Contains(id)).ToArray()) _plan.Remove(id);
        }
        return press;
    }

    /// <summary>
    /// Чи варто брати. Розписний і золотий — завжди; жар і гарбуз — ніколи. Глек і кіт (у середньому по +1) —
    /// коли місць у кошику більше, ніж кращих гостинців устигне доплисти до кінця раунду.
    /// </summary>
    bool Want(Hostyntsi g, Hostyntsi.Player me, int kind, Random rng)
    {
        if (kind is Hostyntsi.Gold or Hostyntsi.Painted) return true;
        if (kind is Hostyntsi.Ember or Hostyntsi.Pumpkin) return false;
        var slots = Hostyntsi.BasketMax - me.Basket.Count;
        if (level == LiveBots.Level.Easy) return kind == Hostyntsi.Jug || rng.Next(2) == 0;
        var timeLeft = (Hostyntsi.RoundTicks - g.Rt) * Hostyntsi.TickMs / 1000.0;
        var good = (Hostyntsi.Weight[Hostyntsi.Gold] + Hostyntsi.Weight[Hostyntsi.Painted]) / 100.0;
        // Скільки кращих ще припливе: нижчим станціям дістаються залишки верхніх.
        var share = level == LiveBots.Level.Hard ? 1.0 / (1 + 0.5 * me.Station) : 0.6;
        var coming = timeLeft * 1000 / g.IntervalMs * good * share;
        var take = slots > coming + (level == LiveBots.Level.Hard ? 0.5 : 0);
        if (kind == Hostyntsi.Cat)
            return take && (level == LiveBots.Level.Hard ? slots >= 2 : rng.Next(2) == 0);
        return take;
    }
}
