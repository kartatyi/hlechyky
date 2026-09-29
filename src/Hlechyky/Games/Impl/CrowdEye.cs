namespace Hlechyky.Games.Impl;

/// <summary>
/// Очі гравця-бота (🤖) у юрма-іграх (Юрма, Вечорниці, Замри!, Купала): бот бачить рівно те, що бачить людина з
/// кадру, — хто куди йде, де спалахнуло, хто стрельнув чи ляснув, — і з цього набирає підозру на кожного селянина.
/// Хто він насправді, бот не знає: і друга-людину, і іншого бота він підозрює однаково.
///
/// Що видає людину серед NPC (NPC так не роблять ніколи чи майже ніколи):
/// <list type="bullet">
/// <item>постріл / ляпас — NPC не б'ються зовсім (<see cref="Shooter"/>);</item>
/// <item>стояв там, де щойно спалахнуло завдання, — NPC роблять це рідко (<see cref="Flash"/>); збився з фігури (<see cref="Miss"/>);</item>
/// <item>розворот на 180° на ходу — NPC ідуть до цілі й зупиняються, а не смикаються назад (<see cref="Reverse"/>);</item>
/// <item>довго тисне в стіну — NPC одразу беруть нову ціль, а тиняються щонайбільше 24 тики (<see cref="WallHold"/>).</item>
/// </list>
/// Легкий бот не полює зовсім, звичайний дивиться лише на явне (удари й спалахи) і зрідка, сильний — на все й часто.
/// </summary>
public sealed class CrowdEye
{
    public const int Shooter = 100, Flash = 45, Miss = 20, Reverse = 12, WallHold = 25, WallTicks = 30;
    /// <summary>Підозра тане на 10 % кожні стільки тиків (≈ 5 с при 40 мс) — давнє забувається.</summary>
    public const int DecayEvery = 125;
    /// <summary>Відомий селянин (упав від удару — отже, NPC): більше не підозрюємо до кінця раунду.</summary>
    const int Known = int.MinValue / 2;

    /// <summary>Як часто бот (тиків) озирається, чи нема кого ловити: легкий — ніколи.</summary>
    public static readonly int[] HuntEvery = [0, 50, 12];
    /// <summary>Скільки підозри треба, щоб піти ловити: звичайному — удар або 2–3 спалахи, сильному — менше.</summary>
    public static readonly int[] HuntMin = [int.MaxValue, 80, 55];
    /// <summary>Реакція (тиків) від рішення «це він» до першого удару.</summary>
    public static readonly int[] Reaction = [0, 25, 8];

    int[] _sus = [], _dir = [], _hold = [];
    bool[] _moving = [];

    public int N => _sus.Length;

    public void Reset(int n)
    {
        _sus = new int[n];
        _dir = new int[n];
        _hold = new int[n];
        _moving = new bool[n];
    }

    /// <summary>
    /// Кадр одного селянина (лише сильний бот дивиться так пильно): <paramref name="pushing"/> — хоче йти, а
    /// стоїть (уперся). Кличе гра після кроку, раз на тик.
    /// </summary>
    public void Watch(int id, bool moving, int dir, bool pushing)
    {
        if ((uint)id >= (uint)_sus.Length) return;
        if (moving && _moving[id] && dir == (_dir[id] + 2) % 4) Add(id, Reverse);
        if (pushing) { if (++_hold[id] == WallTicks) Add(id, WallHold); }
        else _hold[id] = 0;
        _moving[id] = moving;
        _dir[id] = dir;
    }

    public void Add(int id, int pts)
    {
        if ((uint)id < (uint)_sus.Length && _sus[id] > Known) _sus[id] += pts;
    }

    /// <summary>Упав від удару й устав — просто селянин: більше не цілимось.</summary>
    public void Clear(int id)
    {
        if ((uint)id < (uint)_sus.Length) _sus[id] = Known;
    }

    public int Sus(int id) => (uint)id < (uint)_sus.Length ? Math.Max(0, _sus[id]) : 0;

    /// <summary>Раз на тик: кожні <see cref="DecayEvery"/> тиків підозра тане.</summary>
    public void Tick(int t)
    {
        if (t % DecayEvery != 0) return;
        for (var i = 0; i < _sus.Length; i++)
            if (_sus[i] > 0) _sus[i] = _sus[i] * 9 / 10;
    }

    /// <summary>Найпідозріліший (крім себе й тих, кого <paramref name="ok"/> відкидає) з підозрою ≥ <paramref name="min"/>; -1 — нікого.</summary>
    public int Best(int self, int min, Func<int, bool> ok)
    {
        int best = -1, bs = min - 1;
        for (var i = 0; i < _sus.Length; i++)
            if (i != self && _sus[i] > bs && ok(i)) { bs = _sus[i]; best = i; }
        return best;
    }
}

/// <summary>
/// Спільне «🤖 + бот» юрма-ігор, що справді однакове в усіх: куди сідають боти-гравці, як їх звати в журналі й чим
/// кінчається партія з ними (без очок у таблицю — гра просто не кличе <c>Ctx.Score</c>).
/// </summary>
public static class CrowdBots
{
    /// <summary>Перші <paramref name="count"/> вільних місць — туди сядуть боти.</summary>
    public static int[] FreeSeats(IRoomContext ctx, int seats, int count)
    {
        var a = new List<int>(count);
        for (var i = 0; i < seats && a.Count < count; i++)
            if (!ctx.Seated(i)) a.Add(i);
        return [.. a];
    }

    /// <summary>Два боти — у журналі з кольором місця, щоб не було «🤖 бот 5 : 🤖 бот 3».</summary>
    public static string Nick(bool bot, int bots, string nick, string seatName) =>
        bot && bots > 1 ? $"{LiveBots.Name} ({seatName})" : nick;

    /// <summary>
    /// Кінець партії з ботами. Людина сама на вершині — вона переможець («🏆»); бот на вершині — winners порожні й
    /// вердикт «🤖»; нарівні з ботом чи всі однаково — нічия «🤝». <paramref name="winners"/> — за правилами гри (місця).
    /// </summary>
    public static void Finish(IRoomContext ctx, int[] winners, int human, string humanNick, LiveBots.Level level, string log, string line)
    {
        var lvl = LiveBots.Of(level);
        string verdict;
        int[] real = [];
        if (winners.Length == 0) verdict = $"🤝 Нічия з {lvl} ботом";
        else if (human >= 0 && winners is [var w] && w == human)
        {
            real = [human];
            verdict = $"🏆 {humanNick} — перемога над {lvl} ботом";
        }
        else if (human >= 0 && Array.IndexOf(winners, human) >= 0) verdict = $"🤝 {humanNick} нарівні з {lvl} ботом";
        else verdict = $"🤖 Бот переміг: {line}";
        ctx.Finish(real, log, verdict: verdict);
    }
}
