using Hlechyky.Games.Impl;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Зафіксовані журнали натисків для перевірки детермінізму C# ↔ JS (spec §8.4). Ті самі дані дослівно лежать у
/// стенді <c>docs/games/dev/bricks-selftest.html</c> (тест звіряє, що не розійшлись), а той самий прогін є в
/// <c>web/games/bricks.js</c> (<c>BricksCore.run</c>, <c>BricksCore.suite</c>). Посилки <c>[after, dt, rows, hole, from]</c>
/// стають у стіну після події номер <c>after</c>, на тику <c>min(tick + dt, t наступної події)</c> — так, як їх ставить
/// сервер: між подіями, у часі жертви. <c>Bot</c> &gt; 0 — журнал не записаний, а його грає <see cref="BricksBot"/>
/// (той самий бот є в bricks.js): так перевіряється ще й знімок стіни, яким бот зважує ходи. Хеші зафіксовані з C#;
/// розбіжність у стенді — баг у JS.
/// </summary>
public static class BricksFixtures
{
    public sealed record Fx(
        string Name, uint Seed, int Mode, int Stage, string[]? Rows, int[]? Events, (uint Seed, int N)? Gen,
        int[]? Credits, int End, uint Hash, int Lines, int Bot = 0);

    public static readonly Fx[] All =
    [
        new("random_play", 7, BricksCore.ModeNormal, 300, null, null, (7, 300), null, 1400, 3375459698, 0),
        new("das_wall_slides", 3, BricksCore.ModeNormal, 1800, null,
            [5,1, 40,2, 41,3, 60,1, 70,2, 90,4, 91,10, 100,3, 140,1, 141,4, 170,2, 171,10, 180,1, 181,3, 230,4, 231,2, 232,10,
             240,5, 250,1, 290,2, 291,6, 300,10],
            null, null, 330, 243030528, 0),
        new("srs_kicks_and_tspins", 10, BricksCore.ModeNormal, 1800, ["8888088888", "8880008888", "8888000000"],
            [0,7, 1,5, 45,6, 46,7, 47,10, 70,3, 71,4, 72,7, 80,10, 95,1, 96,2, 100,8, 105,10, 120,3, 150,4, 151,7, 160,7, 170,10,
             180,7, 181,10, 190,7, 191,1, 230,2, 231,7, 232,7, 240,10, 250,8, 255,10],
            null, null, 300, 3359725442, 2),
        new("lock_delay_and_reset_budget", 5, BricksCore.ModeNormal, 1800, null,
            [0,5, 50,6, 52,1,53,2, 57,3,58,4, 62,1,63,2, 67,3,68,4, 72,1,73,2, 77,3,78,4, 82,1,83,2, 87,3,88,4, 92,1,93,2,
             97,3,98,4, 102,1,103,2, 107,3,108,4, 112,1,113,2, 117,3,118,4, 122,1,123,2, 127,3,128,4, 132,1,133,2, 150,10],
            null, null, 220, 2797007215, 0),
        new("garbage_and_top_out", 11, BricksCore.ModeNormal, 1800, ["8888888880", "8888888880", "8888888880", "8888888880"],
            // I стоймя вправо до стінки — четвірка гасить першу посилку, решту сміття стіна ковтає, аж поки не впаде
            [0,7, 1,3,2,4, 3,3,4,4, 5,3,6,4, 7,3,8,4, 9,10, 30,10, 40,1,41,2,42,10, 60,3,61,4,62,10, 80,11, 90,10,
             110,1,111,2,112,10, 130,10, 150,10, 170,10, 190,10, 210,10, 230,10, 250,10, 270,10, 290,10],
            null, [0,0,3,2,1, 12,0,5,7,1, 14,8,8,0,-1, 17,0,8,4,2, 20,10,8,9,-1, 22,5,8,5,1], 400, 3401156079, 4),
        new("hard_mode_with_credits", 99, BricksCore.ModeHard, 600, null, null, (99, 240),
            [5,4,2,3,0, 30,2,1,6,2, 60,9,4,1,3, 90,1,2,8,-1], 1200, 2907322712, 0),
        new("bot_game", 21, BricksCore.ModeNormal, 600, null, null, null, GenCredits(21, 900, 71), 0, 2852983956, 29, Bot: 150),
        new("bot_hard_fast", 5, BricksCore.ModeHard, 150, null, null, null, GenCredits(5, 900, 71), 0, 1417207665, 37, Bot: 120),
    ];

    /// <summary>Хеш «набору»: 24 випадкові журнали й 6 ботів із посилками, режимами й темпами (див. <see cref="Suite"/>).</summary>
    public const uint SuiteHash = 2647798469;

    /// <summary>Випадковий журнал: той самий генератор, що й <c>gen</c> у bricks.js.</summary>
    public static int[] Gen(uint seed, int n)
    {
        int[] keys = [0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 11];
        var x = seed == 0 ? 1u : seed;
        var t = 0;
        var e = new int[n * 2];
        for (var i = 0; i < n; i++)
        {
            BricksCore.NextRand(ref x);
            t += (int)(x % 9);
            BricksCore.NextRand(ref x);
            e[i * 2] = t;
            e[i * 2 + 1] = keys[x % 11];
        }
        return e;
    }

    /// <summary>Посилки для журналу з <paramref name="n"/> подій: кожна <paramref name="every"/>-та подія — посилка (дзеркало <c>genCredits</c>).</summary>
    public static int[] GenCredits(uint seed, int n, int every = 23)
    {
        var x = seed * 31 + 7;
        var list = new List<int>();
        for (var i = 3; i < n; i += every)
        {
            BricksCore.NextRand(ref x);
            list.AddRange([i, (int)(x % 20), 1 + (int)(x / 20 % 6), (int)(x / 120 % 10), (int)(x / 1200 % 5) - 1]);
        }
        return [.. list];
    }

