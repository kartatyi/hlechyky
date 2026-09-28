using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Море, на якому грають: розмір поля, склад флоту й час на розстановку. Класика — 10×10 і десять
/// кораблів; швидке — 8×8 і шість, щоб партію на чотирьох можна було дограти за вечір, а не за тиждень.
/// </summary>
public sealed record BattleshipSea(string Key, string Label, int W, int H, int[] Fleet, int PlaceSeconds)
{
    public int Cells => W * H;
    /// <summary>Скільки влучань топить увесь флот.</summary>
    public int Decks => Fleet.Sum();
}

/// <summary>
/// Правила флоту без жодного слова про кімнати: розміри поля, склад ескадри, перевірка розстановки й
/// випадкова розстановка. Окремо від гри, бо саме це найлегше зламати і найлегше перевірити тестом.
/// Клітинки скрізь — один індекс (рядок × ширина + колонка), як на дроті. Методи без моря — класичне море.
/// </summary>
public static class BattleshipRules
{
    public const int W = 10, H = 10;
    public const int Cells = W * H;
    /// <summary>Скільки часу дано на розстановку в класиці, поки не почнеться бій.</summary>
    public const int PlaceSeconds = 120;
    /// <summary>Скільки думати над пострілом. Далі гармата стріляє сама — навмання: один задуманий не тримає весь стіл.</summary>
    public const int TurnSeconds = 40;

    /// <summary>Класика: один чотирипалубний, два трипалубні, три двопалубні, чотири однопалубні.</summary>
    public static readonly int[] Fleet = [4, 3, 3, 2, 2, 2, 1, 1, 1, 1];

    /// <summary>Скільки влучань топить увесь класичний флот — сума <see cref="Fleet"/>.</summary>
    public const int Decks = 20;

    public static readonly BattleshipSea Classic = new("classic", "Класика: 10×10, десять кораблів", W, H, Fleet, PlaceSeconds);
    /// <summary>Швидке море: один трипалубний, два двопалубні, три однопалубні на 8×8.</summary>
    public static readonly BattleshipSea Quick = new("quick", "Швидкий: 8×8, шість кораблів", 8, 8, [3, 2, 2, 1, 1, 1], 60);
    public static readonly BattleshipSea[] Seas = [Classic, Quick];

    /// <summary>Вісім сусідів клітинки, які не вилізли за поле. Кораблі не торкаються навіть кутами, тож саме ця околиця й вирішує.</summary>
    public static IEnumerable<int> Halo(int cell) => Halo(cell, Classic);

    public static IEnumerable<int> Halo(int cell, BattleshipSea sea)
    {
        var (x, y) = (cell % sea.W, cell / sea.W);
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var (nx, ny) = (x + dx, y + dy);
                if (nx >= 0 && nx < sea.W && ny >= 0 && ny < sea.H) yield return ny * sea.W + nx;
            }
    }

    /// <summary>Околиця цілого корабля без нього самого: після потоплення вся вона — гарантований промах.</summary>
    public static IEnumerable<int> Around(int[] ship) => Around(ship, Classic);

    public static IEnumerable<int> Around(int[] ship, BattleshipSea sea) =>
        ship.SelectMany(c => Halo(c, sea)).Distinct().Where(c => !ship.Contains(c));

    static readonly string[] Count = ["нуль", "один", "два", "три", "чотири", "п'ять", "шість", "сім", "вісім", "дев'ять", "десять"];
    static readonly string[] Genitive = ["", "однієї", "двох", "трьох", "чотирьох", "п'яти"];
    static readonly string[] OnSize = ["", "на одну клітинку", "на два", "на три", "на чотири", "на п'ять"];

    static string Word(string[] words, int n) => n >= 0 && n < words.Length ? words[n] : n.ToString();

    /// <summary>«один на чотири, два на три, три на два і чотири на одну клітинку» — склад флоту людською мовою.</summary>
    public static string Describe(int[] fleet)
    {
        var parts = fleet.GroupBy(s => s).OrderByDescending(g => g.Key)
            .Select(g => $"{Word(Count, g.Count())} {Word(OnSize, g.Key)}").ToList();
        return parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " і " + parts[^1];
    }

    /// <summary>Чому ця розстановка не годиться (людською мовою), або null, якщо все гаразд.</summary>
    public static string? Invalid(IReadOnlyList<int[]>? ships) => Invalid(ships, Classic);

    public static string? Invalid(IReadOnlyList<int[]>? ships, BattleshipSea sea)
    {
        var fleet = sea.Fleet;
        if (ships is null || ships.Count != fleet.Length) return $"Кораблів має бути рівно {Word(Count, fleet.Length)}";
        var longest = fleet.Max();

        // owner[клітинка] — чий це корабель; заразом ловить і накладання
        var owner = new int[sea.Cells];
        Array.Fill(owner, -1);
        for (var i = 0; i < ships.Count; i++)
        {
            var cells = ships[i];
            if (cells is null || cells.Length == 0 || cells.Length > longest)
                return longest == 1 ? "Корабель тут — одна клітинка" : $"Корабель буває від однієї до {Word(Genitive, longest)} клітинок";
            if (cells.Any(c => c < 0 || c >= sea.Cells)) return "Корабель виліз за межі поля";
            if (cells.Distinct().Count() != cells.Length) return "Корабель стоїть сам на собі";
            if (!Straight(cells, sea.W)) return "Корабель має бути прямий";
            foreach (var c in cells)
            {
                if (owner[c] >= 0) return "Кораблі налазять один на одного";
                owner[c] = i;
            }
        }

        if (!ships.Select(s => s.Length).OrderDescending().SequenceEqual(fleet.OrderDescending()))
            return "Флот не той: " + Describe(fleet);

        for (var i = 0; i < ships.Count; i++)
            foreach (var cell in ships[i])
                foreach (var near in Halo(cell, sea))
                    if (owner[near] >= 0 && owner[near] != i) return "Кораблі не можуть торкатись навіть кутами";

        return null;
    }

    /// <summary>Корабель — це рівний відрізок по горизонталі або по вертикалі, без дірок.</summary>
    static bool Straight(int[] cells, int w)
    {
        if (cells.Length == 1) return true;
        var sorted = cells.Order().ToArray();
        if (sorted.All(c => c / w == sorted[0] / w))
            return sorted.Zip(sorted.Skip(1)).All(p => p.Second == p.First + 1);
        if (sorted.All(c => c % w == sorted[0] % w))
            return sorted.Zip(sorted.Skip(1)).All(p => p.Second == p.First + w);
        return false;
    }

    /// <summary>
    /// Випадкова розстановка з переданого генератора — і тільки з нього, інакше партія не відтвориться.
    /// Спроби обмежені: флот лягає з першого-другого заходу, але гру не можна лишати в циклі, тому в
    /// найгіршому разі повертаємо готову ручну розстановку.
    /// </summary>
    public static List<int[]> RandomFleet(Random rng) => RandomFleet(rng, Classic);

    public static List<int[]> RandomFleet(Random rng, BattleshipSea sea)
    {
        for (var attempt = 0; attempt < 200; attempt++)
            if (TryFleet(rng, sea) is { } fleet) return fleet;
        return Canonical(sea);
    }

    static List<int[]>? TryFleet(Random rng, BattleshipSea sea)
    {
        // busy — клітинки кораблів разом з околицями: так дотик кутом просто не трапляється
        var busy = new bool[sea.Cells];
        var ships = new List<int[]>(sea.Fleet.Length);
        foreach (var size in sea.Fleet)
        {
            var placed = false;
            for (var tries = 0; tries < 500 && !placed; tries++)
            {
                var horizontal = rng.Next(2) == 0;
                var x = rng.Next(horizontal ? sea.W - size + 1 : sea.W);
                var y = rng.Next(horizontal ? sea.H : sea.H - size + 1);
                var cells = new int[size];
                for (var i = 0; i < size; i++) cells[i] = horizontal ? y * sea.W + x + i : (y + i) * sea.W + x;
                if (cells.Any(c => busy[c])) continue;
                foreach (var cell in cells)
                {
                    busy[cell] = true;
                    foreach (var near in Halo(cell, sea)) busy[near] = true;
                }
                ships.Add(cells);
                placed = true;
            }
            if (!placed) return null;
        }
        return ships;
    }

    /// <summary>Ручна розстановка на випадок, якщо випадковій не пощастило: рівні ряди, дотиків нема.</summary>
    public static List<int[]> Canonical() => Canonical(Classic);

    public static List<int[]> Canonical(BattleshipSea sea) => sea.Key == Quick.Key
        ? [[0, 1, 2], [4, 5], [16, 17], [19], [21], [23]]
        : [[0, 1, 2, 3], [5, 6, 7], [20, 21, 22], [24, 25], [27, 28], [40, 41], [43], [45], [47], [49]];
}

