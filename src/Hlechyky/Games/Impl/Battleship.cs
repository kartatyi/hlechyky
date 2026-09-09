using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Правила флоту без жодного слова про кімнати: розміри поля, склад ескадри, перевірка розстановки й
/// випадкова розстановка. Окремо від гри, бо саме це найлегше зламати і найлегше перевірити тестом.
/// Клітинки скрізь — один індекс 0..99 (рядок × 10 + колонка), як на дроті.
/// </summary>
public static class BattleshipRules
{
    public const int W = 10, H = 10;
    public const int Cells = W * H;
    /// <summary>Скільки часу дано на розстановку, поки не почнеться бій.</summary>
    public const int PlaceSeconds = 120;

    /// <summary>Класика: один чотирипалубний, два трипалубні, три двопалубні, чотири однопалубні.</summary>
    public static readonly int[] Fleet = [4, 3, 3, 2, 2, 2, 1, 1, 1, 1];

    /// <summary>Скільки влучань топить увесь флот — сума <see cref="Fleet"/>.</summary>
    public const int Decks = 20;

    /// <summary>Вісім сусідів клітинки, які не вилізли за поле. Кораблі не торкаються навіть кутами, тож саме ця околиця й вирішує.</summary>
    public static IEnumerable<int> Halo(int cell)
    {
        var (x, y) = (cell % W, cell / W);
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var (nx, ny) = (x + dx, y + dy);
                if (nx >= 0 && nx < W && ny >= 0 && ny < H) yield return ny * W + nx;
            }
    }

    /// <summary>Околиця цілого корабля без нього самого: після потоплення вся вона — гарантований промах.</summary>
    public static IEnumerable<int> Around(int[] ship) =>
        ship.SelectMany(Halo).Distinct().Where(c => !ship.Contains(c));

    /// <summary>Чому ця розстановка не годиться (людською мовою), або null, якщо все гаразд.</summary>
    public static string? Invalid(IReadOnlyList<int[]>? ships)
    {
        if (ships is null || ships.Count != Fleet.Length) return "Кораблів має бути рівно десять";

        // owner[клітинка] — чий це корабель; заразом ловить і накладання
        var owner = new int[Cells];
        Array.Fill(owner, -1);
        for (var i = 0; i < ships.Count; i++)
        {
            var cells = ships[i];
            if (cells is null || cells.Length is 0 or > 4) return "Корабель буває від однієї до чотирьох клітинок";
            if (cells.Any(c => c < 0 || c >= Cells)) return "Корабель виліз за межі поля";
            if (cells.Distinct().Count() != cells.Length) return "Корабель стоїть сам на собі";
            if (!Straight(cells)) return "Корабель має бути прямий";
            foreach (var c in cells)
            {
                if (owner[c] >= 0) return "Кораблі налазять один на одного";
                owner[c] = i;
            }
        }

        if (!ships.Select(s => s.Length).OrderDescending().SequenceEqual(Fleet))
            return "Флот не той: один на чотири, два на три, три на два і чотири на одну клітинку";

        for (var i = 0; i < ships.Count; i++)
            foreach (var cell in ships[i])
                foreach (var near in Halo(cell))
                    if (owner[near] >= 0 && owner[near] != i) return "Кораблі не можуть торкатись навіть кутами";

        return null;
    }

    /// <summary>Корабель — це рівний відрізок по горизонталі або по вертикалі, без дірок.</summary>
    static bool Straight(int[] cells)
    {
        if (cells.Length == 1) return true;
        var sorted = cells.Order().ToArray();
        if (sorted.All(c => c / W == sorted[0] / W))
            return sorted.Zip(sorted.Skip(1)).All(p => p.Second == p.First + 1);
        if (sorted.All(c => c % W == sorted[0] % W))
            return sorted.Zip(sorted.Skip(1)).All(p => p.Second == p.First + W);
        return false;
    }

    /// <summary>
    /// Випадкова розстановка з переданого генератора — і тільки з нього, інакше партія не відтвориться.
    /// Спроби обмежені: на 10×10 флот лягає з першого-другого заходу, але гру не можна лишати в циклі,
    /// тому в найгіршому разі повертаємо готову ручну розстановку.
    /// </summary>
    public static List<int[]> RandomFleet(Random rng)
    {
        for (var attempt = 0; attempt < 200; attempt++)
            if (TryFleet(rng) is { } fleet) return fleet;
        return Canonical();
    }

    static List<int[]>? TryFleet(Random rng)
    {
        // busy — клітинки кораблів разом з околицями: так дотик кутом просто не трапляється
        var busy = new bool[Cells];
        var ships = new List<int[]>(Fleet.Length);
        foreach (var size in Fleet)
        {
            var placed = false;
            for (var tries = 0; tries < 500 && !placed; tries++)
            {
                var horizontal = rng.Next(2) == 0;
                var x = rng.Next(horizontal ? W - size + 1 : W);
                var y = rng.Next(horizontal ? H : H - size + 1);
                var cells = new int[size];
                for (var i = 0; i < size; i++) cells[i] = horizontal ? y * W + x + i : (y + i) * W + x;
                if (cells.Any(c => busy[c])) continue;
                foreach (var cell in cells)
                {
                    busy[cell] = true;
                    foreach (var near in Halo(cell)) busy[near] = true;
                }
                ships.Add(cells);
                placed = true;
            }
            if (!placed) return null;
        }
        return ships;
    }

    /// <summary>Ручна розстановка на випадок, якщо випадковій не пощастило: рівні ряди, дотиків нема.</summary>
    public static List<int[]> Canonical() =>
    [
        [0, 1, 2, 3], [5, 6, 7], [20, 21, 22], [24, 25], [27, 28], [40, 41], [43], [45], [47], [49],
    ];
}

