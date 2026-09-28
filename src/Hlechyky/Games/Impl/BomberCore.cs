namespace Hlechyky.Games.Impl;

/// <summary>Що стоїть у клітинці: порожньо, незламна стіна або ящик.</summary>
public enum BomberTile { Free, Wall, Box }

/// <summary>
/// Що випадає з розбитого ящика: довший вибух, ще одна бомба, черевики — і в «хаосі» (прохід №3) ще
/// рукавиця-копняк і череп із прокляттям.
/// </summary>
public enum BomberBonus { Range, Bomb, Boots, Kick, Skull }

/// <summary>Прокляття черепа: керування навпаки, повзеш як равлик або сиплеш бомби, не питаючи.</summary>
public enum BomberCurse { None, Reverse, Slow, Bombs }

/// <summary>Мапа: «Класика» (55 % ящиків), «Відкрита» (30 %) і «Лабіринт» (довгі стіни й 40 % ящиків).</summary>
public enum BomberMap { Classic, Open, Maze }

/// <summary>Що сталося на полі — для стрічки подій і підсумку партії.</summary>
public enum BomberHow
{
    /// <summary>Підірвав суперника.</summary>
    Kill,
    /// <summary>Сам себе.</summary>
    Self,
    /// <summary>Свого з команди (лише з дружнім вогнем).</summary>
    Team,
    /// <summary>Привид дістав помстою.</summary>
    Revenge,
    /// <summary>Привалило стіною стискання.</summary>
    Wall,
}

/// <summary>Подія тика: хто кого (Killer = -1 — стіна).</summary>
public readonly record struct BomberEvent(BomberHow How, int Killer, int Victim);

/// <summary>
/// Бомбер одного місця. Рух клітинний: гравець їде з <see cref="Cell"/> у сусідню клітинку в напрямку
/// <see cref="Move"/>, а <see cref="Step"/> каже, скільки дванадцятих частин шляху вже позаду. Клієнт із
/// цих трьох чисел робить плавну картинку, а сервер тримає рівно цілі клітинки — інакше «хто де стояв на
/// момент вибуху» не відтворилось би в тестах.
/// </summary>
public sealed class BomberMan
{
    /// <summary>Клітинка, з якої зараз їде (або в якій стоїть).</summary>
    public int Cell;
    /// <summary>Напрямок поточного кроку; -1 — стоїть рівно в клітинці.</summary>
    public int Move = -1;
    /// <summary>Скільки дванадцятих частин клітинки пройдено в цьому кроці (0..<see cref="BomberCore.Sub"/>).</summary>
    public int Step;
    /// <summary>Напрямок, який гравець тримає пальцем чи клавішею; -1 — відпустив.</summary>
    public int Want = -1;
    public bool Alive;
    /// <summary>Чи це місце взагалі грає цей раунд (порожнє місце або той, хто встав, не з'являється).</summary>
    public bool Plays;
    /// <summary>Скільки бомб можна тримати на полі одночасно.</summary>
    public int Bombs = BomberCore.StartBombs;
    /// <summary>Довжина променя вибуху в клітинках.</summary>
    public int Range = BomberCore.StartRange;
    /// <summary>Черевики: клітинка за три тики замість чотирьох.</summary>
    public bool Boots;
    /// <summary>Скільки своїх бомб зараз цокає на полі.</summary>
    public int Fused;
    /// <summary>Рукавиця: забіг у бомбу — вона поїхала до першої перешкоди.</summary>
    public bool Kick;
    public BomberCurse Curse;
    /// <summary>Скільки тиків ще діє прокляття.</summary>
    public int CurseLeft;
    /// <summary>Щойно передав прокляття — стільки тиків воно на нього не перескочить назад (інакше пінг-понг у клітинці).</summary>
    public int Immune;
    /// <summary>Підірвали, але граємо привидом: літає крізь стіни, не вмирає, не підбирає.</summary>
    public bool Ghost;
    /// <summary>Привид ще не кинув свою помсту цього раунду.</summary>
    public bool Revenge;
    /// <summary>Команда (0 або 1); -1 — кожен сам за себе.</summary>
    public int Team = -1;
}