/// <summary>
/// Морський бій на 2–4. Гра <see cref="GameInfo.Hidden"/>: свій флот бачить лише господар, решта — самі
/// влучання й промахи, глядач — усі поля без жодного корабля. Удвох це класика; утрьох і вчотирьох —
/// «кожен проти кожного»: у свій хід б'єш по будь-якому живому полю, чий флот на дні — той вибув і далі
/// дивиться, останній на плаву виграв. Тик потрібен не для руху, а для годинників (розстановка й хід) і
/// ботів: інакше той, хто пішов пити чай, лишив би стіл висіти назавжди.
/// Порожні місця може зайняти «Глек підсідає» (<see cref="BattleshipBot"/>) — без нагород, з 🤖 в імені.
/// </summary>
public sealed partial class Battleship : Game
{
    public const int Seats = 4;

    /// <summary>Тик — чверть секунди: види після пострілу летять із тика (див. spec), і секунда затримки була помітна оком.</summary>
    public const int TickMillis = 250;

    /// <summary>Реакції 😱 😂 🎯 — не частіше за раз на стільки з одного місця.</summary>
    public const int ReactGapMs = 1200;

    enum Phase { Lobby, Placing, Battle, Done }

    /// <summary>Одне поле: чий флот, чи готовий господар, куди по ньому вже стріляли і як він сам стріляв.</summary>
    sealed class Side
    {
        /// <summary>Сидів за столом на старті партії (або це бот) — тобто має поле.</summary>
        public bool In { get; set; }
        /// <summary>Вибув: флот на дні або встає з-за столу.</summary>
        public bool Out { get; set; }
        /// <summary>Встав з-за столу (а не потонув): такому ні помсти, ні шелягів.</summary>
        public bool Gone { get; set; }
        /// <summary>Це «Глек підсідає», а не людина; <see cref="Name"/> — як його звати за столом.</summary>
        public bool Bot { get; set; }
        public string Name { get; set; } = "";
        public List<int[]> Ships { get; set; } = [];
        public bool Ready { get; set; }
        /// <summary>Клітинки цього поля, куди влучили.</summary>
        public HashSet<int> Hits { get; } = [];
        /// <summary>Промахи по цьому полю — разом з околицями потоплених, які сервер домальовує сам.</summary>
        public HashSet<int> Misses { get; } = [];
        /// <summary>Скільки пострілів зробив господар цього поля (а не по ньому).</summary>
        public int Shots { get; set; }
        /// <summary>Скільки з них влучили.</summary>
        public int Scored { get; set; }
        /// <summary>Скільки чужих кораблів господар потопив.</summary>
        public int Sank { get; set; }

        public bool Sunk(int[] ship) => ship.All(Hits.Contains);
        public int[]? ShipAt(int cell) => Ships.FirstOrDefault(s => s.Contains(cell));
        public int Left => Ships.Count(s => !Sunk(s));
        public bool Alive => In && !Out;
    }

    /// <summary>
    /// Один постріл (чи одна штука з арсеналу) для стрічки подій: хто, по кому, куди і що вийшло. Слово
    /// Глека (<see cref="Quip"/>) і реакції столу (<see cref="Rx"/>) чіпляються саме до нього.
    /// </summary>
    sealed class Shot(int by, int at, int cell, string res, int size, bool auto)
    {
        public int By = by, At = at, Cell = cell, Size = size;
        public string Res = res;
        public bool Auto = auto;
        /// <summary>Остання помста вибулого.</summary>
        public bool Revenge;
        public string? Quip;
        /// <summary>Реакція кожного місця на цей постріл: -1 — нема, 0..2 — 😱 😂 🎯.</summary>
        public readonly int[] Rx = [-1, -1, -1, -1];
        /// <summary>Штука з арсеналу (plane, torpedo, bomb, radar, repair) — null для звичайного пострілу.</summary>
        public string? Tool;
        /// <summary>Усі клітинки, яких торкнулась штука (для анімації); для пострілу — null.</summary>
        public int[]? Cells;
        /// <summary>Радар: скільки палуб у квадраті 3×3. Міна: куди прилетів рикошет.</summary>
        public int? N;
        /// <summary>Спрацювала міна: рикошет по тому, хто стріляв (hit|sunk|out або none).</summary>
        public string? Boom;
    }