/// <summary>
/// Морський бій на двох. Гра <see cref="GameInfo.Hidden"/>: свій флот бачить лише господар, суперник —
/// самі влучання й промахи, глядач — обидва поля без жодного корабля. Тик раз на секунду потрібен не для
/// руху, а для таймера розстановки: інакше двоє, що пішли пити чай, лишили б стіл висіти назавжди.
/// </summary>
public sealed class Battleship : Game
{
    enum Phase { Placing, Battle, Done }

    /// <summary>Одне поле: чий флот, чи готовий господар і куди по ньому вже стріляли.</summary>
    sealed class Side
    {
        public List<int[]> Ships { get; set; } = [];
        public bool Ready { get; set; }
        /// <summary>Клітинки цього поля, куди суперник влучив.</summary>
        public HashSet<int> Hits { get; } = [];
        /// <summary>Промахи по цьому полю — разом з околицями потоплених, які сервер домальовує сам.</summary>
        public HashSet<int> Misses { get; } = [];
        /// <summary>Скільки пострілів зробив господар цього поля (а не по ньому).</summary>
        public int Shots { get; set; }

        public bool Sunk(int[] ship) => ship.All(Hits.Contains);
        public int Left => Ships.Count(s => !Sunk(s));
    }

    public override GameInfo Info { get; } = new(
        "battleship", "Морський бій", "морський бій", GameGroup.Board, 2, 2,
        TickMs: 1000, Hidden: true, Rated: true,
        Hint: "Розстав кораблі, стріляй по чужому полю. Влучив — стріляєш ще");

    Side[] _sides = [new(), new()];
    Phase _phase;
    int _turn;
    /// <summary>Коли скінчиться розстановка; null — партія ще не почалась (стіл у лобі).</summary>
    DateTimeOffset? _placeUntil;
    int? _winner;
    /// <summary>
    /// Стан змінився після останнього тика. У ігор з TickMs > 0 каркас не шле види після Act (Rooms.Act),
    /// тож роздати їх може лише тик — звідси й прапорець.
    /// </summary>
    bool _dirty;

    public override string SeatName(int seat) => seat == 0 ? "синій" : "червоний";

    public override void Start()
    {
        _sides = [new(), new()];
        _phase = Phase.Placing;
        _turn = 0;
        _winner = null;
        _placeUntil = Ctx.Clock.UtcNow.AddSeconds(BattleshipRules.PlaceSeconds);
        _dirty = false;
    }

    // ---------- ходи ----------

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (_phase == Phase.Done) return ActResult.Fail("Партію зіграно, тисни «Ще раз»");
        return action switch
        {
            "place" => Place(seat, payload),
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
        if (BattleshipRules.Invalid(ships) is { } why) return ActResult.Fail(why);
        _sides[seat].Ships = ships;
        _dirty = true;
        return ActResult.Done;
    }

