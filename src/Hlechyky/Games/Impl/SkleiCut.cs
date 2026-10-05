namespace Hlechyky.Games.Impl;

/// <summary>
/// Як розбито картинку «Склей глек» (specs/sklei.md §3): сітка N×N з кривими швами-тріщинами між сусідами і розклад
/// черепків на столі. Усе — цілими числами, щоб вид був малий, а клієнт малював без жодної своєї випадковості:
/// той самий сід картинки й те саме N дають ту саму розбивку в усіх гравців (і в бота, і в тестах).
/// <para>Одиниці — сота частка клітинки (cu): клітинка = 100 cu, картинка = N·100 cu.</para>
/// </summary>
public sealed class SkleiCut
{
    /// <summary>Наскільки зсуваємо внутрішні вузли сітки (±cu) — шов іде не по лінійці, а «як тріснуло».</summary>
    public const int VertexJitter = 16;
    /// <summary>Наскільки вигинається шов між вузлами (±cu) у двох точках — на третині й двох третинах.</summary>
    public const int CrackJitter = 13;
    /// <summary>Розклад: координати центру черепка — проміле (0..1000) прямокутника «стіл», який клієнт кладе де хоче.</summary>
    public const int TrayScale = 1000;

    public int N { get; }
    public int Pieces => N * N;
    /// <summary>Зсув вузлів: (N+1)² пар (dx, dy), рядок за рядком: вузол (i — стовпчик, j — рядок) → [(j·(N+1)+i)·2].</summary>
    public int[] V { get; }
    /// <summary>Горизонтальні внутрішні шви (рядки j = 1..N−1, стовпчики i = 0..N−1): по два вигини вниз (+) → [((j−1)·N+i)·2].</summary>
    public int[] Eh { get; }
    /// <summary>Вертикальні внутрішні шви (стовпчики i = 1..N−1, рядки j = 0..N−1): по два вигини вправо (+) → [((i−1)·N+j)·2].</summary>
    public int[] Ev { get; }
    /// <summary>Розклад на столі: на черепок k — (x, y) центру в проміле столу і початковий поворот r0 (0..3 чверті за годинниковою).</summary>
    public int[] Tray { get; }

    public SkleiCut(int n, int seed, bool rotate)
    {
        if (n is < 2 or > 8) throw new ArgumentOutOfRangeException(nameof(n));
        N = n;
        // Окремий генератор на (картинку, N): різні N тієї самої картинки не тягнуть одне одному числа.
        var rng = new Random(unchecked(seed * 31 + n * 7919));
        V = new int[(n + 1) * (n + 1) * 2];
        for (var j = 0; j <= n; j++)
            for (var i = 0; i <= n; i++)
            {
                var at = (j * (n + 1) + i) * 2;
                bool edgeX = i == 0 || i == n, edgeY = j == 0 || j == n;
                // Зовнішній край — рівний квадрат: вузол на краю ходить лише вздовж краю, кути стоять.
                V[at] = edgeX ? 0 : Jit(rng, VertexJitter);
                V[at + 1] = edgeY ? 0 : Jit(rng, VertexJitter);
            }
        Eh = new int[(n - 1) * n * 2];
        for (var k = 0; k < Eh.Length; k++) Eh[k] = Jit(rng, CrackJitter);
        Ev = new int[(n - 1) * n * 2];
        for (var k = 0; k < Ev.Length; k++) Ev[k] = Jit(rng, CrackJitter);

        // Розклад: черепки по клітинках сітки столу (щоб не лягли купою один на одного), клітинки перемішані, усередині —
        // трохи вбік. Стіл квадратний на cols×rows, де cols·rows ≥ N²; клієнт розтягне проміле на свій прямокутник.
        var pieces = n * n;
        var cols = (int)Math.Ceiling(Math.Sqrt(pieces));
        var rows = (pieces + cols - 1) / cols;
        var slots = Enumerable.Range(0, cols * rows).ToArray();
        for (var k = slots.Length - 1; k > 0; k--)
        {
            var s = rng.Next(k + 1);
            (slots[k], slots[s]) = (slots[s], slots[k]);
        }
        Tray = new int[pieces * 3];
        for (var k = 0; k < pieces; k++)
        {
            int c = slots[k] % cols, r = slots[k] / cols;
            Tray[k * 3] = (int)Math.Round((c + 0.5 + (rng.NextDouble() - 0.5) * 0.3) * TrayScale / cols);
            Tray[k * 3 + 1] = (int)Math.Round((r + 0.5 + (rng.NextDouble() - 0.5) * 0.3) * TrayScale / rows);
            Tray[k * 3 + 2] = rotate ? rng.Next(4) : 0;
        }
        // Жоден черепок не лежить уже як треба: хоч один (за потреби) повернутий — інакше «з поворотами» буває без поворотів.
        if (rotate && pieces > 0 && Enumerable.Range(0, pieces).All(k => Tray[k * 3 + 2] == 0)) Tray[2] = 1;
    }