    static readonly string[] Names = ["синій", "червоний", "зелений", "жовтий"];

    public override GameInfo Info { get; } = new(
        "battleship", "Морський бій", "морський бій", GameGroup.Board, 1, Seats,
        TickMs: TickMillis, Start: StartMode.ByHost, Hidden: true,
        Options:
        [
            new GameOption("fleet", "Море", [.. BattleshipRules.Seas.Select(s => (s.Key, s.Label))], BattleshipRules.Classic.Key),
            new GameOption("mode", "Режим",
                [("classic", "Класика"), ("arsenal", "⚓ Арсенал: радар, літак, торпеда, бомба, міна за 🪙 шеляги")], "classic"),
            new GameOption("clock", "Годинник ходу",
                [("20", "20 с — темп"), ("40", "40 с"), ("60", "60 с — з запасом")], "40"),
            new GameOption("bots", "🤖 Глек підсідає на порожні місця",
                [("0", "ні"), ("1", "один Глек"), ("2", "два"), ("3", "три")], "0"),
        ],
        Hint: "Розстав кораблі, стріляй по чужих полях. Влучив — стріляєш ще. Удвох або кожен проти кожного на трьох-чотирьох; порожнє місце може зайняти Глек 🤖");

    BattleshipSea _sea = BattleshipRules.Classic;
    int _turnSeconds = BattleshipRules.TurnSeconds;
    int _bots;
    Side[] _sides = Fresh();
    Phase _phase = Phase.Lobby;
    int _turn;
    /// <summary>Коли скінчиться розстановка; null — партія ще не почалась (стіл у лобі).</summary>
    DateTimeOffset? _placeUntil;
    /// <summary>Коли гармата того, чий хід, вистрілить сама.</summary>
    DateTimeOffset? _turnUntil;
    /// <summary>Коли бот, чий зараз постріл, натисне на гачок; null — ще не почав «думати».</summary>
    DateTimeOffset? _botAt;
    int? _winner;
    /// <summary>Остання помста: <c>By</c> (щойно потоплений) має один постріл по <c>On</c> (хто потопив).</summary>
    (int By, int On)? _revenge;
    /// <summary>Хто вибув, по порядку: перший тут — останнє місце.</summary>
    readonly List<int> _sunkOrder = [];
    readonly List<Shot> _feed = [];
    /// <summary>Реакції: остання (0..2) і лічильник на місце (клієнт показує бульбашку, коли число росте), коли.</summary>
    readonly int[] _rxE = new int[Seats];
    readonly int[] _rxN = new int[Seats];
    readonly DateTimeOffset[] _rxAt = new DateTimeOffset[Seats];
    /// <summary>
    /// Стан змінився після останнього тика. У ігор з TickMs > 0 каркас не шле види після Act (Rooms.Act),
    /// тож роздати їх може лише тик — звідси й прапорець.
    /// </summary>
    bool _dirty;
    /// <summary>Останній відлік розстановки, що пішов у кадрі: кадр шлемо, лише коли змінилась секунда.</summary>
    int? _sentLeft;

    static Side[] Fresh() => [new(), new(), new(), new()];