    ActResult Scatter(int seat)
    {
        if (_phase != Phase.Placing) return ActResult.Fail("Бій уже почався, кораблі не рухаються");
        if (_sides[seat].Ready) return ActResult.Fail("Ти вже сказав «Готово»");
        _sides[seat].Ships = BattleshipRules.RandomFleet(Ctx.Rng);
        _dirty = true;
        return ActResult.Accept("Розставив за тебе");
    }

    ActResult Ready(int seat)
    {
        if (_phase != Phase.Placing) return ActResult.Fail("Бій уже почався");
        if (_sides[seat].Ready) return ActResult.Fail("Ти вже сказав «Готово»");
        if (BattleshipRules.Invalid(_sides[seat].Ships) is { } why) return ActResult.Fail(why);
        _sides[seat].Ready = true;
        _dirty = true;
        if (_sides[0].Ready && _sides[1].Ready) _phase = Phase.Battle;
        return ActResult.Done;
    }

    ActResult Shoot(int seat, JsonElement payload)
    {
        if (_phase != Phase.Battle) return ActResult.Fail("Спершу розстав кораблі");
        if (seat != _turn) return ActResult.Fail("Зараз не твій хід");
        if (ReadCell(payload) is not { } cell || cell < 0 || cell >= BattleshipRules.Cells)
            return ActResult.Fail("Не зрозумів, куди стріляти");

        var foe = _sides[1 - seat];
        if (foe.Hits.Contains(cell) || foe.Misses.Contains(cell)) return ActResult.Fail("Сюди вже стріляв");

        _sides[seat].Shots++;
        _dirty = true;
        if (foe.Ships.FirstOrDefault(s => s.Contains(cell)) is not { } ship)
        {
            foe.Misses.Add(cell);
            _turn = 1 - seat;
            return ActResult.Accept("Мимо");
        }

        foe.Hits.Add(cell);
        if (!foe.Sunk(ship)) return ActResult.Accept("Влучив! Стріляй ще");
        // Навколо потопленого корабля стояти нема чому — домальовуємо промахи самі, щоб не гадати руками.
        foreach (var near in BattleshipRules.Around(ship)) foe.Misses.Add(near);
        if (foe.Left > 0) return ActResult.Accept("Потопив! Стріляй ще");

        Win(seat);
        return ActResult.Accept("Флот суперника на дні!");
    }

