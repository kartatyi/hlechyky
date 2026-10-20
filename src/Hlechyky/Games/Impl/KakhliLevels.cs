namespace Hlechyky.Games.Impl;

/// <summary>
/// Ціль рівня: <c>s</c> — набрати <see cref="Count"/> очок, <c>c</c> — зібрати <see cref="Count"/> кахлів барви
/// <see cref="Color"/>, <c>l</c> — розбити всю сиру глину, <c>r</c> — розв'язати всі мотузки.
/// </summary>
public sealed record KakhliGoal(char Kind, int Color = -1, int Count = 0)
{
    public static KakhliGoal Score(int points) => new('s', -1, points);
    public static KakhliGoal Collect(int color, int count) => new('c', color, count);
    public static readonly KakhliGoal Clay = new('l');
    public static readonly KakhliGoal Ropes = new('r');
}

/// <summary>
/// Рівень «Шляху кахляра». <see cref="Map"/> — розмітка 8×8 (<see cref="KakhliBoard"/>: <c>#</c> дірка, <c>1</c>/<c>2</c> глина,
/// <c>r</c> мотузка, <c>x</c> мотузка на глині). Зірки: ★ — цілі виконано (для рівня на очки — набрано
/// ціль), ★★ від <see cref="Two"/> очок, ★★★ від <see cref="Three"/>.
/// </summary>
public sealed record KakhliLevel(string Name, int Colors, int Moves, int Two, int Three, string Map, KakhliGoal[] Goals, string Tip = "")
{
    /// <summary>Рівень лише на очки: граємо всі ходи, ★ — ціль очок. Інакше рівень кінчається, щойно цілі виконано.</summary>
    public bool ScoreOnly => Goals.All(g => g.Kind == 's');
    public int Target => Goals.FirstOrDefault(g => g.Kind == 's')?.Count ?? 0;

    public int Stars(long score, bool goals) =>
        !goals ? 0 : score >= Three ? 3 : score >= Two ? 2 : 1;
}

/// <summary>Тридцять рівнів у трьох печах: перша — очки й барви, друга — сира глина, третя — мотузки й усе разом.</summary>
public static class KakhliLevels
{
    public const int W = 8, H = 8;
    public const int Red = 0, Blue = 1, Yellow = 2, Green = 3, White = 4, Violet = 5;

    const string Plain = """
        ........
        ........
        ........
        ........
        ........
        ........
        ........
        ........
        """;

    static KakhliGoal[] G(params KakhliGoal[] goals) => goals;

    /// <summary>Печі — назви трійок по десять рівнів.</summary>
    public static readonly string[] Kilns = ["Перша піч", "Сира глина", "Мотузки"];