    public override string SeatName(int seat) => seat >= 0 && seat < Seats ? Names[seat] : base.SeatName(seat);

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue("fleet", out var key) && BattleshipRules.Seas.FirstOrDefault(s => s.Key == key) is { } sea)
            _sea = sea;
        if (options.TryGetValue("clock", out var clock) && clock is "20" or "40" or "60") _turnSeconds = int.Parse(clock);
        if (options.TryGetValue("bots", out var bots) && bots is "0" or "1" or "2" or "3") _bots = int.Parse(bots);
        ConfigureArsenal(options);
    }

    /// <summary>Сам із собою не повоюєш: треба ще хоч одна людина або Глек на порожньому місці.</summary>
    public override string? CanStart()
    {
        var humans = Enumerable.Range(0, Seats).Count(Ctx.Seated);
        return humans + Math.Min(_bots, Seats - humans) >= 2 ? null
            : "Самому нема з ким воювати: хай хтось сяде — або відкрий стіл з «🤖 Глек підсідає»";
    }

    public override void Start()
    {
        _sides = Fresh();
        for (var s = 0; s < Seats; s++) _sides[s].In = Ctx.Seated(s);
        // Глеки сідають на вільні місця за порядком; флот розставляють одразу й кажуть «Готово».
        var bots = 0;
        for (var s = 0; s < Seats && bots < _bots; s++)
        {
            if (_sides[s].In) continue;
            var side = _sides[s];
            side.In = side.Bot = side.Ready = true;
            side.Name = BattleshipBot.Names[bots++];
            side.Ships = BattleshipRules.RandomFleet(Ctx.Rng, _sea);
        }
        _phase = Phase.Placing;
        _turn = First();
        _winner = null;
        _revenge = null;
        _botAt = null;
        _sunkOrder.Clear();
        _feed.Clear();
        Array.Clear(_rxE);
        Array.Clear(_rxN);
        Array.Clear(_rxAt);
        _placeUntil = Ctx.Clock.UtcNow.AddSeconds(_sea.PlaceSeconds);
        _turnUntil = null;
        _dirty = false;
        _sentLeft = null;
        StartArsenal();
    }

    IEnumerable<int> Players => Enumerable.Range(0, Seats).Where(s => _sides[s].In);
    IEnumerable<int> Alive => Enumerable.Range(0, Seats).Where(s => _sides[s].Alive);

    /// <summary>Перший живий за порядком місць: у рематчі каркас зсуває місця, тож починає щоразу інший.</summary>
    int First() => Enumerable.Range(0, Seats).FirstOrDefault(s => _sides[s].Alive, 0);

    /// <summary>Наступний живий після <paramref name="seat"/> по колу.</summary>
    int NextAlive(int seat)
    {
        for (var i = 1; i <= Seats; i++)
        {
            var s = (seat + i) % Seats;
            if (_sides[s].Alive) return s;
        }
        return seat;
    }

    string Nick(int seat) => _sides[seat].Bot ? _sides[seat].Name : Ctx.NickOf(seat) ?? SeatName(seat);
    public override string? SeatBot(int seat) => seat >= 0 && seat < _sides.Length && _sides[seat].Bot ? _sides[seat].Name : null;

    /// <summary>Хто зараз тисне на гачок: у помсту — вибулий месник, інакше той, чий хід.</summary>
    int Shooter => _revenge?.By ?? _turn;

    // ---------- ходи ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action == "react") return React(seat, payload);
        if (_phase == Phase.Done) return ActResult.Fail("Партію зіграно, тисни «Ану ще раз»");
        if (seat < 0 || seat >= Seats || !_sides[seat].In || _sides[seat].Bot) return ActResult.Fail("Ти тут не граєш");
        // Час на розстановку перевіряємо і тут, а не лише в Tick: між тиками є проміжок, і за нього
        // ніхто не має права ані переставити кораблі, ані сказати «Готово» після дзвінка.
        if (_phase == Phase.Placing && _placeUntil is { } until && Ctx.Clock.UtcNow >= until) ForceReady();
        return action switch
        {
            "place" => Place(seat, payload),
            "clear" => Clear(seat),
            "random" => Scatter(seat),
            "ready" => Ready(seat),
            "shoot" => Shoot(seat, payload),
            "buy" => Buy(seat, payload),
            "sell" => Sell(seat, payload),
            "use" => Use(seat, payload),
            _ => ActResult.Fail("Тут так не ходять"),
        };
    }

    /// <summary>
    /// 😱 😂 🎯 на свіжий постріл: косметика, тож можна й тому, хто вже на дні (він же дивиться далі), і
    /// після кінця. Реакція чіпляється до останнього пострілу в стрічці й показується бульбашкою над полем того, хто реагує.
    /// </summary>
    ActResult React(int seat, JsonElement payload)
    {
        if (seat < 0 || seat >= Seats || !_sides[seat].In || _sides[seat].Bot) return ActResult.Fail("Ти тут не граєш");
        if (_phase is not (Phase.Battle or Phase.Done) || _feed.Count == 0) return ActResult.Fail("Ще нема на що реагувати");
        var e = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("e", out var x)
            && x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var n) ? n : -1;
        if (e is < 0 or > 2) return ActResult.Fail("Нема такої реакції");
        var now = Ctx.Clock.UtcNow;
        if (now - _rxAt[seat] < TimeSpan.FromMilliseconds(ReactGapMs)) return ActResult.Fail("Не так часто — хай усі розгледять");
        _rxAt[seat] = now;
        _rxE[seat] = e;
        _rxN[seat]++;
        _feed[^1].Rx[seat] = e;
        _dirty = true;
        return ActResult.Done;
    }

    ActResult Place(int seat, JsonElement payload)
    {
        if (_phase != Phase.Placing) return ActResult.Fail("Бій уже почався, кораблі не рухаються");
        if (_sides[seat].Ready) return ActResult.Fail("«Готово» вже сказано — чекаємо решту");
        if (ReadShips(payload) is not { } ships) return ActResult.Fail("Не зрозумів розстановку");
        if (BattleshipRules.Invalid(ships, _sea) is { } why) return ActResult.Fail(why);
        _sides[seat].Ships = ships;
        _dirty = true;
        return ActResult.Done;
    }

    /// <summary>
    /// «Скинути»: людина стерла свою розстановку в браузері — сервер має забути її разом з нею. Інакше
    /// таймер дочекався б кінця й повів у бій із флотом, який гравець уже вважає стертим.
    /// </summary>
    ActResult Clear(int seat)
    {
        if (_phase != Phase.Placing) return ActResult.Fail("Бій уже почався, кораблі не рухаються");
        if (_sides[seat].Ready) return ActResult.Fail("«Готово» вже сказано — чекаємо решту");
        if (_sides[seat].Ships.Count == 0) return ActResult.Done;
        _sides[seat].Ships = [];
        _dirty = true;
        return ActResult.Done;
    }

    ActResult Scatter(int seat)
    {
        if (_phase != Phase.Placing) return ActResult.Fail("Бій уже почався, кораблі не рухаються");
        if (_sides[seat].Ready) return ActResult.Fail("«Готово» вже сказано — чекаємо решту");
        _sides[seat].Ships = BattleshipRules.RandomFleet(Ctx.Rng, _sea);
        _dirty = true;
        return ActResult.Accept("Гоп — розставив за тебе");
    }

    ActResult Ready(int seat)
    {
        if (_phase != Phase.Placing) return ActResult.Fail("Бій уже почався");
        if (_sides[seat].Ready) return ActResult.Fail("«Готово» вже сказано — чекаємо решту");
        if (BattleshipRules.Invalid(_sides[seat].Ships, _sea) is { } why) return ActResult.Fail(why);
        _sides[seat].Ready = true;
        _dirty = true;
        if (Alive.All(s => _sides[s].Ready)) Battle();
        return ActResult.Done;
    }

    void Battle()
    {
        _phase = Phase.Battle;
        _turn = First();
        _turnUntil = Ctx.Clock.UtcNow.AddSeconds(_turnSeconds);
        _botAt = null;
        _dirty = true;
        DeployMines();
    }

    /// <summary>Перевірки, спільні для пострілу й штуки з арсеналу: чия черга і по кому.</summary>
    string? CheckTurn(int seat, JsonElement payload, out int at)
    {
        at = -1;
        if (_phase != Phase.Battle) return "Спершу розстав кораблі";
        if (_revenge is { } rv)
        {
            if (seat != rv.By) return $"Зачекай: {Nick(rv.By)} має останній постріл";
            if (ReadTarget(payload) is { } t && t != rv.On) return $"Помста — лише по {NickCases.Dative(Nick(rv.On))}";
            at = rv.On;
            return null;
        }
        if (_sides[seat].Out) return "Твій флот на дні — лишається дивитись";
        if (seat != _turn) return "Не так швидко — зараз не твій хід";
        if (ReadTarget(payload) is { } asked)
        {
            if (asked == seat) return "По своєму флоту не стріляють";
            if (asked < 0 || asked >= Seats || !_sides[asked].In) return "Там ні душі";
            if (_sides[asked].Out) return "Цей флот уже на дні";
            at = asked;
            return null;
        }
        // Удвох ціль одна, і старий клієнт (та й тести) шлють голе {cell}; у компанії треба сказати, по кому.
        var foes = Alive.Where(s => s != seat).ToArray();
        if (foes.Length != 1) return "Обери, по чиєму полю стріляти";
        at = foes[0];
        return null;
    }

    ActResult Shoot(int seat, JsonElement payload)
    {
        if (CheckTurn(seat, payload, out var at) is { } why) return ActResult.Fail(why);
        if (ReadCell(payload) is not { } cell || cell < 0 || cell >= _sea.Cells)
            return ActResult.Fail("Не зрозумів, куди стріляти");
        var foe = _sides[at];
        if (foe.Hits.Contains(cell) || foe.Misses.Contains(cell)) return ActResult.Fail("Сюди вже стріляли");
        return Fire(seat, at, cell, auto: false);
    }

    enum Blow { Miss, Hit, Sunk, Out, Mine }

    /// <summary>
    /// Одна клітинка під вогнем — без жодного слова про чергу: її ділять і звичайний постріл, і штуки з
    /// арсеналу, що б'ють кількома клітинками. <paramref name="size"/> — розмір потопленого корабля.
    /// </summary>
    Blow Strike(int by, int at, int cell, out int size, Shot? note = null)
    {
        size = 0;
        var me = _sides[by];
        var foe = _sides[at];
        if (TripMine(by, at, cell, note)) return Blow.Mine;
        if (foe.ShipAt(cell) is not { } ship)
        {
            foe.Misses.Add(cell);
            return Blow.Miss;
        }
        foe.Hits.Add(cell);
        Unpatch(at, cell);
        me.Scored++;
        if (!foe.Sunk(ship)) return Blow.Hit;
        me.Sank++;
        size = ship.Length;
        // Навколо потопленого корабля стояти нема чому — домальовуємо промахи самі, щоб не гадати руками.
        foreach (var near in BattleshipRules.Around(ship, _sea)) foe.Misses.Add(near);
        if (foe.Left > 0) return Blow.Sunk;
        // Флот цього поля скінчився: господар вибуває і далі тільки дивиться.
        foe.Out = true;
        _sunkOrder.Add(at);
        return Blow.Out;
    }

    static string ResOf(Blow b) => b switch
    {
        Blow.Miss => "miss", Blow.Hit => "hit", Blow.Sunk => "sunk", Blow.Out => "out", _ => "mine",
    };

    /// <summary>Постріл, який уже перевірено: і людський, і той, що гармата (чи бот) робить сама.</summary>
    ActResult Fire(int seat, int at, int cell, bool auto)
    {
        _sides[seat].Shots++;
        _dirty = true;
        var shot = new Shot(seat, at, cell, "miss", 0, auto) { Revenge = _revenge is not null };
        var blow = Strike(seat, at, cell, out var size, shot);
        shot.Res = ResOf(blow);
        shot.Size = size;
        Quip(shot);
        Note(shot);
        return AfterBlow(seat, at, blow, shot);
    }

    /// <summary>
    /// Що далі після пострілу чи штуки: чия черга, чи не скінчилась партія, чи не час для помсти. Тости —
    /// лише на потоплення й перемогу: промах і влучання видно на полі за чверть секунди з анімацією, а стос
    /// тостів на телефоні накривав своє поле (№63).
    /// </summary>
    ActResult AfterBlow(int seat, int at, Blow blow, Shot shot)
    {
        _turnUntil = Ctx.Clock.UtcNow.AddSeconds(_turnSeconds);
        _botAt = null;
        var alive = Alive.ToList();

        // Помста відбулась (хоч би чим скінчилась): хід вертається тому, хто потопив месника.
        if (shot.Revenge && _revenge is { } rv)
        {
            _revenge = null;
            if (alive.Count <= 1 || !alive.Any(s => !_sides[s].Bot)) return Win(Leader(alive), shot);
            _turn = _sides[rv.On].Alive ? rv.On : NextAlive(rv.On);
            return blow == Blow.Out ? ActResult.Accept($"Помста вдалась: флот {NickCases.Genitive(Nick(at))} на дні!")
                : blow == Blow.Sunk ? ActResult.Accept("Помста: корабель на дні!") : ActResult.Done;
        }

        if (alive.Count <= 1 || !alive.Any(s => !_sides[s].Bot)) return Win(Leader(alive), shot);

        if (blow is Blow.Miss or Blow.Mine || !_sides[seat].Alive)
        {
            _turn = NextAlive(seat);
            return blow == Blow.Mine ? ActResult.Accept("Бабах! Там була міна — рикошет прилетів тобі") : ActResult.Done;
        }
        if (blow == Blow.Out)
        {
            // Остання помста (№57): у компанії потоплений має один постріл по тому, хто його потопив.
            if (Players.Count() >= 3 && !_sides[at].Gone)
            {
                _revenge = (at, seat);
                return ActResult.Accept($"Є! Флот {NickCases.Genitive(Nick(at))} на дні — але за ним останній постріл по тобі");
            }
            return ActResult.Accept($"Є! Флот {NickCases.Genitive(Nick(at))} на дні — стріляй ще");
        }
        return blow == Blow.Sunk ? ActResult.Accept("Є! Корабель на дні — стріляй ще") : ActResult.Done;
    }

    /// <summary>Хто лишився головним: живий з найбільшим флотом (коли людей на плаву не лишилось — бот-переможець).</summary>
    int Leader(List<int> alive) =>
        alive.Count == 0 ? _turn : alive.OrderByDescending(s => _sides[s].Left).ThenBy(s => s).First();

    ActResult Win(int seat, Shot? shot)
    {
        WinCore(seat);
        var human = !_sides[seat].Bot && seat == shot?.By;
        return !human ? ActResult.Done
            : ActResult.Accept(Players.Count() > 2 ? "Є! Останній флот на плаву — твій!" : "Є! Флот суперника на дні — твоя взяла!");
    }

    /// <summary>Слово Глека на потоплення й вибування — з банку, щоб стрічка не була сухою (№60).</summary>
    void Quip(Shot shot)
    {
        if (shot.Res == "out") shot.Quip = BattleshipLines.ForOut(Ctx.Rng, Nick(shot.At));
        else if (shot.Res == "sunk") shot.Quip = BattleshipLines.ForSunk(Ctx.Rng, shot.Size);
    }

    void Note(Shot shot)
    {
        _feed.Add(shot);
        if (_feed.Count > 8) _feed.RemoveAt(0);
    }

    void WinCore(int seat)
    {
        _phase = Phase.Done;
        _winner = seat;
        _turnUntil = null;
        _revenge = null;
        SettleArsenal();
        var players = Players.ToArray();
        var scores = players.ToDictionary(s => s, s => (long)_sides[s].Sank);
        var log = Ledger();
        if (players.Length == 2)
        {
            var lost = players.First(s => s != seat);
            // Рахунок — потоплені кораблі: у переможця весь флот суперника, у суперника стільки, скільки встиг.
            Ctx.Finish([seat],
                $"{Info.Title}: {Nick(seat)} {SeatName(seat)} {_sides[seat].Sank}:{_sides[lost].Sank} "
                + $"{Nick(lost)} {SeatName(lost)}, пострілів {_sides[seat].Shots}{log}", scores);
            return;
        }
        var rest = Places().Skip(1).Select(s => $"{Nick(s)} {SeatName(s)}");
        Ctx.Finish([seat],
            $"{Info.Title}: останній флот на плаву — {Nick(seat)} {SeatName(seat)} (потоплено: {_sides[seat].Sank}); "
            + $"далі {string.Join(", ", rest)}{log}", scores);
    }

    /// <summary>Місця від першого до останнього: переможець, потім вибулі у зворотному порядку.</summary>
    IEnumerable<int> Places()
    {
        var alive = Alive.OrderByDescending(s => _sides[s].Left).ThenBy(s => s == _winner ? 0 : 1).ToList();
        if (_winner is { } w && alive.Remove(w)) alive.Insert(0, w);
        return alive.Concat(Enumerable.Reverse(_sunkOrder));
    }

    /// <summary>Розстановка так, як її шле модуль: <c>{ ships: [{ cells: [...] }] }</c>; голий масив теж приймаємо.</summary>
    static List<int[]>? ReadShips(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;
        if (!payload.TryGetProperty("ships", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        var ships = new List<int[]>();
        foreach (var item in list.EnumerateArray())
        {
            JsonElement cells;
            if (item.ValueKind == JsonValueKind.Array) cells = item;
            else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("cells", out var inner)) cells = inner;
            else return null;
            if (cells.ValueKind != JsonValueKind.Array) return null;

            var ship = new List<int>();
            foreach (var n in cells.EnumerateArray())
            {
                if (n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out var v)) return null;
                ship.Add(v);
            }
            ships.Add([.. ship]);
        }
        return ships;
    }

    /// <summary>Постріл приймаємо і як <c>{cell:34}</c>, і як голе число — тим самим ключем, що й решта наших ігор.</summary>
    static int? ReadCell(JsonElement payload) => payload.ValueKind switch
    {
        JsonValueKind.Number when payload.TryGetInt32(out var n) => n,
        JsonValueKind.Object when payload.TryGetProperty("cell", out var c)
            && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) => n,
        _ => null,
    };

    /// <summary>По чийому полю: <c>{cell, at}</c>. Нема — значить, гравець певен, що ціль одна.</summary>
    static int? ReadTarget(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("at", out var a)
            && a.ValueKind == JsonValueKind.Number && a.TryGetInt32(out var n) ? n : null;

    // ---------- годинники й боти ----------

    public override TickResult Tick()
    {
        var now = Ctx.Clock.UtcNow;
        if (_phase == Phase.Placing && _placeUntil is { } until && now >= until)
        {
            ForceReady();
            _dirty = false;
            return TickResult.Both;
        }
        if (_phase == Phase.Battle)
        {
            if (_sides[Shooter].Bot)
            {
                // Бот «думає» секунду: миттєвий постріл читався б як глюк, а людям треба встигнути побачити попередній.
                if (_botAt is null) _botAt = now.AddMilliseconds(BattleshipBot.ThinkMs);
                else if (now >= _botAt) BotMove();
            }
            else if (_turnUntil is { } turnUntil && now >= turnUntil) AutoShot();
        }
        if (_dirty)
        {
            _dirty = false;
            _sentLeft = PlaceLeft();
            return TickResult.Both;
        }
        // У розстановці кадр іде раз на секунду — у ньому відлік і те, хто вже готовий.
        if (_phase == Phase.Placing && PlaceLeft() != _sentLeft)
        {
            _sentLeft = PlaceLeft();
            return TickResult.FrameOnly;
        }
        return TickResult.None;
    }

    int? PlaceLeft() => _phase == Phase.Placing && _placeUntil is { } until
        ? Math.Max(0, (int)Math.Ceiling((until - Ctx.Clock.UtcNow).TotalSeconds))
        : null;

    /// <summary>Недобиті влучання на полі — те, що бачить кожен за столом.</summary>
    List<int> WoundedOf(int at) => BattleshipBot.Wounded(_sides[at].Hits, _sides[at].Ships.Where(_sides[at].Sunk));

    /// <summary>
    /// Хід бота: спершу добиває підбите (на будь-якому полі), інакше — випадковий суперник і шаховий візерунок.
    /// В Арсеналі інколи пускає в хід свою штуку. У помсту — лише по кривднику.
    /// </summary>
    void BotMove()
    {
        _botAt = null;
        var seat = Shooter;
        int at;
        if (_revenge is { } rv) at = rv.On;
        else
        {
            var foes = Alive.Where(s => s != seat).ToList();
            if (foes.Count == 0) return;
            var hurt = foes.Where(s => WoundedOf(s).Count > 0).ToList();
            at = hurt.Count > 0 ? hurt[Ctx.Rng.Next(hurt.Count)] : foes[Ctx.Rng.Next(foes.Count)];
            if (hurt.Count == 0 && BotTool(seat, at)) return;
        }
        var foe = _sides[at];
        var cell = BattleshipBot.Aim(Ctx.Rng, _sea, foe.Hits, foe.Misses, foe.Ships.Where(foe.Sunk));
        if (cell >= 0) Fire(seat, at, cell, auto: false);
    }

    /// <summary>
    /// Хід простояв. Гармата стріляє сама — навмання, по випадковому живому полю: чекати без кінця нечесно
    /// щодо решти столу, а пропуск ходу був би подарунком тому, хто задумався. Простояна помста — по кривднику.
    /// </summary>
    void AutoShot()
    {
        var seat = Shooter;
        var foes = _revenge is { } rv ? [rv.On] : Alive.Where(s => s != seat).ToArray();
        if (foes.Length == 0) return;
        var at = foes[Ctx.Rng.Next(foes.Length)];
        var foe = _sides[at];
        var free = Enumerable.Range(0, _sea.Cells).Where(c => !foe.Hits.Contains(c) && !foe.Misses.Contains(c)).ToArray();
        if (free.Length == 0) return;
        Fire(seat, at, free[Ctx.Rng.Next(free.Length)], auto: true);
    }

    /// <summary>
    /// Час вийшов. Хто нічого не поставив — отримує випадкову розстановку; хто встиг розставити, але не
    /// натиснув «Готово», іде в бій зі своєю: викидати чужу працю через невчасний клік нечесно.
    /// </summary>
    void ForceReady()
    {
        var late = new List<string>();
        foreach (var seat in Alive)
        {
            var side = _sides[seat];
            if (side.Ready) continue;
            if (BattleshipRules.Invalid(side.Ships, _sea) is not null) side.Ships = BattleshipRules.RandomFleet(Ctx.Rng, _sea);
            side.Ready = true;
            late.Add(Nick(seat));
        }
        if (late.Count > 0) Ctx.Log($"{Info.Title}: час на розстановку вийшов — {Join(late)} у бій як є");
        Battle();   // фаза змінилась — види мусять піти, хоч би хто нас покликав: тик чи Act
    }

    static string Join(List<string> names) =>
        names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " і " + names[^1];

    /// <summary>
    /// Хтось устав. Удвох — це техпоразка, як і було. У компанії партія не ламається: флот того, хто пішов,
    /// іде на дно, решта грають далі; кінець — лише коли на плаву лишився один (або самі Глеки).
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (seat < 0 || seat >= Seats || _phase is Phase.Lobby or Phase.Done) return;
        if (!_sides[seat].Alive)
        {
            // Вибулий устав: партії це не чіпає, лише його помста (якщо ще не відбулась) згорає.
            if (_revenge is { } gone && gone.By == seat) { _revenge = null; _turn = _sides[gone.On].Alive ? gone.On : NextAlive(gone.On); _dirty = true; }
            _sides[seat].Gone = true;
            return;
        }
        var rest = Alive.Where(s => s != seat).ToArray();
        _sides[seat].Out = true;
        _sides[seat].Gone = true;
        _sunkOrder.Add(seat);
        _dirty = true;
        if (rest.Length >= 2 && rest.Any(s => !_sides[s].Bot))
        {
            Ctx.Log($"{Info.Title}: {Nick(seat)} встає з-за столу, флот іде на дно — решта б'ються далі");
            Note(new Shot(seat, seat, -1, "left", 0, false));
            if (_revenge is { } rv && rv.On == seat) { _revenge = null; _turn = NextAlive(seat); }
            if (_phase == Phase.Placing && rest.All(s => _sides[s].Ready)) Battle();
            else if (_phase == Phase.Battle && _turn == seat && _revenge is null)
            {
                _turn = NextAlive(seat);
                _turnUntil = Ctx.Clock.UtcNow.AddSeconds(_turnSeconds);
                _botAt = null;
            }
            return;
        }
        _phase = Phase.Done;
        _winner = rest.Length == 0 ? null : rest.Length == 1 ? rest[0] : Leader([.. rest]);
        _turnUntil = null;
        _revenge = null;
        // Техперемога платить бонус переможця, лише коли флот утікача вже наполовину на дні. Інакше «сядь другим
        // ніком, стрельни шість разів і встань» друкувало б по 50 шелягів за пів хвилини; база за бій — лишається.
        var fled = _sides[seat];
        SettleArsenal(fullWin: fled.Ships.Count(fled.Sunk) * 2 >= fled.Ships.Count);
        Ctx.Finish(_winner is { } w ? [w] : [], $"{Info.Title}: {Nick(seat)} встає з-за столу, партію не дограли");
    }

    // ---------- види ----------

    string PhaseName => _phase switch
    {
        Phase.Lobby => "lobby", Phase.Placing => "placing", Phase.Battle => "battle", _ => "done",
    };

    /// <summary>Моє поле: кораблі тут є, і саме тому цей шматок нікому, крім господаря, не дістається.</summary>
    object Own(int seat) => new
    {
        ships = _sides[seat].Ships.Select(s => s.Order().ToArray()).ToArray(),
        ready = _sides[seat].Ready,
        hits = _sides[seat].Hits.Order().ToArray(),
        misses = _sides[seat].Misses.Order().ToArray(),
        mines = MinesOf(seat),
    };

    /// <summary>
    /// Скільки кораблів поля ще на плаву. У розстановці — завжди весь флот: інакше з лічильника чужого
    /// поля читалось би, чи суперник уже розставився (а на початку там стояв би відверто брехливий нуль).
    /// </summary>
    int LeftOf(int seat) => _phase is Phase.Placing or Phase.Lobby ? _sea.Fleet.Length : _sides[seat].Left;

    /// <summary>
    /// Публічне знання про поле <paramref name="of"/>: куди по ньому влучили, де промахнулись, що вже
    /// потоплено. Кораблі цілком — лише після кінця партії: тоді вже можна подивитись, де ховався чотирипалубний.
    /// </summary>
    object Known(int of)
    {
        var side = _sides[of];
        return new
        {
            hits = side.Hits.Order().ToArray(),
            misses = side.Misses.Order().ToArray(),
            sunk = side.Ships.Where(side.Sunk).Select(s => s.Order().ToArray()).ToArray(),
            ready = side.Ready,
            left = LeftOf(of),
            @out = side.Out,
            bot = side.Bot ? side.Name : null,
            reveal = _phase == Phase.Done ? side.Ships.Select(s => s.Order().ToArray()).ToArray() : null,
            boom = BoomOf(of),
            patched = PatchedOf(of),
        };
    }

    public override object View(int? seat)
    {
        var mine = seat is >= 0 and < Seats && _sides[seat.Value].In && !_sides[seat.Value].Bot ? seat.Value : -1;
        var boards = Enumerable.Range(0, Seats).Select(s => _sides[s].In ? Known(s) : null).ToArray();
        // «Чуже поле» старого виду на двох: суперник. У компанії клієнт бере boards, а тут — перший живий чужий.
        var rival = Players.Where(s => s != mine).OrderByDescending(s => _sides[s].Alive).FirstOrDefault(-1);
        if (rival < 0) rival = mine == 1 ? 0 : 1;
        return new
        {
            phase = PhaseName,
            turn = _phase == Phase.Battle ? Shooter : (int?)null,
            revenge = _phase == Phase.Battle && _revenge is { } rv ? new { by = rv.By, on = rv.On } : null,
            placeUntil = _phase == Phase.Placing ? _placeUntil?.ToString("o") : null,
            turnUntil = _phase == Phase.Battle ? _turnUntil?.ToString("o") : null,
            placeSeconds = _sea.PlaceSeconds,
            turnSeconds = _turnSeconds,
            sea = new { key = _sea.Key, w = _sea.W, h = _sea.H, fleet = _sea.Fleet },
            players = Players.ToArray(),
            bots = _bots,
            me = mine >= 0 ? Own(mine) : null,
            enemy = boards[rival] ?? Known(rival),
            boards,
            feed = _feed.Select(f => new
            {
                by = f.By, at = f.At, cell = f.Cell, res = f.Res, size = f.Size, auto = f.Auto,
                revenge = f.Revenge, quip = f.Quip, rx = f.Rx.Any(x => x >= 0) ? (int[])f.Rx.Clone() : null,
                // Число радара — лише тому, хто його пустив: глядач (чи телевізор) поруч із гравцем — теж підказка.
                tool = f.Tool, cells = f.Cells, n = f.Tool == "radar" && mine >= 0 && mine == f.By ? f.N : null, boom = f.Boom,
            }).ToArray(),
            react = (int[])_rxE.Clone(),
            reactN = (int[])_rxN.Clone(),
            arsenal = ArsenalView(mine),
            shots = _sides.Sum(s => s.Shots),
            result = _winner is { } w
                ? new
                {
                    winner = w,
                    shots = _sides.Select(s => s.Shots).ToArray(),
                    hits = _sides.Select(s => s.Scored).ToArray(),
                    sank = _sides.Select(s => s.Sank).ToArray(),
                    places = Places().ToArray(),
                }
                : null,
        };
    }

    /// <summary>Кадр — тільки відлік і лічильники: жодного корабля тут бути не може, бо він летить усім одразу.</summary>
    public override object? Frame() => new
    {
        phase = PhaseName,
        turn = _phase == Phase.Battle ? Shooter : (int?)null,
        placeLeft = PlaceLeft(),
        ready = _sides.Select(s => s.Ready).ToArray(),
        left = Enumerable.Range(0, Seats).Select(LeftOf).ToArray(),
        shots = _sides.Sum(s => s.Shots),
        winner = _winner,
    };

    // ---------- збереження ----------

    sealed record SideSnap(int[][] Ships, bool Ready, int[] Hits, int[] Misses, int Shots,
        bool In = true, bool Out = false, int Scored = 0, int Sank = 0, bool Bot = false, string? Name = null, bool Gone = false);
    sealed record ShotSnap(int By, int At, int Cell, string Res, int Size, bool Auto,
        bool Revenge = false, string? Quip = null, string? Tool = null, int[]? Cells = null, int? N = null, string? Boom = null);
    sealed record Snap(string Phase, int Turn, int? Winner, DateTimeOffset? PlaceUntil, SideSnap[] Sides,
        string? Sea = null, DateTimeOffset? TurnUntil = null, int[]? SunkOrder = null, ShotSnap[]? Feed = null,
        int TurnSeconds = BattleshipRules.TurnSeconds, int Bots = 0, int[]? Revenge = null, string? Arsenal = null);

    public override string? Save() => JsonSerializer.Serialize(new Snap(
        PhaseName, _turn, _winner, _placeUntil,
        [.. _sides.Select(s => new SideSnap(
            [.. s.Ships], s.Ready, [.. s.Hits.Order()], [.. s.Misses.Order()], s.Shots, s.In, s.Out, s.Scored, s.Sank,
            s.Bot, s.Bot ? s.Name : null, s.Gone))],
        _sea.Key, _turnUntil, [.. _sunkOrder],
        [.. _feed.Select(f => new ShotSnap(f.By, f.At, f.Cell, f.Res, f.Size, f.Auto, f.Revenge, f.Quip, f.Tool, f.Cells, f.N, f.Boom))],
        _turnSeconds, _bots, _revenge is { } rv ? [rv.By, rv.On] : null, SaveArsenal()));

    public override void Load(string json)
    {
        if (JsonSerializer.Deserialize<Snap>(json) is not { Sides.Length: >= 2 } snap) return;
        _phase = snap.Phase switch
        {
            "battle" => Phase.Battle, "done" => Phase.Done, "lobby" => Phase.Lobby, _ => Phase.Placing,
        };
        _sea = BattleshipRules.Seas.FirstOrDefault(s => s.Key == snap.Sea) ?? BattleshipRules.Classic;
        _turn = snap.Turn;
        _winner = snap.Winner;
        _placeUntil = snap.PlaceUntil;
        _turnUntil = snap.TurnUntil;
        _turnSeconds = snap.TurnSeconds;
        _bots = snap.Bots;
        _revenge = snap.Revenge is [var by, var on] ? (by, on) : null;
        _botAt = null;
        _sides = Fresh();
        // Старий знімок (до компанії) мав рівно два поля і не знав про In — там сиділи обидва.
        for (var seat = 0; seat < Math.Min(Seats, snap.Sides.Length); seat++)
        {
            var from = snap.Sides[seat];
            var side = _sides[seat];
            side.In = from.In;
            side.Out = from.Out;
            side.Gone = from.Gone;
            side.Bot = from.Bot;
            side.Name = from.Name ?? "";
            side.Ships = [.. from.Ships];
            side.Ready = from.Ready;
            side.Shots = from.Shots;
            side.Scored = from.Scored;
            side.Sank = from.Sank;
            foreach (var c in from.Hits) side.Hits.Add(c);
            foreach (var c in from.Misses) side.Misses.Add(c);
        }
        _sunkOrder.Clear();
        _sunkOrder.AddRange(snap.SunkOrder ?? []);
        _feed.Clear();
        foreach (var f in snap.Feed ?? [])
            _feed.Add(new Shot(f.By, f.At, f.Cell, f.Res, f.Size, f.Auto)
            { Revenge = f.Revenge, Quip = f.Quip, Tool = f.Tool, Cells = f.Cells, N = f.N, Boom = f.Boom });
        LoadArsenal(snap.Arsenal);
        _dirty = false;
    }
}
