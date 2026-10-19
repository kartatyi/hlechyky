namespace Hlechyky.Games.Impl;

/// <summary>
/// Простий жадібний бот (дзеркало <c>botPlan</c> у bricks.js): перебирає оберти й зсуви, кожен варіант справді
/// програє на копії стіни й зважує результат цілими числами — висота, дірки, горбатість, ряди. Грає за стіл
/// «🤖 + боти» (<see cref="BricksBotDriver"/>), а ще ним зафіксовано журнали стенда й перф-тести.
/// </summary>
public static class BricksBot
{
    /// <summary>Скільки тиків думає бот зафіксованих журналів (BricksFixtures, стенд). Бот за столом має свій темп.</summary>
    public const int Delay = 8;
    const int WLines = 760, WHeight = 510, WHoles = 3560, WBump = 184;
    /// <summary>Скільки найкращих ходів пам'ятаємо — з них бот, що помиляється, бере не перший.</summary>
    public const int Top = 5;

    /// <summary>Клавіші для поточної фігурки: оберти, кроки (натиск+відпуск), жорстке падіння.</summary>
    public static int[] Plan(BricksCore c, BricksCore scratch) => Plan(c, scratch, 0);

    /// <summary>
    /// Те саме, але хід — <paramref name="pick"/>-й за оцінкою (0 — найкращий; більше, ніж варіантів, — останній
    /// з тих, що є). Рівні оцінки стоять у порядку перебору, тож <c>pick = 0</c> — рівно той хід, що й завжди.
    /// </summary>
    public static int[] Plan(BricksCore c, BricksCore scratch, int pick)
    {
        if (c.Type < 0 || !c.Alive) return [];
        Span<int> score = stackalloc int[Top];
        Span<int> rot = stackalloc int[Top];
        Span<int> sh = stackalloc int[Top];
        var n = 0;
        var rots = c.Type == BricksCore.O ? 1 : 4;
        for (var r = 0; r < rots; r++)
        {
            for (var dx = -5; dx <= 5; dx++)
            {
                scratch.CopyFrom(c);
                if (!Place(scratch, r, dx)) continue;
                var lines = scratch.Lines;
                scratch.Apply(BricksCore.KHard);
                var s = Score(scratch, scratch.Lines - lines);
                // вставка за спаданням; рівний стає ПІСЛЯ рівних — перший знайдений лишається першим
                var at = n;
                while (at > 0 && score[at - 1] < s) at--;
                if (at >= Top) continue;
                for (var i = Math.Min(n, Top - 1); i > at; i--)
                {
                    score[i] = score[i - 1];
                    rot[i] = rot[i - 1];
                    sh[i] = sh[i - 1];
                }
                score[at] = s;
                rot[at] = r;
                sh[at] = dx;
                if (n < Top) n++;
            }
        }
        if (n == 0) return [BricksCore.KHard];
        var k = Math.Clamp(pick, 0, n - 1);
        int bestR = rot[k], bestDx = sh[k];
        var keys = new List<int>(12);
        if (bestR == 3) keys.Add(BricksCore.KCcw);
        else for (var i = 0; i < bestR; i++) keys.Add(BricksCore.KCw);
        for (var i = 0; i < Math.Abs(bestDx); i++)
        {
            keys.Add(bestDx < 0 ? BricksCore.KLeft : BricksCore.KRight);
            keys.Add(bestDx < 0 ? BricksCore.KLeftUp : BricksCore.KRightUp);
        }
        keys.Add(BricksCore.KHard);
        return [.. keys];
    }

    static bool Place(BricksCore c, int r, int dx)
    {
        if (r == 3)
        {
            c.Apply(BricksCore.KCcw);
            if (c.Rot != 3) return false;
        }
        else
        {
            for (var i = 0; i < r; i++)
            {
                var was = c.Rot;
                c.Apply(BricksCore.KCw);
                if (c.Rot == was) return false;
            }
        }
        for (var i = 0; i < Math.Abs(dx); i++)
        {
            var bx = c.Bx;
            c.Apply(dx < 0 ? BricksCore.KLeft : BricksCore.KRight);
            c.Apply(dx < 0 ? BricksCore.KLeftUp : BricksCore.KRightUp);
            if (c.Bx == bx) return false;
        }
        return true;
    }

    /// <summary>Оцінка стіни так, ніби повні ряди вже зняли.</summary>
    public static int Score(BricksCore c, int lines)
    {
        if (!c.Alive) return int.MinValue + 1;
        int agg = 0, holes = 0, bump = 0, prev = -1;
        for (var x = 0; x < BricksCore.W; x++)
        {
            int ey = 0, top = 0;
            for (var y = 0; y < BricksCore.H; y++)
            {
                if (c.Mask[y] == BricksCore.FullRow) continue;
                if ((c.Mask[y] >> x & 1) != 0) top = ey + 1;
                ey++;
            }
            ey = 0;
            for (var y = 0; y < BricksCore.H; y++)
            {
                if (c.Mask[y] == BricksCore.FullRow) continue;
                if (ey < top && (c.Mask[y] >> x & 1) == 0) holes++;
                ey++;
            }
            agg += top;
            if (prev >= 0) bump += Math.Abs(top - prev);
            prev = top;
        }
        return lines * WLines - agg * WHeight - holes * WHoles - bump * WBump;
    }
}

