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
/// дивиться, останній на плаву виграв. Тик потрібен не для руху, а для годинників (розстановка й хід):
/// інакше той, хто пішов пити чай, лишив би стіл висіти назавжди.
/// </summary>
public sealed class Battleship : Game
{
    public const int Seats = 4;

    /// <summary>Тик — чверть секунди: види після пострілу летять із тика (див. spec), і секунда затримки була помітна оком.</summary>
    public const int TickMillis = 250;

    enum Phase { Lobby, Placing, Battle, Done }

    /// <summary>Одне поле: чий флот, чи готовий господар, куди по ньому вже стріляли і як він сам стріляв.</summary>
    sealed class Side
    {
        /// <summary>Сидів за столом на старті партії — тобто має поле.</summary>
        public bool In { get; set; }
        /// <summary>Вибув: флот на дні або встав з-за столу.</summary>
        public bool Out { get; set; }
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
        public int Left => Ships.Count(s => !Sunk(s));
        public bool Alive => In && !Out;
    }

    /// <summary>Один постріл для стрічки подій: хто, по кому, куди і що вийшло.</summary>
    sealed record Shot(int By, int At, int Cell, string Res, int Size, bool Auto);

    static readonly string[] Names = ["синій", "червоний", "зелений", "жовтий"];

    public override GameInfo Info { get; } = new(
        "battleship", "Морський бій", "морський бій", GameGroup.Board, 2, Seats,
        TickMs: TickMillis, Start: StartMode.ByHost, Hidden: true,
        Options: [new GameOption("fleet", "Море",
            [.. BattleshipRules.Seas.Select(s => (s.Key, s.Label))], BattleshipRules.Classic.Key)],
        Hint: "Розстав кораблі, стріляй по чужих полях. Влучив — стріляєш ще. Удвох або кожен проти кожного на трьох-чотирьох");