/// <summary>
/// Бомба на полі. Радіус запам'ятовується при постановці: інакше вогонь, підібраний уже після того, як
/// запал пішов, ретроспективно робив би вибух довшим, і гравець не міг би розрахувати, куди тікати.
/// </summary>
public sealed class BomberBomb
{
    public required int Cell { get; set; }
    public required int Owner { get; init; }
    public required int Range { get; init; }
    public int Fuse;
    /// <summary>Повільна бомба-помста від привида.</summary>
    public bool Revenge { get; init; }
    /// <summary>Куди їде після копняка; -1 — лежить.</summary>
    public int Slide = -1;
    public int SlideStep;
}

/// <summary>Бонус, що лежить на звільненій клітинці й чекає, поки хтось на нього наїде.</summary>
public sealed class BomberDrop
{
    public required int Cell { get; init; }
    public required BomberBonus Kind { get; init; }
}

/// <summary>
/// Поле, бомбери, бомби й вибухи — без жодного слова про кімнати, раунди й Журнал. Тут живуть тільки
/// правила одного раунду, і саме тому їх можна ганяти тестами тисячами тиків без сервера.
/// </summary>
public sealed class BomberCore
{
    /// <summary>Класичне поле; велике (прохід №3) — <see cref="BigW"/>×<see cref="BigH"/>.</summary>
    public const int W = 15, H = 13;
    public const int BigW = 19, BigH = 15;
    public const int TickMs = 60;
    /// <summary>Дванадцяті частки клітинки: 12 ділиться і на 2 (прокляття), 3 (звичайний крок) і 4 (черевики).</summary>
    public const int Sub = 12;
    /// <summary>Звичайний крок: 4 тики на клітинку — 240 мс.</summary>
    public const int SlowStep = 3;
    /// <summary>У черевиках: 3 тики на клітинку — 180 мс.</summary>
    public const int FastStep = 4;
    /// <summary>Під прокляттям «равлик»: 6 тиків на клітинку.</summary>
    public const int SnailStep = 2;
    /// <summary>Запал — рівно дві секунди.</summary>
    public const int FuseTicks = 33;
    /// <summary>Помста привида повільніша — три секунди: від неї можна втекти, якщо дивишся.</summary>
    public const int RevengeFuse = 50;
    /// <summary>Полум'я живе десь чотири десятих секунди.</summary>
    public const int FlameTicks = 7;
    /// <summary>Дві хвилини — і раунд нічий, скільки б там хто не бігав.</summary>
    public const int RoundTicks = 2000;
    /// <summary>Стискання (опцією) починається з 90-ї секунди.</summary>
    public const int ShrinkFrom = 1500;
    /// <summary>Прокляття — десять секунд.</summary>
    public const int CurseTicks = 167;
    /// <summary>Скільки тиків той, хто передав прокляття, не може отримати його знову (≈2 с).</summary>
    public const int ImmuneTicks = 33;
    /// <summary>Бомба після копняка їде клітинку за два тики.</summary>
    public const int SlideTicks = 2;
    public const int StartBombs = 1, MaxBombs = 4;
    public const int StartRange = 2, MaxRange = 6;
    /// <summary>Скільки відсотків вільних клітинок заставляємо ящиками (на «Класиці»).</summary>
    public const int BoxChance = 55;
    /// <summary>З якою ймовірністю ящик лишає по собі бонус.</summary>
    public const int DropChance = 30;
    /// <summary>
    /// Місць за столом: чотири кути і ще двоє посередині верхнього й нижнього краю. Поле те саме 15×13 —
    /// так грали вп'ятьох-ушістьох і в класиці, тісніше, зате весело.
    /// </summary>
    public const int Seats = 6;
    /// <summary>У масці полум'я біти 0..5 — звичайні бомби місць, 8..13 — помста привидів.</summary>
    const int RevengeBit = 8;

    /// <summary>0 праворуч, 1 вниз, 2 ліворуч, 3 вгору — як скрізь на платформі.</summary>
    public static readonly (int Dx, int Dy)[] Deltas = [(1, 0), (0, 1), (-1, 0), (0, -1)];

    readonly Random _rng;

    public BomberCore(Random rng, int width = W, int height = H, BomberMap map = BomberMap.Classic)
    {
        _rng = rng;
        Width = width;
        Height = height;
        Map = map;
        Tiles = new BomberTile[width * height];
        Flame = new int[width * height];
        FlameBy = new int[width * height];
        // Перші два — по діагоналі: на двох гравцях стіл має бути чесним, а не «сусіди через одну стіну».
        Corners = [Cell(1, 1), Cell(width - 2, height - 2), Cell(width - 2, 1), Cell(1, height - 2)];
        // П'ятий і шостий старти — середина верхнього й нижнього краю (x непарний — це прохід, а не стовп).
        // Розчищаються лише тоді, коли на них хтось грає: на двох–чотирьох поле лишається тим самим.
        Mids = [Cell(width / 2, 1), Cell(width / 2, height - 2)];
        Starts = [.. Corners, .. Mids];
    }

