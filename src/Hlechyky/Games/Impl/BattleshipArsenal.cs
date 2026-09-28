using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Штука з арсеналу морського бою. <see cref="Target"/>: foe — клітинка чужого поля, own — свого, none —
/// ставиться сама (міна). <see cref="Max"/> — скільки таких можна взяти в один бій.
/// </summary>
public sealed record BattleshipTool(string Key, string Icon, string Name, int Price, int Max, string Target, string Text);

/// <summary>
/// Режим «⚓ Арсенал»: перед боєм купуєш за шеляги (🪙, окрема валюта гри, не черепки) до трьох штук, у бою
/// пускаєш їх у хід замість звичайного пострілу. Ціни й обмеження підібрані так, щоб класичні навички лишались
/// головними: штука дає інформацію чи кілька клітинок за хід, але не топить флот сама, а трюм — лише на три.
/// </summary>
public static class BattleshipArsenal
{
    /// <summary>Скільки штук влазить у трюм на один бій.</summary>
    public const int Hold = 3;
    /// <summary>З чим приходить новачок: вистачить одразу спробувати літак і радар.</summary>
    public const int StartPurse = 40;
    /// <summary>Кожному, хто дограв бій до кінця (не встав).</summary>
    public const int BasePay = 10;
    /// <summary>Переможцеві — ще стільки ж…</summary>
    public const int WinPay = 10;
    /// <summary>…і плюс те, що лишилось від цієї межі після витраченого: хто переміг ощадливо, той і багатший.</summary>
    public const int ThriftCap = 30;
    /// <summary>Скільки пострілів мусить бути в бою, щоб за нього платили (вийти одразу — не заробіток).</summary>
    public const int MinShots = 6;
    /// <summary>
    /// Стеля доходу на нік за київську добу: три-чотири чесні перемоги. Далі бої безкоштовні, але без заробітку —
    /// щоб «другим ніком проти себе» чи проти Глека шеляги не друкувались мішками (рецензія проходу №3).
    /// </summary>
    public const int DailyCap = 120;

    public static readonly BattleshipTool[] Tools =
    [
        new("radar", "📡", "Радар", 6, 2, "foe", "Скільки цілих палуб у квадраті 3×3 довкола клітинки. Бачиш лише ти; хід переходить далі"),
        new("bomb", "💣", "Бомба", 12, 2, "foe", "Б'є хрестом: клітинка й чотири сусіди. Влучив хоч раз — стріляєш ще"),
        new("torpedo", "🚀", "Торпеда", 16, 1, "foe", "Іде стовпцем від ближчого краю й вибухає на першому кораблі"),
        new("plane", "✈️", "Літак", 20, 1, "foe", "Летить рядком від ближчого краю й бомбить кожну клітинку, доки не влучить у корабель"),
        new("mine", "🧨", "Міна", 8, 2, "none", "Боцман кладе її на твою воду, не біля кораблів. Хто в неї стрельне — отримає рикошет у свій корабель"),
        new("repair", "🔧", "Ремонт", 10, 1, "own", "Латає одну підбиту (ще не потоплену) палубу свого корабля. Замість пострілу"),
    ];

    public static BattleshipTool? Find(string? key) => Tools.FirstOrDefault(t => t.Key == key);

    /// <summary>Скільки отримує капітан за бій: база, а переможцеві — ще й бонус тим більший, чим менше витрачено.</summary>
    public static int Income(bool won, int spent) => BasePay + (won ? WinPay + Math.Max(0, ThriftCap - spent) : 0);
}

/// <summary>
/// Гаманці шелягів за ніком (ключ — як у черепків). Гра читає лише пам'ять (під замком кімнати SQLite не чіпаємо),
/// нові суми лягають у канал, який фоновий цикл зливає в базу. Без бази (тести) — лише пам'ять.
/// </summary>
public sealed class BattleshipPurse : BackgroundService
{
    const string Schema = """
        CREATE TABLE IF NOT EXISTS battleship_purse(nick_key TEXT PRIMARY KEY, nick TEXT NOT NULL, coins INTEGER NOT NULL,
          games INTEGER NOT NULL, at TEXT NOT NULL);
        """;

