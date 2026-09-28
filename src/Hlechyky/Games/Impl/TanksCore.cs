namespace Hlechyky.Games.Impl;

/// <summary>
/// Що стоїть у клітинці: порожньо, сталь (незламна), цегла (снаряд ламає), кущ (ховає танк, снаряд летить крізь),
/// лід (танк ковзає на клітинку далі), глек-база команди (розбили — команда програла).
/// </summary>
public enum TankTile { Free, Steel, Brick, Bush, Ice, Base }

/// <summary>
/// Що випадає з розбитої цегли: ⚡ швидкість, 🔫 два снаряди в польоті, 🚀 швидкий снаряд і коротша
/// перезарядка, 🛡 щит на п'ять секунд, 💥 пробивний снаряд (ламає сталь, крізь цеглу летить далі),
/// 🪃 рикошет (снаряд раз відскакує від сталі вбік).
/// </summary>
public enum TankBonus { Speed, Twin, Rapid, Shield, Pierce, Bounce }

/// <summary>Що сталося за тик — для стрічки під полем, серій і підсумку.</summary>
public enum TankHow
{
    /// <summary>A підбив B; N — серія A без смертей.</summary>
    Kill,
    /// <summary>A підбив B, який щойно підбив його; N — серія.</summary>
    Revenge,
    /// <summary>A розбив глек команди B.</summary>
    Base,
    /// <summary>Почалась хвиля N.</summary>
    Wave,
    /// <summary>Усі хвилі відбито.</summary>
    Won,
}

public readonly record struct TankEvent(TankHow How, int A, int B, int N);

/// <summary>Бонус, що лежить на клітинці й чекає, поки хтось на нього наїде. Довго не лежить.</summary>
public sealed class TankDrop
{
    public required int Cell { get; init; }
    public required TankBonus Kind { get; init; }
    public int Ttl;
}

/// <summary>
/// Танк одного місця (0..5) або ворожий 🤖 хвиль (6..9). Рух клітинний, як у бомбера: їде з <see cref="Cell"/>
/// у сусідню в напрямку <see cref="Move"/>, <see cref="Step"/> — скільки дванадцятих шляху позаду. Куди
/// повернувся востаннє (<see cref="Dir"/>) — туди й дуло.
/// </summary>
public sealed class Tank
{
    public int Cell;
    /// <summary>Напрямок поточного кроку; -1 — стоїть рівно в клітинці.</summary>
    public int Move = -1;
    public int Step;
    /// <summary>Напрямок, який гравець тримає; -1 — відпустив.</summary>
    public int Want = -1;
    /// <summary>Дуло: 0 праворуч, 1 вниз, 2 ліворуч, 3 вгору.</summary>
    public int Dir;
    /// <summary>На полі (не підбитий і не чекає повернення).</summary>
    public bool Alive;
    /// <summary>Чи це місце взагалі грає (порожнє чи той, хто встав, не з'являється).</summary>
    public bool Plays;
    /// <summary>Тиків щита після повернення: снаряди пролітають крізь.</summary>
    public int Shield;
    /// <summary>Тиків до наступного пострілу.</summary>
    public int Reload;
    /// <summary>Тиків до повернення на поле; 0 — не чекає.</summary>
    public int Respawn;
    public int Frags;
    /// <summary>Скільки своїх снарядів зараз летить; ліміт — один, з 🔫 — два.</summary>
    public int ShellsOut;
    /// <summary>Стартовий кут.</summary>
    public int Home;
    // Бонуси. Згорають разом із танком: той, хто вирвався вперед, не тікає назавжди.
    /// <summary>⚡ клітинка за 3 тики замість 4.</summary>
    public bool Fast;
    /// <summary>🔫 два снаряди в польоті.</summary>
    public bool Twin;
    /// <summary>🚀 снаряд 12/тик і перезарядка 6 тиків.</summary>
    public bool Rapid;
    /// <summary>💥 наступний снаряд пробивний.</summary>
    public bool Pierce;
    /// <summary>🪃 кожен снаряд раз відскакує від сталі.</summary>
    public bool Bounce;

    /// <summary>Гравець тримає «💥»: стріляє сам, щойно можна.</summary>
    public bool Hold;
    /// <summary>Натиснув трохи зарано — стільки тиків постріл чекає, поки дозволять (буфер).</summary>
    public int Buffer;
    /// <summary>Ковзнув по льоду й уже не ковзає, доки не стане на звичну клітинку.</summary>
    public bool Slid;

    /// <summary>Ворожий танк хвиль: не повертається, бонусів не бере, думає сам.</summary>
    public bool Bot;
    /// <summary>Скільки клітинок бот ще їде в обраний бік, перш ніж подумати знову.</summary>
    public int Think;

    // Для підсумку й драматургії.
    public int Deaths, Shots, Hits, Streak, BestStreak, Revenges;
    /// <summary>Хто підбив мене востаннє, поки я не відплатив; -1 — нікому не винен.</summary>
    public int Nemesis = -1;

    public void Strip() => Fast = Twin = Rapid = Pierce = Bounce = false;
}