    public int Width { get; }
    public int Height { get; }
    public BomberMap Map { get; }
    public int[] Corners { get; }
    public int[] Mids { get; }
    /// <summary>Старт кожного місця: спершу кути, потім середини країв.</summary>
    public int[] Starts { get; }

    // ---------- опції столу (ставить Bomber перед Reset) ----------

    /// <summary>Бонуси-хаос: у ящиках ще й 🧤 і 💀.</summary>
    public bool Chaos;
    /// <summary>Підірвані грають далі привидами.</summary>
    public bool Ghosts;
    /// <summary>Стискання з 90-ї секунди.</summary>
    public bool Shrink;
    /// <summary>У командах свої бомби ранять своїх.</summary>
    public bool FriendlyFire;
    /// <summary>Команди місць (-1 — сам за себе); null — граємо кожен сам.</summary>
    public int[]? Teams;

    public BomberTile[] Tiles { get; }
    /// <summary>Скільки тиків ще горіти в кожній клітинці; 0 — не горить.</summary>
    public int[] Flame { get; }
    /// <summary>Чиє полум'я в клітинці: біт місця (помста — біт 8 + місце). Для «хто кого» і дружнього вогню.</summary>
    public int[] FlameBy { get; }
    public BomberMan[] Players { get; } = [.. Enumerable.Range(0, Seats).Select(_ => new BomberMan())];
    public List<BomberBomb> Bombs { get; } = [];
    public List<BomberDrop> Drops { get; } = [];
    /// <summary>Події тиків, які ще ніхто не забрав (Bomber забирає щотику).</summary>
    public List<BomberEvent> Events { get; } = [];
    /// <summary>Скільки ящиків розбило кожне місце цього раунду.</summary>
    public int[] Broke { get; } = new int[Seats];
    /// <summary>Скільки тиків триває цей раунд.</summary>
    public int Ticks { get; private set; }
    /// <summary>Стіни рамки, стовпів і лабіринту — без стискання (їх шле вид).</summary>
    public int[] BaseWalls { get; private set; } = [];
    /// <summary>Порядок стискання: спіраллю від краю, минаючи стіни.</summary>
    public int[] ShrinkOrder { get; private set; } = [];
    /// <summary>Скільки клітинок уже забрало стискання.</summary>
    public int Shrunk { get; private set; }
    /// <summary>Раз на скільки тиків стискання забирає клітинку: так, щоб поле зійшлось до кінця раунду.</summary>
    public int ShrinkEvery { get; private set; } = 3;

    public int Cell(int x, int y) => y * Width + x;
    public int X(int cell) => cell % Width;
    public int Y(int cell) => cell / Width;

    /// <summary>Сусідня клітинка в напрямку dir; -1 — за краєм поля.</summary>
    public int Ahead(int cell, int dir)
    {
        if (dir is < 0 or > 3) return -1;
        var (dx, dy) = Deltas[dir];
        var (x, y) = (X(cell) + dx, Y(cell) + dy);
        return x < 0 || x >= Width || y < 0 || y >= Height ? -1 : Cell(x, y);
    }

    /// <summary>Клітинка, «в якій центр» бомбера: саме за нею рахують бомби, бонуси й смерть у полум'ї.</summary>
    public int Center(BomberMan p)
    {
        if (p.Move < 0 || p.Step * 2 < Sub) return p.Cell;
        var next = Ahead(p.Cell, p.Move);
        return next < 0 ? p.Cell : next;
    }

    public int PosX(BomberMan p) => X(p.Cell) * Sub + (p.Move >= 0 ? Deltas[p.Move].Dx * p.Step : 0);
    public int PosY(BomberMan p) => Y(p.Cell) * Sub + (p.Move >= 0 ? Deltas[p.Move].Dy * p.Step : 0);

    public int AliveCount
    {
        get
        {
            var n = 0;
            foreach (var p in Players) if (p.Alive) n++;
            return n;
        }
    }