    readonly Db? _db;
    readonly ILogger<BattleshipPurse>? _log;
    readonly ConcurrentDictionary<string, (int Coins, int Games)> _coins = new();
    /// <summary>Заробіток за сьогодні: лише пам'ять (перезапуск сервера — рідкість, а стеля — від фарму, не від гри).</summary>
    readonly Dictionary<string, (string Day, int Earned)> _today = [];
    readonly Channel<(string Key, string Nick, int Coins, int Games, DateTimeOffset At)> _rows = Channel.CreateUnbounded<(string, string, int, int, DateTimeOffset)>();
    readonly object _gate = new();

    public BattleshipPurse(Db? db, ILogger<BattleshipPurse>? log = null)
    {
        _db = db;
        _log = log;
        if (_db is null) return;
        try
        {
            _db.With(c =>
            {
                using (var cmd = c.CreateCommand()) { cmd.CommandText = Schema; cmd.ExecuteNonQuery(); }
                using var read = c.CreateCommand();
                read.CommandText = "SELECT nick_key, coins, games FROM battleship_purse";
                using var r = read.ExecuteReader();
                while (r.Read()) _coins[r.GetString(0)] = (r.GetInt32(1), r.GetInt32(2));
            });
        }
        catch (Exception ex) { _log?.LogWarning(ex, "battleship: не прочитав гаманці шелягів"); }
    }

    public static string Key(string nick) => Rooms.NickKey(nick);

    /// <summary>Скільки шелягів у гаманці; новачок — із запасом, щоб одразу спробувати.</summary>
    public int Balance(string nick) => _coins.TryGetValue(Key(nick), out var v) ? v.Coins : BattleshipArsenal.StartPurse;

    /// <summary>Додати (чи зняти) шеляги за бій. Нижче нуля гаманець не падає.</summary>
    public int Add(string nick, int delta, DateTimeOffset at)
    {
        lock (_gate)
        {
            var key = Key(nick);
            var (coins, games) = _coins.TryGetValue(key, out var v) ? v : (BattleshipArsenal.StartPurse, 0);
            coins = Math.Max(0, coins + delta);
            _coins[key] = (coins, games + 1);
            if (_db is not null) _rows.Writer.TryWrite((key, nick, coins, games + 1, at));
            return coins;
        }
    }

    /// <summary>
    /// Скільки з доходу <paramref name="income"/> ще влазить у денну стелю ніка (<see cref="BattleshipArsenal.DailyCap"/>);
    /// влізле одразу записується як зароблене сьогодні.
    /// </summary>
    public int Earn(string nick, int income, DateTimeOffset at)
    {
        if (income <= 0) return income;
        lock (_gate)
        {
            var key = Key(nick);
            var day = Days.Of(at);
            var earned = _today.TryGetValue(key, out var t) && t.Day == day ? t.Earned : 0;
            var take = Math.Min(income, Math.Max(0, BattleshipArsenal.DailyCap - earned));
            _today[key] = (day, earned + take);
            return take;
        }
    }

    public void Flush()
    {
        if (_db is null) return;
        while (_rows.Reader.TryRead(out var row))
        {
            try
            {
                _db.With(c =>
                {
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = """
                        INSERT INTO battleship_purse(nick_key, nick, coins, games, at) VALUES($k, $n, $c, $g, $at)
                        ON CONFLICT(nick_key) DO UPDATE SET nick = excluded.nick, coins = excluded.coins, games = excluded.games, at = excluded.at
                        """;
                    cmd.Parameters.AddWithValue("$k", row.Key);
                    cmd.Parameters.AddWithValue("$n", row.Nick);
                    cmd.Parameters.AddWithValue("$c", row.Coins);
                    cmd.Parameters.AddWithValue("$g", row.Games);
                    cmd.Parameters.AddWithValue("$at", row.At.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                    cmd.ExecuteNonQuery();
                });
            }
            catch (Exception ex) { _log?.LogWarning(ex, "battleship: гаманець не записався"); }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_db is null) return;
        try { while (await _rows.Reader.WaitToReadAsync(ct)) Flush(); }
        catch (OperationCanceledException) { }
        Flush();
    }
}

public static class BattleshipSetup
{
    public static IServiceCollection AddBattleship(this IServiceCollection services)
    {
        services.AddSingleton(sp => new BattleshipPurse(sp.GetService<Db>(), sp.GetService<ILogger<BattleshipPurse>>()));
        services.AddHostedService(sp => sp.GetRequiredService<BattleshipPurse>());
        return services;
    }
}

public sealed partial class Battleship
{
    bool _arsenal;
    BattleshipPurse? _purse;
    BattleshipPurse Purse => _purse ??= Ctx.Services.GetService<BattleshipPurse>() ?? new BattleshipPurse(null);