    BattleshipSea _sea = BattleshipRules.Classic;
    Side[] _sides = Fresh();
    Phase _phase = Phase.Lobby;
    int _turn;
    /// <summary>Коли скінчиться розстановка; null — партія ще не почалась (стіл у лобі).</summary>
    DateTimeOffset? _placeUntil;
    /// <summary>Коли гармата того, чий хід, вистрілить сама.</summary>
    DateTimeOffset? _turnUntil;
    int? _winner;
    /// <summary>Хто вибув, по порядку: перший тут — останнє місце.</summary>
    readonly List<int> _sunkOrder = [];
    readonly List<Shot> _feed = [];
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
    }

    public override void Start()
    {
        _sides = Fresh();
        for (var s = 0; s < Seats; s++) _sides[s].In = Ctx.Seated(s);
        _phase = Phase.Placing;
        _turn = First();
        _winner = null;
        _sunkOrder.Clear();
        _feed.Clear();
        _placeUntil = Ctx.Clock.UtcNow.AddSeconds(_sea.PlaceSeconds);
        _turnUntil = null;
        _dirty = false;
        _sentLeft = null;
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

    string Nick(int seat) => Ctx.NickOf(seat) ?? SeatName(seat);

    // ---------- ходи ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (_phase == Phase.Done) return ActResult.Fail("Партію зіграно, тисни «Ще раз»");
        if (seat < 0 || seat >= Seats || !_sides[seat].In) return ActResult.Fail("Ти тут не граєш");
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
            _ => ActResult.Fail("Тут так не ходять"),
        };
    }

    ActResult Place(int seat, JsonElement payload)
    {
        if (_phase != Phase.Placing) return ActResult.Fail("Бій уже почався, кораблі не рухаються");
        if (_sides[seat].Ready) return ActResult.Fail("Ти вже сказав «Готово»");
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
        if (_sides[seat].Ready) return ActResult.Fail("Ти вже сказав «Готово»");
        if (_sides[seat].Ships.Count == 0) return ActResult.Done;
        _sides[seat].Ships = [];
        _dirty = true;
        return ActResult.Done;
    }

    ActResult Scatter(int seat)
    {
        if (_phase != Phase.Placing) return ActResult.Fail("Бій уже почався, кораблі не рухаються");
        if (_sides[seat].Ready) return ActResult.Fail("Ти вже сказав «Готово»");
        _sides[seat].Ships = BattleshipRules.RandomFleet(Ctx.Rng, _sea);
        _dirty = true;
        return ActResult.Accept("Розставив за тебе");
    }

    ActResult Ready(int seat)
    {
        if (_phase != Phase.Placing) return ActResult.Fail("Бій уже почався");
        if (_sides[seat].Ready) return ActResult.Fail("Ти вже сказав «Готово»");
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
        _turnUntil = Ctx.Clock.UtcNow.AddSeconds(BattleshipRules.TurnSeconds);
        _dirty = true;
    }

    ActResult Shoot(int seat, JsonElement payload)
    {
        if (_phase != Phase.Battle) return ActResult.Fail("Спершу розстав кораблі");
        if (_sides[seat].Out) return ActResult.Fail("Твій флот на дні — лишається дивитись");
        if (seat != _turn) return ActResult.Fail("Зараз не твій хід");
        if (ReadCell(payload) is not { } cell || cell < 0 || cell >= _sea.Cells)
            return ActResult.Fail("Не зрозумів, куди стріляти");

        int at;
        if (ReadTarget(payload) is { } asked)
        {
            if (asked == seat) return ActResult.Fail("По своєму флоту не стріляють");
            if (asked < 0 || asked >= Seats || !_sides[asked].In) return ActResult.Fail("Там нікого нема");
            if (_sides[asked].Out) return ActResult.Fail("Цей флот уже на дні");
            at = asked;
        }
        else
        {
            // Удвох ціль одна, і старий клієнт (та й тести) шлють голе {cell}; у компанії треба сказати, по кому.
            var foes = Alive.Where(s => s != seat).ToArray();
            if (foes.Length != 1) return ActResult.Fail("Обери, по чиєму полю стріляти");
            at = foes[0];
        }

        var foe = _sides[at];
        if (foe.Hits.Contains(cell) || foe.Misses.Contains(cell)) return ActResult.Fail("Сюди вже стріляли");
        return Fire(seat, at, cell, auto: false);
    }

    /// <summary>Постріл, який уже перевірено: і людський, і той, що гармата робить сама по годиннику.</summary>
    ActResult Fire(int seat, int at, int cell, bool auto)
    {
        var me = _sides[seat];
        var foe = _sides[at];
        me.Shots++;
        _dirty = true;
        _turnUntil = Ctx.Clock.UtcNow.AddSeconds(BattleshipRules.TurnSeconds);

        if (foe.Ships.FirstOrDefault(s => s.Contains(cell)) is not { } ship)
        {
            foe.Misses.Add(cell);
            Note(new Shot(seat, at, cell, "miss", 0, auto));
            _turn = NextAlive(seat);
            return ActResult.Accept("Мимо");
        }

        foe.Hits.Add(cell);
        me.Scored++;
        if (!foe.Sunk(ship))
        {
            Note(new Shot(seat, at, cell, "hit", 0, auto));
            return ActResult.Accept("Влучив! Стріляй ще");
        }

        me.Sank++;
        // Навколо потопленого корабля стояти нема чому — домальовуємо промахи самі, щоб не гадати руками.
        foreach (var near in BattleshipRules.Around(ship, _sea)) foe.Misses.Add(near);
        if (foe.Left > 0)
        {
            Note(new Shot(seat, at, cell, "sunk", ship.Length, auto));
            return ActResult.Accept("Потопив! Стріляй ще");
        }

        // Флот цього поля скінчився: господар вибуває і далі тільки дивиться.
        foe.Out = true;
        _sunkOrder.Add(at);
        Note(new Shot(seat, at, cell, "out", ship.Length, auto));
        if (Alive.Count() > 1) return ActResult.Accept($"Флот {Nick(at)} на дні! Стріляй ще");

        Win(seat);
        return ActResult.Accept(Players.Count() > 2 ? "Останній флот на плаву — твій!" : "Флот суперника на дні!");
    }

    void Note(Shot shot)
    {
        _feed.Add(shot);
        if (_feed.Count > 8) _feed.RemoveAt(0);
    }

    void Win(int seat)
    {
        _phase = Phase.Done;
        _winner = seat;
        _turnUntil = null;
        var players = Players.ToArray();
        var scores = players.ToDictionary(s => s, s => (long)_sides[s].Sank);
        if (players.Length == 2)
        {
            var lost = players.First(s => s != seat);
            // Рахунок — потоплені кораблі: у переможця весь флот суперника, у суперника стільки, скільки встиг.
            Ctx.Finish([seat],
                $"{Info.Title}: {Ctx.NickOf(seat)} {SeatName(seat)} {_sides[seat].Sank}:{_sides[lost].Sank} "
                + $"{Ctx.NickOf(lost)} {SeatName(lost)}, пострілів {_sides[seat].Shots}", scores);
            return;
        }
        var rest = Places().Skip(1).Select(s => $"{Nick(s)} {SeatName(s)}");
        Ctx.Finish([seat],
            $"{Info.Title}: останній флот на плаву — {Ctx.NickOf(seat)} {SeatName(seat)} (потопив {_sides[seat].Sank}); "
            + $"далі {string.Join(", ", rest)}", scores);
    }

    /// <summary>Місця від першого до останнього: переможець, потім вибулі у зворотному порядку.</summary>
    IEnumerable<int> Places()
    {
        var alive = Alive.OrderByDescending(s => _sides[s].Left).ToList();
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

    // ---------- годинники ----------

    public override TickResult Tick()
    {
        var now = Ctx.Clock.UtcNow;
        if (_phase == Phase.Placing && _placeUntil is { } until && now >= until)
        {
            ForceReady();
            _dirty = false;
            return TickResult.Both;
        }
        if (_phase == Phase.Battle && _turnUntil is { } turnUntil && now >= turnUntil) AutoShot();
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

    /// <summary>
    /// Хід простояв. Гармата стріляє сама — навмання, по випадковому живому полю: чекати без кінця нечесно
    /// щодо решти столу, а пропуск ходу був би подарунком тому, хто задумався.
    /// </summary>
    void AutoShot()
    {
        var seat = _turn;
        var foes = Alive.Where(s => s != seat).ToArray();
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
    /// іде на дно, решта грають далі; кінець — лише коли на плаву лишився один.
    /// </summary>
    public override void OnLeave(int seat)
    {
        if (seat < 0 || seat >= Seats || !_sides[seat].Alive || _phase is Phase.Lobby or Phase.Done) return;
        var rest = Alive.Where(s => s != seat).ToArray();
        _sides[seat].Out = true;
        _sunkOrder.Add(seat);
        _dirty = true;
        if (rest.Length >= 2)
        {
            Ctx.Log($"{Info.Title}: {Ctx.NickOf(seat)} встав з-за столу, його флот пішов на дно — решта б'ються далі");
            Note(new Shot(seat, seat, -1, "left", 0, false));
            if (_phase == Phase.Placing && rest.All(s => _sides[s].Ready)) Battle();
            else if (_phase == Phase.Battle && _turn == seat)
            {
                _turn = NextAlive(seat);
                _turnUntil = Ctx.Clock.UtcNow.AddSeconds(BattleshipRules.TurnSeconds);
            }
            return;
        }
        _phase = Phase.Done;
        _winner = rest.Length == 1 ? rest[0] : null;
        _turnUntil = null;
        Ctx.Finish(rest, $"{Info.Title}: {Ctx.NickOf(seat)} встав з-за столу, партію не дограли");
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
            reveal = _phase == Phase.Done ? side.Ships.Select(s => s.Order().ToArray()).ToArray() : null,
        };
    }

    public override object View(int? seat)
    {
        var mine = seat is >= 0 and < Seats && _sides[seat.Value].In ? seat.Value : -1;
        var boards = Enumerable.Range(0, Seats).Select(s => _sides[s].In ? Known(s) : null).ToArray();
        // «Чуже поле» старого виду на двох: суперник. У компанії клієнт бере boards, а тут — перший живий чужий.
        var rival = Players.Where(s => s != mine).OrderByDescending(s => _sides[s].Alive).FirstOrDefault(-1);
        if (rival < 0) rival = mine == 1 ? 0 : 1;
        return new
        {
            phase = PhaseName,
            turn = _phase == Phase.Battle ? _turn : (int?)null,
            placeUntil = _phase == Phase.Placing ? _placeUntil?.ToString("o") : null,
            turnUntil = _phase == Phase.Battle ? _turnUntil?.ToString("o") : null,
            placeSeconds = _sea.PlaceSeconds,
            turnSeconds = BattleshipRules.TurnSeconds,
            sea = new { key = _sea.Key, w = _sea.W, h = _sea.H, fleet = _sea.Fleet },
            players = Players.ToArray(),
            me = mine >= 0 ? Own(mine) : null,
            enemy = boards[rival] ?? Known(rival),
            boards,
            feed = _feed.Select(f => new { by = f.By, at = f.At, cell = f.Cell, res = f.Res, size = f.Size, auto = f.Auto }).ToArray(),
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
        turn = _phase == Phase.Battle ? _turn : (int?)null,
        placeLeft = PlaceLeft(),
        ready = _sides.Select(s => s.Ready).ToArray(),
        left = Enumerable.Range(0, Seats).Select(LeftOf).ToArray(),
        shots = _sides.Sum(s => s.Shots),
        winner = _winner,
    };

    // ---------- збереження ----------

    sealed record SideSnap(int[][] Ships, bool Ready, int[] Hits, int[] Misses, int Shots,
        bool In = true, bool Out = false, int Scored = 0, int Sank = 0);
    sealed record ShotSnap(int By, int At, int Cell, string Res, int Size, bool Auto);
    sealed record Snap(string Phase, int Turn, int? Winner, DateTimeOffset? PlaceUntil, SideSnap[] Sides,
        string? Sea = null, DateTimeOffset? TurnUntil = null, int[]? SunkOrder = null, ShotSnap[]? Feed = null);

    public override string? Save() => JsonSerializer.Serialize(new Snap(
        PhaseName, _turn, _winner, _placeUntil,
        [.. _sides.Select(s => new SideSnap(
            [.. s.Ships], s.Ready, [.. s.Hits.Order()], [.. s.Misses.Order()], s.Shots, s.In, s.Out, s.Scored, s.Sank))],
        _sea.Key, _turnUntil, [.. _sunkOrder],
        [.. _feed.Select(f => new ShotSnap(f.By, f.At, f.Cell, f.Res, f.Size, f.Auto))]));

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
        _sides = Fresh();
        // Старий знімок (до компанії) мав рівно два поля і не знав про In — там сиділи обидва.
        for (var seat = 0; seat < Math.Min(Seats, snap.Sides.Length); seat++)
        {
            var from = snap.Sides[seat];
            var side = _sides[seat];
            side.In = from.In;
            side.Out = from.Out;
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
        foreach (var f in snap.Feed ?? []) _feed.Add(new Shot(f.By, f.At, f.Cell, f.Res, f.Size, f.Auto));
        _dirty = false;
    }
}