/// <summary>Снаряд у дванадцятих частках клітинки. Id — щоб клієнт вів саме його між кадрами.</summary>
public sealed class Shell
{
    public required int Id { get; init; }
    public required int Owner { get; init; }
    public required int Dir { get; set; }
    public required int Speed { get; init; }
    /// <summary>💥: ламає сталь (крім рамки) і летить крізь цеглу, ламаючи її дорогою.</summary>
    public bool Pierce { get; init; }
    /// <summary>🪃: ще може раз відскочити від сталі.</summary>
    public bool Bounce { get; set; }
    public int X, Y;
}

/// <summary>
/// Поле, танки й снаряди — правила однієї партії без жодного слова про кімнати й Журнал. Мапа з
/// <paramref name="rng"/>, але дзеркальна по обох осях: кути рівні, скільки б там не випало.
/// Розмір — за складом: до чотирьох — 21×15, на п'ятьох-шістьох — 27×19 (див. <see cref="SizeFor"/>).
/// Прохід №3: команди з глеками-базами (<see cref="SetSides"/>), хвилі ворожих 🤖 (<see cref="Waves"/>),
/// кущі й лід (<see cref="Wild"/>), серії, помста й підсумок.
/// </summary>
public sealed class TanksCore
{
    public const int SmallW = 21, SmallH = 15, BigW = 27, BigH = 19;
    /// <summary>Скільки людей уміщає звична мапа; більше — велика.</summary>
    public const int SmallSeats = 4;
    public readonly int W, H;
    readonly Random _rng;

    public TanksCore(Random rng, int w = SmallW, int h = SmallH)
    {
        _rng = rng;
        W = w;
        H = h;
        Tiles = new TankTile[W * H];
        Tanks = new Tank[All];
        for (var i = 0; i < All; i++) Tanks[i] = new Tank { Bot = i >= Seats };
        Team = [.. Enumerable.Repeat(-1, All)];
        Starts = [Cell(1, 1), Cell(W - 2, H - 2), Cell(W - 2, 1), Cell(1, H - 2), Cell(1, H / 2), Cell(W - 2, H / 2)];
        BotSpawns = [Cell(W - 2, 1), Cell(W - 2, H / 2), Cell(W - 2, H - 2)];
    }

    /// <summary>Розмір мапи під стількох гравців.</summary>
    public static (int W, int H) SizeFor(int players) => players > SmallSeats ? (BigW, BigH) : (SmallW, SmallH);

    /// <summary>25 кадрів на секунду: крок каркаса 10 мс ділить його рівно.</summary>
    public const int TickMs = 40;
    public const int Sub = 12;
    /// <summary>Клітинка за 4 тики — 160 мс; з ⚡ — 4/тик, клітинка за 3 тики; 🤖 — 2/тик, клітинка за 6.</summary>
    public const int StepSub = 3, FastStepSub = 4, BotStepSub = 2;
    /// <summary>Снаряд: 8 дванадцятих за тик, ≈ 17 клітинок/с; з 🚀 — 12, рівно клітинка за тик. Жодну не перескочить.</summary>
    public const int ShellSpeed = 8, RapidShellSpeed = 12;
    public const int RapidReloadTicks = 6;
    /// <summary>Натиск за стільки тиків (≈150 мс) до кінця перезарядки не губиться — постріл вийде, щойно можна.</summary>
    public const int BufferTicks = 4;
    /// <summary>🛡 із цегли — п'ять секунд.</summary>
    public const int BonusShieldTicks = 125;
    /// <summary>З якою ймовірністю розбита цегла лишає бонус і скільки він лежить (25 с — щоб устигнути доїхати через пів мапи).</summary>
    public const int DropChance = 25, DropTicks = 625;
    /// <summary>Звідки вилітає снаряд: центр танка плюс стільки в напрямку дула.</summary>
    public const int ShellNose = 7;
    /// <summary>Пів танка в дванадцятих — для влучання снаряда.</summary>
    public const int Half = 6;
    public const int ReloadTicks = 10;
    public const int RespawnTicks = 50;
    public const int ShieldTicks = 38;
    /// <summary>Дві хвилини.</summary>
    public const int MatchTicks = 3000;
    public const int SteelChance = 6, BrickChance = 28;
    /// <summary>Людські місця; за ними — ворожі 🤖 хвиль.</summary>
    public const int Seats = 6, MaxBots = 4, All = Seats + MaxBots;

    /// <summary>0 праворуч, 1 вниз, 2 ліворуч, 3 вгору — як скрізь на платформі.</summary>
    public static readonly (int Dx, int Dy)[] Deltas = [(1, 0), (0, 1), (-1, 0), (0, -1)];

    /// <summary>
    /// Старти по місцях: чотири кути (перші два — по діагоналі, щоб на двох стіл був чесним), далі
    /// середина лівого й правого краю. Мапа дзеркальна, тож усі шість — рівні.
    /// </summary>
    public int[] Starts { get; }
    /// <summary>Звідки лізуть 🤖 хвиль: правий край, верх, середина й низ.</summary>
    public int[] BotSpawns { get; }

    public TankTile[] Tiles { get; }
    /// <summary>0..5 — місця, 6..9 — ворожі 🤖.</summary>
    public Tank[] Tanks { get; }
    public List<Shell> Shells { get; } = [];
    public List<TankDrop> Drops { get; } = [];
    public int Ticks { get; private set; }
    int _nextShell;

    // ---------- проходу №3: команди, бази, хвилі, мапа, помста ----------

