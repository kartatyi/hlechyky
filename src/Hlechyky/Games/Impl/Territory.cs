using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Один загарбник: голова, напрямок, черга поворотів і відлік до воскресіння. Тіла в нього нема — є лише
/// слід, який він лишає за межами своєї землі, і саме той слід у цій грі вбиває.
/// </summary>
public sealed class TerritoryRider
{
    /// <summary>Це місце грає цього раунду. Порожні місця стоять поза полем і в підрахунок не йдуть.</summary>
    public bool On;
    public bool Alive;
    public int X, Y;
    /// <summary>0 праворуч, 1 вниз, 2 ліворуч, 3 вгору — як і скрізь на платформі.</summary>
    public int Dir;
    /// <summary>Скільки тиків лишилось до воскресіння; 0 — або живий, або ще не грав.</summary>
    public int RespawnIn;
    /// <summary>Чи є зараз на полі його слід: без цього повернення додому не рахувалось би замиканням.</summary>
    public bool HasTrail;
    public readonly Queue<int> Turns = new();
}

/// <summary>
/// Поле і правила загарбання — без жодного слова про кімнати й чат. Дві сітки на 40×30: <see cref="Owner"/>
/// (чия земля) і <see cref="Trail"/> (чий слід). Обидві тримаємо байтами, де 0 — нічия, а 1..4 — номер місця
/// плюс один: так вид їде на дріт рядком із 1200 символів, а кадр — списком змінених клітинок.
/// </summary>
/// <param name="rng">Сідований генератор кімнати: воскресіння на випадковому місці має відтворюватись у тестах.</param>
public sealed class TerritoryCore(Random rng)
{
    public const int W = 40, H = 30;
    public const int Cells = W * H;
    public const int TickMs = 100;
    /// <summary>Раунд — 90 секунд, тобто 900 тиків по 100 мс.</summary>
    public const int RoundTicks = 900;
    /// <summary>Три секунди на роздуми після того, як згорів.</summary>
    public const int RespawnTicks = 30;
    /// <summary>Далі вже не пам'ять гравця, а хвіст лагу — як у змійки.</summary>
    public const int MaxQueued = 2;
    public const int MaxPlayers = 4;
    /// <summary>Наділ на старті — квадрат 3×3 навколо голови.</summary>
    public const int Plot = 1;

    static readonly (int Dx, int Dy)[] Deltas = [(1, 0), (0, 1), (-1, 0), (0, -1)];

    /// <summary>
    /// Кутки поля, звідки починають, і куди дивиться голова. Пари 0–1 і 2–3 стоять по діагоналі, а напрямки
    /// підібрані так, щоб жодні двоє не їхали назустріч по одному ряду: інакше четверо, які просто нічого не
    /// натиснули, згорали б лоб у лоб уже на першій секунді. Уздовж поля їхати теж є куди — до стіни двадцять
    /// із гаком клітинок, тобто дві секунди на подумати.
    /// </summary>
    static readonly (int X, int Y, int Dir)[] Starts = [(8, 7, 0), (31, 22, 2), (31, 7, 1), (8, 22, 3)];

    readonly HashSet<int> _ownerChanged = [];
    readonly HashSet<int> _trailChanged = [];
    readonly int[] _area = new int[MaxPlayers];
    readonly bool[] _seen = new bool[Cells];
    readonly Queue<int> _bfs = new();

    /// <summary>Чия це земля: 0 — нічия, інакше номер місця плюс один.</summary>
    public byte[] Owner { get; } = new byte[Cells];

    /// <summary>Чий тут слід: 0 — чисто, інакше номер місця плюс один.</summary>
    public byte[] Trail { get; } = new byte[Cells];

    /// <summary>Голови по місцях; довжина завжди <see cref="MaxPlayers"/>, щоб індекс дорівнював місцю.</summary>
    public TerritoryRider[] Riders { get; } = [.. Enumerable.Range(0, MaxPlayers).Select(_ => new TerritoryRider())];

    /// <summary>Скільки тиків минуло від початку раунду.</summary>
    public int Ticks { get; private set; }

    public int TicksLeft => Math.Max(0, RoundTicks - Ticks);