    /// <summary>Арсенал одного поля: що куплено, що вже пущено в хід, міни, латки й розвідка радаром.</summary>
    sealed class Kit
    {
        public Dictionary<string, int> Bought { get; set; } = [];
        public Dictionary<string, int> Used { get; set; } = [];
        public HashSet<int> Mines { get; set; } = [];
        public HashSet<int> Boom { get; set; } = [];
        public HashSet<int> Patched { get; set; } = [];
        /// <summary>Радар цього капітана: по чиєму полю, куди, скільки палуб.</summary>
        public List<int[]> Intel { get; set; } = [];
        /// <summary>Нік на старті партії: той, хто встав, до кінця вже без ніка в каркасі, а гаманець його.</summary>
        public string? Nick { get; set; }
        /// <summary>Підсумок гаманця після бою: витрачено, отримано, стало.</summary>
        public int[]? Ledger { get; set; }

        public int Left(string key) => Bought.GetValueOrDefault(key) - Used.GetValueOrDefault(key);
        public int Count => Bought.Values.Sum();
        public int Cost => Bought.Sum(p => (BattleshipArsenal.Find(p.Key)?.Price ?? 0) * p.Value);
        public int Spent => Used.Sum(p => (BattleshipArsenal.Find(p.Key)?.Price ?? 0) * p.Value);
    }

    Kit[] _kits = FreshKits();
    bool _settled;

    static Kit[] FreshKits() => [new(), new(), new(), new()];

    void ConfigureArsenal(IReadOnlyDictionary<string, string> options) =>
        _arsenal = options.TryGetValue("mode", out var mode) && mode == "arsenal";

    void StartArsenal()
    {
        _kits = FreshKits();
        _settled = false;
        for (var s = 0; s < Seats; s++)
        {
            if (!_sides[s].In) continue;
            if (!_sides[s].Bot) { _kits[s].Nick = Ctx.NickOf(s); continue; }
            if (!_arsenal) continue;
            // Глек бере дві штуки задарма (гаманця в нього нема): щось далекобійне і щось на своє поле.
            var kit = _kits[s];
            string[] far = ["plane", "torpedo", "bomb"], home = ["mine", "repair", "bomb"];
            kit.Bought[far[Ctx.Rng.Next(far.Length)]] = 1;
            var second = home[Ctx.Rng.Next(home.Length)];
            kit.Bought[second] = kit.Bought.GetValueOrDefault(second) + 1;
        }
    }

