using System.Text;

namespace Hlechyky.Games.Impl;

/// <summary>Місто (§1.2): № на дроті, slug і назва. Координат на сервері нема — геометрія лише в клієнта (§4.1).</summary>
public sealed record SlCity(int I, string Slug, string Name);

/// <summary>Шлях (§1.3): кінці, вантаж (індекс <see cref="SolyanyiMap.Cargo"/> або <see cref="SolyanyiMap.Grey"/>), довжина, парний шлях (-1 — нема).</summary>
public sealed record SlRoute(int Id, int A, int B, int Cargo, int Len, int Twin)
{
    public int Pts => SolyanyiMap.RoutePoints[Len];
}

/// <summary>Замовлення (§1.4): міста, найкоротше за всіма коридорами, очки.</summary>
public sealed record SlTicket(int Id, int A, int B, int D, int Pts);

/// <summary>
/// Граф «Соляного шляху» (контракт §1, §4): вісім вантажів, міста, шляхи, замовлення — без Ctx і без геометрії. Ті самі
/// числа лежать у <c>web/games/solyanyi/consts.js</c>; відбиток <see cref="MapSig"/> у виді й тест не дають їм розійтись.
/// </summary>
public static class SolyanyiMap
{
    public const int Salt = 0, Fish = 1, Grain = 2, Tar = 3, Honey = 4, Wool = 5, Hides = 6, Linen = 7;
    /// <summary>Вид карти «віл» (індекс у руці); шляхів такого кольору нема.</summary>
    public const int Wild = 8;
    /// <summary>Колір шляху «будь-який один вантаж».</summary>
    public const int Grey = -1;

    public const int MaxSeats = 5, DeckEach = 12, DeckWild = 14, DeckSize = 110, HandStart = 4, MarketSize = 5, DrawPicks = 2;
    public const int WipeAt = 3, WipeMax = 3, Offer = 3, KeepStart = 2, KeepLater = 1, LastAt = 2, LongestBonus = 10;

    /// <summary>Вісім вантажів у незмінному порядку (рука, плата, переліки).</summary>
    public static readonly string[] Cargo = ["salt", "fish", "grain", "tar", "honey", "wool", "hides", "linen"];
    /// <summary>Карти на дроті: вісім вантажів і віл.</summary>
    public static readonly string[] Cards = [.. Cargo, "wild"];
    /// <summary>Родовий: «бракує 2 солі».</summary>
    public static readonly string[] CargoGen = ["солі", "риби", "збіжжя", "дьогтю", "меду", "вовни", "шкір", "полотна"];
    /// <summary>Знахідний: «бере сіль», віл — останній.</summary>
    public static readonly string[] CargoAcc = ["сіль", "рибу", "збіжжя", "дьоготь", "мед", "вовну", "шкіри", "полотно", "вола"];

    /// <summary>Очки за шлях за довжиною (індекс — довжина).</summary>
    public static readonly int[] RoutePoints = [0, 1, 2, 4, 7, 10, 15];

    /// <summary>Міста Криму — для ачівки, статистики й Глека.</summary>
    public const int Perekop = 28, Simferopol = 29, Sevastopol = 30;

    public static bool IsCrimea(int city) => city is Perekop or Simferopol or Sevastopol;
    public static bool IsCrimea(SlTicket t) => IsCrimea(t.A) || IsCrimea(t.B);