    void Win(int seat)
    {
        _phase = Phase.Done;
        _winner = seat;
        var lost = 1 - seat;
        // Рахунок — потоплені кораблі: у переможця всі десять, у суперника стільки, скільки він устиг.
        var theirs = _sides[seat].Ships.Count(_sides[seat].Sunk);
        Ctx.Finish([seat],
            $"{Info.Title}: {Ctx.NickOf(seat)} {SeatName(seat)} {BattleshipRules.Fleet.Length}:{theirs} "
            + $"{Ctx.NickOf(lost)} {SeatName(lost)}, пострілів {_sides[seat].Shots}");
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

    // ---------- таймер ----------

    public override TickResult Tick()
    {
        if (_phase == Phase.Placing && _placeUntil is { } until && Ctx.Clock.UtcNow >= until)
        {
            ForceReady();
            _dirty = false;
            return TickResult.Both;
        }
        if (_dirty)
        {
            _dirty = false;
            return TickResult.Both;
        }
        // У розстановці кадр іде щосекунди — у ньому відлік і те, хто вже готовий.
        return _phase == Phase.Placing ? TickResult.FrameOnly : TickResult.None;
    }

    /// <summary>
    /// Час вийшов. Хто нічого не поставив — отримує випадкову розстановку; хто встиг розставити, але не
    /// натиснув «Готово», іде в бій зі своєю: викидати чужу працю через невчасний клік нечесно.
    /// </summary>
    void ForceReady()
    {
        var late = new List<string>();
        for (var seat = 0; seat < 2; seat++)
        {
            var side = _sides[seat];
            if (side.Ready) continue;
            if (BattleshipRules.Invalid(side.Ships) is not null) side.Ships = BattleshipRules.RandomFleet(Ctx.Rng);
            side.Ready = true;
            late.Add(Ctx.NickOf(seat) ?? SeatName(seat));
        }
        if (late.Count > 0) Ctx.Log($"{Info.Title}: час на розстановку вийшов — {string.Join(" і ", late)} у бій як є");
        _phase = Phase.Battle;
        _turn = 0;
    }

    // ---------- види ----------

    string PhaseName => _phase switch { Phase.Placing => "placing", Phase.Battle => "battle", _ => "done" };

    /// <summary>Моє поле: кораблі тут є, і саме тому цей шматок нікому, крім господаря, не дістається.</summary>
    object Own(int seat) => new
    {
        ships = _sides[seat].Ships.Select(s => s.Order().ToArray()).ToArray(),
        ready = _sides[seat].Ready,
        hits = _sides[seat].Hits.Order().ToArray(),
        misses = _sides[seat].Misses.Order().ToArray(),
    };

    /// <summary>Публічне знання про поле <paramref name="of"/>: куди по ньому влучили, де промахнулись, що вже потоплено.</summary>
    object Known(int of) => new
    {
        hits = _sides[of].Hits.Order().ToArray(),
        misses = _sides[of].Misses.Order().ToArray(),
        sunk = _sides[of].Ships.Where(_sides[of].Sunk).Select(s => s.Order().ToArray()).ToArray(),
        ready = _sides[of].Ready,
        left = _sides[of].Left,
    };

    public override object View(int? seat)
    {
        var mine = seat is >= 0 and <= 1 ? seat.Value : -1;
        var known0 = Known(0);
        var known1 = Known(1);
        return new
        {
            phase = PhaseName,
            turn = _phase == Phase.Battle ? _turn : (int?)null,
            placeUntil = _phase == Phase.Placing ? _placeUntil?.ToString("o") : null,
            me = mine >= 0 ? Own(mine) : null,
            // Глядачеві «своє» поле не належить, але форму виду ламати не хочеться: віддаємо йому друге з boards.
            enemy = mine == 0 ? known1 : mine == 1 ? known0 : known1,
            boards = mine >= 0 ? null : new[] { known0, known1 },
            shots = _sides[0].Shots + _sides[1].Shots,
            result = _winner is { } w ? new { winner = w, shots = new[] { _sides[0].Shots, _sides[1].Shots } } : null,
        };
    }

    /// <summary>Кадр — тільки відлік і лічильники: жодного корабля тут бути не може, бо він летить усім одразу.</summary>
    public override object? Frame() => new
    {
        phase = PhaseName,
        turn = _phase == Phase.Battle ? _turn : (int?)null,
        placeLeft = _phase == Phase.Placing && _placeUntil is { } until
            ? Math.Max(0, (int)Math.Ceiling((until - Ctx.Clock.UtcNow).TotalSeconds))
            : (int?)null,
        ready = new[] { _sides[0].Ready, _sides[1].Ready },
        left = new[] { _sides[0].Left, _sides[1].Left },
        shots = _sides[0].Shots + _sides[1].Shots,
        winner = _winner,
    };

    // ---------- збереження ----------

    sealed record SideSnap(int[][] Ships, bool Ready, int[] Hits, int[] Misses, int Shots);
    sealed record Snap(string Phase, int Turn, int? Winner, DateTimeOffset? PlaceUntil, SideSnap[] Sides);

    public override string? Save() => JsonSerializer.Serialize(new Snap(
        PhaseName, _turn, _winner, _placeUntil,
        [.. _sides.Select(s => new SideSnap(
            [.. s.Ships], s.Ready, [.. s.Hits.Order()], [.. s.Misses.Order()], s.Shots))]));

    public override void Load(string json)
    {
        if (JsonSerializer.Deserialize<Snap>(json) is not { Sides.Length: 2 } snap) return;
        _phase = snap.Phase switch { "battle" => Phase.Battle, "done" => Phase.Done, _ => Phase.Placing };
        _turn = snap.Turn;
        _winner = snap.Winner;
        _placeUntil = snap.PlaceUntil;
        _sides = [new(), new()];
        for (var seat = 0; seat < 2; seat++)
        {
            var from = snap.Sides[seat];
            _sides[seat].Ships = [.. from.Ships];
            _sides[seat].Ready = from.Ready;
            _sides[seat].Shots = from.Shots;
            foreach (var c in from.Hits) _sides[seat].Hits.Add(c);
            foreach (var c in from.Misses) _sides[seat].Misses.Add(c);
        }
        _dirty = false;
    }
}
