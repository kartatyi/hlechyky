using System.Text.RegularExpressions;
using Hlechyky.Games.Impl;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Відгуки друзів, 28.09 (пакет kiln). Записка Smaug №12: «якщо натиснув більше разів, ніж є промінчиків, — розпис
/// закривається». Клієнт тепер закінчує мінігру за зробленим (зафарбовані пелюстки, відбиті позначки, протягнуті
/// смуги), а не за кількістю натисків, і пропускає тики пензлем. Тут — правила краси на сервері, на які він спирається:
/// зайвий мазок по вже готовому не псує роботи, а клієнт рахує пелюстки тими самими числами, що й сервер.
/// Записка Smaug №2 (відлік обпалу «скидається на початок або зависає»): контракт ядра кола — серверне «зараз» береться
/// лише зі свіжого виду, а не з того самого, який каркас передає вдруге на кожну подію лобі.
/// </summary>
public class ClickerKilnPlayTests
{
    const int Seed = 424242;

    static string Js(string file) => File.ReadAllText(Hlechyky.Paths.Resolve("web/games/" + file));

    /// <summary>Мазок від основи до кінчика пелюстки: 21 точка з дрібним тремором, як у руки.</summary>
    static IEnumerable<KilnPaint.Pt> Stroke(int[] petal, double ms, Random rnd)
    {
        for (var i = 0; i <= 20; i++)
        {
            var (x, y) = KilnPaint.PetalAt(petal, i / 20.0);
            yield return new KilnPaint.Pt(ms + i * 16, x + rnd.NextDouble() * 4 - 2, y + rnd.NextDouble() * 4 - 2, i == 0);
        }
    }

    static List<KilnPaint.Pt> Flower(KilnPaint.Brush z, IEnumerable<int> order, int seed = 7)
    {
        var rnd = new Random(seed);
        var pts = new List<KilnPaint.Pt>();
        var ms = 0.0;
        foreach (var i in order)
        {
            pts.AddRange(Stroke(z.Petals[i], ms, rnd));
            ms = pts[^1].Ms + 180;
        }
        return pts;
    }

    static KilnPaint.Brush BrushPattern() => (KilnPaint.Brush)KilnPaint.Pattern("brush", Seed);

    [Fact]
    public void A_second_stroke_over_a_painted_petal_does_not_lower_the_beauty()
    {
        var z = BrushPattern();
        var all = Enumerable.Range(0, z.Petals.Length).ToList();
        var once = KilnPaint.Beauty("brush", Seed, Flower(z, all));
        Assert.True(once >= 95, $"уся квітка: {once}");
        // Перша пелюстка вийшла кривою — гравець провів по ній ще раз, а тоді й по другій: краса та сама, не менша.
        var again = KilnPaint.Beauty("brush", Seed, Flower(z, all.Concat([0, 1])));
        Assert.Equal(once, again);
    }

    [Fact]
    public void A_flower_with_a_petal_left_is_far_from_full_so_the_brush_must_not_stop_on_the_stroke_count()
    {
        // Старий клієнт здавав розпис на N-му мазку. Мазок по першій пелюстці вдруге — і остання лишалась порожньою:
        // стільки ж мазків, скільки пелюсток, а квітка з дірою.
        var z = BrushPattern();
        var n = z.Petals.Length;
        var holed = KilnPaint.Beauty("brush", Seed, Flower(z, new[] { 0 }.Concat(Enumerable.Range(0, n - 1))));
        var full = KilnPaint.Beauty("brush", Seed, Flower(z, Enumerable.Range(0, n)));
        Assert.True(holed <= Math.Round(100.0 * (n - 1) / n) + 1, $"{holed} з {n} пелюстками без однієї");
        Assert.True(full - holed >= 100 / n - 2, $"повна {full}, з дірою {holed}");
    }

    [Fact]
    public void Brush_taps_are_not_strokes_past_the_flower()
    {
        // Тик — одна-дві точки. Навіть далеко від квітки це не «мазок повз» (там треба хоч три точки мимо) — і клієнт
        // їх однаково не шле; тут перевіряємо, що й сервер за них нічого не знімає.
        var z = BrushPattern();
        var pts = Flower(z, Enumerable.Range(0, z.Petals.Length));
        var clean = KilnPaint.Beauty("brush", Seed, pts);
        var ms = pts[^1].Ms;
        var tapped = pts.Concat([
            new KilnPaint.Pt(ms + 300, 500, 500, true),
            new KilnPaint.Pt(ms + 600, 40, 40, true),
            new KilnPaint.Pt(ms + 620, 41, 40, false),
            new KilnPaint.Pt(ms + 900, 960, 950, true),
        ]).ToList();
        Assert.Equal(clean, KilnPaint.Beauty("brush", Seed, tapped));
    }

    [Fact]
    public void A_real_stroke_past_the_flower_still_costs_eight()
    {
        var z = BrushPattern();
        var pts = Flower(z, Enumerable.Range(0, z.Petals.Length));
        var clean = KilnPaint.Beauty("brush", Seed, pts);
        var ms = pts[^1].Ms + 200;
        var past = pts.Concat(Enumerable.Range(0, 12).Select(i => new KilnPaint.Pt(ms + i * 16, 30 + i * 3, 30, i == 0))).ToList();
        Assert.Equal(Math.Max(0, clean - 8), KilnPaint.Beauty("brush", Seed, past));
    }