    /// <summary>Скільки команд ще має живих (без команд — скільки живих).</summary>
    int AliveSides
    {
        get
        {
            if (Teams is null) return AliveCount;
            var mask = 0;
            foreach (var p in Players) if (p.Alive) mask |= p.Team < 0 ? 4 : 1 << p.Team;
            return (mask & 1) + ((mask >> 1) & 1) + ((mask >> 2) & 1);
        }
    }

    /// <summary>Раунд скінчився: лишився щонайбільше один живий (одна команда) або вийшов час.</summary>
    public bool RoundOver => AliveSides <= 1 || Ticks >= RoundTicks;

    /// <summary>Єдиний живий; -1, коли живих нема або їх ще кілька (тоді раунд нічий).</summary>
    public int LastStanding
    {
        get
        {
            var found = -1;
            for (var i = 0; i < Players.Length; i++)
            {
                if (!Players[i].Alive) continue;
                if (found >= 0) return -1;
                found = i;
            }
            return found;
        }
    }

    /// <summary>Команда, що лишилась на полі сама; -1 — нікого або ще обидві.</summary>
    public int LastTeam
    {
        get
        {
            var found = -1;
            foreach (var p in Players)
            {
                if (!p.Alive) continue;
                if (found >= 0 && found != p.Team) return -1;
                found = p.Team;
            }
            return found;
        }
    }

    bool HasBomb(int cell)
    {
        foreach (var b in Bombs) if (b.Cell == cell) return true;
        return false;
    }

    BomberBomb? BombAt(int cell)
    {
        foreach (var b in Bombs) if (b.Cell == cell) return b;
        return null;
    }

    /// <summary>Чи можна зайти в цю клітинку: не стіна, не ящик і не чужа бомба.</summary>
    public bool Walkable(int cell) => cell >= 0 && Tiles[cell] == BomberTile.Free && !HasBomb(cell);

    /// <summary>Привид літає крізь стіни й ящики, але не за рамку поля.</summary>
    bool GhostWalkable(int cell) =>
        cell >= 0 && X(cell) > 0 && X(cell) < Width - 1 && Y(cell) > 0 && Y(cell) < Height - 1;

    // ---------- поле ----------

    /// <summary>
    /// Тільки геометрія: рамка по краю, «стовпи» на парних координатах (у «Лабіринті» — ще й довгі стіни) і
    /// бомбери по кутах. Випадковості тут нема свідомо — таке поле можна показати в лобі, ще не витративши
    /// жодного числа з Ctx.Rng.
    /// </summary>
    public void Layout()
    {
        Ticks = 0;
        Shrunk = 0;
        Bombs.Clear();
        Drops.Clear();
        Events.Clear();
        Array.Clear(Flame);
        Array.Clear(FlameBy);
        Array.Clear(Broke);
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                Tiles[Cell(x, y)] = x == 0 || y == 0 || x == Width - 1 || y == Height - 1 || (x % 2 == 0 && y % 2 == 0)
                    ? BomberTile.Wall
                    : BomberTile.Free;
        if (Map == BomberMap.Maze) MazeWalls();
        var walls = new List<int>();
        for (var i = 0; i < Tiles.Length; i++) if (Tiles[i] == BomberTile.Wall) walls.Add(i);
        BaseWalls = [.. walls];
        ShrinkOrder = Spiral();
        ShrinkEvery = Math.Max(1, (RoundTicks - 50 - ShrinkFrom) / Math.Max(1, ShrinkOrder.Length));
        // Напрямок, який людина тримає пальцем чи клавішею, переживає новий раунд: інакше той, хто не
        // відпускав стрілку на «Готуйсь», стояв би стовпом, поки не перетисне клавішу.
        for (var i = 0; i < Seats; i++)
            Players[i] = new BomberMan { Cell = Starts[i], Want = Players[i].Want, Team = Teams is null ? -1 : Teams[i] };
    }

    /// <summary>
    /// «Лабіринт»: кожен другий стовп (шахівкою) витягується на клітинку — то вбік, то вниз. Виходять довгі
    /// стіни й коридори, але кожен прохід зв'язний (тест обходить поле пошуком) і старти не зачеплені.
    /// </summary>
    void MazeWalls()
    {
        for (var y = 2; y < Height - 1; y += 2)
            for (var x = 2; x < Width - 1; x += 2)
            {
                if ((x / 2 + y / 2) % 2 != 0) continue;
                var (ex, ey) = (x / 2) % 2 == 0 ? (x + 1, y) : (x, y + 1);
                if (ex >= Width - 1 || ey >= Height - 1 || NearStart(ex, ey)) continue;
                Tiles[Cell(ex, ey)] = BomberTile.Wall;
            }
    }

