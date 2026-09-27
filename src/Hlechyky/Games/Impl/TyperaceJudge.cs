namespace Hlechyky.Games.Impl;

/// <summary>
/// Що суддя сказав про журнал натискань: <see cref="Flag"/> null — зараховано, інакше код причини (spec §2.3).
/// Лічильники — з програвання журналу (до місця, де він зламався, якщо зламався).
/// </summary>
public readonly record struct TyperaceVerdict(string? Flag, int Correct, int Wrong, int Swallowed, long LogMs)
{
    public bool Ok => Flag is null;
}

/// <summary>
/// Суддя Клавоперегонів: чиста детермінована перевірка журналу натискань, що приходить разом із фінішем.
/// Час партії він не рахує (офіційний — серверний), а відповідає на два запитання: «чи це надруковано по літері»
/// і «чи це друкувала людина». Перевірки — у фіксованому порядку, перший провал дає прапорець.
/// <para>
/// Журнал: <c>k</c> — по знаку на подію (<c>c</c> правильний, <c>x</c> помилка, <c>b</c> Backspace, <c>s</c> проковтнутий;
/// велика літера — подія не від людини), <c>d</c> — по два знаки base64url на подію, дельта від попередньої в кроках
/// по 4 мс (стеля 4095 кроків ≈ 16,4 с). Кодувальник — у <c>typerace.js</c> (<c>enc</c>), міняти разом.
/// </para>
/// </summary>
public static class TyperaceJudge
{
    public const int MaxEvents = 1600;
    public const int StepMs = 4;
    public const int MaxStep = 4095;
    public const int MaxHumanCpm = 1200;
    /// <summary>Журнал може «бачити» трохи більше часу, ніж сервер (зсув моменту старту в браузері), але не більше.</summary>
    public const int ClockAheadMs = 500;
    /// <summary>…і менше — на латентність і паузу до першого натиску.</summary>
    public const int ClockBehindMs = 6000;
    public const int ScriptPercent = 10;
    public const int MetronomeRun = 10;
    public const int MetronomeMin = 40;
    public const double MetronomeCv = 0.12;
    /// <summary>«Черга»: проміжок коротший за 5 кроків (20 мс).</summary>
    public const int BurstSteps = 5;
    public const int BurstPercent = 15;
    /// <summary>Нижче цієї кількості проміжків частка «черги» ще нічого не каже.</summary>
    public const int BurstMin = 20;
    /// <summary>
    /// «Бачив, як друкував»: сервер знає з <c>pos</c>, коли гонщик перетнув чверть, половину й три чверті тексту. Мить
    /// із журналу не може бути пізнішою за мить на сервері більш ніж на <see cref="SeenLeadMs"/> (pos іде вже після
    /// натиску) і ранішою більш ніж на <see cref="SeenLagMs"/> (кліпнув зв'язок, SignalR перепідключився — у межах
    /// 20 с, які каркас тримає місце).
    /// </summary>
    public const int SeenLeadMs = 1500, SeenLagMs = 20_000;
    /// <summary>Скільки відміток прогресу сервер пам'ятає: ¼, ½, ¾ тексту.</summary>
    public const int SeenMarks = 3;

    public const string BadLog = "bad-log", Mismatch = "mismatch", Clock = "clock", Fast = "fast",
        Script = "script", Metronome = "metronome", Burst = "burst", Unseen = "unseen";

    public const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    static readonly sbyte[] Reverse = BuildReverse();

    static sbyte[] BuildReverse()
    {
        var r = new sbyte[128];
        Array.Fill(r, (sbyte)-1);
        for (var i = 0; i < Alphabet.Length; i++) r[Alphabet[i]] = (sbyte)i;
        return r;
    }

    /// <summary>Причина прапорця людською мовою — для підсумку й відповіді на фініш.</summary>
    public static string Reason(string? flag) => flag switch
    {
        BadLog => "журнал не читається",
        Mismatch => "журнал не сходиться з текстом",
        Clock => "годинник не сходиться",
        Fast => "швидше за людину",
        Script => "натиски не з клавіатури",
        Metronome => "ритм метронома",
        Burst => "черга натисків",
        Unseen => "сервер не бачив самого друку",
        null => "",
        _ => "щось не те",
    };

    /// <summary>Дельта в мілісекундах → два знаки журналу (кроки по 4 мс, стеля 16 380 мс).</summary>
    public static string Encode(long ms)
    {
        var v = (int)Math.Clamp(Math.Round(ms / (double)StepMs, MidpointRounding.AwayFromZero), 0, MaxStep);
        return string.Concat(Alphabet[v >> 6], Alphabet[v & 63]);
    }