    [Fact]
    public void Extra_stamp_taps_cost_eight_each_but_the_hit_marks_stay()
    {
        var z = (KilnPaint.Stamp)KilnPaint.Pattern("stamp", Seed);
        var pts = new List<KilnPaint.Pt>();
        for (var i = 0; i < z.Marks.Length; i++)
        {
            double ms = i * z.Step;
            pts.Add(new KilnPaint.Pt(ms, z.Marks[i][0], z.Marks[i][1], true));
            pts.Add(new KilnPaint.Pt(ms + 30, z.Marks[i][0] + 2, z.Marks[i][1], false));
        }
        var clean = KilnPaint.Beauty("stamp", Seed, pts);
        Assert.True(clean >= 95, $"{clean}");
        // Три зайві тики в серединку посудини (далеко від позначок): мінігра від них не закінчується, а сервер знімає по 8.
        var extra = pts.Concat(Enumerable.Range(0, 3).Select(k => new KilnPaint.Pt(z.Step * (k + 0.5), 500, 500, true)))
            .OrderBy(p => p.Ms).ToList();
        Assert.Equal(clean - 24, KilnPaint.Beauty("stamp", Seed, extra));
    }

    [Fact]
    public void A_second_pull_through_the_same_flyand_mark_changes_nothing()
    {
        var z = (KilnPaint.Flyand)KilnPaint.Pattern("flyand", Seed);
        var rnd = new Random(3);
        IEnumerable<KilnPaint.Pt> Pull(int[] mark, double ms) => Enumerable.Range(0, 25)
            .Select(i => new KilnPaint.Pt(ms + i * 16, mark[0] + rnd.NextDouble() * 4 - 2, mark[1] == 1 ? 260 + 20 * i : 740 - 20 * i, i == 0));
        var pts = new List<KilnPaint.Pt>();
        foreach (var m in z.Marks) pts.AddRange(Pull(m, pts.Count == 0 ? 0 : pts[^1].Ms + 200));
        var clean = KilnPaint.Beauty("flyand", Seed, pts);
        Assert.True(clean >= 90, $"{clean}");
        var twice = pts.Concat(Pull(z.Marks[0], pts[^1].Ms + 200)).ToList();
        Assert.Equal(clean, KilnPaint.Beauty("flyand", Seed, twice));
    }

    [Fact]
    public void The_client_counts_petals_with_the_server_numbers()
    {
        // Клієнт сам бачить, які пелюстки зафарбовані (і коли квітка готова), — тими самими 14 позначками й допуском 62.
        var js = Js("clicker-kiln.js");
        var m = Regex.Match(js, @"const PETAL_MARKS = (\d+), BRUSH_TOL = (\d+);");
        Assert.True(m.Success, "у clicker-kiln.js нема PETAL_MARKS / BRUSH_TOL");
        Assert.Equal(KilnPaint.PetalSamples, int.Parse(m.Groups[1].Value));
        Assert.Equal(KilnPaint.BrushTol, double.Parse(m.Groups[2].Value));
        var done = Regex.Match(js, @"const PETAL_DONE = (\d+);");
        Assert.True(done.Success && int.Parse(done.Groups[1].Value) is > 0 and <= KilnPaint.PetalSamples);
    }

    [Fact]
    public void The_minigames_finish_on_work_done_not_on_the_number_of_presses()
    {
        var js = Js("clicker-kiln.js");
        // Старі умови: N-й мазок (пензель) чи N-й дотик (штампик) — і вікно зачинялось, хай що там на полотні.
        Assert.DoesNotContain("g.strokes >= p.shape.petals.length", js);
        Assert.DoesNotContain("g.taps >= p.shape.marks.length", js);
        Assert.Contains("g.doneN >= n", js);
        Assert.Contains("g.hitMarks.size >= p.shape.marks.length", js);
        // Підсумок на місці полотна замість миттєвого зачинення: зайві натиски не пролітають на кнопки горна під вікном.
        Assert.Contains("showDone(st, api, g, r)", js);
    }

    [Fact]
    public void The_core_takes_the_server_time_only_from_a_fresh_view()
    {
        var js = Js("clicker.js");
        var at = js.IndexOf("    update(root, ctx) {", StringComparison.Ordinal);
        Assert.True(at > 0, "нема update у clicker.js");
        var end = js.IndexOf("    onKey(e, ctx) {", at, StringComparison.Ordinal);
        var body = js[at..end];
        // Каркас кличе update і на кожну подію лобі — з тим самим видом. Правду сервера (число, «зараз», розгін) беремо
        // лише з нового об'єкта виду; інакше відлік горна відкочувався до розпалу.
        var fresh = body.IndexOf("const fresh = !!v && v !== st.lastView;", StringComparison.Ordinal);
        var guard = body.IndexOf("if (v && v.pots != null && fresh) {", StringComparison.Ordinal);
        var clock = body.IndexOf("st.recvAt = ", StringComparison.Ordinal);
        var viewNow = body.IndexOf("st.viewNow = ", StringComparison.Ordinal);
        var basePots = body.IndexOf("st.base = v.pots;", StringComparison.Ordinal);
        Assert.True(fresh > 0 && guard > fresh, "нема перевірки «свіжий вид»");
        Assert.True(clock > guard && viewNow > guard && basePots > guard, "правда сервера береться поза перевіркою");
        Assert.Contains("if (v && !fresh && !st.again && mine === st.mine) return;", body);
        // Перемалювати зі старим видом (частина догнала картку) — можна, але явно.
        Assert.Matches(@"st\.again = true;\s*MOD\.update\(st\.root, st\.ctx\);", js);
    }
}