/// <summary>
/// Бот за столом «Цеглин» (стіл <c>bricks</c>, «🤖 + боти»): веде стіну свого місця на годиннику сервера так, як її
/// вів би клієнт людини, — тисне клавіші в рушій зі своїм темпом. Планувальник кличеться раз на фігурку, у мить,
/// коли бот «додумав» (фігурка за цей час могла трохи впасти — план рахується вже з того місця).
/// Темп і похибки — у <see cref="Pace"/>; самих рівнів троє (<see cref="LiveBots.Level"/>).
/// </summary>
public sealed class BricksBotDriver
{
    /// <summary>
    /// Темп рівня (тики стіни, 60 Гц): <c>Think</c> — скільки дивиться на нову фігурку до першої клавіші;
    /// <c>Gap</c> — між клавішами (менше за DAS 10, тож затиснута стрілка не повторюється); <c>Period</c> — не раніше
    /// цього від появи фігурки вона падає донизу (типова фігурка — рівно стільки); <c>Miss</c> — відсоток фігурок, де
    /// бот бере не найкращий хід, а випадковий з <c>MissTop</c> найкращих (без першого).
    /// Легкий ≈ 1 фігурка / 1,6 с і часто кривить; звичайний ≈ 1 / 0,9 с, рідко; сильний ≈ 1 / 0,45 с без похибок, але
    /// жадібний: т-обертів і колодязів під четвірку не будує, тож добрий гравець його перетискає сміттям.
    /// </summary>
    public readonly record struct Pace(int Think, int Gap, int Period, int Miss, int MissTop);

    public static readonly Pace Easy = new(Think: 40, Gap: 5, Period: 96, Miss: 45, MissTop: 5);
    public static readonly Pace Normal = new(Think: 20, Gap: 3, Period: 54, Miss: 8, MissTop: 3);
    public static readonly Pace Hard = new(Think: 6, Gap: 2, Period: 27, Miss: 0, MissTop: 1);

    public static Pace For(LiveBots.Level level) => level switch
    {
        LiveBots.Level.Easy => Easy,
        LiveBots.Level.Hard => Hard,
        _ => Normal,
    };

    readonly Pace _pace;
    readonly Random _rng;
    readonly BricksCore _scratch = new();
    int[] _keys = [];
    int _i;
    /// <summary>Номер фігурки (<see cref="BricksCore.Pi"/>), над якою бот зараз думає; −1 — ще жодної.</summary>
    int _piece = -1;
    int _spawnAt, _planAt, _nextAt;
    bool _planned;

    public BricksBotDriver(LiveBots.Level level, int seed)
    {
        _pace = For(level);
        _rng = new Random(seed);
    }

    /// <summary>Новий раунд — стіна чиста, думати почне з першої фігурки.</summary>
    public void Reset()
    {
        _piece = -1;
        _keys = [];
        _i = 0;
        _planned = false;
    }

    /// <summary>
    /// Довести стіну до тику <paramref name="wall"/>: клавіші, що настали, — у рушій (як події журналу, з <c>Seq</c>),
    /// між ними — гравітація. Повертає, чи стіна рухалась (тоді кімната забирає з неї ряди, сміття й вибування).
    /// </summary>
    public bool Drive(BricksCore c, int wall)
    {
        if (!c.Alive || c.Tick >= wall) return false;
        var guard = 0;
        while (c.Alive && guard++ < 256)
        {
            if (c.Type < 0)
            {
                // ряди зникають — нова фігурка з'явиться за кілька тиків
                if (c.Tick >= wall) break;
                c.Step();
                continue;
            }
            if (c.Pi != _piece) NewPiece(c);
            var next = !_planned ? _planAt : _i < _keys.Length ? _nextAt : int.MaxValue;
            if (next > wall) break;
            c.AdvanceTo(next);
            if (c.Type < 0 || c.Pi != _piece) continue;   // фігурка встигла лягти сама — думаємо над наступною
            if (!_planned)
            {
                var miss = _pace.Miss > 0 && _rng.Next(100) < _pace.Miss;
                _keys = BricksBot.Plan(c, _scratch, miss ? 1 + _rng.Next(Math.Max(1, _pace.MissTop - 1)) : 0);
                _i = 0;
                _planned = true;
                _nextAt = c.Tick;
                continue;
            }
            var k = _keys[_i++];
            c.Apply(k);
            c.Seq++;
            // остання клавіша (падіння) — не раніше Period від появи: так темп рівня рівний, хоч би скільки було кроків
            _nextAt = c.Tick + _pace.Gap;
            if (_i == _keys.Length - 1) _nextAt = Math.Max(_nextAt, _spawnAt + _pace.Period);
        }
        c.AdvanceTo(wall);
        return true;
    }

    void NewPiece(BricksCore c)
    {
        _piece = c.Pi;
        _spawnAt = c.Tick;
        // ±25 % до «подумати», щоб боти одного столу не тиснули хором
        var j = _pace.Think / 4;
        _planAt = c.Tick + _pace.Think + (j > 0 ? _rng.Next(-j, j + 1) : 0);
        _planned = false;
        _keys = [];
        _i = 0;
    }
}
