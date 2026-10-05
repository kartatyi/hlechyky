using System.Runtime.InteropServices;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Скирта одного місця: що покладено, де верх, який сніп зараз їде і вітри на нього. Живе в <see cref="Skyrta"/>;
/// окремим класом — щоб бот і тести бачили той самий стан, що й гра.
/// </summary>
public sealed class SkyrtaStack
{
    public bool Plays;
    /// <summary>Устав з-за столу посеред партії: скирта лишається на полі, але вже не грає і не виграє.</summary>
    public bool Gone;
    /// <summary>Покладено снопів (висота), номер снопа, що їде (з 1, промахи теж рахуються), і коли він виїхав (мс гри).</summary>
    public int H, N = 1, S;
    /// <summary>Верх скирти: лівий край і ширина — такої ширини і сніп, що їде.</summary>
    public int L = SkyrtaCore.BaseL, W = SkyrtaCore.BaseW;
    public int Streak, BestStreak, Perfects, Misses, Winds;
    /// <summary>Був обріз більше ніж на половину (чи промах) — для «Скиртоправа».</summary>
    public bool BigCut;
    /// <summary>Коли доклав до мети (мс гри, момент тапу); −1 — ще ні.</summary>
    public int DoneAt = -1;
    /// <summary>Покладені снопи [лівий край, ширина]; [0] — підвалина.</summary>
    public readonly List<int[]> Sheaves = [];
    /// <summary>Вікна вітру на поточний сніп: a0, b0, a1, b1… (мс гри). Версія — щоб бот перерахував план.</summary>
    public readonly List<int> Wind = [];
    public int WindVer;

    public ReadOnlySpan<int> WindSpan => CollectionsMarshal.AsSpan(Wind);

    public void Reset(bool plays)
    {
        Plays = plays;
        Gone = false;
        H = 0; N = 1; S = 0;
        L = SkyrtaCore.BaseL; W = SkyrtaCore.BaseW;
        Streak = BestStreak = Perfects = Misses = Winds = 0;
        BigCut = false;
        DoneAt = -1;
        Sheaves.Clear();
        Sheaves.Add([L, W]);
        Wind.Clear();
        WindVer = 0;
    }

    /// <summary>Вітер дме чи ось-ось налетить (на мить <paramref name="now"/>).</summary>
    public bool Windy(int now)
    {
        for (var i = 1; i < Wind.Count; i += 2) if (Wind[i] > now) return true;
        return false;
    }

    /// <summary>Центр снопа, що їде, через <paramref name="t"/> мс після виїзду.</summary>
    public int CenterAt(int t) => SkyrtaCore.Center(t, SkyrtaCore.Speed(H), SkyrtaCore.FromLeft(N), SkyrtaCore.Sways(H), S, WindSpan);

    /// <summary>Лівий край снопа, що їде, через <paramref name="t"/> мс після виїзду.</summary>
    public int LeftAt(int t) => SkyrtaCore.Left(CenterAt(t), W);
}

/// <summary>
/// Бот Скирти: знає рух снопа так само, як браузер (рушій детермінований), тож шукає мить, коли сніп рівно над
/// верхом, і тапає з похибкою свого рівня — нормальний розкид у мілісекундах і зрідка грубий промах. Сильний
/// влучає часто, але не завжди; легкий — помітно мимо, скирта в нього швидко худне. План — раз на сніп
/// (і заново, коли на нього налетів вітер), тож на тик це майже нічого не коштує.
/// </summary>
public sealed class SkyrtaBot(LiveBots.Level level)
{
    /// <summary>Найраніше від виїзду снопа (мс), розкид похибки (σ, мс) і шанс грубого промаху (%) — легкий/звичайний/сильний.</summary>
    static readonly int[] React = [700, 430, 270];
    static readonly int[] Sigma = [65, 45, 26];
    static readonly int[] Blunder = [7, 4, 2];
    const int Step = 4, Look = 6000;

    public LiveBots.Level Level => level;
    int _n = -1, _ver = -1, _t;

    /// <summary>Коли (мс від виїзду поточного снопа) тапнути. Перераховує, якщо сніп новий чи змінився вітер.</summary>
    public int Plan(SkyrtaStack st, int now, Random rng)
    {
        if (_n == st.N && _ver == st.WindVer) return _t;
        _n = st.N;
        _ver = st.WindVer;
        var i = LiveBots.Index(level);
        var elapsed = Math.Max(0, now - st.S);
        var from = Math.Max(React[i] + rng.Next(0, 160), elapsed + 60);
        var best = Aim(st, from);
        var err = Gauss(rng) * Sigma[i];
        if (rng.Next(100) < Blunder[i]) err = (rng.Next(2) == 0 ? -1 : 1) * rng.Next(150, 350);
        _t = Math.Max(elapsed, best + (int)Math.Round(err));
        return _t;
    }

    /// <summary>Перша мить від <paramref name="from"/>, коли лівий край снопа збігається з верхом (перетин нуля).</summary>
    public static int Aim(SkyrtaStack st, int from)
    {
        var prev = st.LeftAt(from) - st.L;
        if (prev == 0) return from;
        for (var t = from + Step; t <= from + Look; t += Step)
        {
            var d = st.LeftAt(t) - st.L;
            if (d == 0) return t;
            if ((d > 0) != (prev > 0)) return Math.Abs(d) <= Math.Abs(prev) ? t : t - Step;
            prev = d;
        }
        return from + 300;
    }

    static double Gauss(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