    /// <summary>Команда кожного танка; -1 — кожен сам за себе. Своїх снаряди не ранять.</summary>
    public int[] Team { get; }
    /// <summary>Глек-база кожної з двох команд (-1 — нема) і чи він ще цілий.</summary>
    public int[] BaseCell { get; } = [-1, -1];
    public bool[] BaseUp { get; } = [false, false];
    /// <summary>Мапа з кущами й льодом.</summary>
    public bool Wild;
    /// <summary>Помста дає +1 фраг зверху.</summary>
    public bool RevengeFrag;
    /// <summary>Хвилі ворожих 🤖 проти команди 0.</summary>
    public bool Waves;
    /// <summary>Перша хвиля — за три секунди після «Готуйсь»: роз'їхатись і стати біля глека.</summary>
    public const int FirstWaveTicks = 75;
    public const int WaveCount = 5, WaveGapTicks = 100, BotSpawnTicks = 40, BotReloadTicks = 25, BotShieldTicks = 25;
    public int Wave { get; private set; }
    /// <summary>Скільки 🤖 цієї хвилі ще не виїхало.</summary>
    public int WaveLeft { get; private set; }
    int _waveGap, _spawnIn, _spawnAt;
    /// <summary>Усі хвилі відбито.</summary>
    public bool Won { get; private set; }
    /// <summary>Скільки людей грає — від цього залежить, скільки 🤖 у хвилі.</summary>
    int _humans;

    /// <summary>Хто кого скільки разів підбив: [хто, кого], лише люди.</summary>
    public int[,] KillsBy { get; } = new int[Seats, Seats];
    public List<TankEvent> Events { get; } = [];

    public int Cell(int x, int y) => y * W + x;
    public int X(int cell) => cell % W;
    public int Y(int cell) => cell / W;

    public int Ahead(int cell, int dir)
    {
        if (dir is < 0 or > 3) return -1;
        var (dx, dy) = Deltas[dir];
        var (x, y) = (X(cell) + dx, Y(cell) + dy);
        return x < 0 || x >= W || y < 0 || y >= H ? -1 : Cell(x, y);
    }

    /// <summary>Лівий верхній кут танка в дванадцятих; сам танк — квадрат Sub×Sub від нього.</summary>
    public int PosX(Tank t) => X(t.Cell) * Sub + (t.Move >= 0 ? Deltas[t.Move].Dx * t.Step : 0);
    public int PosY(Tank t) => Y(t.Cell) * Sub + (t.Move >= 0 ? Deltas[t.Move].Dy * t.Step : 0);
    /// <summary>Центр танка — від нього рахуються дуло і влучання.</summary>
    public int CenterX(Tank t) => PosX(t) + Sub / 2;
    public int CenterY(Tank t) => PosY(t) + Sub / 2;

    /// <summary>Чи займає танк клітинку: ту, звідки їде, і ту, куди (коли посеред кроку).</summary>
    bool Covers(Tank t, int cell) => t.Cell == cell || (t.Move >= 0 && t.Step > 0 && Ahead(t.Cell, t.Move) == cell);

    bool Occupied(int cell, Tank? who)
    {
        foreach (var o in Tanks)
            if (o != who && o.Alive && Covers(o, cell)) return true;
        return false;
    }

    // ---------- команди ----------

    /// <summary>
    /// Команди перед <see cref="Reset"/>: <paramref name="teams"/> — команда кожного місця (0 ліворуч, 1 праворуч,
    /// -1 не грає). <paramref name="bases"/> — у кожної команди глек у цеглі посеред свого краю; з
    /// <paramref name="waves"/> глек лише в команди 0, а праворуч лізуть 🤖.
    /// </summary>
    public void SetSides(int[]? teams, bool bases, bool waves)
    {
        for (var i = 0; i < All; i++) Team[i] = i >= Seats ? (waves ? 1 : -1) : teams is not null && i < teams.Length ? teams[i] : -1;
        Waves = waves;
        BaseCell[0] = bases || waves ? Cell(1, H / 2) : -1;
        BaseCell[1] = bases && !waves ? Cell(W - 2, H / 2) : -1;
    }

    /// <summary>Місця команди на своєму краю: два кути, далі між кутом і глеком.</summary>
    public int[] Slots(int team)
    {
        var x = team == 0 ? 1 : W - 2;
        var q = (1 + H / 2) / 2;
        return [Cell(x, 1), Cell(x, H - 2), Cell(x, q), Cell(x, H - 1 - q)];
    }

    /// <summary>
    /// Цегла навколо глека: дві підкови, відкриті до сталевої рамки. Одна тоненька падала від першого ж 🤖 за
    /// кілька секунд — з двома є час доїхати й заступитись.
    /// </summary>
    int[] Ring(int team)
    {
        var b = BaseCell[team];
        var (x, y) = (X(b), Y(b));
        var dx = team == 0 ? 1 : -1;
        return
        [
            Cell(x, y - 1), Cell(x, y + 1), Cell(x + dx, y - 1), Cell(x + dx, y), Cell(x + dx, y + 1),
            Cell(x, y - 2), Cell(x + dx, y - 2), Cell(x + 2 * dx, y - 2), Cell(x + 2 * dx, y - 1), Cell(x + 2 * dx, y),
            Cell(x + 2 * dx, y + 1), Cell(x + 2 * dx, y + 2), Cell(x + dx, y + 2), Cell(x, y + 2),
        ];
    }