    /// <summary>Скільки місць грає цього раунду.</summary>
    public int Playing => Riders.Count(r => r.On);

    /// <summary>Клітинки, у яких за останній тик змінився господар. Саме з них збирається кадр.</summary>
    public IReadOnlyCollection<int> OwnerChanged => _ownerChanged;

    /// <summary>Клітинки, у яких за останній тик з'явився або згас слід.</summary>
    public IReadOnlyCollection<int> TrailChanged => _trailChanged;

    public static int Cell(int x, int y) => y * W + x;

    /// <summary>Скільки клітинок належить місцю seat.</summary>
    public int Area(int seat) => seat >= 0 && seat < MaxPlayers ? _area[seat] : 0;

    /// <summary>
    /// Частка поля у відсотках, з одним знаком: 9 клітинок на старті — це 0,8 %. Половинку округлюємо
    /// вгору, а не «до парного»: інакше 15 клітинок ставали б 1,2 %, а 45 — 3,8 %, і гравець не розумів би,
    /// чому однакові півклітинки їдуть у різні боки.
    /// </summary>
    public double Percent(int seat) => Math.Round(Area(seat) * 100.0 / Cells, 1, MidpointRounding.AwayFromZero);

    /// <summary>Новий раунд. <paramref name="seated"/> — які місця зайняті; порожні на полі не з'являються.</summary>
    public void Reset(IReadOnlyList<bool> seated)
    {
        Array.Clear(Owner);
        Array.Clear(Trail);
        Array.Clear(_area);
        _ownerChanged.Clear();
        _trailChanged.Clear();
        Ticks = 0;
        for (var i = 0; i < MaxPlayers; i++)
        {
            var r = Riders[i];
            r.On = i < seated.Count && seated[i];
            r.Alive = false;
            r.HasTrail = false;
            r.RespawnIn = 0;
            r.X = r.Y = -1;
            r.Dir = 0;
            r.Turns.Clear();
            if (r.On) Spawn(i, Starts[i].X, Starts[i].Y, Starts[i].Dir);
        }
    }

    /// <summary>Чи саме ці місця грають зараз — щоб у лобі не перекладати поле на кожен вид.</summary>
    public bool SameSeats(IReadOnlyList<bool> seated)
    {
        for (var i = 0; i < MaxPlayers; i++)
            if (Riders[i].On != (i < seated.Count && seated[i])) return false;
        return true;
    }

    /// <summary>
    /// Гравець просить повернути. Розворот на 180° і повтор того самого ігноруємо, а кожен наступний поворот
    /// міряємо від останнього в черзі: без цього два швидкі натиски злипались би в один і везли б у власний слід.
    /// </summary>
    public void Turn(int seat, int dir)
    {
        if (dir is < 0 or > 3 || seat is < 0 or >= MaxPlayers) return;
        var r = Riders[seat];
        if (!r.On || !r.Alive || r.Turns.Count >= MaxQueued) return;
        var last = r.Turns.Count > 0 ? r.Turns.Last() : r.Dir;
        if (dir == last || (dir + 2) % 4 == last) return;
        r.Turns.Enqueue(dir);
    }