    /// <summary>Два знаки журналу → кроки; -1 — знак поза абеткою.</summary>
    public static int DecodeSteps(char hi, char lo)
    {
        if (hi >= 128 || lo >= 128) return -1;
        int a = Reverse[hi], b = Reverse[lo];
        return a < 0 || b < 0 ? -1 : (a << 6) | b;
    }

    /// <summary>Скільки правильних знаків — це відмітка прогресу <paramref name="q"/> (0 — чверть, 1 — половина, 2 — три чверті).</summary>
    public static int Mark(int len, int q) => ((q + 1) * len + 3) / 4;

    /// <summary>
    /// Перевірити журнал заїзду по тексту довжиною <paramref name="len"/>; <paramref name="serverMs"/> — офіційний час
    /// фінішу, який бачив сервер; <paramref name="seen"/> — коли (мс від старту) сервер із <c>pos</c> бачив, що гонщик
    /// останній раз перетнув ¼, ½ і ¾ тексту (-1 — не бачив). Порожній <paramref name="seen"/> — перевірку 8 пропускаємо
    /// (чистий суддя в тестах). Без алокацій: 1600 подій — кілька мікросекунд.
    /// </summary>
    public static TyperaceVerdict Check(int len, string? k, string? d, long serverMs, ReadOnlySpan<long> seen = default)
    {
        // 1. журнал читається
        if (string.IsNullOrEmpty(k) || k.Length > MaxEvents || d is null || d.Length != 2 * k.Length)
            return new(BadLog, 0, 0, 0, 0);
        var n = k.Length;
        Span<int> steps = n <= 2048 ? stackalloc int[n] : new int[n];
        long logSteps = 0;
        var clamped = false;
        var upper = 0;
        for (var i = 0; i < n; i++)
        {
            var kind = k[i];
            if (kind is not ('c' or 'x' or 'b' or 's' or 'C' or 'X' or 'B' or 'S')) return new(BadLog, 0, 0, 0, 0);
            if (kind is >= 'A' and <= 'Z') upper++;
            var v = DecodeSteps(d[2 * i], d[2 * i + 1]);
            if (v < 0) return new(BadLog, 0, 0, 0, 0);
            if (v >= MaxStep) clamped = true;
            steps[i] = v;
            logSteps += v;
        }
        var logMs = logSteps * StepMs;

        // 2. програвання по тексту; заразом — коли журнал останній раз перетнув ¼, ½, ¾ (для перевірки 8)
        int cur = 0, correct = 0, wrong = 0, swallowed = 0;
        var red = false;
        // перший і останній раз, коли журнал перетнув відмітку (Backspace через неї — і перетне ще раз)
        Span<long> markFirst = stackalloc long[SeenMarks], markAt = stackalloc long[SeenMarks];
        markFirst.Fill(-1);
        markAt.Fill(-1);
        long at = 0;
        for (var i = 0; i < n; i++)
        {
            at += steps[i];
            switch (k[i] | 0x20)   // до малої: 'C' → 'c'
            {
                case 'c':
                    if (red || cur >= len) return new(Mismatch, correct, wrong, swallowed, logMs);
                    cur++; correct++;
                    for (var q = 0; q < SeenMarks; q++)
                        if (cur == Mark(len, q))
                        {
                            markAt[q] = at * StepMs;
                            if (markFirst[q] < 0) markFirst[q] = markAt[q];
                        }
                    break;
                case 'x':
                    if (red || cur >= len) return new(Mismatch, correct, wrong, swallowed, logMs);
                    red = true; wrong++;
                    break;
                case 's':
                    if (!red) return new(Mismatch, correct, wrong, swallowed, logMs);
                    swallowed++;
                    break;
                default:   // 'b'
                    if (red) red = false;
                    else if (cur > 0)
                    {
                        for (var q = 0; q < SeenMarks; q++) if (cur == Mark(len, q)) markAt[q] = -1;   // відмітку перетнуть ще раз
                        cur--;
                    }
                    else return new(Mismatch, correct, wrong, swallowed, logMs);
                    break;
            }
        }
        if (cur != len || red) return new(Mismatch, correct, wrong, swallowed, logMs);

        // 3. годинник: журнал не може бачити більше часу, ніж сервер; менше — лише в межах допуску. Дельта на стелі
        // означає «пауза щонайменше 16 с» (людина відійшла) — тоді нижню межу не перевіряємо: скільки там було насправді,
        // журнал не знає.
        if (logMs > serverMs + ClockAheadMs || (!clamped && logMs < serverMs - ClockBehindMs))
            return new(Clock, correct, wrong, swallowed, logMs);

        // 4. швидкість
        if (serverMs <= 0 || (long)len * 60_000 > (long)MaxHumanCpm * serverMs)
            return new(Fast, correct, wrong, swallowed, logMs);

        // 5. події не від людини
        if (upper * 100 > n * ScriptPercent) return new(Script, correct, wrong, swallowed, logMs);

        // 6–7. ритм: проміжки між сусідніми натисками знаків (c і x; Backspace і проковтнуті — ні: проковтнуті — це
        // здебільшого затиснута клавіша з автоповтором ОС, рівним до мілісекунди, а він людський)
        Span<int> gaps = n <= 2048 ? stackalloc int[n] : new int[n];
        var m = 0;
        long t = 0, lastKey = -1;
        for (var i = 0; i < n; i++)
        {
            t += steps[i];
            var kind = k[i] | 0x20;
            if (kind is not ('c' or 'x')) continue;
            if (lastKey >= 0) gaps[m++] = (int)(t - lastKey);
            lastKey = t;
        }
        if (IsMetronome(gaps[..m])) return new(Metronome, correct, wrong, swallowed, logMs);
        if (m >= BurstMin)
        {
            var quick = 0;
            for (var i = 0; i < m; i++) if (gaps[i] < BurstSteps) quick++;
            if (quick * 100 > m * BurstPercent) return new(Burst, correct, wrong, swallowed, logMs);
        }

        // 8. сервер бачив друк: pos доходили під час заїзду й у ті самі миті, що й у журналі. Фініш «з нуля» одним
        // викликом із консолі (журнал без жодного pos) сюди не пройде. Чверть має прийти справжнім pos; ½ і ¾, яких
        // сервер не бачив (зв'язок ліг під кінець), рахуються миттю фінішу — тоді й вони мусять бути в межах запізнення.
        if (seen.Length >= SeenMarks)
        {
            // пауза на стелі (≥ 16,4 с) зсуває в журналі всі миті після неї на невідоме — але не більше, ніж журнал недобачив
            var slack = clamped ? Math.Max(0, serverMs - logMs) : 0;
            for (var q = 0; q < SeenMarks; q++)
            {
                var server = seen[q];
                if (server < 0)
                {
                    if (q == 0) return new(Unseen, correct, wrong, swallowed, logMs);
                    server = serverMs;
                }
                // сервер міг бачити будь-який із перетинів (коротку ямку назад pos раз на 200 мс і не помітить)
                if (markAt[q] < 0 || server < markFirst[q] - SeenLeadMs || server > markAt[q] + SeenLagMs + slack)
                    return new(Unseen, correct, wrong, swallowed, logMs);
            }
        }
        return new(null, correct, wrong, swallowed, logMs);
    }