    // ---------- поле ----------

    /// <summary>Лише рамка й танки по кутах: таке поле можна показати в лобі, не витративши жодного числа з Rng.</summary>
    public void Layout()
    {
        Ticks = 0;
        Shells.Clear();
        Drops.Clear();
        Events.Clear();
        Array.Clear(KillsBy);
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                Tiles[Cell(x, y)] = x == 0 || y == 0 || x == W - 1 || y == H - 1 ? TankTile.Steel : TankTile.Free;
        for (var i = 0; i < Seats; i++)
            Tanks[i] = new Tank { Cell = Starts[i], Home = Starts[i], Want = Tanks[i].Want, Dir = X(Starts[i]) < W / 2 ? 0 : 2 };
        for (var i = Seats; i < All; i++) Tanks[i] = new Tank { Bot = true, Cell = BotSpawns[0], Dir = 2 };
        Wave = WaveLeft = _spawnIn = _spawnAt = 0;
        _waveGap = FirstWaveTicks;
        Won = false;
    }

    /// <summary>
    /// Партія: мапа з Rng, дзеркальна по обох осях (генерується чверть із середніми рядом і стовпцем,
    /// решта — відбиття), 3×3 біля стартів порожні, танки на місцях, які цього разу грають.
    /// </summary>
    public void Reset(bool[] plays)
    {
        Layout();
        var teamed = Team[0] >= 0 || Team.Take(Seats).Any(t => t >= 0) || Waves;
        // Командні старти: кожна команда на своєму краю, по черзі місць.
        if (teamed)
        {
            var used = new int[2];
            for (var i = 0; i < Seats; i++)
            {
                var team = Team[i];
                if (team < 0 || i >= plays.Length || !plays[i]) continue;
                var slots = Slots(team);
                var home = slots[Math.Min(used[team]++, slots.Length - 1)];
                Tanks[i].Cell = Tanks[i].Home = home;
                Tanks[i].Dir = team == 0 ? 0 : 2;
            }
        }
        var keep = new bool[W * H];
        void Clear(int c, int r)
        {
            for (var y = Y(c) - r; y <= Y(c) + r; y++)
                for (var x = X(c) - r; x <= X(c) + r; x++)
                    if (x >= 0 && y >= 0 && x < W && y < H) keep[Cell(x, y)] = true;
        }
        if (!teamed) foreach (var s in Starts) Clear(s, 1);
        else
        {
            for (var t = 0; t < 2; t++)
                if (t == 0 || !Waves) foreach (var s in Slots(t)) Clear(s, 1);
            foreach (var b in BaseCell) if (b >= 0) Clear(b, 2);
            if (Waves) foreach (var s in BotSpawns) Clear(s, 1);
        }
        for (var y = 1; y <= H / 2; y++)
            for (var x = 1; x <= W / 2; x++)
            {
                var roll = _rng.Next(100);
                var tile = roll < SteelChance ? TankTile.Steel : roll < SteelChance + BrickChance ? TankTile.Brick : TankTile.Free;
                foreach (var cell in new[] { Cell(x, y), Cell(W - 1 - x, y), Cell(x, H - 1 - y), Cell(W - 1 - x, H - 1 - y) })
                    Tiles[cell] = keep[cell] ? TankTile.Free : tile;
            }
        if (Wild) Grow(keep);
        for (var t = 0; t < 2; t++)
        {
            BaseUp[t] = BaseCell[t] >= 0;
            if (!BaseUp[t]) continue;
            Tiles[BaseCell[t]] = TankTile.Base;
            foreach (var c in Ring(t)) Tiles[c] = TankTile.Brick;
        }
        _humans = 0;
        for (var i = 0; i < Seats; i++)
        {
            var active = i < plays.Length && plays[i];
            Tanks[i].Plays = active;
            Tanks[i].Alive = active;
            if (active) _humans++;
        }
    }

    /// <summary>
    /// Кущі й лід — латками: чверть мапи ділиться на квадрати 3×3, кожному випадає «кущ», «лід» чи нічого,
    /// і в латці 3 з 4 вільних клітинок стають тим, що випало. Та сама дзеркальність, що й у цегли.
    /// </summary>
    void Grow(bool[] keep)
    {
        for (var by = 1; by <= H / 2; by += 3)
            for (var bx = 1; bx <= W / 2; bx += 3)
            {
                var roll = _rng.Next(100);
                var kind = roll < 30 ? TankTile.Bush : roll < 50 ? TankTile.Ice : TankTile.Free;
                for (var y = by; y < by + 3 && y <= H / 2; y++)
                    for (var x = bx; x < bx + 3 && x <= W / 2; x++)
                    {
                        var on = _rng.Next(4) > 0;
                        if (kind == TankTile.Free || !on) continue;
                        foreach (var cell in new[] { Cell(x, y), Cell(W - 1 - x, y), Cell(x, H - 1 - y), Cell(W - 1 - x, H - 1 - y) })
                            if (!keep[cell] && Tiles[cell] == TankTile.Free) Tiles[cell] = kind;
                    }
            }
    }

    // ---------- наміри ----------

