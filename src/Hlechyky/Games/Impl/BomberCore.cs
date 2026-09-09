namespace Hlechyky.Games.Impl;

/// <summary>Що стоїть у клітинці: порожньо, незламна стіна або ящик.</summary>
public enum BomberTile { Free, Wall, Box }

/// <summary>Що випадає з розбитого ящика: довший вибух, ще одна бомба, черевики.</summary>
public enum BomberBonus { Range, Bomb, Boots }

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
}

/// <summary>
/// Бомба на полі. Радіус запам'ятовується при постановці: інакше вогонь, підібраний уже після того, як
/// запал пішов, ретроспективно робив би вибух довшим, і гравець не міг би розрахувати, куди тікати.
/// </summary>
public sealed class BomberBomb
{
    public required int Cell { get; init; }
    public required int Owner { get; init; }
    public required int Range { get; init; }
    public int Fuse;
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
/// <param name="rng">Сідований генератор кімнати: те саме поле й ті самі бонуси з тим самим сідом.</param>
public sealed class BomberCore(Random rng)
{
    public const int W = 15, H = 13;
    public const int TickMs = 60;
    /// <summary>Дванадцяті частки клітинки: 12 ділиться і на 3 (черевики), і на 4 (звичайний крок).</summary>
    public const int Sub = 12;
    /// <summary>Звичайний крок: 4 тики на клітинку — 240 мс.</summary>
    public const int SlowStep = 3;
    /// <summary>У черевиках: 3 тики на клітинку — 180 мс.</summary>
    public const int FastStep = 4;
    /// <summary>Запал — рівно дві секунди.</summary>
    public const int FuseTicks = 33;
    /// <summary>Полум'я живе десь чотири десятих секунди.</summary>
    public const int FlameTicks = 7;
    /// <summary>Дві хвилини — і раунд нічий, скільки б там хто не бігав.</summary>
    public const int RoundTicks = 2000;
    public const int StartBombs = 1, MaxBombs = 4;
    public const int StartRange = 2, MaxRange = 6;
    /// <summary>Скільки відсотків вільних клітинок заставляємо ящиками.</summary>
    public const int BoxChance = 55;
    /// <summary>З якою ймовірністю ящик лишає по собі бонус.</summary>
    public const int DropChance = 30;
    /// <summary>Місць за столом — стільки ж кутів на полі.</summary>
    public const int Seats = 4;

    /// <summary>0 праворуч, 1 вниз, 2 ліворуч, 3 вгору — як скрізь на платформі.</summary>
    public static readonly (int Dx, int Dy)[] Deltas = [(1, 0), (0, 1), (-1, 0), (0, -1)];

    /// <summary>
    /// Стартові кути. Перші два — по діагоналі: на двох гравцях стіл має бути чесним, а не «сусіди через
    /// одну стіну». Третій і четвертий добирають решту кутів.
    /// </summary>
    public static readonly int[] Corners = [Cell(1, 1), Cell(W - 2, H - 2), Cell(W - 2, 1), Cell(1, H - 2)];

    public BomberTile[] Tiles { get; } = new BomberTile[W * H];
    /// <summary>Скільки тиків ще горіти в кожній клітинці; 0 — не горить.</summary>
    public int[] Flame { get; } = new int[W * H];
    public BomberMan[] Players { get; } = [.. Enumerable.Range(0, Seats).Select(_ => new BomberMan())];
    public List<BomberBomb> Bombs { get; } = [];
    public List<BomberDrop> Drops { get; } = [];
    /// <summary>Скільки тиків триває цей раунд.</summary>
    public int Ticks { get; private set; }

    public static int Cell(int x, int y) => y * W + x;
    public static int X(int cell) => cell % W;
    public static int Y(int cell) => cell / W;

    /// <summary>Сусідня клітинка в напрямку dir; -1 — за краєм поля.</summary>
    public static int Ahead(int cell, int dir)
    {
        if (dir is < 0 or > 3) return -1;
        var (dx, dy) = Deltas[dir];
        var (x, y) = (X(cell) + dx, Y(cell) + dy);
        return x < 0 || x >= W || y < 0 || y >= H ? -1 : Cell(x, y);
    }

    /// <summary>Клітинка, «в якій центр» бомбера: саме за нею рахують бомби, бонуси й смерть у полум'ї.</summary>
    public static int Center(BomberMan p)
    {
        if (p.Move < 0 || p.Step * 2 < Sub) return p.Cell;
        var next = Ahead(p.Cell, p.Move);
        return next < 0 ? p.Cell : next;
    }

    public static int PosX(BomberMan p) => X(p.Cell) * Sub + (p.Move >= 0 ? Deltas[p.Move].Dx * p.Step : 0);
    public static int PosY(BomberMan p) => Y(p.Cell) * Sub + (p.Move >= 0 ? Deltas[p.Move].Dy * p.Step : 0);

    public int AliveCount => Players.Count(p => p.Alive);

    /// <summary>Раунд скінчився: лишився щонайбільше один живий або вийшов час.</summary>
    public bool RoundOver => AliveCount <= 1 || Ticks >= RoundTicks;

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

    /// <summary>Чи можна зайти в цю клітинку: не стіна, не ящик і не чужа бомба.</summary>
    public bool Walkable(int cell) =>
        cell >= 0 && Tiles[cell] == BomberTile.Free && !Bombs.Any(b => b.Cell == cell);

    // ---------- поле ----------