    string? ReadItem(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("item", out var i) && i.ValueKind == JsonValueKind.String
            ? i.GetString() : payload.ValueKind == JsonValueKind.String ? payload.GetString() : null;

    ActResult Buy(int seat, JsonElement payload)
    {
        if (!_arsenal) return ActResult.Fail("Арсенал — лише в режимі «⚓ Арсенал»");
        if (_phase != Phase.Placing) return ActResult.Fail("Крамниця працює лише до бою");
        if (_sides[seat].Ready) return ActResult.Fail("«Готово» вже сказано — трюм задраєно");
        if (BattleshipArsenal.Find(ReadItem(payload)) is not { } tool) return ActResult.Fail("Такого в арсеналі нема");
        var kit = _kits[seat];
        if (kit.Bought.GetValueOrDefault(tool.Key) >= tool.Max) return ActResult.Fail($"{tool.Name} — не більше {tool.Max} на бій");
        if (kit.Count >= BattleshipArsenal.Hold) return ActResult.Fail($"У трюм влазить лише {BattleshipArsenal.Hold} штуки");
        var purse = Purse.Balance(kit.Nick ?? Nick(seat));
        if (kit.Cost + tool.Price > purse) return ActResult.Fail($"Не вистачає шелягів: у гаманці {purse} 🪙");
        kit.Bought[tool.Key] = kit.Bought.GetValueOrDefault(tool.Key) + 1;
        _dirty = true;
        return ActResult.Done;
    }

    ActResult Sell(int seat, JsonElement payload)
    {
        if (!_arsenal) return ActResult.Fail("Арсенал — лише в режимі «⚓ Арсенал»");
        if (_phase != Phase.Placing) return ActResult.Fail("Крамниця працює лише до бою");
        if (_sides[seat].Ready) return ActResult.Fail("«Готово» вже сказано — трюм задраєно");
        var key = ReadItem(payload) ?? "";
        var kit = _kits[seat];
        if (kit.Bought.GetValueOrDefault(key) <= 0) return ActResult.Fail("Цього в трюмі нема");
        if (--kit.Bought[key] == 0) kit.Bought.Remove(key);
        _dirty = true;
        return ActResult.Done;
    }

    /// <summary>Міни боцман кладе на старті бою: на воду, що не торкається жодного корабля (інакше міна видавала б сусіда).</summary>
    void DeployMines()
    {
        for (var s = 0; s < Seats; s++)
        {
            var kit = _kits[s];
            var n = kit.Bought.GetValueOrDefault("mine");
            if (n <= 0 || !_sides[s].Alive) continue;
            var near = new HashSet<int>();
            foreach (var ship in _sides[s].Ships)
                foreach (var c in ship) { near.Add(c); foreach (var h in BattleshipRules.Halo(c, _sea)) near.Add(h); }
            var water = Enumerable.Range(0, _sea.Cells).Where(c => !near.Contains(c)).ToList();
            for (var i = 0; i < n && water.Count > 0; i++)
            {
                var k = Ctx.Rng.Next(water.Count);
                kit.Mines.Add(water[k]);
                water.RemoveAt(k);
            }
            kit.Used["mine"] = n;
        }
    }

    /// <summary>
    /// Постріл у міну: клітинка стає промахом, а рикошет влучає в випадкову цілу палубу того, хто стріляв —
    /// може й потопити, і навіть покласти на дно весь його флот.
    /// </summary>
    bool TripMine(int by, int at, int cell, Shot? note)
    {
        var kit = _kits[at];
        if (!kit.Mines.Contains(cell) || kit.Boom.Contains(cell)) return false;
        kit.Boom.Add(cell);
        _sides[at].Misses.Add(cell);
        var me = _sides[by];
        var decks = me.Ships.Where(s => !me.Sunk(s)).SelectMany(s => s).Where(c => !me.Hits.Contains(c)).ToList();
        var res = "none";
        if (decks.Count > 0 && me.Alive)
        {
            var hit = decks[Ctx.Rng.Next(decks.Count)];
            me.Hits.Add(hit);
            _kits[by].Patched.Remove(hit);
            res = "hit";
            if (me.ShipAt(hit) is { } ship && me.Sunk(ship))
            {
                _sides[at].Sank++;
                foreach (var near in BattleshipRules.Around(ship, _sea)) me.Misses.Add(near);
                res = "sunk";
                if (me.Left == 0)
                {
                    me.Out = true;
                    _sunkOrder.Add(by);
                    res = "out";
                }
            }
            if (note is not null) note.N = hit;
        }
        if (note is not null) note.Boom = res;
        return true;
    }

    void Unpatch(int at, int cell) => _kits[at].Patched.Remove(cell);

    // ---------- штуки в бою ----------

    ActResult Use(int seat, JsonElement payload)
    {
        if (!_arsenal) return ActResult.Fail("Арсенал — лише в режимі «⚓ Арсенал»");
        if (_phase != Phase.Battle) return ActResult.Fail("Спершу розстав кораблі");
        if (_revenge is not null) return ActResult.Fail("Помста — лише звичайним пострілом");
        if (BattleshipArsenal.Find(ReadItem(payload)) is not { } tool || tool.Target == "none")
            return ActResult.Fail("Цим не стріляють");
        if (_kits[seat].Left(tool.Key) <= 0) return ActResult.Fail($"{tool.Name} в трюмі скінчився");
        if (_sides[seat].Out) return ActResult.Fail("Твій флот на дні — лишається дивитись");
        if (seat != _turn) return ActResult.Fail("Не так швидко — зараз не твій хід");
        if (ReadCell(payload) is not { } cell || cell < 0 || cell >= _sea.Cells) return ActResult.Fail("Не зрозумів, куди");
        if (tool.Key == "repair") return Repair(seat, cell);
        if (CheckTurn(seat, payload, out var at) is { } why) return ActResult.Fail(why);
        return Launch(seat, at, tool.Key, cell);
    }

    ActResult Repair(int seat, int cell)
    {
        var me = _sides[seat];
        if (!me.Hits.Contains(cell) || me.ShipAt(cell) is not { } ship) return ActResult.Fail("Латати можна лише підбиту палубу свого корабля");
        if (me.Sunk(ship)) return ActResult.Fail("Потоплене вже не залатати");
        me.Hits.Remove(cell);
        _kits[seat].Patched.Add(cell);
        Spend(seat, "repair");
        Note(new Shot(seat, seat, cell, "repair", ship.Length, false) { Tool = "repair" });
        PassTurn(seat);
        return ActResult.Accept("🔧 Залатано! Палуба знову ціла");
    }

    void Spend(int seat, string key)
    {
        var kit = _kits[seat];
        kit.Used[key] = kit.Used.GetValueOrDefault(key) + 1;
        _dirty = true;
    }

    void PassTurn(int seat)
    {
        _turnUntil = Ctx.Clock.UtcNow.AddSeconds(_turnSeconds);
        _botAt = null;
        _turn = NextAlive(seat);
        _dirty = true;
    }

    /// <summary>Клітинки, по яких пройде штука, у порядку удару. Літак і торпеда — від ближчого до клітинки краю.</summary>
    List<int> Path(string key, int cell)
    {
        var (x, y) = (cell % _sea.W, cell / _sea.W);
        var list = new List<int>();
        switch (key)
        {
            case "plane":
                if (x < _sea.W / 2) for (var i = 0; i < _sea.W; i++) list.Add(y * _sea.W + i);
                else for (var i = _sea.W - 1; i >= 0; i--) list.Add(y * _sea.W + i);
                break;
            case "torpedo":
                if (y < _sea.H / 2) for (var j = 0; j < _sea.H; j++) list.Add(j * _sea.W + x);
                else for (var j = _sea.H - 1; j >= 0; j--) list.Add(j * _sea.W + x);
                break;
            case "bomb":
                list.Add(cell);
                if (y > 0) list.Add(cell - _sea.W);
                if (x > 0) list.Add(cell - 1);
                if (x < _sea.W - 1) list.Add(cell + 1);
                if (y < _sea.H - 1) list.Add(cell + _sea.W);
                break;
            default:
                // радар: квадрат 3×3
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                        if (x + dx >= 0 && x + dx < _sea.W && y + dy >= 0 && y + dy < _sea.H) list.Add(cell + dy * _sea.W + dx);
                break;
        }
        return list;
    }

    static int Rank(Blow b) => b switch { Blow.Out => 4, Blow.Sunk => 3, Blow.Hit => 2, Blow.Mine => 1, _ => 0 };

    /// <summary>Радар, бомба, торпеда чи літак — по полю <paramref name="at"/>.</summary>
    ActResult Launch(int seat, int at, string key, int cell)
    {
        var foe = _sides[at];
        bool Fresh(int c) => !foe.Hits.Contains(c) && !foe.Misses.Contains(c);
        var path = Path(key, cell);

        if (key == "radar")
        {
            var n = path.Count(c => foe.ShipAt(c) is not null && !foe.Hits.Contains(c));
            _kits[seat].Intel.Add([at, cell, n]);
            Spend(seat, key);
            Note(new Shot(seat, at, cell, "radar", 0, false) { Tool = key, N = n, Cells = [.. path] });
            PassTurn(seat);
            return ActResult.Accept(n == 0 ? "📡 Тиша: у цьому квадраті жодної цілої палуби" : $"📡 У квадраті 3×3 — цілих палуб: {n}");
        }

        if (!path.Any(Fresh)) return ActResult.Fail(key == "bomb" ? "Тут уже все обстріляно" : key == "plane" ? "Цей рядок уже весь обстріляно" : "Цей стовпець уже весь обстріляно");
        var shot = new Shot(seat, at, cell, "miss", 0, false) { Tool = key };
        var struck = new List<int>();
        var best = Blow.Miss;
        var size = 0;
        foreach (var c in path)
        {
            if (!Fresh(c)) continue;   // літак і торпеда минають уже обстріляне; бомба його просто не чіпає
            if (!_sides[seat].Alive) break;
            struck.Add(c);
            _sides[seat].Shots++;
            var blow = Strike(seat, at, c, out var sz, shot);
            if (Rank(blow) > Rank(best)) { best = blow; size = Math.Max(size, sz); }
            else if (blow is Blow.Sunk or Blow.Out) size = Math.Max(size, sz);
            // Літак і торпеда зупиняються на першому кораблі (чи міні); бомба рветься вся одразу.
            if (key != "bomb" && blow != Blow.Miss) break;
            if (key == "bomb" && foe.Out) break;
        }
        shot.Cells = [.. struck];
        shot.Cell = struck[^1];
        shot.Res = ResOf(best);
        shot.Size = size;
        Spend(seat, key);
        Quip(shot);
        Note(shot);
        // Тости — як і для пострілу (№63): лише потоплення, міна й перемога; решту видно анімацією й у стрічці.
        return AfterBlow(seat, at, best, shot);
    }

    /// <summary>Глек теж уміє в арсенал — простенько: іноді чинить підбите, іноді пускає далекобійне туди, де найбільше невідомого.</summary>
    bool BotTool(int seat, int at)
    {
        if (!_arsenal) return false;
        var kit = _kits[seat];
        var me = _sides[seat];
        if (kit.Left("repair") > 0 && Ctx.Rng.Next(2) == 0)
        {
            var hurt = me.Hits.Where(c => me.ShipAt(c) is { } s && !me.Sunk(s)).ToList();
            if (hurt.Count > 0) { Repair(seat, hurt[Ctx.Rng.Next(hurt.Count)]); return true; }
        }
        if (Ctx.Rng.Next(100) >= 35) return false;
        var foe = _sides[at];
        bool Fresh(int c) => !foe.Hits.Contains(c) && !foe.Misses.Contains(c);
        foreach (var key in new[] { "plane", "torpedo", "bomb" })
        {
            if (kit.Left(key) <= 0) continue;
            // Туди, де шлях зачепить найбільше необстріляного.
            var bestCell = -1;
            var bestFresh = 0;
            for (var c = 0; c < _sea.Cells; c++)
            {
                if (!Fresh(c)) continue;
                var n = Path(key, c).Count(Fresh);
                if (n > bestFresh) { bestFresh = n; bestCell = c; }
            }
            if (bestCell < 0 || bestFresh < 3) continue;
            Launch(seat, at, key, bestCell);
            return true;
        }
        return false;
    }

    // ---------- шеляги після бою ----------

    /// <summary>
    /// Кінець партії: кожному (людині) — рахунок гаманця. Платимо лише за штуки, пущені в хід (куплене, але не
    /// використане — лишається в гаманці), дохід — за справжній бій, і хто встав, той без доходу.
    /// </summary>
    void SettleArsenal(bool fullWin = true)
    {
        if (!_arsenal || _settled) return;
        _settled = true;
        var played = _sides.Sum(s => s.Shots) >= BattleshipArsenal.MinShots;
        for (var s = 0; s < Seats; s++)
        {
            var side = _sides[s];
            var kit = _kits[s];
            if (!side.In || side.Bot || kit.Nick is null) continue;
            var spent = kit.Spent;
            var earned = played && !side.Gone ? BattleshipArsenal.Income(_winner == s && fullWin, spent) : 0;
            var income = Purse.Earn(kit.Nick, earned, Ctx.Clock.UtcNow);
            var now = Purse.Add(kit.Nick, income - spent, Ctx.Clock.UtcNow);
            kit.Ledger = [spent, income, now, earned - income];     // останнє — скільки з'їла денна стеля
        }
    }

    string Ledger()
    {
        if (!_arsenal) return "";
        var parts = new List<string>();
        for (var s = 0; s < Seats; s++)
            if (_kits[s].Ledger is { } l && _kits[s].Nick is { } n) parts.Add($"{n} {(l[1] - l[0] >= 0 ? "+" : "")}{l[1] - l[0]}");
        return parts.Count == 0 ? "" : "; ⚓ шеляги: " + string.Join(", ", parts);
    }

    int[]? MinesOf(int seat) => _arsenal ? [.. _kits[seat].Mines.Order()] : null;
    int[]? BoomOf(int seat) => _arsenal ? [.. _kits[seat].Boom.Order()] : null;
    int[]? PatchedOf(int seat) => _arsenal ? [.. _kits[seat].Patched.Order()] : null;

    object? ArsenalView(int mine)
    {
        if (!_arsenal) return null;
        var kit = mine >= 0 ? _kits[mine] : null;
        return new
        {
            hold = BattleshipArsenal.Hold,
            items = BattleshipArsenal.Tools.Select(t => new { key = t.Key, icon = t.Icon, name = t.Name, price = t.Price, max = t.Max, target = t.Target, text = t.Text }).ToArray(),
            pay = new { @base = BattleshipArsenal.BasePay, win = BattleshipArsenal.WinPay, cap = BattleshipArsenal.ThriftCap },
            purse = kit is null ? (int?)null : Purse.Balance(kit.Nick ?? Nick(mine)),
            bought = kit?.Bought.ToDictionary(p => p.Key, p => p.Value),
            used = kit?.Used.ToDictionary(p => p.Key, p => p.Value),
            intel = kit?.Intel.Select(i => (int[])i.Clone()).ToArray(),
            // скільки штук ще в трюмі в кожного — число, без назв: що саме там, суперникам знати не треба
            left = Enumerable.Range(0, Seats).Select(s => _sides[s].In ? _kits[s].Bought.Sum(p => _kits[s].Left(p.Key)) : 0).ToArray(),
            ledger = Enumerable.Range(0, Seats).Select(s => _kits[s].Ledger).ToArray(),
        };
    }

    sealed record KitSnap(Dictionary<string, int> Bought, Dictionary<string, int> Used, int[] Mines, int[] Boom, int[] Patched,
        int[][] Intel, string? Nick, int[]? Ledger);
    sealed record ArsenalSnap(bool On, bool Settled, KitSnap[] Kits);

    string? SaveArsenal() => !_arsenal ? null : JsonSerializer.Serialize(new ArsenalSnap(true, _settled,
        [.. _kits.Select(k => new KitSnap(k.Bought, k.Used, [.. k.Mines.Order()], [.. k.Boom.Order()], [.. k.Patched.Order()],
            [.. k.Intel], k.Nick, k.Ledger))]));

    void LoadArsenal(string? json)
    {
        _kits = FreshKits();
        _arsenal = false;
        _settled = false;
        if (json is null || JsonSerializer.Deserialize<ArsenalSnap>(json) is not { } snap) return;
        _arsenal = snap.On;
        _settled = snap.Settled;
        for (var s = 0; s < Math.Min(Seats, snap.Kits.Length); s++)
        {
            var k = snap.Kits[s];
            _kits[s] = new Kit
            {
                Bought = k.Bought, Used = k.Used, Mines = [.. k.Mines], Boom = [.. k.Boom], Patched = [.. k.Patched],
                Intel = [.. k.Intel], Nick = k.Nick, Ledger = k.Ledger,
            };
        }
    }
}