    public static readonly IReadOnlyList<KakhliLevel> All =
    [
        // ---------- Перша піч: очки й барви ----------
        new("Перша кахля", 5, 20, 5100, 9300, Plain, G(KakhliGoal.Score(3200)),
            "Поміняй дві сусідні кахлі, щоб три однакові стали в ряд"),
        new("Червоні маки", 5, 20, 2700, 4100, Plain, G(KakhliGoal.Collect(Red, 20)),
            "Збирай червоні — кожна знята йде в ціль"),
        new("Рушник на стіл", 5, 22, 5700, 9900, Plain, G(KakhliGoal.Score(3600)),
            "Чотири в ряд — рушник: знімає весь рядок чи стовпчик"),
        new("Сині хвилі", 6, 22, 2500, 3900, Plain, G(KakhliGoal.Collect(Blue, 22), KakhliGoal.Collect(Yellow, 22))),
        new("Хрестик", 6, 22, 2000, 3500, """
            ##....##
            ##....##
            ........
            ........
            ........
            ........
            ##....##
            ##....##
            """, G(KakhliGoal.Score(1200)), "Поле буває й не квадратне — кахлі падають повз дірки"),
        new("Жаринка", 6, 24, 2500, 3800, Plain, G(KakhliGoal.Collect(Green, 24), KakhliGoal.Collect(Red, 24)),
            "Склади кутом чи літерою Т — вийде жаринка, вибух 3×3"),
        new("Миска", 6, 22, 1900, 3500, """
            ........
            ........
            ........
            ........
            #......#
            #......#
            ##....##
            ###..###
            """, G(KakhliGoal.Score(1200))),
        new("Чарівний глек", 6, 25, 2800, 4600, Plain, G(KakhliGoal.Score(1700)),
            "П'ять у ряд — чарівний глек: поміняй його з будь-якою кахлею, і вся та барва зникне"),
        new("Ярмарок", 6, 25, 2200, 3700, Plain, G(KakhliGoal.Collect(Red, 18), KakhliGoal.Collect(Blue, 18), KakhliGoal.Collect(White, 18))),
        new("Піч розгорілась", 6, 25, 2700, 4600, Plain, G(KakhliGoal.Score(1700)),
            "Дві спецкахлі поруч — поміняй їх між собою, буде сильніше"),

        // ---------- Сира глина ----------
        new("Перша глина", 5, 22, 2200, 4800, """
            ........
            ........
            ........
            ..1111..
            ..1111..
            ........
            ........
            ........
            """, G(KakhliGoal.Clay), "Під кахлями сира глина — знімай кахлі просто на ній, і глина розіб'ється"),
        new("Глиняна стежка", 5, 24, 4600, 8000, """
            ........
            ........
            ........
            ........
            11111111
            ........
            11111111
            ........
            """, G(KakhliGoal.Clay)),
        new("Кути печі", 5, 30, 5800, 10700, """
            11....11
            1......1
            ........
            ........
            ........
            ........
            1......1
            11....11
            """, G(KakhliGoal.Clay), "У кути добре б'ють рушники"),
        new("Подвійна глина", 6, 25, 3000, 4600, """
            ........
            ........
            ..2222..
            ..2222..
            ..2222..
            ..2222..
            ........
            ........
            """, G(KakhliGoal.Clay), "Темна глина — у два шари: бий двічі"),
        new("Глечик", 6, 25, 1600, 2700, """
            ##....##
            #......#
            #.1111.#
            ..1111..
            ..1111..
            #.1111.#
            #......#
            ##....##
            """, G(KakhliGoal.Clay)),
        new("Глина й маки", 6, 26, 2500, 3900, """
            ........
            ........
            ........
            11111111
            ........
            ........
            ........
            ........
            """, G(KakhliGoal.Clay, KakhliGoal.Collect(Red, 20))),
        new("Шахівниця", 5, 28, 6900, 10700, """
            1.1.1.1.
            .1.1.1.1
            1.1.1.1.
            .1.1.1.1
            1.1.1.1.
            .1.1.1.1
            1.1.1.1.
            .1.1.1.1
            """, G(KakhliGoal.Clay)),
        new("Глибока глина", 5, 30, 7600, 11700, """
            ........
            ........
            ........
            ........
            ........
            ........
            22222222
            22222222
            """, G(KakhliGoal.Clay), "Низ поля — глибоко: туди найкраще б'ють стовпчикові рушники"),
        new("Колодязь", 6, 25, 1700, 2800, """
            ........
            ........
            ..1111..
            ..1##1..
            ..1##1..
            ..1111..
            ........
            ........
            """, G(KakhliGoal.Clay)),
        new("Уся в глині", 5, 32, 8400, 13200, """
            11111111
            11111111
            11111111
            11122111
            11122111
            11111111
            11111111
            11111111
            """, G(KakhliGoal.Clay)),

        // ---------- Мотузки й усе разом ----------
        new("Перша мотузка", 5, 24, 4000, 6500, """
            ........
            ........
            ........
            rrrrrrrr
            ........
            ........
            ........
            ........
            """, G(KakhliGoal.Ropes), "Зв'язану кахлю не посунеш — склади з нею лінію, і мотузка розв'яжеться"),
        new("Мотузки й маки", 5, 24, 4200, 7500, """
            ........
            .r....r.
            ........
            ...rr...
            ...rr...
            ........
            .r....r.
            ........
            """, G(KakhliGoal.Ropes, KakhliGoal.Collect(Red, 20))),
        new("Сплетені кути", 5, 27, 6000, 9700, """
            rr....rr
            r1....1r
            ........
            ........
            ........
            ........
            r1....1r
            rr....rr
            """, G(KakhliGoal.Ropes, KakhliGoal.Clay)),
        new("Драбина", 5, 25, 4200, 7700, """
            ..r..r..
            ..r..r..
            ..r..r..
            ..r..r..
            ..r..r..
            ..r..r..
            ..r..r..
            ..r..r..
            """, G(KakhliGoal.Ropes)),
        new("Вузлик на глині", 5, 26, 4200, 7800, """
            ........
            ........
            ..xxxx..
            ..x11x..
            ..x11x..
            ..xxxx..
            ........
            ........
            """, G(KakhliGoal.Ropes, KakhliGoal.Clay), "Мотузка на глині: спершу розв'яжи, потім розбий"),
        new("Віконце", 6, 26, 800, 1400, """
            ........
            .######.
            .#....#.
            .#.rr.#.
            .#.rr.#.
            .#....#.
            .######.
            ........
            """, G(KakhliGoal.Ropes)),
        new("Ярмаркова юрма", 5, 28, 4400, 8900, """
            ........
            ........
            r..rr..r
            ........
            ........
            r..rr..r
            ........
            ........
            """, G(KakhliGoal.Ropes, KakhliGoal.Collect(Blue, 16), KakhliGoal.Collect(Green, 16), KakhliGoal.Collect(Yellow, 16))),
        new("Зав'язана піч", 5, 30, 7200, 10700, """
            rrrrrrrr
            r......r
            r.1111.r
            r.1221.r
            r.1221.r
            r.1111.r
            r......r
            rrrrrrrr
            """, G(KakhliGoal.Ropes, KakhliGoal.Clay)),
        new("Хрест-навхрест", 5, 30, 6200, 10800, """
            ...xx...
            ...xx...
            ...xx...
            xxx22xxx
            xxx22xxx
            ...xx...
            ...xx...
            ...xx...
            """, G(KakhliGoal.Ropes, KakhliGoal.Clay)),
        new("Майстер-кахляр", 5, 32, 6100, 9500, """
            ##.xx.##
            #.1221.#
            .x2..2x.
            x2.rr.2x
            x2.rr.2x
            .x2..2x.
            #.1221.#
            ##.xx.##
            """, G(KakhliGoal.Ropes, KakhliGoal.Clay), "Остання піч. Усе, чого навчився, — разом"),
    ];

    /// <summary>Найбільше зірок на шляху.</summary>
    public static int MaxStars => All.Count * 3;
}