    bool NearStart(int x, int y)
    {
        foreach (var s in Starts)
            if (Math.Abs(X(s) - x) <= 1 && Math.Abs(Y(s) - y) <= 1) return true;
        return false;
    }

    /// <summary>Спіраль від краю всередину по вільних від стін клітинках — порядок стискання (клієнт рахує так само).</summary>
    int[] Spiral()
    {
        var order = new List<int>();
        int l = 1, t = 1, r = Width - 2, b = Height - 2;
        void Add(int x, int y)
        {
            var c = Cell(x, y);
            if (Tiles[c] != BomberTile.Wall) order.Add(c);
        }
        while (l <= r && t <= b)
        {
            for (var x = l; x <= r; x++) Add(x, t);
            for (var y = t + 1; y <= b; y++) Add(r, y);
            if (b > t) for (var x = r - 1; x >= l; x--) Add(x, b);
            if (r > l) for (var y = b - 1; y > t; y--) Add(l, y);
            l++; t++; r--; b--;
        }
        return [.. order];
    }

    /// <summary>Новий раунд: свіже поле з ящиками і живі бомбери на тих місцях, які цього разу грають.</summary>
    public void Reset(bool[] plays)
    {
        Layout();
        // 3×3 навколо кожного кута лишаємо порожнім — і за чотирьох, і за двох гравців. Інакше той, кому
        // випав кут із ящиками під носом, першу ж свою бомбу ставив би собі в глухий кут.
        // Середини країв — лише коли за столом більше чотирьох: на меншому столі там стоять ящики, як і раніше.
        var crowd = plays.Skip(Corners.Length).Any(p => p);
        var safe = new HashSet<int>();
        foreach (var corner in crowd ? Starts : Corners)
            for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                    safe.Add(Cell(X(corner) + dx, Y(corner) + dy));
        var chance = Map switch { BomberMap.Open => 30, BomberMap.Maze => 40, _ => BoxChance };
        for (var y = 1; y < Height - 1; y++)
            for (var x = 1; x < Width - 1; x++)
            {
                var cell = Cell(x, y);
                if (Tiles[cell] != BomberTile.Free || safe.Contains(cell)) continue;
                if (_rng.Next(100) < chance) Tiles[cell] = BomberTile.Box;
            }
        for (var i = 0; i < Seats; i++)
        {
            var active = i < plays.Length && plays[i];
            Players[i].Plays = active;
            Players[i].Alive = active;
        }
    }

    // ---------- наміри гравця ----------

    /// <summary>
    /// Гравець тримає напрямок (0..3) або відпустив (-1). Це лише намір: рухає його найближчий тик, і
    /// то лише якщо бомбер живий. Небіжчикові теж не відмовляємо — інакше клавіша, відпущена між
    /// раундами, лишалась би «натиснутою» до самого нового поля.
    /// </summary>
    public void Turn(int seat, int dir)
    {
        if (seat < 0 || seat >= Players.Length) return;
        Players[seat].Want = dir is >= 0 and <= 3 ? dir : -1;
    }

    /// <summary>Покласти бомбу під себе. false — ліміт вичерпано або тут уже щось цокає.</summary>
    public bool Bomb(int seat)
    {
        if (seat < 0 || seat >= Players.Length) return false;
        var p = Players[seat];
        if (!p.Alive || p.Fused >= p.Bombs) return false;
        var cell = Center(p);
        if (HasBomb(cell)) return false;
        Bombs.Add(new BomberBomb { Cell = cell, Owner = seat, Range = p.Range, Fuse = FuseTicks });
        p.Fused++;
        return true;
    }

    /// <summary>
    /// Помста привида: раз на раунд повільна бомба там, де він зараз, — лише на вільній клітинці (у стіну чи
    /// ящик не кинеш). false — привида нема, помсту вже кинуто або тут не можна.
    /// </summary>
    public bool Revenge(int seat)
    {
        if (seat < 0 || seat >= Players.Length) return false;
        var p = Players[seat];
        if (!p.Ghost || !p.Revenge) return false;
        var cell = Center(p);
        if (Tiles[cell] != BomberTile.Free || HasBomb(cell)) return false;
        Bombs.Add(new BomberBomb { Cell = cell, Owner = seat, Range = StartRange, Fuse = RevengeFuse, Revenge = true });
        p.Revenge = false;
        return true;
    }