    static int Jit(Random rng, int max) => rng.Next(-max, max + 1);

    /// <summary>Початковий поворот черепка (чверті за годинниковою).</summary>
    public int R0(int piece) => Tray[piece * 3 + 2];

    /// <summary>Скільки тапів «+90°» треба, щоб черепок став рівно (0..3).</summary>
    public int Needed(int piece) => (4 - R0(piece)) % 4;

    /// <summary>Вид для клієнта: усе, що треба, щоб намалювати черепки і стіл.</summary>
    public object View() => new { n = N, v = (int[])V.Clone(), eh = (int[])Eh.Clone(), ev = (int[])Ev.Clone(), tray = (int[])Tray.Clone() };
}

/// <summary>Звідки картинка: вбудована (SVG у web/games/sklei), фото «Де це?», малюнок з альбому Піктіонарі, своя (куплена).</summary>
public static class SkleiKind
{
    public const string Builtin = "svg", Photo = "geo", Art = "art", Own = "own";
}

/// <summary>
/// Картинка партії: <paramref name="Key"/> — стабільний ключ («svg:glek-syniy», «art:42», «own:7», «geo:kyiv-lavra/1»),
/// <paramref name="Url"/> — звідки клієнт її бере, <paramref name="Title"/> — підпис, <paramref name="Author"/> — чия
/// (малюнок друга чи своя картинка; для вбудованих і фото — null).
/// </summary>
public sealed record SkleiPicture(string Key, string Kind, string Url, string Title, string? Author)
{
    public object View() => new { key = Key, kind = Kind, url = Url, title = Title, by = Author };
}

/// <summary>Вбудований набір: власні SVG 600×600 у <c>web/games/sklei/&lt;id&gt;.svg</c> — яскраві, з великими деталями.</summary>
public static class SkleiBuiltin
{
    public static readonly IReadOnlyList<(string Id, string Title)> All =
    [
        ("glek-syniy", "Синій глек"),
        ("glek-chervonyi", "Червоний глек з квітами"),
        ("makitra", "Макітра з маком"),
        ("petrykivka-buket", "Петриківський букет"),
        ("petrykivka-ptakh", "Жар-птиця"),
        ("vyshyvka-romby", "Вишиванка: ромби"),
        ("vyshyvka-zirky", "Вишиванка: зорі"),
        ("pysanka", "Писанка"),
        ("sonyashnyky", "Соняшникове поле"),
        ("karpaty", "Карпати"),
        ("khata", "Біла хата"),
        ("nich-selo", "Ніч над селом"),
        ("kalyna", "Калина"),
        ("leleka", "Лелека над ставом"),
    ];

    public static SkleiPicture Picture(string id, string title) =>
        new($"{SkleiKind.Builtin}:{id}", SkleiKind.Builtin, $"/games/sklei/{id}.svg", title, null);
}