    /// <summary>Один крок усього поля: воскресіння, повороти, зіткнення, слід і замикання.</summary>
    public void Step()
    {
        Ticks++;
        _ownerChanged.Clear();
        _trailChanged.Clear();

        // Хто відсидів свої три секунди — повертається на вільне місце з новим наділом.
        for (var i = 0; i < MaxPlayers; i++)
        {
            var r = Riders[i];
            if (!r.On || r.Alive || r.RespawnIn <= 0) continue;
            if (--r.RespawnIn == 0) Respawn(i);
        }

        for (var i = 0; i < MaxPlayers; i++)
        {
            var r = Riders[i];
            if (r.On && r.Alive && r.Turns.Count > 0) r.Dir = r.Turns.Dequeue();
        }

        Span<int> next = stackalloc int[MaxPlayers];
        Span<bool> moves = stackalloc bool[MaxPlayers];
        var dead = new bool[MaxPlayers];
        for (var i = 0; i < MaxPlayers; i++)
        {
            next[i] = -1;
            moves[i] = false;
            var r = Riders[i];
            if (!r.On || !r.Alive) continue;
            var (dx, dy) = Deltas[r.Dir];
            var (x, y) = (r.X + dx, r.Y + dy);
            if (x < 0 || x >= W || y < 0 || y >= H) { dead[i] = true; continue; }   // за поле — і по всьому
            next[i] = Cell(x, y);
            moves[i] = true;
        }

        // Лоб у лоб: дві голови просяться в одну клітинку — горять обидві.
        for (var i = 0; i < MaxPlayers; i++)
            for (var j = i + 1; j < MaxPlayers; j++)
                if (moves[i] && moves[j] && next[i] == next[j]) dead[i] = dead[j] = true;

        // Слід. Наїхав на чужий — горить його власник, наїхав на свій — сам. Список смертей беремо
        // знімком: усе відбувається одночасно, тож той, кому щойно перерізали слід, устигає перерізати чужий.
        var stopped = (bool[])dead.Clone();
        for (var i = 0; i < MaxPlayers; i++)
        {
            if (!moves[i] || stopped[i]) continue;
            var who = Trail[next[i]];
            if (who != 0) dead[who - 1] = true;
        }

        for (var i = 0; i < MaxPlayers; i++)
        {
            if (!moves[i] || dead[i]) continue;
            var r = Riders[i];
            r.X = next[i] % W;
            r.Y = next[i] / W;
        }

        // Спершу гасимо тих, хто згорів (їхня земля і слід зникають), і лише потім кладемо новий слід:
        // інакше той, хто щойно переїхав чужу мітку, поклав би свою під ту, що зараз зітруть.
        for (var i = 0; i < MaxPlayers; i++)
            if (dead[i]) Burn(i);

        for (var i = 0; i < MaxPlayers; i++)
        {
            if (!moves[i] || dead[i]) continue;
            var r = Riders[i];
            var cell = next[i];
            if (Owner[cell] == (byte)(i + 1))
            {
                if (r.HasTrail) Capture(i);      // повернувся додому — усе обведене твоє
            }
            else
            {
                SetTrail(cell, (byte)(i + 1));
                r.HasTrail = true;
            }
        }

        // Землю можна забрати всю: без неї повертатись нема куди, тож гравець теж згоряє і починає спочатку.
        for (var i = 0; i < MaxPlayers; i++)
        {
            var r = Riders[i];
            if (r.On && r.Alive && !dead[i] && _area[i] == 0) Burn(i);
        }
    }

    /// <summary>
    /// Замикання: заливка від краю по клітинках, які НЕ мої (4-зв'язність — крізь діагональний кут вона
    /// не пролізе), а все, куди вона не дійшла, стає моїм. Чужа земля всередині петлі переходить теж.
    /// </summary>
    public void Capture(int seat)
    {
        var me = (byte)(seat + 1);
        Array.Clear(_seen);
        _bfs.Clear();
        for (var x = 0; x < W; x++) { Push(Cell(x, 0)); Push(Cell(x, H - 1)); }
        for (var y = 0; y < H; y++) { Push(Cell(0, y)); Push(Cell(W - 1, y)); }
        while (_bfs.Count > 0)
        {
            var c = _bfs.Dequeue();
            var (x, y) = (c % W, c / W);
            if (x > 0) Push(c - 1);
            if (x < W - 1) Push(c + 1);
            if (y > 0) Push(c - W);
            if (y < H - 1) Push(c + W);
        }
        for (var c = 0; c < Cells; c++)
        {
            if (!_seen[c]) SetOwner(c, me);
            if (Trail[c] == me) SetTrail(c, 0);
        }
        Riders[seat].HasTrail = false;

        void Push(int c)
        {
            if (_seen[c] || Owner[c] == me || Trail[c] == me) return;
            _seen[c] = true;
            _bfs.Enqueue(c);
        }
    }

    /// <summary>Гравець згорів: земля і слід зникають, а сам він повертається через <see cref="RespawnTicks"/>.</summary>
    public void Burn(int seat)
    {
        var r = Riders[seat];
        if (!r.On || !r.Alive) return;
        r.Alive = false;
        r.HasTrail = false;
        r.RespawnIn = RespawnTicks;
        r.Turns.Clear();
        var me = (byte)(seat + 1);
        for (var c = 0; c < Cells; c++)
        {
            if (Owner[c] == me) SetOwner(c, 0);
            if (Trail[c] == me) SetTrail(c, 0);
        }
    }