    /// <summary>Прогін так, як його переганяє сервер (дзеркало <c>run</c> у bricks.js).</summary>
    public static BricksCore Run(Fx fx)
    {
        var c = new BricksCore(fx.Seed, fx.Mode, fx.Stage);
        if (fx.Rows is not null) c.LoadRows(fx.Rows);
        var cr = fx.Credits ?? [];
        int seq = 0, ci = 0, gs = 0;
        void Credit(int next)
        {
            while (ci * 5 < cr.Length && cr[ci * 5] == seq)
            {
                var at = Math.Min(c.Tick + cr[ci * 5 + 1], next);
                var from = cr[ci * 5 + 4];
                c.AdvanceTo(at);
                c.AddCredit(++gs, cr[ci * 5 + 2], cr[ci * 5 + 3], from == -1 ? at : at + BricksCore.RipeTicks, from);
                ci++;
            }
        }
        void Event(int t, int k)
        {
            Credit(t);
            c.AdvanceTo(t);
            c.Apply(k);
            c.Seq = ++seq;
        }
        if (fx.Bot > 0)
        {
            // Бот думає BotDelay тиків, потім тисне по клавіші на тик; між фігурками — чекає появи наступної.
            var scratch = new BricksCore();
            for (var piece = 0; piece < fx.Bot && c.Alive; piece++)
            {
                while (c.Type < 0 && c.Alive) c.Step();
                if (!c.Alive) break;
                var keys = BricksBot.Plan(c, scratch);
                var t = c.Tick + BricksBot.Delay;
                foreach (var k in keys)
                {
                    if (!c.Alive) break;
                    Event(t++, k);
                }
            }
            Credit(int.MaxValue);
            c.AdvanceTo(c.Tick + 60);
            return c;
        }
        var ev = fx.Gen is { } g ? Gen(g.Seed, g.N) : fx.Events!;
        for (var i = 0; i < ev.Length; i += 2) Event(ev[i], ev[i + 1]);
        Credit(int.MaxValue);
        c.AdvanceTo(fx.End);
        return c;
    }

    /// <summary>
    /// 24 журнали по 400 подій і 6 ботів по 80 фігурок, з посилками, у всіх трьох режимах сміття й чотирьох темпах;
    /// їхні хеші, ряди й сміття згорнуті FNV-1a в одне число. Дзеркало <c>BricksCore.suite()</c> у bricks.js.
    /// </summary>
    public static uint Suite()
    {
        var h = 2166136261u;
        int[] stages = [0, 300, 600, 1200];
        for (var s = 1u; s <= 30; s++)
        {
            var bot = s > 24;
            var ev = bot ? null : Gen(s, 400);
            var fx = new Fx("suite", s * 7919, (int)(s % 3), stages[s % 4], null, ev, null, GenCredits(s, 400, bot ? 71 : 23),
                bot ? 0 : ev![^2] + 200, 0, 0, Bot: bot ? 80 : 0);
            var c = Run(fx);
            h = Mix(h, (int)c.Hash());
            h = Mix(h, c.Lines);
            h = Mix(h, c.Sent);
            h = Mix(h, c.Recv);
        }
        return h;
    }

    /// <summary>
    /// Блок <c>&lt;script id="bricks-fx"&gt;</c> для стенда: пини, хеш набору й журнали — рівно те, що тут. Тест звіряє
    /// його з тим, що лежить у стенді; розійшлись — справжній текст лягає в %TEMP%/bricks-fx.json.
    /// </summary>
    public static string StandJson()
    {
        var list = All.Select(f =>
        {
            var o = new Dictionary<string, object> { ["name"] = f.Name, ["seed"] = f.Seed, ["mode"] = f.Mode, ["stage"] = f.Stage };
            if (f.Rows is not null) o["rows"] = f.Rows;
            if (f.Events is not null) o["events"] = f.Events;
            if (f.Gen is { } g) o["gen"] = new[] { (int)g.Seed, g.N };
            if (f.Credits is not null) o["credits"] = f.Credits;
            if (f.Bot > 0) o["bot"] = f.Bot;
            o["end"] = f.End;
            o["hash"] = f.Hash;
            o["lines"] = f.Lines;
            return o;
        }).ToList();
        return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["xorshift"] = BricksPins.Xorshift,
            ["spawnT"] = BricksPins.SpawnT,
            ["suite"] = SuiteHash,
            ["fixtures"] = list,
        });
    }

    static uint Mix(uint h, int v)
    {
        var u = (uint)v;
        for (var i = 0; i < 4; i++) h = unchecked((h ^ ((u >> (i * 8)) & 0xFF)) * 16777619u);
        return h;
    }
}

/// <summary>
/// Простий жадібний бот (дзеркало <c>BricksCore.bot</c> у bricks.js): перебирає оберти й зсуви, кожен варіант
/// справді програє на копії стіни й зважує результат цілими числами — висота, дірки, горбатість, ряди. Для
/// зафіксованих журналів, перф-тесту й ботів у живій перевірці.
/// </summary>
public static class BricksBot
{
    public const int Delay = 8;
    const int WLines = 760, WHeight = 510, WHoles = 3560, WBump = 184;

    /// <summary>Клавіші для поточної фігурки: оберти, кроки (натиск+відпуск), жорстке падіння.</summary>
    public static int[] Plan(BricksCore c, BricksCore scratch)
    {
        if (c.Type < 0 || !c.Alive) return [];
        var best = int.MinValue;
        int bestR = 0, bestDx = 0;
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
                if (s > best)
                {
                    best = s;
                    bestR = r;
                    bestDx = dx;
                }
            }
        }
        var keys = new List<int>();
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
