using System.Numerics;
using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>Трипільська знахідка з глинища: ключ і назва (опис — у каталозі альбому).</summary>
public sealed record ClickerFind(string Key, string Name);

/// <summary>Кахля в печі: її розпис (порожній — проста) і якість (2 добра, 3 дзвінка, 4 розкішна).</summary>
public sealed record StoveTile(string Style, int Quality);

/// <summary>
/// Альбом майстра (пакет B3 сьомого оновлення, docs/games/specs/clicker-v7-album.md; дев'яте оновлення —
/// clicker-v9.md §C і clicker-v9-album.md): те, що лишається від ремесла назавжди, — навіть обпал-престиж його не чіпає.
///
/// 1) Сітка «виріб × розпис»: клітинку відкриває перший обпалений виріб цієї пари, зірку — перший дзвінкий
///    (від дев'ятого оновлення й розкішний). Клітинка +0,5 %, повний рядок +5 %, повний стовпчик +6 %;
///    зірка +1 %, рядок у зірках ще +5 %, весь альбом у зірках ще +25 %.
/// 2) Майстерність за кожним виробом — від лічильника обпалених (<see cref="FiredOf"/>): десять рівнів, кожен −6 %
///    роботи й +10 % ціни виробу; на десятому — «золоті руки», ціна ще ×1,25.
/// 3) Кахляна піч: дванадцять гнізд для кахлів із комори; бонус за якістю (добра 1,5 %, дзвінка 3 %, розкішна 4 %),
///    повна піч ще +5 %, а якщо вся вона з дзвінких і кращих — +10 % замість. Кахлю в гнізді можна поміняти:
///    стара повертається в комору.
/// 4) Трипільський музей: кожен виліплений виріб — 1,5 % знайти в глинищі одну з восьми речей; дубль — уламок, п'ять
///    уламків склеюються в річ, якої бракує. Кожна річ +1 %, усі вісім ще +5 %.
/// 5) Виставка: до трьох клітинок альбому гравець ставить на видноту — вони їдуть у вид (хата й хата друга).
///
/// Усі бонуси складаються між собою і йдуть одним множником <see cref="AlbumAllMult"/>. Правила відкриття, рівнів і
/// бонусів — чисті статичні функції (їх тестують напряму), стан — лише бітові маски й списки. Ні розмір сітки, ні
/// найвища якість тут не зашиті: сітка росте з <see cref="Wares"/> і <see cref="Styles"/>, якість — з довжини
/// <see cref="StoveQualityBonus"/> (і <see cref="QualityMult"/>).
/// </summary>
public sealed partial class Clicker
{
    public const double AlbumCellBonus = 0.005, AlbumRowBonus = 0.05, AlbumColumnBonus = 0.06;
    /// <summary>Зірка (дзвінкий і кращий виріб у клітинці) — окремий бонус поверх клітинки.</summary>
    public const double AlbumStarBonus = 0.01, AlbumStarRowBonus = 0.05, AlbumStarAllBonus = 0.25;
    /// <summary>З якої якості клітинка дістає зірку.</summary>
    public const int StarQuality = 3;
    public const int StoveSlots = 12, StoveMinQuality = 2;
    /// <summary>Скільки дає кахля за якістю: [0] і [1] — звичайна на піч не йде, далі добра, дзвінка, розкішна.</summary>
    public static readonly double[] StoveQualityBonus = [0, 0, 0.015, 0.03, 0.04];
    /// <summary>Повна піч додає ще стільки; якщо вся з дзвінких і кращих — <see cref="StoveRingBonus"/> замість.</summary>
    public const double StoveFullBonus = 0.05, StoveRingBonus = 0.10;
    /// <summary>З якої якості кахля вважається «дзвінкою піччю».</summary>
    public const int StoveRingQuality = 3;
    /// <summary>Скільки клітинок альбому можна поставити на видноту.</summary>
    public const int ShowMax = 3;
    /// <summary>Шанс знахідки на кожен виліплений виріб.</summary>
    public const double FindChance = 0.015;
    public const double FindBonus = 0.01, MuseumFullBonus = 0.05;
    /// <summary>Стільки уламків склеюються в річ, якої в музеї ще нема.</summary>
    public const int ShardsForGlue = 5;
    /// <summary>Обпалених виробів одного виду для рівнів майстерності 1…10.</summary>
    public static readonly long[] MasteryAt = [5, 15, 40, 100, 250, 600, 1_500, 4_000, 10_000, 25_000];
    public const double MasteryWorkStep = 0.06, MasteryValueStep = 0.10;
    /// <summary>«Золоті руки»: на десятому рівні ціна виробу ще ×1,25 зверху.</summary>
    public const double MasteryTopValue = 1.25;