    /// <summary>Намалювати землю руками: стартові наділи і розкладки тестів ходять сюди ж, щоб площі не розійшлись.</summary>
    public void PaintOwner(int cell, int seat) => SetOwner(cell, (byte)(seat + 1));

    public void PaintTrail(int cell, int seat) => SetTrail(cell, (byte)(seat + 1));

    /// <summary>Стерти поле, не чіпаючи голів: так тест розкладає землю з нуля, а площі лишаються чесними.</summary>
    public void Wipe()
    {
        for (var c = 0; c < Cells; c++)
        {
            SetOwner(c, 0);
            SetTrail(c, 0);
        }
        foreach (var r in Riders) r.HasTrail = false;
    }

    /// <summary>Поле рядком із 1200 символів '0'..'4' — саме так воно летить у виді після реконекту.</summary>
    public string OwnerRow() => Row(Owner);

    public string TrailRow() => Row(Trail);

    static string Row(byte[] map) => string.Create(Cells, map, static (span, src) =>
    {
        for (var i = 0; i < span.Length; i++) span[i] = (char)('0' + src[i]);
    });

    void SetOwner(int cell, byte value)
    {
        var was = Owner[cell];
        if (was == value) return;
        if (was != 0) _area[was - 1]--;
        if (value != 0) _area[value - 1]++;
        Owner[cell] = value;
        _ownerChanged.Add(cell);
    }

    void SetTrail(int cell, byte value)
    {
        if (Trail[cell] == value) return;
        Trail[cell] = value;
        _trailChanged.Add(cell);
    }

    void Spawn(int seat, int x, int y, int dir)
    {
        var r = Riders[seat];
        r.X = Math.Clamp(x, Plot, W - 1 - Plot);
        r.Y = Math.Clamp(y, Plot, H - 1 - Plot);
        r.Dir = dir;
        r.Alive = true;
        r.HasTrail = false;
        r.RespawnIn = 0;
        r.Turns.Clear();
        for (var dy = -Plot; dy <= Plot; dy++)
            for (var dx = -Plot; dx <= Plot; dx++)
                SetOwner(Cell(r.X + dx, r.Y + dy), (byte)(seat + 1));
    }

    /// <summary>Повернення на вільне місце; напрямок — до середини поля, щоб не стартувати носом у стіну.</summary>
    void Respawn(int seat)
    {
        var (x, y) = FreeSpot();
        Spawn(seat, x, y, x < W / 2 ? 0 : 2);
    }

    /// <summary>
    /// Порожній квадрат 5×5 під новий наділ. Спершу тицяємо навмання (так воскресіння не збирається в кутку),
    /// далі — чесний перебір, і лише коли поле геть зайняте, стаємо в кут просто поверх усього.
    /// </summary>
    (int X, int Y) FreeSpot()
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var x = 2 + rng.Next(W - 4);
            var y = 2 + rng.Next(H - 4);
            if (Free(x, y)) return (x, y);
        }
        for (var y = 2; y < H - 2; y++)
            for (var x = 2; x < W - 2; x++)
                if (Free(x, y)) return (x, y);
        return (2, 2);
    }

    bool Free(int cx, int cy)
    {
        for (var dy = -2; dy <= 2; dy++)
            for (var dx = -2; dx <= 2; dx++)
            {
                var c = Cell(cx + dx, cy + dy);
                if (Owner[c] != 0 || Trail[c] != 0) return false;
            }
        foreach (var r in Riders)
            if (r.On && r.Alive && Math.Abs(r.X - cx) <= 2 && Math.Abs(r.Y - cy) <= 2) return false;
        return true;
    }
}

/// <summary>
/// Загарбання землі на двох-чотирьох. Виїжджаєш зі свого наділу, обводиш шматок поля, вертаєшся — обведене
/// твоє; перерізали слід — уся земля згоріла. Правила живуть тут, браузер лише малює кадри.
/// </summary>
public sealed class Territory : Game
{
    /// <summary>Кольори наділів; вони ж — назви місць у чіпах і в Журналі.</summary>
    static readonly string[] Colours = ["жовта", "зелена", "глиняна", "блакитна"];