    /// <summary>
    /// Тримає напрямок (0..3) або відпустив (-1). Танк, що стоїть, одразу повертає дуло: коротке натискання
    /// — це й розворот, і одна клітинка ходу. Посеред кроку дуло повернеться на межі клітинок.
    /// </summary>
    public void Turn(int seat, int dir)
    {
        if (seat < 0 || seat >= Seats) return;
        var t = Tanks[seat];
        t.Want = dir is >= 0 and <= 3 ? dir : -1;
        if (t.Want >= 0 && t.Alive && t.Move < 0) t.Dir = t.Want;
    }

    /// <summary>Постріл із дула. false — не на полі, снарядів у польоті вже досить або перезарядка.</summary>
    public bool Fire(int seat) => seat >= 0 && seat < All && Shoot(seat);

    /// <summary>
    /// Натиснув «💥»: стріляє зараз, а коли ще не можна — натиск чекає <see cref="BufferTicks"/> тиків
    /// і стріляє сам, щойно перезарядився. true — вистрілив одразу.
    /// </summary>
    public bool Press(int seat)
    {
        if (seat < 0 || seat >= Seats) return false;
        if (Shoot(seat)) return true;
        if (Tanks[seat].Alive) Tanks[seat].Buffer = BufferTicks;
        return false;
    }

    /// <summary>Тримає «💥» (true) чи відпустив: поки тримає, танк стріляє сам, щойно можна.</summary>
    public void Hold(int seat, bool on)
    {
        if (seat < 0 || seat >= Seats) return;
        Tanks[seat].Hold = on;
        if (on) Press(seat);
    }

    bool Shoot(int i)
    {
        var t = Tanks[i];
        if (!t.Alive || t.ShellsOut >= (t.Twin ? 2 : 1) || t.Reload > 0) return false;
        var (dx, dy) = Deltas[t.Dir];
        Shells.Add(new Shell
        {
            Id = ++_nextShell, Owner = i, Dir = t.Dir, Speed = t.Rapid ? RapidShellSpeed : ShellSpeed, Pierce = t.Pierce, Bounce = t.Bounce,
            X = CenterX(t) + dx * ShellNose, Y = CenterY(t) + dy * ShellNose,
        });
        t.Pierce = false;   // 💥 — на один постріл
        t.ShellsOut++;
        t.Shots++;
        t.Buffer = 0;
        t.Reload = t.Bot ? BotReloadTicks : t.Rapid ? RapidReloadTicks : ReloadTicks;
        return true;
    }

    // ---------- тик ----------

    /// <summary>
    /// Один крок світу: спершу лічильники (перезарядка, щит, повернення), хвилі й думки 🤖, потім їдуть танки,
    /// тоді затиснуті й запам'ятовані постріли, і нарешті летять снаряди — той, хто виїхав під снаряд, дістає
    /// його того самого тика.
    /// </summary>
    public void Step()
    {
        Ticks++;
        Timers();
        if (Waves) WaveStep();
        Brains();
        Walk();
        Collect();
        Triggers();
        Fly();
    }

    void Timers()
    {
        for (var i = 0; i < All; i++)
        {
            var t = Tanks[i];
            if (!t.Plays) continue;
            if (t.Reload > 0) t.Reload--;
            if (t.Shield > 0) t.Shield--;
            if (t.Respawn > 0 && --t.Respawn == 0) Spawn(i);
        }
        for (var i = Drops.Count - 1; i >= 0; i--)
            if (--Drops[i].Ttl <= 0) Drops.RemoveAt(i);
    }

    /// <summary>Затиснутий «💥» і натиск із буфера: стріляють, щойно перезарядка й снаряди в польоті дозволяють.</summary>
    void Triggers()
    {
        for (var i = 0; i < Seats; i++)
        {
            var t = Tanks[i];
            if (!t.Alive) { t.Buffer = 0; continue; }
            if (!t.Hold && t.Buffer <= 0) continue;
            if (!Shoot(i) && t.Buffer > 0) t.Buffer--;
        }
    }

    /// <summary>Наїхав на бонус — забрав. Клітинка «в якій центр», як у бомбера. 🤖 бонусів не беруть.</summary>
    void Collect()
    {
        if (Drops.Count == 0) return;
        for (var n = 0; n < Seats; n++)
        {
            var t = Tanks[n];
            if (!t.Alive) continue;
            var cell = Cell(CenterX(t) / Sub, CenterY(t) / Sub);
            for (var i = 0; i < Drops.Count; i++)
            {
                if (Drops[i].Cell != cell) continue;
                Apply(t, Drops[i].Kind);
                Drops.RemoveAt(i);
                break;
            }
        }
    }

    public static void Apply(Tank t, TankBonus kind)
    {
        switch (kind)
        {
            case TankBonus.Speed: t.Fast = true; break;
            case TankBonus.Twin: t.Twin = true; break;
            case TankBonus.Rapid: t.Rapid = true; break;
            case TankBonus.Shield: t.Shield = Math.Max(t.Shield, BonusShieldTicks); break;
            case TankBonus.Pierce: t.Pierce = true; break;
            case TankBonus.Bounce: t.Bounce = true; break;
        }
    }

    static readonly TankBonus[] Kinds = Enum.GetValues<TankBonus>();