    /// <summary>Вісім реальних типів трипільських знахідок.</summary>
    public static readonly ClickerFind[] Finds =
    [
        new("spiral", "Черепок зі спіраллю"),
        new("binocular", "Біноклеподібна посудина"),
        new("figurine", "Жіноча статуетка"),
        new("house", "Модель житла"),
        new("grain", "Миска зі знаком зерна"),
        new("krater", "Кратер"),
        new("ladle", "Ложка-черпак"),
        new("whorl", "Пряслице"),
    ];

    /// <summary>Стовпчиків у сітці: простий + усі розписи.</summary>
    public static int AlbumColumns => Styles.Length + 1;
    public static int AlbumSize => Wares.Length * AlbumColumns;
    static int FullRowMask => (1 << AlbumColumns) - 1;
    static int FullMuseumMask => (1 << Finds.Length) - 1;
    /// <summary>Найвища якість, яку знає гра: і ціна виробу, і бонус кахлі мусять її знати.</summary>
    static int AlbumTopQuality => Math.Min(QualityMult.Length, StoveQualityBonus.Length) - 1;

    readonly int[] _albumCells = new int[Wares.Length];
    readonly int[] _albumStars = new int[Wares.Length];
    readonly List<StoveTile> _stove = [];
    /// <summary>Клітинки на видноту: «виріб|розпис», не більше <see cref="ShowMax"/>.</summary>
    readonly List<string> _albumShow = [];
    int _finds;
    int _findShards;
    LastFindRow? _lastFind;

    // ---------- чисті правила ----------

    /// <summary>Стовпчик розпису: 0 — простий, 1…N — <see cref="Styles"/> по черзі; −1 — такого нема.</summary>
    public static int AlbumStyleIndex(string? style)
    {
        if (string.IsNullOrEmpty(style)) return 0;
        var i = Array.FindIndex(Styles, s => s.Key == style);
        return i < 0 ? -1 : i + 1;
    }

    /// <summary>Рядок виробу в сітці (порядок <see cref="Wares"/>); −1 — такого нема.</summary>
    public static int AlbumWareIndex(string? ware) => Array.FindIndex(Wares, w => w.Key == ware);

    /// <summary>Ключ клітинки для виставки: «виріб|розпис» (простий — порожній розпис).</summary>
    public static string AlbumCellKey(string ware, string style) => $"{ware}|{style}";

    /// <summary>Рівень майстерності 0…10 за кількістю обпалених виробів одного виду.</summary>
    public static int MasteryLevel(long fired)
    {
        var level = 0;
        while (level < MasteryAt.Length && fired >= MasteryAt[level]) level++;
        return level;
    }

    /// <summary>Скільки роботи лишається від майстерності: −6 % за рівень (основа все одно не пускає нижче 40 %).</summary>
    public static double MasteryWorkMult(int level) => 1 - MasteryWorkStep * Math.Clamp(level, 0, MasteryAt.Length);

    /// <summary>Ціна виробу від майстерності: +10 % за рівень, а «золоті руки» (десятий) — ще ×1,25.</summary>
    public static double MasteryValueMult(int level)
    {
        var l = Math.Clamp(level, 0, MasteryAt.Length);
        return (1 + MasteryValueStep * l) * (l >= MasteryAt.Length ? MasteryTopValue : 1);
    }

    /// <summary>Скільки клітинок відкрито в масках рядків.</summary>
    public static int AlbumOpenCount(IReadOnlyList<int> cells) => cells.Sum(m => BitOperations.PopCount((uint)(m & FullRowMask)));

    /// <summary>Скільки виробів зібрано в усіх розписах.</summary>
    public static int AlbumFullRows(IReadOnlyList<int> cells) => cells.Count(m => (m & FullRowMask) == FullRowMask);

    /// <summary>Скільки розписів зібрано на всіх виробах.</summary>
    public static int AlbumFullColumns(IReadOnlyList<int> cells)
    {
        if (cells.Count < Wares.Length) return 0;
        var all = FullRowMask;
        foreach (var m in cells) all &= m;
        return BitOperations.PopCount((uint)all);
    }

