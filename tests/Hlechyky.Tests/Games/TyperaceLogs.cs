using System.Text;
using Hlechyky.Games.Impl;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Журнали натискань для тестів Клавоперегонів: складання «людського» журналу з розкидом, журналу з помилками
/// й Backspace, і три зафіксовані журнали, які склав кодувальник браузера (<c>typerace.js</c>, <c>TyperaceCore.log</c>)
/// на тих самих сценаріях, — C# має читати їх так само.
/// </summary>
public static class TyperaceLogs
{
    /// <summary>Журнал: події (<c>k</c>), дельти (<c>d</c>) і скільки часу він «бачив».</summary>
    public sealed record Log(string K, string D, long Ms);

    /// <summary>Збирач журналу по кроках (4 мс) — рівно те, що робить клієнт.</summary>
    public sealed class Builder
    {
        readonly StringBuilder _k = new(), _d = new();
        long _steps;

        public Builder Add(char kind, int ms)
        {
            var steps = (int)Math.Clamp(Math.Round(ms / 4.0, MidpointRounding.AwayFromZero), 0, TyperaceJudge.MaxStep);
            _k.Append(kind);
            _d.Append(TyperaceJudge.Encode(ms));
            _steps += steps;
            return this;
        }

        public int Count => _k.Length;
        public Log Build() => new(_k.ToString(), _d.ToString(), _steps * TyperaceJudge.StepMs);
    }

    /// <summary>
    /// Людина друкує текст довжиною <paramref name="len"/> без помилок: проміжок навколо <paramref name="meanMs"/>
    /// з розкидом ±60 %, перша дельта — реакція на старт.
    /// </summary>
    public static Log Human(int len, int seed = 1, int meanMs = 200, int firstMs = 600)
    {
        var rng = new Random(seed);
        var b = new Builder();
        for (var i = 0; i < len; i++)
            b.Add('c', i == 0 ? firstMs : Jitter(rng, meanMs));
        return b.Build();
    }

    /// <summary>
    /// Людина з помилками: на кожній <paramref name="every"/>-й літері — червона (x), іноді ще й проковтнута (s), потім
    /// Backspace і правильна. Раз на заїзд — стерти два правильні знаки й переписати.
    /// </summary>
    public static Log Sloppy(int len, int every = 10, int seed = 2, int meanMs = 220)
    {
        var rng = new Random(seed);
        var b = new Builder();
        var rewrote = false;
        for (var i = 0; i < len; i++)
        {
            var first = i == 0;
            if (i > 0 && i % every == 0)
            {
                b.Add('x', Jitter(rng, meanMs));
                if (i % (every * 2) == 0) b.Add('s', Jitter(rng, meanMs / 2));
                b.Add('b', Jitter(rng, meanMs * 2));
            }
            if (!rewrote && i == len / 2 && i >= 2)
            {
                rewrote = true;
                b.Add('b', Jitter(rng, meanMs)).Add('b', Jitter(rng, meanMs));
                b.Add('c', Jitter(rng, meanMs)).Add('c', Jitter(rng, meanMs));
            }
            b.Add('c', first ? 700 : Jitter(rng, meanMs));
        }
        return b.Build();
    }

    /// <summary>
    /// Людина, що друкувала рівно <paramref name="totalMs"/> (з точністю до кроку): живий розкид проміжків, підігнаний
    /// під потрібний час, — щоб тест міг поставити фініш на конкретну мить серверного годинника.
    /// </summary>
    public static Log HumanIn(int len, long totalMs, int seed = 1)
    {
        var rng = new Random(seed);
        var raw = new double[len];
        double sum = 0;
        for (var i = 0; i < len; i++) { raw[i] = i == 0 ? 3 : 0.4 + 1.2 * rng.NextDouble(); sum += raw[i]; }
        var totalSteps = totalMs / TyperaceJudge.StepMs;
        var b = new Builder();
        long used = 0;
        for (var i = 0; i < len; i++)
        {
            var steps = i == len - 1 ? totalSteps - used : (long)Math.Round(raw[i] / sum * totalSteps);
            steps = Math.Clamp(steps, 1, TyperaceJudge.MaxStep);
            used += steps;
            b.Add('c', (int)(steps * TyperaceJudge.StepMs));
        }
        return b.Build();
    }

    /// <summary>
    /// Як <see cref="HumanIn"/>, але на знаку <paramref name="missAt"/> людина спершу помилилась (x) і стерла (b).
    /// </summary>
    public static Log HumanInWithMiss(int len, long totalMs, int missAt, int seed = 1)
    {
        var rng = new Random(seed);
        var n = len + 2;
        var raw = new double[n];
        double sum = 0;
        for (var i = 0; i < n; i++) { raw[i] = i == 0 ? 3 : 0.4 + 1.2 * rng.NextDouble(); sum += raw[i]; }
        var totalSteps = totalMs / TyperaceJudge.StepMs;
        var b = new Builder();
        long used = 0;
        var e = 0;
        void Add(char kind)
        {
            var steps = e == n - 1 ? totalSteps - used : (long)Math.Round(raw[e] / sum * totalSteps);
            steps = Math.Clamp(steps, 1, TyperaceJudge.MaxStep);
            used += steps;
            e++;
            b.Add(kind, (int)(steps * TyperaceJudge.StepMs));
        }
        for (var i = 0; i < len; i++)
        {
            if (i == missAt) { Add('x'); Add('b'); }
            Add('c');
        }
        return b.Build();
    }