    // ---------- тик ----------

    /// <summary>
    /// Один крок світу. Порядок важливий: спершу догоряє старе полум'я і сунеться стискання, потім їдуть
    /// копнуті бомби й вибухають ті, що догоріли (разом із ланцюгом), і лише тоді рухаються бомбери — тож той,
    /// хто вбіг у щойно народжене полум'я, гине того самого тика, а не наступного.
    /// </summary>
    public void Step()
    {
        Ticks++;
        Cool();
        if (Shrink) Squeeze();
        Slides();
        Fuses();
        Walk();
        Collect();
        Curses();
        Reap();
    }

    void Cool()
    {
        for (var i = 0; i < Flame.Length; i++)
            if (Flame[i] > 0 && --Flame[i] == 0) FlameBy[i] = 0;
    }

    /// <summary>Стискання: з 90-ї секунди спіраллю від краю — клітинка за клітинкою стає стіною.</summary>
    void Squeeze()
    {
        if (Ticks < ShrinkFrom || (Ticks - ShrinkFrom) % ShrinkEvery != 0 || Shrunk >= ShrinkOrder.Length) return;
        var cell = ShrinkOrder[Shrunk++];
        Tiles[cell] = BomberTile.Wall;
        Flame[cell] = 0;
        FlameBy[cell] = 0;
        Drops.RemoveAll(d => d.Cell == cell);
        var bomb = BombAt(cell);
        if (bomb is not null)
        {
            Bombs.Remove(bomb);
            if (!bomb.Revenge) Players[bomb.Owner].Fused = Math.Max(0, Players[bomb.Owner].Fused - 1);
        }
        for (var i = 0; i < Players.Length; i++)
        {
            var p = Players[i];
            if (!p.Alive) continue;
            if (Center(p) == cell) { Die(i, BomberHow.Wall, -1); continue; }
            // ще в своїй клітинці, але біг саме туди — лишається де був
            if (p.Move >= 0 && Ahead(p.Cell, p.Move) == cell) { p.Move = -1; p.Step = 0; }
        }
    }

    /// <summary>Копнута бомба їде клітинку за <see cref="SlideTicks"/> тики, поки не впреться в стіну, ящик, бомбу чи живого.</summary>
    void Slides()
    {
        foreach (var b in Bombs)
        {
            if (b.Slide < 0 || ++b.SlideStep < SlideTicks) continue;
            b.SlideStep = 0;
            var next = Ahead(b.Cell, b.Slide);
            if (!SlideFree(next)) { b.Slide = -1; continue; }
            b.Cell = next;
            if (Flame[next] > 0) b.Fuse = 1;   // заїхала у вогонь — бахне наступного тика
        }
    }

    bool SlideFree(int cell)
    {
        if (!Walkable(cell)) return false;
        foreach (var p in Players) if (p.Alive && Center(p) == cell) return false;
        return true;
    }

    void Fuses()
    {
        List<BomberBomb>? due = null;
        foreach (var bomb in Bombs)
            if (--bomb.Fuse <= 0) (due ??= []).Add(bomb);
        if (due is not null) Detonate(due);
    }