    /// <summary>Цегла розлетілась: іноді під нею щось лежить.</summary>
    void Break(int cell)
    {
        Tiles[cell] = TankTile.Free;
        if (_rng.Next(100) >= DropChance) return;
        foreach (var d in Drops) if (d.Cell == cell) return;
        Drops.Add(new TankDrop { Cell = cell, Kind = Kinds[_rng.Next(Kinds.Length)], Ttl = DropTicks });
    }

    /// <summary>Повернення на свій старт, а як він зайнятий — на найближчий вільний свій; зі щитом.</summary>
    void Spawn(int i)
    {
        var t = Tanks[i];
        var spots = Team[i] >= 0 ? Slots(Team[i]) : Starts;
        var spot = spots.OrderBy(c => c == t.Home ? 0 : 1 + Math.Abs(X(c) - X(t.Home)) + Math.Abs(Y(c) - Y(t.Home)))
            .FirstOrDefault(c => !Occupied(c, t) && Tiles[c] != TankTile.Brick && Tiles[c] != TankTile.Steel, t.Home);
        t.Cell = spot;
        t.Move = -1;
        t.Step = 0;
        t.Alive = true;
        t.Slid = false;
        t.Shield = ShieldTicks;
        t.Dir = X(spot) < W / 2 ? 0 : 2;
        if (t.Want >= 0) t.Dir = t.Want;
    }

    /// <summary>Чи можна заїхати: порожня клітинка (кущ і лід — теж), яку не займає інший живий танк.</summary>
    public bool Drivable(int cell, Tank who) =>
        cell >= 0 && Tiles[cell] is TankTile.Free or TankTile.Bush or TankTile.Ice && !Occupied(cell, who);

    void Walk()
    {
        foreach (var t in Tanks)
        {
            if (!t.Alive) continue;
            if (t.Move < 0)
            {
                if (t.Want < 0) continue;
                t.Dir = t.Want;                                  // уперся — все одно дивиться туди
                if (!Drivable(Ahead(t.Cell, t.Want), t)) continue;
                t.Move = t.Want;
                t.Step = 0;
            }
            t.Step += t.Bot ? BotStepSub : t.Fast ? FastStepSub : StepSub;
            if (t.Step < Sub) continue;
            t.Cell = Ahead(t.Cell, t.Move);
            t.Step = 0;
            // ❄ Лід: хто на нього заїхав і не тримає той самий бік — ковзає ще на клітинку (раз).
            if (Tiles[t.Cell] == TankTile.Ice && !t.Slid && t.Want != t.Move && Drivable(Ahead(t.Cell, t.Move), t))
            {
                t.Slid = true;
                continue;
            }
            t.Slid = false;
            t.Move = -1;
        }
    }

    /// <summary>
    /// Скільки дванадцятих снаряд проходить за один підкрок. Раніше снаряд стрибав на всю швидкість за раз
    /// і перевірявся лише в кінці стрибка: два зустрічні снаряди (8 + 8 = 16 за тик) або 🚀 назустріч танку
    /// (12 + 4) могли проскочити один крізь одного — вікно влучання лише 13. Тепер снаряд летить підкроками
    /// по 4, і між двома перевірками відстань змінюється щонайбільше на 8 — повз вікно не проскочиш.
    /// </summary>
    public const int ShellSubStep = 4;

    readonly HashSet<Shell> _gone = [];

    void Fly()
    {
        if (Shells.Count == 0) return;
        var gone = _gone;
        gone.Clear();
        var most = 0;
        foreach (var s in Shells) most = Math.Max(most, s.Speed);
        for (var done = 0; done < most; done += ShellSubStep)
        {
            foreach (var s in Shells)
            {
                if (gone.Contains(s) || done >= s.Speed) continue;
                var (dx, dy) = Deltas[s.Dir];
                var step = Math.Min(ShellSubStep, s.Speed - done);
                s.X += dx * step;
                s.Y += dy * step;
                Strike(s, gone);
            }
            // Два снаряди в одній точці гасять один одного — і лоб у лоб, і навздогін.
            for (var a = 0; a < Shells.Count; a++)
                for (var b = a + 1; b < Shells.Count; b++)
                {
                    var (p, q) = (Shells[a], Shells[b]);
                    if (gone.Contains(p) || gone.Contains(q)) continue;
                    if (Math.Abs(p.X - q.X) <= Half && Math.Abs(p.Y - q.Y) <= Half) { gone.Add(p); gone.Add(q); }
                }
        }
        if (gone.Count == 0) return;
        foreach (var s in gone)
        {
            Shells.Remove(s);
            Tanks[s.Owner].ShellsOut = Math.Max(0, Tanks[s.Owner].ShellsOut - 1);
        }
    }