    /// <summary>Міста (§1.2) за №.</summary>
    public static readonly SlCity[] Cities =
    [
        new(0, "kyiv", "Київ"),
        new(1, "chernihiv", "Чернігів"),
        new(2, "nizhyn", "Ніжин"),
        new(3, "konotop", "Конотоп"),
        new(4, "sumy", "Суми"),
        new(5, "poltava", "Полтава"),
        new(6, "opishnia", "Опішня"),
        new(7, "kharkiv", "Харків"),
        new(8, "kupiansk", "Куп'янськ"),
        new(9, "luhansk", "Луганськ"),
        new(10, "kramatorsk", "Краматорськ"),
        new(11, "donetsk", "Донецьк"),
        new(12, "mariupol", "Маріуполь"),
        new(13, "berdiansk", "Бердянськ"),
        new(14, "melitopol", "Мелітополь"),
        new(15, "zaporizhzhia", "Запоріжжя"),
        new(16, "dnipro", "Дніпро"),
        new(17, "kremenchuk", "Кременчук"),
        new(18, "cherkasy", "Черкаси"),
        new(19, "bila", "Біла Церква"),
        new(20, "uman", "Умань"),
        new(21, "kropyvnytskyi", "Кропивницький"),
        new(22, "kryvyi", "Кривий Ріг"),
        new(23, "voznesensk", "Вознесенськ"),
        new(24, "mykolaiv", "Миколаїв"),
        new(25, "odesa", "Одеса"),
        new(26, "izmail", "Ізмаїл"),
        new(27, "kherson", "Херсон"),
        new(28, "perekop", "Перекоп"),
        new(29, "simferopol", "Сімферополь"),
        new(30, "sevastopol", "Севастополь"),
        new(31, "vinnytsia", "Вінниця"),
        new(32, "zhytomyr", "Житомир"),
        new(33, "korosten", "Коростень"),
        new(34, "rivne", "Рівне"),
        new(35, "khmelnytskyi", "Хмельницький"),
        new(36, "kamianets", "Кам'янець-Подільський"),
        new(37, "chernivtsi", "Чернівці"),
        new(38, "kolomyia", "Коломия"),
        new(39, "frankivsk", "Івано-Франківськ"),
        new(40, "ternopil", "Тернопіль"),
        new(41, "lviv", "Львів"),
        new(42, "lutsk", "Луцьк"),
        new(43, "kovel", "Ковель"),
        new(44, "uzhhorod", "Ужгород"),
    ];