    /// <summary>Бот: рівно однаковий проміжок між усіма натисками.</summary>
    public static Log Robot(int len, int ms = 100)
    {
        var b = new Builder();
        for (var i = 0; i < len; i++) b.Add('c', ms);
        return b.Build();
    }

    static int Jitter(Random rng, int mean) => Math.Max(24, (int)(mean * (0.4 + 1.2 * rng.NextDouble())));

    // ---------------------------------------------------------------------------------------------
    // Три сценарії, які той самий кодувальник склав у браузері (typerace.js → TyperaceCore.log.*):
    // ті самі події з тими самими мілісекундами. Скрипт звірки: docs/games/dev/typerace-parity.js.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Сценарій: (подія, мілісекунди від попередньої), текст на 26 знаків.</summary>
    public static readonly (char Kind, int Ms)[] ScenarioClean =
    [
        ('c', 812), ('c', 143), ('c', 201), ('c', 97), ('c', 166), ('c', 250), ('c', 131), ('c', 178), ('c', 222), ('c', 190),
        ('c', 145), ('c', 305), ('c', 99), ('c', 160), ('c', 188), ('c', 173), ('c', 240), ('c', 121), ('c', 134), ('c', 207),
        ('c', 169), ('c', 158), ('c', 196), ('c', 187), ('c', 212), ('c', 264),
    ];

    /// <summary>Той самий текст із двома помилками, проковтнутим натиском і переписаним знаком.</summary>
    public static readonly (char Kind, int Ms)[] ScenarioErrors =
    [
        ('c', 1030), ('c', 150), ('x', 90), ('s', 60), ('b', 420), ('c', 180), ('c', 170), ('c', 210), ('c', 190), ('c', 140),
        ('b', 330), ('c', 260), ('c', 200), ('c', 150), ('c', 180), ('x', 110), ('b', 380), ('c', 160), ('c', 170), ('c', 190),
        ('c', 200), ('c', 150), ('c', 160), ('c', 170), ('c', 180), ('c', 150), ('c', 160), ('c', 170), ('c', 190), ('c', 200),
        ('c', 210), ('c', 180), ('c', 190),
    ];

    /// <summary>Пауза довша за стелю (20 с — стане 16 380 мс) і події не від людини (великі літери).</summary>
    public static readonly (char Kind, int Ms)[] ScenarioPause =
    [
        ('c', 500), ('c', 150), ('c', 160), ('c', 20000), ('C', 140), ('c', 150), ('c', 160), ('c', 170), ('c', 180), ('c', 190),
        ('c', 200), ('c', 210), ('c', 220), ('c', 230), ('c', 240), ('c', 250), ('c', 160), ('c', 170), ('c', 180), ('c', 190),
        ('c', 200), ('c', 210), ('c', 220), ('c', 230), ('c', 240), ('c', 250),
    ];

    /// <summary>Що склав браузер на цих сценаріях (k, d) — вписано з живого прогону typerace-parity.js.</summary>
    public static readonly (string K, string D)[] FromJs =
    [
        ("cccccccccccccccccccccccccc",
         "DLAkAyAYAqA_AhAtA4AwAkBMAZAoAvArA8AeAiA0AqAoAxAvA1BC"),
        ("ccxsbcccccbccccxbcccccccccccccccc",
         "ECAmAXAPBpAtArA1AwAjBTBBAyAmAtAcBfAoArAwAyAmAoArAtAmAoArAwAyA1AtAw"),
        ("ccccCccccccccccccccccccccc",
         "B9AmAo__AjAmAoArAtAwAyA1A3A6A8A_AoArAtAwAyA1A3A6A8A_"),
    ];

    public static Log Encode((char Kind, int Ms)[] scenario)
    {
        var b = new Builder();
        foreach (var (kind, ms) in scenario) b.Add(kind, ms);
        return b.Build();
    }