    /// <summary>Скільки клітинок із зіркою.</summary>
    public static int AlbumStarCount(IReadOnlyList<int>? stars) =>
        stars is null ? 0 : stars.Sum(m => BitOperations.PopCount((uint)(m & FullRowMask)));

    /// <summary>Скільки виробів зібрано в зірках у всіх розписах.</summary>
    public static int AlbumStarRows(IReadOnlyList<int>? stars) =>
        stars is null ? 0 : stars.Count(m => (m & FullRowMask) == FullRowMask);

    /// <summary>Сітка: клітинки, повні рядки й стовпчики, зірки, рядки зірок і весь альбом у зірках.</summary>
    public static double AlbumGridBonus(IReadOnlyList<int> cells, IReadOnlyList<int>? stars)
    {
        var grid = AlbumCellBonus * AlbumOpenCount(cells)
            + AlbumRowBonus * AlbumFullRows(cells)
            + AlbumColumnBonus * AlbumFullColumns(cells);
        var starN = AlbumStarCount(stars);
        if (starN == 0) return grid;
        return grid + AlbumStarBonus * starN + AlbumStarRowBonus * AlbumStarRows(stars)
            + (starN >= AlbumSize ? AlbumStarAllBonus : 0);
    }

    /// <summary>Піч: кожна кахля за своєю якістю, повна ще +5 %, а вся з дзвінких і кращих — +10 % замість.</summary>
    public static double AlbumStoveBonus(IReadOnlyList<StoveTile>? stove)
    {
        if (stove is null || stove.Count == 0) return 0;
        var n = Math.Min(stove.Count, StoveSlots);
        double sum = 0;
        var ring = true;
        for (var i = 0; i < n; i++)
        {
            var q = stove[i]?.Quality ?? 0;
            sum += StoveQualityBonus[Math.Clamp(q, 0, StoveQualityBonus.Length - 1)];
            if (q < StoveRingQuality) ring = false;
        }
        return n < StoveSlots ? sum : sum + (ring ? StoveRingBonus : StoveFullBonus);
    }

    /// <summary>Музей: кожна річ +1 %, усі вісім ще +5 %.</summary>
    public static double AlbumMuseumBonus(int finds)
    {
        var found = BitOperations.PopCount((uint)(finds & FullMuseumMask));
        return FindBonus * found + (found == Finds.Length ? MuseumFullBonus : 0);
    }

    /// <summary>
    /// Бонус альбому до всього (частка, 0,12 = +12 %): сітка із зірками, кахляна піч і музей.
    /// </summary>
    public static double AlbumBonusFor(IReadOnlyList<int> cells, IReadOnlyList<int>? stars, IReadOnlyList<StoveTile>? stove, int finds) =>
        AlbumGridBonus(cells, stars) + AlbumStoveBonus(stove) + AlbumMuseumBonus(finds);

    // ---------- гачки основи ----------

    double AlbumAllMult => 1 + AlbumBonusFor(_albumCells, _albumStars, _stove, _finds);

    double AlbumWorkMult(string ware) => MasteryWorkMult(MasteryLevel(FiredOf(ware)));

    double AlbumValueMult(string ware) => MasteryValueMult(MasteryLevel(FiredOf(ware)));

    /// <summary>Горно видало вироби: нова клітинка, зірка за дзвінкий, рівень майстерності (лічильник основа вже збільшила).</summary>
    void AlbumOnFired(ItemInfo item, int n)
    {
        if (n <= 0) return;
        var row = AlbumWareIndex(item.Ware);
        var col = AlbumStyleIndex(item.Style);
        if (row < 0 || col < 0) return;
        var bit = 1 << col;
        var fresh = (_albumCells[row] & bit) == 0;
        var newStar = item.Quality >= StarQuality && (_albumStars[row] & bit) == 0;
        var what = $"{WareOf(item.Ware)!.Name.ToLowerInvariant()}, {StyleWord(item.Style)}";
        if (fresh)
        {
            _albumCells[row] |= bit;
            AwayNote(newStar ? $"📒 Альбом: нова клітинка, ще й із зіркою — {what}" : $"📒 Альбом: нова клітинка — {what}");
            if (_albumCells[row] == FullRowMask) Achieve("potter-album-row");
            if (AlbumOpenCount(_albumCells) == AlbumSize) Achieve("potter-album-all");
        }
        if (newStar)
        {
            _albumStars[row] |= bit;
            if (!fresh) AwayNote($"⭐ Альбом: зірка — {what}");
            if (_albumStars[row] == FullRowMask)
            {
                AwayNote($"⭐ Увесь рядок у зірках: {WareOf(item.Ware)!.Name.ToLowerInvariant()} — +5 % до всього");
                Wonder("album-row-stars");
            }
            if (AlbumStarCount(_albumStars) == AlbumSize) Achieve("potter-album-stars");
        }
        var after = MasteryLevel(FiredOf(item.Ware));
        var before = MasteryLevel(Math.Max(0, FiredOf(item.Ware) - n));
        if (after > before && after == MasteryAt.Length)
        {
            AwayNote($"🖐 Золоті руки: {WareOf(item.Ware)!.Name.ToLowerInvariant()} — майстерність 10, ціна ще ×1,25");
            Achieve("potter-mastery");
            Wonder("mastery-10");
        }
    }