    /// <summary>
    /// Де в тексті журнал помилявся: <paramref name="into"/>[i] = true, якщо на знаку i хоч раз висів червоний. Для
    /// «слова-пастки» в підсумку. Програє журнал так само, як <see cref="Check"/>, і спиняється там, де той зламався б.
    /// </summary>
    public static void Misses(string? k, int len, Span<bool> into)
    {
        if (string.IsNullOrEmpty(k)) return;
        var cur = 0;
        var red = false;
        foreach (var ch in k)
        {
            switch (ch | 0x20)
            {
                case 'c':
                    if (red || cur >= len) return;
                    cur++;
                    break;
                case 'x':
                    if (red || cur >= len) return;
                    red = true;
                    if (cur < into.Length) into[cur] = true;
                    break;
                case 's':
                    if (!red) return;
                    break;
                case 'b':
                    if (red) red = false;
                    else if (cur > 0) cur--;
                    else return;
                    break;
                default:
                    return;
            }
        }
    }

    /// <summary>Серія з 10 проміжків, рівних з точністю до кроку, або рівний ритм (σ/μ &lt; 0,12) на 40+ проміжках.</summary>
    static bool IsMetronome(ReadOnlySpan<int> gaps)
    {
        for (var i = 0; i + MetronomeRun <= gaps.Length; i++)
        {
            int lo = gaps[i], hi = gaps[i];
            var even = true;
            for (var j = i + 1; j < i + MetronomeRun; j++)
            {
                if (gaps[j] < lo) lo = gaps[j];
                if (gaps[j] > hi) hi = gaps[j];
                if (hi - lo > 1) { even = false; break; }
            }
            if (even) return true;
        }
        if (gaps.Length < MetronomeMin) return false;
        double sum = 0;
        foreach (var g in gaps) sum += g;
        var mean = sum / gaps.Length;
        if (mean <= 0) return true;
        double sq = 0;
        foreach (var g in gaps) sq += (g - mean) * (g - mean);
        var sd = Math.Sqrt(sq / gaps.Length);
        return sd / mean < MetronomeCv;
    }
}