    /// <summary>Шляхи (§1.3): id — місце в масиві; вантаж — індекс <see cref="Cargo"/> або <see cref="Grey"/>; <c>Twin</c> — парний шлях або -1.</summary>
    public static readonly SlRoute[] Routes =
    [
        new(0, 0, 1, Salt, 3, -1),
        new(1, 0, 2, Grey, 2, -1),
        new(2, 1, 2, Fish, 1, -1),
        new(3, 2, 3, Tar, 2, -1),
        new(4, 1, 3, Grey, 3, -1),
        new(5, 3, 4, Honey, 2, -1),
        new(6, 4, 6, Fish, 2, -1),
        new(7, 4, 7, Wool, 3, -1),
        new(8, 5, 7, Hides, 3, -1),
        new(9, 5, 6, Grey, 1, -1),
        new(10, 6, 7, Linen, 2, -1),
        new(11, 17, 22, Grey, 3, -1),
        new(12, 7, 8, Fish, 2, -1),
        new(13, 8, 9, Salt, 4, -1),
        new(14, 8, 10, Hides, 2, -1),
        new(15, 7, 10, Grey, 3, -1),
        new(16, 10, 9, Honey, 3, -1),
        new(17, 10, 11, Grain, 2, 101),
        new(18, 9, 11, Wool, 3, -1),
        new(19, 11, 12, Hides, 2, -1),
        new(20, 11, 15, Linen, 4, -1),
        new(21, 12, 13, Grey, 1, -1),
        new(22, 13, 14, Fish, 2, -1),
        new(23, 15, 14, Tar, 2, 95),
        new(24, 15, 16, Grey, 1, -1),
        new(25, 16, 10, Salt, 4, -1),
        new(26, 16, 7, Honey, 4, 93),
        new(27, 16, 5, Wool, 3, -1),
        new(28, 16, 17, Grain, 3, 94),
        new(29, 16, 22, Hides, 3, -1),
        new(30, 15, 22, Grey, 3, -1),
        new(31, 17, 5, Tar, 2, 92),
        new(32, 17, 18, Linen, 2, -1),
        new(33, 17, 21, Fish, 2, -1),
        new(34, 18, 0, Hides, 3, -1),
        new(35, 18, 19, Grey, 3, -1),
        new(36, 18, 21, Honey, 2, -1),
        new(37, 0, 5, Grain, 6, -1),
        new(38, 0, 19, Grey, 2, 90),
        new(39, 19, 20, Salt, 2, 91),
        new(40, 19, 31, Linen, 3, -1),
        new(41, 20, 21, Wool, 3, -1),
        new(42, 20, 31, Tar, 3, -1),
        new(43, 20, 23, Grey, 3, -1),
        new(44, 21, 22, Fish, 2, -1),
        new(45, 21, 23, Linen, 3, -1),
        new(46, 22, 27, Honey, 3, -1),
        new(47, 22, 24, Grain, 3, -1),
        new(48, 23, 24, Hides, 2, -1),
        new(49, 23, 25, Salt, 3, -1),
        new(50, 24, 25, Wool, 2, 98),
        new(51, 24, 27, Grey, 1, -1),
        new(52, 25, 26, Fish, 4, -1),
        new(53, 27, 28, Tar, 2, 96),
        new(54, 14, 28, Salt, 3, -1),
        new(55, 27, 14, Linen, 4, -1),
        new(56, 28, 29, Hides, 3, 97),
        new(57, 29, 30, Grey, 1, -1),
        new(58, 28, 30, Grain, 4, -1),
        new(59, 0, 32, Honey, 3, 89),
        new(60, 0, 33, Grey, 3, -1),
        new(61, 33, 32, Wool, 2, -1),
        new(62, 33, 34, Tar, 4, -1),
        new(63, 33, 43, Salt, 5, -1),
        new(64, 32, 34, Grain, 4, -1),
        new(65, 32, 31, Fish, 2, -1),
        new(66, 31, 35, Grey, 2, 99),
        new(67, 35, 36, Hides, 2, -1),
        new(68, 36, 37, Grey, 1, -1),
        new(69, 35, 40, Linen, 2, -1),
        new(70, 35, 34, Honey, 3, -1),
        new(71, 26, 23, Tar, 6, -1),
        new(72, 31, 23, Grain, 5, -1),
        new(73, 40, 41, Salt, 2, 100),
        new(74, 40, 34, Grey, 3, -1),
        new(75, 40, 39, Wool, 2, -1),
        new(76, 40, 37, Fish, 3, -1),
        new(77, 37, 38, Grey, 1, -1),
        new(78, 38, 39, Linen, 1, -1),
        new(79, 39, 41, Grain, 2, -1),
        new(80, 39, 44, Tar, 4, -1),
        new(81, 41, 44, Honey, 4, -1),
        new(82, 41, 42, Hides, 3, -1),
        new(83, 42, 34, Grey, 1, -1),
        new(84, 42, 43, Wool, 1, -1),
        new(85, 43, 41, Grey, 3, -1),
        new(86, 36, 31, Grey, 3, -1),
        new(87, 3, 6, Grey, 3, -1),
        new(88, 14, 29, Grey, 5, -1),
        new(89, 0, 32, Linen, 3, 59),
        new(90, 0, 19, Fish, 2, 38),
        new(91, 19, 20, Hides, 2, 39),
        new(92, 17, 5, Honey, 2, 31),
        new(93, 16, 7, Wool, 4, 26),
        new(94, 16, 17, Fish, 3, 28),
        new(95, 15, 14, Linen, 2, 23),
        new(96, 27, 28, Wool, 2, 53),
        new(97, 28, 29, Salt, 3, 56),
        new(98, 24, 25, Tar, 2, 50),
        new(99, 31, 35, Hides, 2, 66),
        new(100, 40, 41, Fish, 2, 73),
        new(101, 10, 11, Linen, 2, 17),
    ];