    /// <summary>
    /// Дарунок друга (цех) відкриває клітинку альбому, але не зірку й не майстерність: виріб обпалив не ти. Інакше двоє
    /// друзів дарували б дзвінкі вироби один одному замість горна.
    /// </summary>
    void AlbumOnGift(ItemInfo item)
    {
        // Лише те, що отримувач і сам міг би виліпити й розписати: відкритий виріб і розпис із його колекції. Інакше
        // ветеран за місяць дарунків складав би новачкові весь альбом без жодного розпису.
        if (!WareOpen(item.Ware) || (item.Style.Length > 0 && !_styles.Contains(item.Style))) return;
        var row = AlbumWareIndex(item.Ware);
        var col = AlbumStyleIndex(item.Style);
        if (row < 0 || col < 0) return;
        var bit = 1 << col;
        if ((_albumCells[row] & bit) != 0) return;
        _albumCells[row] |= bit;
        AwayNote($"📒 Альбом: нова клітинка з дарунка — {WareOf(item.Ware)!.Name.ToLowerInvariant()}, {StyleWord(item.Style)}");
        if (_albumCells[row] == FullRowMask) Achieve("potter-album-row");
        if (AlbumOpenCount(_albumCells) == AlbumSize) Achieve("potter-album-all");
    }

    /// <summary>Виліплено виріб: копнули глини — може, щось трипільське. Повний музей більше не шукає.</summary>
    void AlbumOnFormed(string ware)
    {
        if ((_finds & FullMuseumMask) == FullMuseumMask) return;
        if (Ctx.Rng.NextDouble() >= FindChance) return;
        var i = Ctx.Rng.Next(Finds.Length);
        var find = Finds[i];
        var now = Ctx.Clock.UtcNow;
        if ((_finds & (1 << i)) != 0)
        {
            _findShards++;
            _lastFind = new LastFindRow(find.Key, now, true);
            AwayNote($"🏺 У глинищі ще один «{find.Name.ToLowerInvariant()}» — уламок у скарбничку");
            return;
        }
        Found(i, now);
        AwayNote($"🏺 У глинищі знайшли: {find.Name.ToLowerInvariant()}");
    }

    void Found(int i, DateTimeOffset now)
    {
        _finds |= 1 << i;
        _lastFind = new LastFindRow(Finds[i].Key, now, false);
        if ((_finds & FullMuseumMask) == FullMuseumMask) Achieve("potter-museum-shards");
    }

    static string StyleWord(string style) =>
        string.IsNullOrEmpty(style) ? "простий" : Styles.FirstOrDefault(s => s.Key == style)?.Name.ToLowerInvariant() ?? style;

    static string QualityWord(int q) => q switch { 4 => "розкішна", 3 => "дзвінка", 2 => "добра", _ => "звичайна" };

    static string Pc(double share) => (share * 100).ToString("0.#", Uk);

    // ---------- дія album ----------

    ActResult? ActAlbum(string action, JsonElement payload)
    {
        if (action != "album") return null;
        return Str(payload, "op") switch
        {
            "tile" => StoveTileIn(payload),
            "glue" => GlueShards(),
            "show" => AlbumShowSet(payload),
            _ => ActResult.Fail("Такого в альбомі не роблять"),
        };
    }