    /// <summary>
    /// Тільки геометрія: рамка по краю, «стовпи» на парних координатах і бомбери по кутах. Випадковості
    /// тут нема свідомо — таке поле можна показати в лобі, ще не витративши жодного числа з Ctx.Rng.
    /// </summary>
    public void Layout()
    {
        Ticks = 0;
        Bombs.Clear();
        Drops.Clear();
        Array.Clear(Flame);
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                Tiles[Cell(x, y)] = x == 0 || y == 0 || x == W - 1 || y == H - 1 || (x % 2 == 0 && y % 2 == 0)
                    ? BomberTile.Wall
                    : BomberTile.Free;
        // Напрямок, який людина тримає пальцем чи клавішею, переживає новий раунд: інакше той, хто не
        // відпускав стрілку на «Готуйсь», стояв би стовпом, поки не перетисне клавішу.
        for (var i = 0; i < Seats; i++) Players[i] = new BomberMan { Cell = Corners[i], Want = Players[i].Want };
    }

    /// <summary>Новий раунд: свіже поле з ящиками і живі бомбери на тих місцях, які цього разу грають.</summary>
    public void Reset(bool[] plays)
    {
        Layout();
        // 3×3 навколо кожного кута лишаємо порожнім — і за чотирьох, і за двох гравців. Інакше той, кому
        // випав кут із ящиками під носом, першу ж свою бомбу ставив би собі в глухий кут.
        var safe = new HashSet<int>();
        foreach (var corner in Corners)
            for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                    safe.Add(Cell(X(corner) + dx, Y(corner) + dy));
        for (var y = 1; y < H - 1; y++)
            for (var x = 1; x < W - 1; x++)
            {
                var cell = Cell(x, y);
                if (Tiles[cell] != BomberTile.Free || safe.Contains(cell)) continue;
                if (rng.Next(100) < BoxChance) Tiles[cell] = BomberTile.Box;
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
        if (Bombs.Any(b => b.Cell == cell)) return false;
        Bombs.Add(new BomberBomb { Cell = cell, Owner = seat, Range = p.Range, Fuse = FuseTicks });
        p.Fused++;
        return true;
    }

    // ---------- тик ----------

    /// <summary>
    /// Один крок світу. Порядок важливий: спершу догоряє старе полум'я, потім вибухають бомби (разом із
    /// ланцюгом), і лише тоді рухаються бомбери — тож той, хто вбіг у щойно народжене полум'я, гине
    /// того самого тика, а не наступного.
    /// </summary>
    public void Step()
    {
        Ticks++;
        Cool();
        Fuses();
        Walk();
        Collect();
        Reap();
    }

    void Cool()
    {
        for (var i = 0; i < Flame.Length; i++)
            if (Flame[i] > 0) Flame[i]--;
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
            owner.Fused = Math.Max(0, owner.Fused - 1);
            Burn(bomb.Cell);
            foreach (var (dx, dy) in Deltas)
            {
                var (x, y) = (X(bomb.Cell), Y(bomb.Cell));
                for (var i = 0; i < bomb.Range; i++)
                {
                    x += dx;
                    y += dy;
                    if (x < 0 || x >= W || y < 0 || y >= H) break;
                    var cell = Cell(x, y);
                    if (Tiles[cell] == BomberTile.Wall) break;
                    if (broken.Contains(cell)) break;   // цей ящик уже зламала сусідка по ланцюгу — далі не йдемо
                    if (Tiles[cell] == BomberTile.Box)
                    {
                        // Підпалюємо руками, а не через Burn: бонус, який щойно випав із цього ящика,
                        // не має згоріти в тому самому полум'ї, що його й відкрило.
                        Flame[cell] = FlameTicks;
                        broken.Add(cell);
                        Break(cell);
                        break;
                    }
                    Burn(cell);
                    // Сусідська бомба летить у ту саму чергу, а промінь іде далі: ланцюг має доганяти все підряд.
                    var next = Bombs.FirstOrDefault(b => b.Cell == cell);
                    if (next is not null && doomed.Add(next)) queue.Enqueue(next);
                }
            }
        }
    }

    void Burn(int cell)
    {
        Flame[cell] = FlameTicks;
        Drops.RemoveAll(d => d.Cell == cell);   // бонус, що лежав на дорозі, згоряє
    }

    void Break(int cell)
    {
        Tiles[cell] = BomberTile.Free;
        if (rng.Next(100) >= DropChance) return;
        Drops.Add(new BomberDrop { Cell = cell, Kind = (BomberBonus)rng.Next(3) });
    }

    void Walk()
    {
        foreach (var p in Players)
        {
            if (!p.Alive) continue;
            if (p.Move < 0)
            {
                if (p.Want < 0) continue;
                if (!Walkable(Ahead(p.Cell, p.Want))) continue;   // уперся — стоїть і чекає
                p.Move = p.Want;
                p.Step = 0;
            }
            p.Step += p.Boots ? FastStep : SlowStep;
            if (p.Step < Sub) continue;
            // Sub ділиться і на 3, і на 4 без остачі, тож крок завжди завершується рівно в клітинці.
            p.Cell = Ahead(p.Cell, p.Move);
            p.Step = 0;
            p.Move = -1;
        }
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
                default: p.Boots = true; break;
            }
            Drops.RemoveAt(i);
        }
    }

    void Reap()
    {
        foreach (var p in Players)
            if (p.Alive && Flame[Center(p)] > 0) p.Alive = false;
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