    /// <summary>
    /// Коли журнал останній раз перетнув ¼, ½, ¾ тексту (мс від старту) — саме тоді клієнт шле pos із цим c, і сервер
    /// запам'ятовує мить для перевірки 8 судді.
    /// </summary>
    public static IEnumerable<(int C, long Ms)> Marks(Log log, int len)
    {
        var at = new long[TyperaceJudge.SeenMarks];
        Array.Fill(at, -1L);
        var cur = 0;
        var red = false;
        long t = 0;
        for (var i = 0; i < log.K.Length; i++)
        {
            t += TyperaceJudge.DecodeSteps(log.D[2 * i], log.D[2 * i + 1]) * TyperaceJudge.StepMs;
            switch (log.K[i] | 0x20)
            {
                case 'c':
                    if (red || cur >= len) break;
                    cur++;
                    for (var q = 0; q < at.Length; q++) if (cur == TyperaceJudge.Mark(len, q)) at[q] = t;
                    break;
                case 'x': red = true; break;
                case 'b':
                    if (red) { red = false; break; }
                    if (cur == 0) break;
                    for (var q = 0; q < at.Length; q++) if (cur == TyperaceJudge.Mark(len, q)) at[q] = -1;
                    cur--;
                    break;
            }
        }
        for (var q = 0; q < at.Length; q++)
            if (at[q] >= 0) yield return (TyperaceJudge.Mark(len, q), at[q]);
    }

    // ---------------------------------------------------------------------------------------------
    // Що клієнт справді пише в журнал (typerace.js, journal): проковтнуті натиски (s) — не більше чотирьох за
    // одну червону літеру і жодного, коли до стелі журналу лишається менше, ніж треба на решту тексту + 200 подій.
    // Пропущений натиск свою дельту віддає наступній події. Міняти разом із SWALLOW_PER_RED / RESERVE у JS.
    // ---------------------------------------------------------------------------------------------

    public const int ClientSwallowsPerRed = 4, ClientReserve = 200;

    /// <summary>
    /// Сирий потік неохайного друкаря на тексті в 40 знаків (помилки на 5, 12, 20, 33 і по 6, 2, 9, 5 проковтнутих) —
    /// той самий, що четвертий сценарій у docs/games/dev/typerace-parity.js.
    /// </summary>
    public static List<(char Kind, int Ms)> ScenarioSwallows()
    {
        var miss = new Dictionary<int, int> { [5] = 6, [12] = 2, [20] = 9, [33] = 5 };
        var raw = new List<(char, int)>();
        for (var i = 0; i < 40; i++)
        {
            if (miss.TryGetValue(i, out var n))
            {
                raw.Add(('x', 100 + i));
                for (var s = 0; s < n; s++) raw.Add(('s', 50 + 7 * s));
                raw.Add(('b', 300));
            }
            raw.Add(('c', 120 + i * 37 % 90));
        }
        return raw;
    }

    /// <summary>Що з <see cref="ScenarioSwallows"/> записав у журнал браузер (TyperaceCore.capped) — з живого прогону.</summary>
    public static readonly (string K, string D) FromJsCapped =
        ("cccccxssssbcccccccxssbccccccccxssssbcccccccccccccxssssbccccccc",
         "AeAnAxAjAtAaANAOAQASB0AfApAyAlAuAhAqAcANAOBLAzAmAvAiArA0AnAwAeANAOAQASC-AjAsAfAoAyAkAuAgAqAzAmAvAiAhANAOAQASBfArA0AnAwAjAsAf");

    public static Log AsClientWrites(IEnumerable<(char Kind, int Ms)> raw, int len)
    {
        var b = new Builder();
        int events = 0, cur = 0, swallowsInRed = 0;
        var red = false;
        long carry = 0;
        foreach (var (kind, ms) in raw)
        {
            carry += ms;
            if (kind == 's' && (swallowsInRed >= ClientSwallowsPerRed || TyperaceJudge.MaxEvents - events <= len - cur + ClientReserve))
                continue;
            b.Add(kind, (int)Math.Min(int.MaxValue, carry));
            carry = 0;
            events++;
            switch (kind)
            {
                case 'c': cur++; break;
                case 'x': red = true; swallowsInRed = 0; break;
                case 's': swallowsInRed++; break;
                case 'b': if (red) red = false; else if (cur > 0) cur--; break;
            }
        }
        return b.Build();
    }

    /// <summary>
    /// Неохайний друкар на довгому тексті, як його описала рецензія: <paramref name="errors"/> помилок, і після кожної —
    /// ще <paramref name="swallows"/> натисків, поки не помітив червоне. Сирий потік подій (до того, як клієнт його стисне).
    /// </summary>
    public static List<(char Kind, int Ms)> SloppyRaw(int len, int errors, int swallows, int seed = 7, int meanMs = 210)
    {
        var rng = new Random(seed);
        var raw = new List<(char, int)>();
        var every = Math.Max(2, len / (errors + 1));
        var made = 0;
        for (var i = 0; i < len; i++)
        {
            if (i > 0 && i % every == 0 && made < errors)
            {
                made++;
                raw.Add(('x', Jitter(rng, meanMs)));
                for (var s = 0; s < swallows; s++) raw.Add(('s', Jitter(rng, meanMs)));
                raw.Add(('b', Jitter(rng, meanMs * 2)));
            }
            raw.Add(('c', i == 0 ? 700 : Jitter(rng, meanMs)));
        }
        return raw;
    }
}