    /// <summary>
    /// Кахля в піч: <c>{ key }</c> — саме ця з комори, без ключа — будь-яка добра (спершу найгірша). З <c>{ slot }</c> —
    /// заміна кахлі в гнізді: нова з комори, стара назад у комору (без ключа беремо найкращу з комори).
    /// </summary>
    ActResult StoveTileIn(JsonElement payload)
    {
        var slot = (int)Math.Clamp(Num(payload, "slot") ?? -1, -1, StoveSlots);
        var swap = slot >= 0 && slot < _stove.Count;
        if (!swap && _stove.Count >= StoveSlots)
            return ActResult.Fail("Піч уже вся в кахлях — тепер кахлю можна лише поміняти: тапни гніздо");
        if (slot >= _stove.Count && slot >= 0 && _stove.Count < StoveSlots)
            slot = -1;                                          // гніздо ще порожнє — просто вставляємо наступну
        var key = Str(payload, "key");
        ItemInfo? pick;
        if (key.Length > 0)
        {
            if (ParseItem(key) is not { } it || it.Ware != "tile") return ActResult.Fail("У піч кладуть лише кахлі");
            if (it.Quality < StoveMinQuality) return ActResult.Fail("Звичайна кахля на піч не йде — треба добра чи краща");
            pick = ItemCount(x => x == it) > 0 ? it : null;
        }
        else
        {
            var good = AllItems().Where(x => x.Item.Ware == "tile" && x.Item.Quality >= StoveMinQuality).Select(x => x.Item);
            // У порожнє гніздо йде найгірша добра кахля (дзвінкі ще знадобляться на замовлення), на заміну — найкраща.
            pick = (swap ? good.OrderByDescending(x => x.Quality) : good.OrderBy(x => x.Quality)).FirstOrDefault();
        }
        if (pick is null) return ActResult.Fail("У коморі нема доброї кахлі — обпали кахлю якості «добра» чи кращої");
        var old = swap ? _stove[slot] : null;
        if (old is not null && old.Style == pick.Style && old.Quality == pick.Quality)
            return ActResult.Fail("Точнісінько така кахля в цьому гнізді вже стоїть");
        if (TakeItems(x => x == pick, 1) != 1) return ActResult.Fail("У коморі нема такої кахлі");
        var ringBefore = StoveRing;
        if (old is not null)
        {
            _stove[slot] = new StoveTile(pick.Style, pick.Quality);
            PutItems("tile", old.Style, old.Quality, 1);
        }
        else _stove.Add(new StoveTile(pick.Style, pick.Quality));
        var bonus = Pc(AlbumStoveBonus(_stove));
        if (_stove.Count == StoveSlots && StoveRing && !ringBefore)
        {
            if (old is null) Wonder("stove-full");
            Achieve("potter-stove");
            Achieve("potter-stove-ring");
            return ActResult.Accept($"🧱 Уся піч у дзвінких кахлях! +{bonus} % до всього — гості такої ще не бачили");
        }
        if (old is not null)
            return ActResult.Accept($"🧱 У {slot + 1}-му гнізді тепер {QualityWord(pick.Quality)} {StyleWord(pick.Style)} кахля"
                + $" — стара ({QualityWord(old.Quality)}) вернулась у комору · піч дає +{bonus} %");
        if (_stove.Count == StoveSlots)
        {
            Wonder("stove-full");
            Achieve("potter-stove");
            return ActResult.Accept($"🧱 Кахляна піч готова! +{bonus} % до всього — у хаті тепло й гарно");
        }
        return ActResult.Accept($"🧱 Кахля в печі: {_stove.Count} з {StoveSlots} · піч дає +{bonus} % до всього");
    }

    /// <summary>Чи вся піч складена з дзвінких і кращих кахлів.</summary>
    bool StoveRing => _stove.Count >= StoveSlots && _stove.All(t => t.Quality >= StoveRingQuality);

    /// <summary>П'ять уламків — склеїти річ, якої в музеї ще нема (котру саме — вирішує випадок).</summary>
    ActResult GlueShards()
    {
        var missing = Enumerable.Range(0, Finds.Length).Where(i => (_finds & (1 << i)) == 0).ToList();
        if (missing.Count == 0) return ActResult.Fail("Музей уже повний — уламки лишаються на згадку");
        if (_findShards < ShardsForGlue) return ActResult.Fail($"Бракує уламків: треба {ShardsForGlue}, є {_findShards}");
        _findShards -= ShardsForGlue;
        var i = missing[Ctx.Rng.Next(missing.Count)];
        Found(i, Ctx.Clock.UtcNow);
        return ActResult.Accept($"🏺 Склеїв з уламків: {Finds[i].Name.ToLowerInvariant()} — +1 % до всього");
    }