    /// <summary>Снаряд щойно зрушив: що він зачепив у новій точці — край, сталь, цеглу, глек чи чужий танк.</summary>
    void Strike(Shell s, HashSet<Shell> gone)
    {
        // Снаряд — точка; клітинка, в якій вона зараз. За краєм поля він просто зникає.
        var (cx, cy) = (s.X / Sub, s.Y / Sub);
        if (s.X < 0 || s.Y < 0 || cx >= W || cy >= H) { gone.Add(s); return; }
        var cell = Cell(cx, cy);
        var border = cx == 0 || cy == 0 || cx == W - 1 || cy == H - 1;
        switch (Tiles[cell])
        {
            case TankTile.Steel:
                if (s.Pierce && !border) { Tiles[cell] = TankTile.Free; gone.Add(s); return; }   // 💥 ламає сталь, але не рамку
                if (s.Bounce) { Ricochet(s, cell); return; }
                gone.Add(s);
                return;
            case TankTile.Brick:
                Break(cell);
                if (!s.Pierce) { gone.Add(s); return; }                // 💥 летить далі крізь цеглу
                break;
            case TankTile.Base:
                gone.Add(s);
                var team = BaseCell[0] == cell ? 0 : 1;
                if (!BaseUp[team] || Team[s.Owner] == team) return;   // свій глек своїм снарядом не б'ється
                BaseUp[team] = false;
                Events.Add(new TankEvent(TankHow.Base, s.Owner, team, 0));
                return;
        }
        for (var i = 0; i < All; i++)
        {
            var t = Tanks[i];
            if (i == s.Owner || !t.Alive) continue;
            if (Math.Abs(s.X - CenterX(t)) > Half || Math.Abs(s.Y - CenterY(t)) > Half) continue;
            gone.Add(s);
            if (Team[i] >= 0 && Team[i] == Team[s.Owner]) return;    // свій: снаряд спинився, нікого не ранив
            Tanks[s.Owner].Hits++;
            if (t.Shield > 0) return;
            Kill(i, s.Owner);
            return;
        }
    }

    /// <summary>
    /// 🪃 Рикошет: снаряд вертається в центр клітинки перед сталлю й повертає праворуч за ходом; праворуч сталь —
    /// ліворуч; і там сталь — летить назад. Відскакує раз.
    /// </summary>
    void Ricochet(Shell s, int steel)
    {
        var (dx, dy) = Deltas[s.Dir];
        var back = Cell(X(steel) - dx, Y(steel) - dy);
        s.Bounce = false;
        s.X = X(back) * Sub + Sub / 2;
        s.Y = Y(back) * Sub + Sub / 2;
        var right = (s.Dir + 1) % 4;
        var left = (s.Dir + 3) % 4;
        bool Open(int d) { var c = Ahead(back, d); return c >= 0 && Tiles[c] != TankTile.Steel; }
        s.Dir = Open(right) ? right : Open(left) ? left : (s.Dir + 2) % 4;
    }

    void Kill(int v, int by)
    {
        var t = Tanks[v];
        t.Alive = false;
        t.Move = -1;
        t.Step = 0;
        t.Shield = 0;
        t.Slid = false;
        t.Respawn = t.Bot ? 0 : RespawnTicks;
        t.Strip();
        t.Deaths++;
        t.Streak = 0;
        if (by < 0 || by >= All) return;
        var k = Tanks[by];
        var revenge = !k.Bot && !t.Bot && k.Nemesis == v;
        if (!k.Bot) t.Nemesis = by;          // помста — лише людям: слот 🤖 після підбиття віддають новому
        k.Frags += revenge && RevengeFrag ? 2 : 1;
        k.Streak++;
        k.BestStreak = Math.Max(k.BestStreak, k.Streak);
        if (revenge)
        {
            k.Nemesis = -1;
            k.Revenges++;
        }
        if (by < Seats && v < Seats) KillsBy[by, v]++;
        Events.Add(new TankEvent(revenge ? TankHow.Revenge : TankHow.Kill, by, v, k.Streak));
    }

    /// <summary>Хтось устав із-за столу: танк зникає, фраги лишаються в рахунку.</summary>
    public void Drop(int seat)
    {
        if (seat < 0 || seat >= Seats) return;
        var t = Tanks[seat];
        t.Alive = false;
        t.Plays = false;
        t.Respawn = 0;
        t.Want = -1;
        t.Hold = false;
        t.Buffer = 0;
        if (_humans > 0) _humans--;
    }

    // ---------- хвилі 🤖 ----------

    /// <summary>Скільки 🤖 у хвилі: 3, 4, 5, 6, 7 на двох, і по одному зверху за кожного третього-четвертого.</summary>
    public int BotsFor(int wave) => 1 + wave + Math.Max(1, _humans - 1);
    /// <summary>Скільки 🤖 водночас на полі: від двох на першій хвилі до чотирьох.</summary>
    public int AtOnce(int wave) => Math.Min(MaxBots, 2 + wave / 2 + (_humans >= 3 ? 1 : 0));

    public int BotsAlive()
    {
        var n = 0;
        for (var i = Seats; i < All; i++) if (Tanks[i].Alive) n++;
        return n;
    }

    void WaveStep()
    {
        if (Won) return;
        if (_waveGap > 0)
        {
            if (--_waveGap > 0) return;
            Wave++;
            WaveLeft = BotsFor(Wave);
            _spawnIn = 0;
            Events.Add(new TankEvent(TankHow.Wave, -1, -1, Wave));
            return;
        }
        var alive = BotsAlive();
        if (WaveLeft > 0)
        {
            if (--_spawnIn <= 0 && alive < AtOnce(Wave) && SpawnBot()) { WaveLeft--; _spawnIn = BotSpawnTicks; }
            return;
        }
        if (alive > 0) return;
        if (Wave >= WaveCount)
        {
            Won = true;
            Events.Add(new TankEvent(TankHow.Won, -1, -1, Wave));
            return;
        }
        _waveGap = WaveGapTicks;
    }