    /// <summary>
    /// Вибух хрестом, із ланцюгом: черга починається з бомб, у яких вигорів запал, і росте за рахунок тих,
    /// кого зачепило чужим полум'ям. Ящик зупиняє промінь на собі, стіна — перед собою.
    /// </summary>
    public void Detonate(IEnumerable<BomberBomb> first)
    {
        var queue = new Queue<BomberBomb>(first);
        var doomed = new HashSet<BomberBomb>(queue);
        // Ящики, зламані цим самим вибухом. Весь ланцюг рахуємо проти поля, яким воно було до нього:
        // інакше промінь сусідньої бомби проходив би крізь укриття, яке щойно розлетілось.
        var broken = new HashSet<int>();
        while (queue.Count > 0)
        {
            var bomb = queue.Dequeue();
            Bombs.Remove(bomb);
            var owner = Players[bomb.Owner];
            if (!bomb.Revenge) owner.Fused = Math.Max(0, owner.Fused - 1);
            var bit = 1 << (bomb.Owner + (bomb.Revenge ? RevengeBit : 0));
            Burn(bomb.Cell, bit);
            foreach (var (dx, dy) in Deltas)
            {
                var (x, y) = (X(bomb.Cell), Y(bomb.Cell));
                for (var i = 0; i < bomb.Range; i++)
                {
                    x += dx;
                    y += dy;
                    if (x < 0 || x >= Width || y < 0 || y >= Height) break;
                    var cell = Cell(x, y);
                    if (Tiles[cell] == BomberTile.Wall) break;
                    if (broken.Contains(cell)) break;   // цей ящик уже зламала сусідка по ланцюгу — далі не йдемо
                    if (Tiles[cell] == BomberTile.Box)
                    {
                        // Підпалюємо руками, а не через Burn: бонус, який щойно випав із цього ящика,
                        // не має згоріти в тому самому полум'ї, що його й відкрило.
                        Flame[cell] = FlameTicks;
                        FlameBy[cell] |= bit;
                        broken.Add(cell);
                        Broke[bomb.Owner]++;
                        Break(cell);
                        break;
                    }
                    Burn(cell, bit);
                    // Сусідська бомба летить у ту саму чергу, а промінь іде далі: ланцюг має доганяти все підряд.
                    var next = BombAt(cell);
                    if (next is not null && doomed.Add(next)) queue.Enqueue(next);
                }
            }
        }
    }

    void Burn(int cell, int bit)
    {
        Flame[cell] = FlameTicks;
        FlameBy[cell] |= bit;
        Drops.RemoveAll(d => d.Cell == cell);   // бонус, що лежав на дорозі, згоряє
    }

    void Break(int cell)
    {
        Tiles[cell] = BomberTile.Free;
        if (_rng.Next(100) >= DropChance) return;
        // Без хаосу — рівно ті самі числа з генератора, що й до проходу №3: партії з тим самим сідом не змінились.
        var kind = !Chaos
            ? (BomberBonus)_rng.Next(3)
            : _rng.Next(10) switch
            {
                < 3 => BomberBonus.Range,
                < 6 => BomberBonus.Bomb,
                6 => BomberBonus.Boots,
                7 => BomberBonus.Kick,
                _ => BomberBonus.Skull,
            };
        Drops.Add(new BomberDrop { Cell = cell, Kind = kind });
    }

    void Walk()
    {
        foreach (var p in Players)
        {
            if (p.Ghost) { Float(p); continue; }
            if (!p.Alive) continue;
            if (p.Move < 0)
            {
                var want = p.Curse == BomberCurse.Reverse && p.Want >= 0 ? (p.Want + 2) % 4 : p.Want;
                if (want < 0) continue;
                var ahead = Ahead(p.Cell, want);
                if (!Walkable(ahead))
                {
                    // Уперся в бомбу з рукавицею — копнув; сам стоїть і чекає, поки вона звільнить дорогу.
                    if (p.Kick && BombAt(ahead) is { Slide: < 0 } bomb && SlideFree(Ahead(ahead, want)))
                    {
                        bomb.Slide = want;
                        bomb.SlideStep = SlideTicks - 1;   // перша клітинка — вже наступного тика
                    }
                    continue;
                }
                p.Move = want;
                p.Step = 0;
            }
            p.Step += p.Curse == BomberCurse.Slow ? SnailStep : p.Boots ? FastStep : SlowStep;
            if (p.Step < Sub) continue;
            // Sub ділиться і на 2, і на 3, і на 4 без остачі, тож крок завжди завершується рівно в клітинці.
            p.Cell = Ahead(p.Cell, p.Move);
            p.Step = 0;
            p.Move = -1;
        }
    }

    /// <summary>Привид: той самий клітинний рух звичайним кроком, але крізь стіни, ящики й бомби.</summary>
    void Float(BomberMan p)
    {
        if (p.Move < 0)
        {
            if (p.Want < 0 || !GhostWalkable(Ahead(p.Cell, p.Want))) return;
            p.Move = p.Want;
            p.Step = 0;
        }
        p.Step += SlowStep;
        if (p.Step < Sub) return;
        p.Cell = Ahead(p.Cell, p.Move);
        p.Step = 0;
        p.Move = -1;
    }