    /// <summary>Виставка: <c>{ keys: ["виріб|розпис", …] }</c> — до трьох відкритих клітинок на видноту в хаті.</summary>
    ActResult AlbumShowSet(JsonElement payload)
    {
        var want = new List<string>();
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("keys", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var v in arr.EnumerateArray())
            {
                if (v.ValueKind != JsonValueKind.String) continue;
                var key = v.GetString() ?? "";
                if (!AlbumCellOpen(key)) return ActResult.Fail("На виставку йде лише те, що вже є в альбомі");
                if (!want.Contains(key, StringComparer.Ordinal)) want.Add(key);
            }
        if (want.Count > ShowMax) return ActResult.Fail($"На видноті вміщається {ShowMax} вироби — більше вже комора");
        _albumShow.Clear();
        _albumShow.AddRange(want);
        return ActResult.Accept(want.Count == 0
            ? "📌 Виставку прибрано — полиця в хаті знову порожня"
            : $"📌 На виставці: {string.Join(", ", want.Select(ShowWord))}");
    }

    /// <summary>Чи відкрита клітинка з ключем «виріб|розпис».</summary>
    bool AlbumCellOpen(string key)
    {
        var (row, col) = AlbumCellAt(key);
        return row >= 0 && col >= 0 && (_albumCells[row] & (1 << col)) != 0;
    }

    static (int Row, int Col) AlbumCellAt(string key)
    {
        var i = key.IndexOf('|');
        if (i < 0) return (-1, -1);
        return (AlbumWareIndex(key[..i]), AlbumStyleIndex(key[(i + 1)..]));
    }

    string ShowWord(string key)
    {
        var i = key.IndexOf('|');
        return i < 0 ? key : $"{WareOf(key[..i])?.Name.ToLowerInvariant() ?? key} ({StyleWord(key[(i + 1)..])})";
    }

    // ---------- життя ----------

    void ResetAlbum(DateTimeOffset now)
    {
        Array.Clear(_albumCells);
        Array.Clear(_albumStars);
        _stove.Clear();
        _albumShow.Clear();
        _finds = 0;
        _findShards = 0;
        _lastFind = null;
    }

    /// <summary>Обпал-престиж альбому не чіпає: клітинки, зірки, кахлі, музей і виставка — назавжди (майстерність — лічильник основи, теж лишається).</summary>
    void FireAlbum(DateTimeOffset now) { }

    void SyncAlbum(DateTimeOffset now, TimeSpan paid) { }

    object? ViewAlbum(DateTimeOffset now)
    {
        var stars = AlbumStarCount(_albumStars);
        return new
        {
            // Рядок на виріб (порядок Wares): біт 0 — простий, біт i — i-й розпис.
            cells = _albumCells,
            stars = _albumStars,
            open = AlbumOpenCount(_albumCells),
            size = AlbumSize,
            rows = AlbumFullRows(_albumCells),
            cols = AlbumFullColumns(_albumCells),
            // Зірки — окремим лічильником, щоб клієнт склав розклад бонусу тими самими числами, що й сервер.
            starOpen = stars,
            starRows = AlbumStarRows(_albumStars),
            mastery = Wares.Select(w => MasteryLevel(FiredOf(w.Key))),
            stove = _stove.Select(t => new { style = t.Style, q = t.Quality }),
            stoveRing = StoveRing,
            stoveBonus = AlbumStoveBonus(_stove),
            tiles = ItemCount(x => x.Ware == "tile" && x.Quality >= StoveMinQuality),
            finds = _finds,
            shards = _findShards,
            find = _lastFind is { } f ? new { key = f.Key, at = f.At, dup = f.Dup } : null,
            // Виставка: що стоїть на видноті (хата й хата друга беруть звідси).
            show = _albumShow.Select(k =>
            {
                var (row, col) = AlbumCellAt(k);
                return new { key = k, ware = Wares[row].Key, style = col == 0 ? "" : Styles[col - 1].Key, star = (_albumStars[row] & (1 << col)) != 0 };
            }),
            bonus = AlbumBonusFor(_albumCells, _albumStars, _stove, _finds),
        };
    }

    // ---------- каталог ----------