    public override GameInfo Info { get; } = new(
        "territory", "Земля", "землю", GameGroup.Live, 2, TerritoryCore.MaxPlayers,
        TickMs: TerritoryCore.TickMs, Start: StartMode.ByHost,
        Hint: "Виїжджай зі своєї землі, обводь шматок поля і повертайся — обведене твоє. Перерізали твій слід — усе згоріло");

    /// <summary>
    /// Більше за стільки змін — і список пар стає дорожчим за все поле (два рядки по 1200 символів це
    /// ~2,4 КБ), а кадр вилазить за 4 КБ з ARCHITECTURE §12. Такий тик шлемо повними рядками.
    /// </summary>
    public const int BigFrame = 300;

    TerritoryCore? _core;
    /// <summary>Раунд, який уже стартував: усе, що не він, — це стіл, який ще чекає на «Почати».</summary>
    int _startedRound = -1;
    /// <summary>Раунд, під який зібране поле лобі: щоб не перекладати наділи на кожен вид.</summary>
    int _laidRound = -1;

    /// <summary>
    /// Поле готове ще до старту: стіл, що чекає на гравців, має виглядати як поле з наділами, а не як
    /// порожнеча. Публічне, щоб тести могли розкласти поле руками, як це роблять із ядром змійки.
    /// </summary>
    public TerritoryCore Core
    {
        get
        {
            if (_core is not null) return _core;
            _core = new TerritoryCore(Ctx.Rng);
            _core.Reset(SeatedMask());
            return _core;
        }
    }

    public override string SeatName(int seat) =>
        seat >= 0 && seat < Colours.Length ? Colours[seat] : $"гравець {seat + 1}";

    public override void Start()
    {
        Core.Reset(SeatedMask());
        _startedRound = Ctx.Round;
    }