    /// <summary>Замовлення (§1.4): міста, найкоротше за всіма коридорами, очки.</summary>
    public static readonly SlTicket[] Tickets =
    [
        new(0, 44, 7, 23, 26),   // Ужгород — Харків
        new(1, 30, 41, 20, 23),   // Севастополь — Львів
        new(2, 37, 11, 19, 21),   // Чернівці — Донецьк
        new(3, 29, 1, 18, 20),   // Сімферополь — Чернігів
        new(4, 43, 15, 17, 19),   // Ковель — Запоріжжя
        new(5, 41, 16, 17, 19),   // Львів — Дніпро
        new(6, 26, 7, 16, 18),   // Ізмаїл — Харків
        new(7, 43, 25, 15, 17),   // Ковель — Одеса
        new(8, 29, 0, 15, 17),   // Сімферополь — Київ
        new(9, 30, 9, 14, 15),   // Севастополь — Луганськ
        new(10, 29, 4, 14, 15),   // Сімферополь — Суми
        new(11, 26, 0, 13, 14),   // Ізмаїл — Київ
        new(12, 30, 7, 13, 14),   // Севастополь — Харків
        new(13, 29, 31, 13, 14),   // Сімферополь — Вінниця
        new(14, 28, 0, 12, 13),   // Перекоп — Київ
        new(15, 29, 6, 12, 13),   // Сімферополь — Опішня
        new(16, 39, 0, 11, 12),   // Івано-Франківськ — Київ
        new(17, 6, 25, 11, 12),   // Опішня — Одеса
        new(18, 25, 0, 10, 11),   // Одеса — Київ
        new(19, 28, 7, 10, 11),   // Перекоп — Харків
        new(20, 34, 23, 10, 11),   // Рівне — Вознесенськ
        new(21, 4, 12, 10, 11),   // Суми — Маріуполь
        new(22, 40, 18, 10, 11),   // Тернопіль — Черкаси
        new(23, 44, 31, 10, 11),   // Ужгород — Вінниця
        new(24, 32, 17, 8, 8),   // Житомир — Кременчук
        new(25, 9, 14, 8, 8),   // Луганськ — Мелітополь
        new(26, 28, 17, 8, 8),   // Перекоп — Кременчук
        new(27, 28, 20, 8, 8),   // Перекоп — Умань
        new(28, 13, 22, 7, 7),   // Бердянськ — Кривий Ріг
        new(29, 3, 18, 7, 7),   // Конотоп — Черкаси
        new(30, 1, 5, 7, 7),   // Чернігів — Полтава
        new(31, 36, 42, 6, 6),   // Кам'янець-Подільський — Луцьк
        new(32, 8, 16, 6, 6),   // Куп'янськ — Дніпро
        new(33, 27, 15, 6, 6),   // Херсон — Запоріжжя
        new(34, 19, 21, 5, 5),   // Біла Церква — Кропивницький
        new(35, 10, 15, 5, 5),   // Краматорськ — Запоріжжя
        new(36, 2, 6, 5, 5),   // Ніжин — Опішня
        new(37, 20, 24, 5, 5),   // Умань — Миколаїв
        new(38, 38, 35, 4, 4),   // Коломия — Хмельницький
        new(39, 33, 31, 4, 4),   // Коростень — Вінниця
    ];

    /// <summary>Шляхи кожного міста — для Дейкстри й обходів.</summary>
    public static readonly SlRoute[][] Adj = [.. Cities.Select(c => Routes.Where(r => r.A == c.I || r.B == c.I).ToArray())];

    /// <summary>Вартість «усі коридори за довжиною» — так рахують очки замовлень (§1.4).</summary>
    public static readonly int[] LenCost = [.. Routes.Select(r => r.Len)];

    public static int TicketPoints(int d) => d + Math.Max(0, d / 5 - 1);