    object? CatalogAlbum() => new
    {
        masteryAt = MasteryAt,
        masteryWork = MasteryWorkStep,
        masteryValue = MasteryValueStep,
        masteryTop = MasteryTopValue,
        cell = AlbumCellBonus,
        row = AlbumRowBonus,
        column = AlbumColumnBonus,
        star = AlbumStarBonus,
        starRow = AlbumStarRowBonus,
        starAll = AlbumStarAllBonus,
        starQuality = StarQuality,
        stoveSlots = StoveSlots,
        stoveQuality = StoveQualityBonus,
        stoveFull = StoveFullBonus,
        stoveRing = StoveRingBonus,
        stoveRingQuality = StoveRingQuality,
        topQuality = AlbumTopQuality,
        showMax = ShowMax,
        findChance = FindChance,
        find = FindBonus,
        museumFull = MuseumFullBonus,
        glue = ShardsForGlue,
        finds = Finds.Select(f => new { key = f.Key, name = f.Name, desc = FindFacts[f.Key] }),
        wares = WareFacts,
        styles = StyleFacts,
    };

    /// <summary>Короткі факти про вироби — лише перевірені (див. docs/games/specs/clicker-v7-album.md).</summary>
    static readonly Dictionary<string, string> WareFacts = new(StringComparer.Ordinal)
    {
        ["pot"] = "Головна посудина української кухні: у горщику варили борщ і кашу просто в печі.",
        ["bowl"] = "З миски їли всією родиною. Бубнівські полив'яні миски з квітами й гронами — окрема гордість Вінниччини.",
        ["jug"] = "Посудина з вузькою шийкою для молока й води.",
        ["makitra"] = "Широка глибока посудина: у ній макогоном розтирали мак і замішували тісто.",
        ["dish"] = "Велика широка миска, в якій страву подавали на стіл.",
        ["candle"] = "Глиняна підставка для свічки — прості й фігурні, з кількома ріжками.",
        ["whistle"] = "Звукова іграшка. Вірили, що свист глиняного півника відганяє зло.",
        ["barrel"] = "Посудина для напоїв — з вузьким горлом, щоб не розхлюпати.",
        ["tile"] = "Кахлями облицьовували печі. Косів славиться мальованими кахлями.",
        ["kumanets"] = "Кільцеподібна посудина з отвором посередині, весільна. Куманець — символ Опішні.",
        ["ram"] = "Баранець-свищик — звукова іграшка, як і півник: свист, вірили, відганяв зло.",
        ["lion"] = "Фігурна посудина у вигляді звіра — вершина гончарного вміння: її ліплять частинами і з'єднують сирими.",
        // Вироби дев'ятого оновлення (додає пакет «Ремесло»): факти описові — про форму й ужиток, без непевних тверджень.
        ["kukhol"] = "Кухоль — посудина з вухом, з якої п'ють: вода, квас, узвар.",
        ["tykva"] = "Назву дала форма: широке пузо й вузька шийка, як у гарбуза-тикви.",
        ["pleskanets"] = "Плескувата, сплющена з боків посудина — таку зручно нести при боці.",
    };

    /// <summary>Осередки й розписи: звідки і що в них особливого.</summary>
    static readonly Dictionary<string, object> StyleFacts = new(StringComparer.Ordinal)
    {
        [""] = new { place = "У кожному гончарному селі", text = "Без розпису — чиста глина: так ліпили щоденний посуд." },
        ["gavarets"] = new { place = "Гавареччина, Львівщина", text = "Чорнодимлена кераміка: виріб лощать камінцем і випалюють без доступу повітря — дим робить його чорним." },
        ["vasylkiv"] = new { place = "Васильків, Київщина", text = "Майоліка. Васильківський півник уцілів на шафі в зруйнованій Бородянці у 2022-му й став символом стійкості." },
        ["bubnivka"] = new { place = "Бубнівка, Вінниччина", text = "Полив'яні миски з квітами й гронами, розпис ріжком. У Національному переліку нематеріальної спадщини з 2018 року." },
        ["kosiv"] = new { place = "Косів, Івано-Франківщина", text = "Мальована кераміка — у списку нематеріальної спадщини ЮНЕСКО з 2019. Розпис продряпують по білому ангобу; зелений, жовтий, коричневий; вершники, олені, птахи." },
        ["opishnia"] = new { place = "Опішня, Полтавщина", text = "Найвідоміший гончарний осередок, з 1986 року тут Національний музей-заповідник українського гончарства. Техніки — фляндрування й ріжкування." },
        ["mezhyhirya"] = new { place = "Межигір'я, під Києвом", text = "Межигірська фаянсова фабрика (XVIII–XIX ст.) робила фаянс — тонкий білий посуд." },
        ["petrykivka"] = new { place = "Петриківка, Дніпропетровщина", text = "Петриківський розпис — у списку ЮНЕСКО з 2013, але це розпис не на кераміці: його малюють на папері, дереві, стінах. На глеку — фантазія гончаря." },
        ["trypillia"] = new { place = "Трипільська культура", text = "Мальована кераміка зі спіралями, біноклеподібні посудини, жіночі статуетки. Посуд ліпили без гончарного кола." },
    };

