namespace Hlechyky.Games.Impl;

/// <summary>
/// Бот тиру. Стріляє тим самим пострілом, що й людина (гра кличе <c>Act("shot")</c> з його t, x, y): ті самі барабан,
/// перезарядка, дим і перевірка влучання. Рівні відрізняються реакцією (скільки мішень має провисіти, поки бот її
/// «побачить»), темпом, похибкою прицілу й тим, як часто він плутає мішень і бахає в діжку чи бабин горщик.
/// Сильний влучає майже завжди, але не встигає за всім і інколи хибить — ідеального стрільця нема.
/// </summary>
public sealed class TyrBot(LiveBots.Level level)
{
    readonly int _lvl = LiveBots.Index(level);
    /// <summary>Реакція, мс: скільки мішень має бути на виду, поки бот по ній стрельне.</summary>
    static readonly int[] React = [800, 520, 340];
    /// <summary>Пауза між пострілами, мс (плюс випадковий хвіст до половини).</summary>
    static readonly int[] Gap = [700, 440, 320];
    /// <summary>Похибка прицілу (сигма по кожній осі), одиниць поля.</summary>
    static readonly double[] Sigma = [30, 21, 17];
    /// <summary>Імовірність, що бот вибере «погану» мішень, коли вона на виду, %.</summary>
    static readonly int[] Mistake = [22, 9, 4];

    int _next, _stand = -1;

    public enum Move { None, Shot, Reload }

    /// <summary>Що зробити в мить <paramref name="t"/> (мс стенду <paramref name="stand"/>). Постріл — у (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public Move Think(int stand, int t, TyrShooter me, IReadOnlyList<TyrTarget> targets, Random rng, out double x, out double y)
    {
        x = y = 0;
        // Новий стенд — мить знову з нуля: пауза з кінця минулого не тягнеться.
        if (stand != _stand) { _stand = stand; _next = 0; }
        if (t < _next || me.Reloading(t) || me.Smoked(t) || t < me.LastT + TyrCore.MinGap) return Move.None;
        me.Settle(t);
        if (me.Ammo <= 0) { _next = t + 150; return Move.Reload; }

        TyrTarget? good = null, bad = null;
        var goodV = double.MinValue;
        for (var i = 0; i < targets.Count; i++)
        {
            var tg = targets[i];
            if (tg.T0 > t - React[_lvl]) break;
            // Мішень, що от-от зникне чи запливе за край, бот уже не ловить.
            if (t >= tg.End - 80 || me.HasHit(tg.Id)) continue;
            var cx = tg.XAt(t);
            if (cx < 10 || cx > TyrCore.W - 10) continue;
            if (TyrCore.Bad(tg.Kind)) { bad ??= tg; continue; }
            // Дорожче й та, що скоро зникне, — першою.
            var v = TyrCore.Points[tg.Kind] * 1000.0 - (tg.End - t);
            if (v > goodV) { goodV = v; good = tg; }
        }
        var aim = bad is not null && rng.Next(100) < Mistake[_lvl] ? bad : good;
        if (aim is null)
        {
            // Нема в що — сильніші доливають барабан заздалегідь.
            if (_lvl > 0 && me.Ammo <= _lvl) { _next = t + 200; return Move.Reload; }
            return Move.None;
        }
        x = aim.XAt(t) + Gauss(rng) * Sigma[_lvl];
        y = aim.Y + Gauss(rng) * Sigma[_lvl];
        _next = t + Gap[_lvl] + rng.Next(Gap[_lvl] / 2 + 1);
        return Move.Shot;
    }

    static double Gauss(Random rng)
    {
        var u = 1.0 - rng.NextDouble();
        var v = rng.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * v);
    }
}