    void Collect()
    {
        foreach (var p in Players)
        {
            if (!p.Alive) continue;
            var cell = Center(p);
            var i = Drops.FindIndex(d => d.Cell == cell);
            if (i < 0) continue;
            switch (Drops[i].Kind)
            {
                case BomberBonus.Range: p.Range = Math.Min(MaxRange, p.Range + 1); break;
                case BomberBonus.Bomb: p.Bombs = Math.Min(MaxBombs, p.Bombs + 1); break;
                case BomberBonus.Kick: p.Kick = true; break;
                case BomberBonus.Skull:
                    p.Curse = (BomberCurse)(1 + _rng.Next(3));
                    p.CurseLeft = CurseTicks;
                    break;
                default: p.Boots = true; break;
            }
            Drops.RemoveAt(i);
        }
    }

    /// <summary>
    /// Прокляття тикає, «сипле бомби» сам і — найсмішніше — перескакує на того, з ким проклятий зіткнувся
    /// в одній клітинці (сам він тоді одужує).
    /// </summary>
    void Curses()
    {
        foreach (var p in Players) if (p.Immune > 0) p.Immune--;
        for (var i = 0; i < Players.Length; i++)
        {
            var p = Players[i];
            if (p.Curse == BomberCurse.None) continue;
            if (!p.Alive || --p.CurseLeft <= 0) { p.Curse = BomberCurse.None; p.CurseLeft = 0; continue; }
            if (p.Curse == BomberCurse.Bombs) Bomb(i);
            var at = Center(p);
            foreach (var q in Players)
            {
                if (q == p || !q.Alive || q.Curse != BomberCurse.None || q.Immune > 0 || Center(q) != at) continue;
                (q.Curse, q.CurseLeft) = (p.Curse, p.CurseLeft);
                (p.Curse, p.CurseLeft, p.Immune) = (BomberCurse.None, 0, ImmuneTicks);
                break;
            }
        }
    }

    void Reap()
    {
        for (var i = 0; i < Players.Length; i++)
        {
            var p = Players[i];
            if (!p.Alive) continue;
            var c = Center(p);
            if (Flame[c] <= 0) continue;
            var by = FlameBy[c];
            // Без дружнього вогню бомби своїх з команди не ранять (свої власні — ранять, як і завжди).
            if (Teams is not null && !FriendlyFire && p.Team >= 0)
                for (var s = 0; s < Seats; s++)
                    if (s != i && Players[s].Team == p.Team) by &= ~((1 << s) | (1 << (s + RevengeBit)));
            if (by == 0 && FlameBy[c] != 0) continue;   // горить лише свій вогонь — живий
            Die(i, How(i, by, out var killer), killer);
        }
    }

    /// <summary>Хто кого: спершу суперник, потім помста, потім свій із команди, і лише тоді «сам себе».</summary>
    BomberHow How(int victim, int by, out int killer)
    {
        var team = Players[victim].Team;
        for (var s = 0; s < Seats; s++)
            if (s != victim && (by & (1 << s)) != 0 && (team < 0 || Players[s].Team != team)) { killer = s; return BomberHow.Kill; }
        for (var s = 0; s < Seats; s++)
            if ((by & (1 << (s + RevengeBit))) != 0) { killer = s; return BomberHow.Revenge; }
        for (var s = 0; s < Seats; s++)
            if (s != victim && (by & (1 << s)) != 0) { killer = s; return BomberHow.Team; }
        killer = victim;
        return BomberHow.Self;
    }

    void Die(int seat, BomberHow how, int killer)
    {
        var p = Players[seat];
        p.Alive = false;
        p.Curse = BomberCurse.None;
        p.CurseLeft = 0;
        Events.Add(new BomberEvent(how, killer, seat));
        if (!Ghosts) return;
        p.Ghost = true;
        p.Revenge = true;
        // привид з'являється рівно в клітинці, де його підірвало
        p.Cell = Center(p);
        p.Move = -1;
        p.Step = 0;
    }

    // ---------- готові масиви для кадра ----------

    public int[] WallCells() => [.. Where(BomberTile.Wall)];
    public int[] BoxCells() => [.. Where(BomberTile.Box)];

    IEnumerable<int> Where(BomberTile what)
    {
        for (var i = 0; i < Tiles.Length; i++)
            if (Tiles[i] == what) yield return i;
    }

    public int[] FlameCells()
    {
        List<int>? cells = null;
        for (var i = 0; i < Flame.Length; i++)
            if (Flame[i] > 0) (cells ??= []).Add(i);
        return cells is null ? [] : [.. cells];
    }
}