    /// <summary>Індекс вантажу за назвою: 0–7, <see cref="Wild"/> або -1 — такого нема.</summary>
    public static int CardIndex(string? name) => name is null ? -1 : Array.IndexOf(Cards, name);

    public static int Other(SlRoute r, int city) => r.A == city ? r.B : r.A;

    public static string RouteName(int id) => $"{Cities[Routes[id].A].Name} — {Cities[Routes[id].B].Name}";

    /// <summary>
    /// Найкоротший шлях від <paramref name="a"/> до <paramref name="b"/> за вартістю <paramref name="cost"/> (за id шляху;
    /// від'ємна — шляхом їхати не можна). Рівність — менше шляхів, далі менший id (§8.1). Повертає суму або -1, якщо шляху
    /// нема; <paramref name="path"/> (якщо дали) — id шляхів від a до b. Масив замість делегата: Глек кличе це тисячі разів за партію.
    /// </summary>
    public static int Dijkstra(int a, int b, int[] cost, List<int>? path = null)
    {
        const int n = 45;
        Span<int> dist = stackalloc int[n], hops = stackalloc int[n], via = stackalloc int[n];
        Span<bool> done = stackalloc bool[n];
        dist.Fill(int.MaxValue);
        hops.Fill(int.MaxValue);
        via.Fill(-1);
        done.Clear();
        dist[a] = 0;
        hops[a] = 0;
        path?.Clear();
        while (true)
        {
            var u = -1;
            for (var i = 0; i < n; i++)
                if (!done[i] && dist[i] != int.MaxValue && (u < 0 || dist[i] < dist[u] || (dist[i] == dist[u] && hops[i] < hops[u]))) u = i;
            if (u < 0) return -1;
            if (u == b) break;
            done[u] = true;
            foreach (var r in Adj[u])
            {
                var c = cost[r.Id];
                if (c < 0) continue;
                var v = r.A == u ? r.B : r.A;
                if (done[v]) continue;
                var d = dist[u] + c;
                var h = hops[u] + 1;
                if (d < dist[v] || (d == dist[v] && (h < hops[v] || (h == hops[v] && r.Id < via[v]))))
                {
                    dist[v] = d;
                    hops[v] = h;
                    via[v] = r.Id;
                }
            }
        }
        if (path is not null)
        {
            for (var at = b; at != a;)
            {
                var r = Routes[via[at]];
                path.Add(r.Id);
                at = r.A == at ? r.B : r.A;
            }
            path.Reverse();
        }
        return dist[b];
    }

    /// <summary>Рядок відбитка (§4.2): шляхи за id, потім замовлення за id, ASCII.</summary>
    public static string SigText()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < Routes.Length; i++)
        {
            var r = Routes[i];
            if (i > 0) sb.Append(';');
            sb.Append('r').Append(r.Id).Append(':').Append(r.A).Append(':').Append(r.B).Append(':')
                .Append(r.Cargo == Grey ? "grey" : Cargo[r.Cargo]).Append(':').Append(r.Len).Append(':');
            if (r.Twin >= 0) sb.Append(r.Twin);
        }
        sb.Append('|');
        for (var i = 0; i < Tickets.Length; i++)
        {
            var t = Tickets[i];
            if (i > 0) sb.Append(';');
            sb.Append('t').Append(t.Id).Append(':').Append(t.A).Append(':').Append(t.B).Append(':').Append(t.Pts);
        }
        return sb.ToString();
    }

    /// <summary>Відбиток мапи (§4.2): FNV-1a 32 над <see cref="SigText"/>, 8 шістнадцяткових цифр.</summary>
    public static string MapSig()
    {
        var h = 0x811c9dc5u;
        foreach (var b in Encoding.ASCII.GetBytes(SigText()))
        {
            h ^= b;
            h = unchecked(h * 0x01000193u);
        }
        return h.ToString("x8");
    }

    /// <summary>Те саме значення один раз — вид кладе його в кожен кадр.</summary>
    public static readonly string Sig = MapSig();
}