    /// <summary>Реалтайм-ввід: самі повороти. Помилки нікого не цікавлять — наступний кадр усе перемалює.</summary>
    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        if (action != "turn") return ActResult.Fail("Тут так не ходять");
        if (Dir(payload) is { } dir) Core.Turn(seat, dir);
        return ActResult.Done;
    }

    /// <summary>Поворот приймаємо і як <c>{dir:1}</c>, і як голе число — так само, як його шле модуль.</summary>
    static int? Dir(JsonElement payload) => payload.ValueKind switch
    {
        JsonValueKind.Number when payload.TryGetInt32(out var n) => n,
        JsonValueKind.Object when payload.TryGetProperty("dir", out var d)
            && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var n) => n,
        _ => null,
    };

    public override TickResult Tick()
    {
        Core.Step();
        if (Core.TicksLeft > 0) return TickResult.FrameOnly;
        FinishRound();
        return TickResult.Both;
    }

    /// <summary>
    /// Хтось устав із-за столу. На чотирьох це не привід ламати раунд: земля того, хто пішов, згоряє, решта
    /// дограють. Партія кінчається, лише коли грати вже нема з ким.
    /// </summary>
    public override void OnLeave(int seat)
    {
        var rest = Enumerable.Range(0, Info.MaxPlayers).Where(s => s != seat && Ctx.Seated(s)).ToArray();
        if (seat >= 0 && seat < TerritoryCore.MaxPlayers)
        {
            var rider = Core.Riders[seat];
            Core.Burn(seat);
            rider.On = false;
            rider.RespawnIn = 0;
        }
        if (rest.Length >= Info.MinPlayers)
        {
            Ctx.Log($"{Info.Title}: {Ctx.NickOf(seat)} встав з-за столу, земля згоріла");
            return;
        }
        Ctx.Finish(rest, $"{Info.Title}: {Ctx.NickOf(seat)} встав з-за столу, партію не дограли");
    }

    /// <summary>
    /// Кадр везе лише зміни — 1200 клітинок десять разів на секунду ніхто б не витримав. Виняток — тик,
    /// у якому згорає великий гравець або хтось замикає пів поля: там змін під тисячу, і список пар важить
    /// утричі більше за саме поле. Тоді кладемо повні рядки, і кадр лишається в межах ARCHITECTURE §12.
    /// </summary>
    public override object? Frame()
    {
        var heavy = Core.OwnerChanged.Count + Core.TrailChanged.Count > BigFrame;
        return new
        {
            t = Core.Ticks,
            heads = Heads(),
            area = Areas(),
            owner = heavy ? Core.OwnerRow() : null,
            trail = heavy ? Core.TrailRow() : null,
            changes = heavy ? Array.Empty<int[]>() : Pairs(Core.OwnerChanged, Core.Owner),
            trails = heavy ? Array.Empty<int[]>() : Pairs(Core.TrailChanged, Core.Trail),
            timeLeft = Core.TicksLeft * TerritoryCore.TickMs,
        };
    }

    public override object View(int? seat)
    {
        // Поки раунд не почався, поле показує рівно стільки наділів, скільки людей уже сіло: і у свіжому
        // лобі, і за дограним столом, який новий гравець відкрив наново (там Ctx.Round уже інший, а склад
        // може збігтися з минулим). Дограну партію, навпаки, лишаємо на столі — картці результату є що
        // показати, аж поки хтось не сяде.
        if (Ctx.Round != _startedRound && (Ctx.Round != _laidRound || !Core.SameSeats(SeatedMask())))
        {
            Core.Reset(SeatedMask());
            _laidRound = Ctx.Round;
        }
        return new
        {
            width = TerritoryCore.W,
            height = TerritoryCore.H,
            turn = (int?)null,
            t = Core.Ticks,
            owner = Core.OwnerRow(),
            trail = Core.TrailRow(),
            heads = Heads(),
            area = Areas(),
            timeLeft = Core.TicksLeft * TerritoryCore.TickMs,
        };
    }

    bool[] SeatedMask()
    {
        var mask = new bool[TerritoryCore.MaxPlayers];
        for (var s = 0; s < mask.Length; s++) mask[s] = Ctx.Seated(s);
        return mask;
    }

    /// <summary>Голови по місцях: індекс у масиві — це номер місця, тому порожні теж на місці.</summary>
    object[] Heads() => [.. Core.Riders.Select(r => new
    {
        on = r.On,
        alive = r.Alive,
        x = r.X,
        y = r.Y,
        dir = r.Dir,
        respawnIn = r.RespawnIn * TerritoryCore.TickMs,
    })];

    double[] Areas() => [.. Enumerable.Range(0, TerritoryCore.MaxPlayers).Select(Core.Percent)];

    /// <summary>Зміни за тик парами [клітинка, хто] — кадр несе лише їх, а не все поле.</summary>
    static int[][] Pairs(IReadOnlyCollection<int> cells, byte[] map)
    {
        var pairs = new int[cells.Count][];
        var i = 0;
        foreach (var c in cells) pairs[i++] = [c, map[c]];
        return pairs;
    }

    /// <summary>Кінець раунду: перемагає найбільша площа, рівні площі — нічия.</summary>
    void FinishRound()
    {
        var seats = Enumerable.Range(0, TerritoryCore.MaxPlayers).Where(s => Core.Riders[s].On).ToArray();
        if (seats.Length == 0)
        {
            Ctx.Finish([], $"{Info.Title}: грати не було кому");
            return;
        }
        var best = seats.Max(Core.Area);
        var winners = seats.Where(s => Core.Area(s) == best).ToArray();
        var board = string.Join(", ", seats
            .OrderByDescending(Core.Area)
            .Select(s => $"{Ctx.NickOf(s)} {SeatName(s)} {Core.Percent(s).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}%"));
        // Ніки чужі, відмінювати їх нема як, тому в Журнал іде табличка відсотків. Переможця називаємо
        // окремо кольором: різниця в одну клітинку — це 0,08 в. п., тож у табличці два однакові відсотки
        // читались би як нічия, якою вони не є.
        if (winners.Length == seats.Length) Ctx.Finish([], $"{Info.Title}: {board} — нічия");
        else if (winners.Length == 1) Ctx.Finish(winners, $"{Info.Title}: {board} — перемогла {SeatName(winners[0])}");
        else Ctx.Finish(winners, $"{Info.Title}: {board} — перемогли {string.Join(" і ", winners.Select(SeatName))}");
    }
}