    static readonly Dictionary<string, string> FindFacts = new(StringComparer.Ordinal)
    {
        ["spiral"] = "Спіраль — найчастіший мотив трипільського розпису: червоним, чорним і білим по глині.",
        ["binocular"] = "Дві з'єднані посудинки, схожі на бінокль. Навіщо — досі гадають; найімовірніше, для обрядів.",
        ["figurine"] = "Глиняні жіночі фігурки — одна з найчастіших трипільських знахідок.",
        ["house"] = "Глиняна модель оселі з деталями всередині — трипільці ліпили й такі.",
        ["grain"] = "Усередині миски — знак, у якому дослідники бачать зерно: наче миска повна колосків.",
        ["krater"] = "Велика посудина з широким розхиленим горлом — у таких зберігали зерно.",
        ["ladle"] = "З глини ліпили не лише горщики, а й ложки, ковші та черпаки.",
        ["whorl"] = "Глиняний тягарець на веретено: з ним пряли нитку.",
    };

    // ---------- збереження ----------

    sealed record LastFindRow(string Key, DateTimeOffset At, bool Dup);

    /// <summary>
    /// Альбом у збереженні. Клітинки й зірки — «виріб → ключі розписів» (порожній ключ — простий): так збереження
    /// читається очима й переживає новий виріб чи розпис у каталозі. Усі поля необов'язкові.
    /// </summary>
    sealed record AlbumRow(
        Dictionary<string, List<string>>? Cells = null, Dictionary<string, List<string>>? Stars = null,
        List<StoveTile>? Stove = null, List<string>? Finds = null, int Shards = 0, LastFindRow? Last = null,
        List<string>? Show = null);

    AlbumRow? SaveAlbum() => new(MasksToKeys(_albumCells), MasksToKeys(_albumStars), _stove.ToList(),
        Finds.Where((_, i) => (_finds & (1 << i)) != 0).Select(f => f.Key).ToList(), _findShards, _lastFind,
        _albumShow.Count > 0 ? [.. _albumShow] : null);

    void LoadAlbum(AlbumRow? row)
    {
        ResetAlbum(Ctx.Clock.UtcNow);
        if (row is null) return;
        KeysToMasks(row.Cells, _albumCells);
        KeysToMasks(row.Stars, _albumStars);
        // Зірка без клітинки — зіпсоване збереження: зірка й відкриває.
        for (var i = 0; i < _albumCells.Length; i++) _albumCells[i] |= _albumStars[i];
        foreach (var t in row.Stove ?? [])
            if (t is not null && _stove.Count < StoveSlots && t.Quality >= StoveMinQuality && t.Quality <= AlbumTopQuality && AlbumStyleIndex(t.Style) >= 0)
                _stove.Add(new StoveTile(t.Style ?? "", t.Quality));
        foreach (var key in row.Finds ?? [])
        {
            var i = Array.FindIndex(Finds, f => f.Key == key);
            if (i >= 0) _finds |= 1 << i;
        }
        _findShards = Math.Clamp(row.Shards, 0, 10_000);
        _lastFind = row.Last is { } l && Finds.Any(f => f.Key == l.Key) ? l : null;
        foreach (var key in row.Show ?? [])
            if (key is not null && _albumShow.Count < ShowMax && AlbumCellOpen(key) && !_albumShow.Contains(key, StringComparer.Ordinal))
                _albumShow.Add(key);
    }

    static Dictionary<string, List<string>> MasksToKeys(int[] masks)
    {
        var d = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var r = 0; r < Wares.Length; r++)
        {
            if (masks[r] == 0) continue;
            var list = new List<string>();
            for (var c = 0; c < AlbumColumns; c++)
                if ((masks[r] & (1 << c)) != 0) list.Add(c == 0 ? "" : Styles[c - 1].Key);
            d[Wares[r].Key] = list;
        }
        return d;
    }

    static void KeysToMasks(Dictionary<string, List<string>>? from, int[] masks)
    {
        foreach (var (ware, styles) in from ?? [])
        {
            var r = AlbumWareIndex(ware);
            if (r < 0) continue;
            foreach (var s in styles ?? [])
            {
                var c = AlbumStyleIndex(s);
                if (c >= 0) masks[r] |= 1 << c;
            }
        }
    }
}