    /// <summary>Новий 🤖 на вільній точці правого краю (по черзі); усі зайняті — почекає.</summary>
    bool SpawnBot()
    {
        var slot = -1;
        for (var i = Seats; i < All; i++) if (!Tanks[i].Alive) { slot = i; break; }
        if (slot < 0) return false;
        for (var k = 0; k < BotSpawns.Length; k++)
        {
            var at = BotSpawns[(_spawnAt + k) % BotSpawns.Length];
            if (Occupied(at, null) || Tiles[at] is TankTile.Brick or TankTile.Steel) continue;
            _spawnAt = (_spawnAt + k + 1) % BotSpawns.Length;
            var t = Tanks[slot];
            t.Cell = t.Home = at;
            t.Move = -1;
            t.Step = 0;
            t.Want = -1;
            t.Dir = 2;
            t.Alive = t.Plays = true;
            t.Shield = BotShieldTicks;
            t.Reload = BotReloadTicks;
            t.Think = 0;
            t.ShellsOut = 0;
            t.Strip();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Думки 🤖 — прості: їде до глека (нема глека — до найближчої людини), більшою різницею вперед; цегла на
    /// шляху — стріляє в неї; сталь чи свій — пробує інший бік; інколи звертає навмання, щоб не застрягати.
    /// Стріляє, коли дуло дивиться на людину чи глек без сталі між ними, або впритул у цеглу.
    /// </summary>
    void Brains()
    {
        if (!Waves) return;
        for (var i = Seats; i < All; i++)
        {
            var t = Tanks[i];
            if (!t.Alive) continue;
            if (t.Move < 0 && (--t.Think <= 0 || (t.Want >= 0 && Blocked(Ahead(t.Cell, t.Want)))))
            {
                t.Want = Choose(t);
                t.Think = 3 + _rng.Next(5);   // стільки клітинок їде, перш ніж подумати знову
            }
            if (t.Reload == 0 && t.ShellsOut == 0 && Sees(t)) Shoot(i);
        }
    }

    /// <summary>Туди бот не проїде й не проб'ється: сталь, рамка чи інший танк.</summary>
    bool Blocked(int cell) => cell < 0 || Tiles[cell] == TankTile.Steel || (Tiles[cell] != TankTile.Brick && Tiles[cell] != TankTile.Base && Occupied(cell, null));

    int Choose(Tank t)
    {
        // Здебільшого — на глек, але частина думок — про найближчу людину: інакше 🤖 не помічали б, хто їх б'є.
        var target = BaseCell[0] >= 0 && BaseUp[0] && _rng.Next(100) < 45 ? BaseCell[0] : -1;
        if (target < 0)
        {
            var best = int.MaxValue;
            for (var i = 0; i < Seats; i++)
            {
                var h = Tanks[i];
                if (!h.Alive) continue;
                var d = Math.Abs(X(h.Cell) - X(t.Cell)) + Math.Abs(Y(h.Cell) - Y(t.Cell));
                if (d < best) { best = d; target = h.Cell; }
            }
        }
        if (target < 0 && BaseUp[0] && BaseCell[0] >= 0) target = BaseCell[0];
        if (target < 0 || _rng.Next(100) < 20) return _rng.Next(4);
        var dx = X(target) - X(t.Cell);
        var dy = Y(target) - Y(t.Cell);
        var hx = dx > 0 ? 0 : 2;
        var hy = dy > 0 ? 1 : 3;
        var (first, second) = Math.Abs(dx) >= Math.Abs(dy) ? (dx == 0 ? hy : hx, dy == 0 ? -1 : hy) : (hy, dx == 0 ? -1 : hx);
        if (!Blocked(Ahead(t.Cell, first))) return first;
        if (second >= 0 && !Blocked(Ahead(t.Cell, second))) return second;
        for (var k = 0; k < 4; k++)
        {
            var d = (first + 1 + k) % 4;
            if (!Blocked(Ahead(t.Cell, d))) return d;
        }
        return first;
    }

    /// <summary>Чи варто бахнути зараз: у лінії дула людина чи глек (сталь заступає), або цегла впритул.</summary>
    bool Sees(Tank t)
    {
        var cell = t.Cell;
        for (var n = 1; n <= 16; n++)
        {
            cell = Ahead(cell, t.Dir);
            if (cell < 0) return false;
            var tile = Tiles[cell];
            if (tile == TankTile.Steel) return false;
            if (tile == TankTile.Base) return BaseUp[0] && cell == BaseCell[0];
            if (tile == TankTile.Brick) return n == 1 || _rng.Next(100) < 4;
            for (var i = 0; i < Seats; i++)
                if (Tanks[i].Alive && Covers(Tanks[i], cell)) return true;
        }
        return false;
    }

    public int[] BrickCells() => Cells(TankTile.Brick);
    public int[] SteelCells() => Cells(TankTile.Steel);
    public int[] BushCells() => Cells(TankTile.Bush);
    public int[] IceCells() => Cells(TankTile.Ice);

    int[] Cells(TankTile kind)
    {
        var n = 0;
        foreach (var t in Tiles) if (t == kind) n++;
        var list = new int[n];
        n = 0;
        for (var c = 0; c < Tiles.Length; c++) if (Tiles[c] == kind) list[n++] = c;
        return list;
    }
}
