using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hlechyky.Games.Impl;

// ============================================================================================
// «Вогник і Крапля»: рівні. Файли data/vohnyk/levels/01..15.json (specs/vohnyk.md §7.1) читаються раз на
// процес і розбираються в VohnykLevel — плитки байтами, сутності з готовими масками сигналів. Сама
// симуляція (VohnykWorld) бачить лише це: про JSON вона не знає нічого.
// ============================================================================================

/// <summary>Самоцвіт: чий (0 — Вогник, 1 — Крапля) і клітинка.</summary>
public sealed record VohnykGemDef(int Who, int Col, int Row);

/// <summary>Кнопка: пластина внизу клітинки.</summary>
public sealed record VohnykButtonDef(string Id, int Col, int Row);

/// <summary>
/// Важіль: клітинка й початковий стан. Дзеркало (друга печера) — той самий важіль: перемикається, коли герой виходить
/// із клітинки (праворуч — «/», ліворуч — «\»), і повертає промінь; <see cref="Fixed"/> — прикручене, не крутиться.
/// </summary>
public sealed record VohnykLeverDef(string Id, int Col, int Row, int Init, bool Mirror = false, bool Fixed = false);

/// <summary>
/// Ліхтар (друга печера): промінь із центру клітинки в бік <see cref="Dir"/> (0 →, 1 ↓, 2 ←, 3 ↑). Чий: 0 — вогняний
/// (червоний, палить Краплю), 1 — водяний (синій, гасить Вогника), 2 — світло (нікого не чіпає). Без сигналів світить
/// завжди; <see cref="ByMask"/> — лише кнопки й важелі.
/// </summary>
public sealed record VohnykBeamDef(string Id, int Col, int Row, int Dir, int Who, string[] By, int ByMask, bool All, bool Inv);

/// <summary>Кришталь-приймач: світиться, поки в нього б'є промінь, — і це сигнал, як кнопка.</summary>
public sealed record VohnykSensorDef(string Id, int Col, int Row);

/// <summary>Портал: два кінці 1 × 2 плитки (at — верхня клітинка). Герой зайшов центром в один — вийшов з другого.</summary>
public sealed record VohnykPortalDef(string Id, int ACol, int ARow, int BCol, int BRow, string[] By, int ByMask, bool All, bool Inv);

/// <summary>Двері на <see cref="Tiles"/> плиток заввишки; <see cref="ByMask"/> — біти сигналів (кнопки, потім важелі).</summary>
public sealed record VohnykDoorDef(string Id, int Col, int Row, int Tiles, string[] By, int ByMask, bool All, bool Inv);

/// <summary>Ліфт: плита <see cref="Tiles"/> плиток завширшки між <c>at</c> і <c>to</c> (лише одна вісь).</summary>
public sealed record VohnykLiftDef(string Id, int Col, int Row, int Tiles, int ToCol, int ToRow, string[] By, int ByMask, bool All, bool Inv);

public sealed record VohnykBoxDef(int Col, int Row);

public sealed record VohnykHintDef(int Col, int Row, int W, string Text);

/// <summary>Записане проходження: хеші світу кожні <see cref="Every"/> кроків і фінальний (§5.5).</summary>
public sealed record VohnykCheck(int Steps, int Hash, int Every, int[] Hashes);

/// <summary>
/// Записане проходження «сам за двох»: у кожен момент рухається лише один герой, світ — у режимі соло (кнопки брам
/// «все разом» тримаються <see cref="VohnykWorld.SoloLatch"/>). Доводить, що рівень проходиться одним гравцем, і
/// заодно звіряє з C# соло-логіку JS-симуляції.
/// </summary>
public sealed class VohnykSoloRun
{
    public int[][] Solution { get; set; } = [];
    public VohnykCheck? Check { get; set; }
}

/// <summary>Розібраний рівень. Плитки: 0 повітря, 1 камінь, 2 вода, 3 лава, 4 болото, 5 тонка платформа (друга печера).</summary>
public sealed class VohnykLevel
{
    public const byte Air = 0, Stone = 1, Water = 2, Lava = 3, Mud = 4, Thin = 5;

    public required int N { get; init; }
    public required string Name { get; init; }
    public required int Par { get; init; }
    public required int W { get; init; }
    public required int H { get; init; }
    public required string[] Rows { get; init; }
    public required byte[] Tiles { get; init; }
    /// <summary>Клітинки спавну [вогник, крапля] — (col, row).</summary>
    public required (int Col, int Row)[] Spawn { get; init; }
    public required (int Col, int Row)[] Exits { get; init; }
    public required VohnykGemDef[] Gems { get; init; }
    public required VohnykButtonDef[] Buttons { get; init; }
    public required VohnykLeverDef[] Levers { get; init; }
    public required VohnykDoorDef[] Doors { get; init; }
    public required VohnykLiftDef[] Lifts { get; init; }
    public required VohnykBoxDef[] Boxes { get; init; }
    public required VohnykHintDef[] Hints { get; init; }
    public VohnykBeamDef[] Beams { get; init; } = [];
    public VohnykSensorDef[] Sensors { get; init; } = [];
    public VohnykPortalDef[] Portals { get; init; } = [];
    /// <summary>Журнал вводу проходження: [крок від першого кроку go (1-based), герой, k].</summary>
    public required int[][] Solution { get; init; }
    public VohnykCheck? Check { get; init; }
    /// <summary>Записане проходження сам за двох (є не на кожному рівні).</summary>
    public VohnykSoloRun? Solo { get; init; }
    public string File { get; init; } = "";

    public int GemsOf(int who)
    {
        var n = 0;
        foreach (var g in Gems) if (g.Who == who) n++;
        return n;
    }

    public int AllGemsMask => Gems.Length >= 31 ? int.MaxValue : (1 << Gems.Length) - 1;

    public byte Tile(int col, int row) =>
        col < 0 || row < 0 || col >= W || row >= H ? Stone : Tiles[row * W + col];
}

/// <summary>Як рівень лежить у файлі (camelCase, JsonSerializerDefaults.Web).</summary>
public sealed class VohnykLevelFile
{
    public int N { get; set; }
    public string Name { get; set; } = "";
    public int Par { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public string[] Rows { get; set; } = [];
    public VohnykSpawnFile Spawn { get; set; } = new();
    public VohnykGemFile[] Gems { get; set; } = [];
    public VohnykSpawnFile Exits { get; set; } = new();
    public VohnykIdAtFile[] Buttons { get; set; } = [];
    public VohnykLeverFile[] Levers { get; set; } = [];
    public VohnykDoorFile[] Doors { get; set; } = [];
    public VohnykLiftFile[] Lifts { get; set; } = [];
    public VohnykAtFile[] Boxes { get; set; } = [];
    public VohnykHintFile[] Hints { get; set; } = [];
    public VohnykMirrorFile[] Mirrors { get; set; } = [];
    public VohnykBeamFile[] Beams { get; set; } = [];
    public VohnykIdAtFile[] Sensors { get; set; } = [];
    public VohnykPortalFile[] Portals { get; set; } = [];
    public int[][] Solution { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VohnykCheck? Check { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VohnykSoloRun? Solo { get; set; }
}

public sealed class VohnykSpawnFile
{
    public int[] Fire { get; set; } = [0, 0];
    public int[] Water { get; set; } = [0, 0];
}

public sealed class VohnykGemFile
{
    public string Who { get; set; } = "fire";
    public int[] At { get; set; } = [0, 0];
}

public class VohnykAtFile
{
    public int[] At { get; set; } = [0, 0];
}

public class VohnykIdAtFile : VohnykAtFile
{
    public string Id { get; set; } = "";
}

public sealed class VohnykLeverFile : VohnykIdAtFile
{
    public int Init { get; set; }
}

public sealed class VohnykDoorFile : VohnykIdAtFile
{
    public int H { get; set; } = 2;
    public string[] By { get; set; } = [];
    public string Mode { get; set; } = "any";
    public bool Inv { get; set; }
}

public sealed class VohnykLiftFile : VohnykIdAtFile
{
    public int W { get; set; } = 2;
    public int[] To { get; set; } = [0, 0];
    public string[] By { get; set; } = [];
    public string Mode { get; set; } = "any";
    public bool Inv { get; set; }
}

public sealed class VohnykMirrorFile : VohnykIdAtFile
{
    public int Init { get; set; }
    public bool Fixed { get; set; }
}

public sealed class VohnykBeamFile : VohnykIdAtFile
{
    /// <summary>r, d, l, u.</summary>
    public string Dir { get; set; } = "r";
    /// <summary>fire, water, light.</summary>
    public string Who { get; set; } = "light";
    public string[] By { get; set; } = [];
    public string Mode { get; set; } = "any";
    public bool Inv { get; set; }
}

public sealed class VohnykPortalFile
{
    public string Id { get; set; } = "";
    public int[] A { get; set; } = [0, 0];
    public int[] B { get; set; } = [0, 0];
    public string[] By { get; set; } = [];
    public string Mode { get; set; } = "any";
    public bool Inv { get; set; }
}

public sealed class VohnykHintFile : VohnykAtFile
{
    public int W { get; set; } = 6;
    public string Text { get; set; } = "";
}

/// <summary>
/// Усі рівні, раз на процес: перша печера 01..15 і друга «Глибше» 16..(скільки лежить файлів підряд, до 30). Кривий
/// файл — виняток з іменем файла при першому зверненні.
/// </summary>
public static class VohnykLevels
{
    /// <summary>Рівнів у першій печері (і стільки мусить лежати завжди).</summary>
    public const int Cave1 = 15;
    /// <summary>Стеля: більше файлів не шукаємо.</summary>
    public const int MaxLevels = 30;
    /// <summary>Скільки рівнів разом (обидві печери).</summary>
    public static int Count => All.Length;
    public const string Dir = "data/vohnyk/levels";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    static readonly Lazy<VohnykLevel[]> Cached = new(() => LoadDir(Paths.Resolve(Dir)));

    /// <summary>Рівні 1..Count (індекс = n − 1).</summary>
    public static VohnykLevel[] All => Cached.Value;

    /// <summary>Чи лежать рівні на диску — без винятку (для Configure, щоб не валити сервер).</summary>
    public static bool Available
    {
        get
        {
            try { return All.Length >= Cave1; }
            catch (Exception) { return false; }
        }
    }

    public static VohnykLevel Get(int n) => All[n - 1];

    public static VohnykLevel[] LoadDir(string dir)
    {
        var list = new List<VohnykLevel>(MaxLevels);
        for (var n = 1; n <= MaxLevels; n++)
        {
            var path = Path.Combine(dir, $"{n:00}.json");
            // перша печера — обов'язково вся; далі — скільки лежить підряд (рівні другої печери додаються частинами)
            if (n > Cave1 && !File.Exists(path)) break;
            var level = LoadFile(path);
            if (level.N != n) throw new InvalidDataException($"{path}: n = {level.N}, а має бути {n}");
            list.Add(level);
        }
        return [.. list];
    }

    public static VohnykLevel LoadFile(string path)
    {
        try
        {
            var file = JsonSerializer.Deserialize<VohnykLevelFile>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("порожній файл");
            var level = Build(file, path);
            var errors = Validate(level);
            if (errors.Count > 0) throw new InvalidDataException(string.Join("; ", errors));
            return level;
        }
        catch (Exception ex) when (ex is not InvalidDataException || !ex.Message.StartsWith(path, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{path}: {ex.Message}", ex);
        }
    }

    static int Who(string who) => who switch
    {
        "fire" => 0,
        "water" => 1,
        _ => throw new InvalidDataException($"невідомий who «{who}»"),
    };

    static (int, int) At(int[] at, string what) =>
        at is { Length: 2 } ? (at[0], at[1]) : throw new InvalidDataException($"{what}: at має бути [col, row]");

    /// <summary>Файл → рівень: плитки, маски сигналів. Перевірки змісту — у <see cref="Validate"/>.</summary>
    public static VohnykLevel Build(VohnykLevelFile f, string path = "")
    {
        var tiles = new byte[f.W * f.H];
        for (var r = 0; r < f.H && r < f.Rows.Length; r++)
        {
            var row = f.Rows[r];
            for (var c = 0; c < f.W && c < row.Length; c++)
                tiles[r * f.W + c] = row[c] switch
                {
                    '#' => VohnykLevel.Stone,
                    'W' => VohnykLevel.Water,
                    'L' => VohnykLevel.Lava,
                    'M' => VohnykLevel.Mud,
                    '_' => VohnykLevel.Thin,
                    _ => VohnykLevel.Air,
                };
        }
        var signals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var b in f.Buttons) signals.TryAdd(b.Id, signals.Count);
        foreach (var l in f.Levers) signals.TryAdd(l.Id, signals.Count);
        foreach (var m in f.Mirrors) signals.TryAdd(m.Id, signals.Count);
        // промінь вмикають лише кнопки, важелі й дзеркала (не кришталі — інакше світло вмикало б саме себе)
        var beamBits = signals.Count;
        foreach (var s in f.Sensors) signals.TryAdd(s.Id, signals.Count);
        int Mask(string[] by)
        {
            var m = 0;
            foreach (var id in by)
                m |= signals.TryGetValue(id, out var bit) ? 1 << bit : throw new InvalidDataException($"сигналу «{id}» нема");
            return m;
        }
        bool All(string mode) => mode switch
        {
            "all" => true,
            "any" or "" => false,
            _ => throw new InvalidDataException($"mode «{mode}» — лише any або all"),
        };
        int Dir(string d) => d switch
        {
            "r" => 0, "d" => 1, "l" => 2, "u" => 3,
            _ => throw new InvalidDataException($"dir «{d}» — лише r, d, l, u"),
        };
        int BeamWho(string w) => w switch
        {
            "fire" => 0, "water" => 1, "light" or "" => 2,
            _ => throw new InvalidDataException($"промінь: who «{w}» — лише fire, water, light"),
        };
        int BeamMask(string[] by, string id)
        {
            var m = Mask(by);
            return m >> beamBits == 0 ? m : throw new InvalidDataException($"промінь {id}: вмикати можна лише кнопками й важелями");
        }

        return new VohnykLevel
        {
            N = f.N,
            Name = f.Name,
            Par = f.Par,
            W = f.W,
            H = f.H,
            Rows = f.Rows,
            Tiles = tiles,
            Spawn = [At(f.Spawn.Fire, "spawn.fire"), At(f.Spawn.Water, "spawn.water")],
            Exits = [At(f.Exits.Fire, "exits.fire"), At(f.Exits.Water, "exits.water")],
            Gems = [.. f.Gems.Select(g => { var (c, r) = At(g.At, "gem"); return new VohnykGemDef(Who(g.Who), c, r); })],
            Buttons = [.. f.Buttons.Select(b => { var (c, r) = At(b.At, b.Id); return new VohnykButtonDef(b.Id, c, r); })],
            Levers = [.. f.Levers.Select(l => { var (c, r) = At(l.At, l.Id); return new VohnykLeverDef(l.Id, c, r, l.Init); }),
                      .. f.Mirrors.Select(m => { var (c, r) = At(m.At, m.Id); return new VohnykLeverDef(m.Id, c, r, m.Init, Mirror: true, Fixed: m.Fixed); })],
            Beams = [.. f.Beams.Select(b => { var (c, r) = At(b.At, b.Id); return new VohnykBeamDef(b.Id, c, r, Dir(b.Dir), BeamWho(b.Who), b.By, BeamMask(b.By, b.Id), All(b.Mode), b.Inv); })],
            Sensors = [.. f.Sensors.Select(s => { var (c, r) = At(s.At, s.Id); return new VohnykSensorDef(s.Id, c, r); })],
            Portals = [.. f.Portals.Select(p =>
            {
                var (ac, ar) = At(p.A, p.Id + ".a");
                var (bc, br) = At(p.B, p.Id + ".b");
                return new VohnykPortalDef(p.Id, ac, ar, bc, br, p.By, Mask(p.By), All(p.Mode), p.Inv);
            })],
            Doors = [.. f.Doors.Select(d => { var (c, r) = At(d.At, d.Id); return new VohnykDoorDef(d.Id, c, r, d.H, d.By, Mask(d.By), All(d.Mode), d.Inv); })],
            Lifts = [.. f.Lifts.Select(l =>
            {
                var (c, r) = At(l.At, l.Id);
                var (tc, tr) = At(l.To, l.Id + ".to");
                return new VohnykLiftDef(l.Id, c, r, l.W, tc, tr, l.By, Mask(l.By), All(l.Mode), l.Inv);
            })],
            Boxes = [.. f.Boxes.Select(b => { var (c, r) = At(b.At, "box"); return new VohnykBoxDef(c, r); })],
            Hints = [.. f.Hints.Select(h => { var (c, r) = At(h.At, "hint"); return new VohnykHintDef(c, r, h.W, h.Text); })],
            Solution = f.Solution,
            Check = f.Check,
            Solo = f.Solo,
            File = path,
        };
    }

    /// <summary>Перевірки §7.1: розміри, рамка, символи, спавни, виходи, унікальні id, одна вісь ліфта, ліміти.</summary>
    public static List<string> Validate(VohnykLevel l)
    {
        var e = new List<string>();
        if (l.W is < 20 or > 30) e.Add($"w = {l.W} (треба 20..30)");
        if (l.H is < 12 or > 17) e.Add($"h = {l.H} (треба 12..17)");
        if (l.Rows.Length != l.H) e.Add($"рядків {l.Rows.Length}, а h = {l.H}");
        for (var r = 0; r < l.Rows.Length; r++)
        {
            var row = l.Rows[r];
            if (row.Length != l.W) e.Add($"рядок {r}: довжина {row.Length}, а w = {l.W}");
            foreach (var ch in row)
                if ("#.WLM_".IndexOf(ch) < 0) { e.Add($"рядок {r}: символ «{ch}»"); break; }
        }
        if (e.Count > 0) return e;
        for (var c = 0; c < l.W; c++)
            if (l.Tile(c, 0) != VohnykLevel.Stone || l.Tile(c, l.H - 1) != VohnykLevel.Stone) { e.Add("рамка: верх/низ не з каменю"); break; }
        for (var r = 0; r < l.H; r++)
            if (l.Tile(0, r) != VohnykLevel.Stone || l.Tile(l.W - 1, r) != VohnykLevel.Stone) { e.Add("рамка: боки не з каменю"); break; }

        bool Inside(int c, int r) => c > 0 && r > 0 && c < l.W - 1 && r < l.H - 1;
        byte Own(int who) => who == 0 ? VohnykLevel.Lava : VohnykLevel.Water;
        for (var who = 0; who < 2; who++)
        {
            var (c, r) = l.Spawn[who];
            if (!Inside(c, r) || l.Tile(c, r) != VohnykLevel.Air) e.Add($"спавн {who}: не повітря");
            else if (l.Tile(c, r + 1) != VohnykLevel.Stone && l.Tile(c, r + 1) != VohnykLevel.Thin && l.Tile(c, r + 1) != Own(who)) e.Add($"спавн {who}: під ногами нема опори");
            var (ec, er) = l.Exits[who];
            if (!Inside(ec, er) || l.Tile(ec, er) != VohnykLevel.Air || l.Tile(ec, er + 1) != VohnykLevel.Air) e.Add($"вихід {who}: не два повітря");
            else if (l.Tile(ec, er + 2) is not (VohnykLevel.Stone or VohnykLevel.Thin)) e.Add($"вихід {who}: не на камені");
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in l.Buttons.Select(b => b.Id).Concat(l.Levers.Select(x => x.Id)).Concat(l.Doors.Select(d => d.Id)).Concat(l.Lifts.Select(x => x.Id))
                     .Concat(l.Beams.Select(x => x.Id)).Concat(l.Sensors.Select(x => x.Id)).Concat(l.Portals.Select(x => x.Id)))
            if (string.IsNullOrEmpty(id) || !ids.Add(id)) e.Add($"id «{id}» порожній або не унікальний");
        foreach (var d in l.Doors)
        {
            if (d.Tiles is < 1 or > 6) e.Add($"двері {d.Id}: h = {d.Tiles}");
            if (d.By.Length == 0) e.Add($"двері {d.Id}: нема сигналів");
            if (!Inside(d.Col, d.Row) || !Inside(d.Col, d.Row + d.Tiles - 1)) e.Add($"двері {d.Id}: поза рівнем");
        }
        foreach (var x in l.Lifts)
        {
            if (x.Tiles is < 1 or > 4) e.Add($"ліфт {x.Id}: w = {x.Tiles}");
            if (x.By.Length == 0) e.Add($"ліфт {x.Id}: нема сигналів");
            if (x.Col != x.ToCol && x.Row != x.ToRow) e.Add($"ліфт {x.Id}: рух не по одній осі");
            if (x.Col == x.ToCol && x.Row == x.ToRow) e.Add($"ліфт {x.Id}: стоїть на місці");
        }
        foreach (var x in l.Levers)
        {
            if (x.Init is not (0 or 1)) e.Add($"важіль {x.Id}: init = {x.Init}");
            if (x.Mirror && (!Inside(x.Col, x.Row) || l.Tile(x.Col, x.Row) != VohnykLevel.Air)) e.Add($"дзеркало {x.Id}: не в повітрі");
        }
        var cells = new HashSet<(int, int)>();
        foreach (var x in l.Levers.Where(v => v.Mirror)) if (!cells.Add((x.Col, x.Row))) e.Add($"дзеркало {x.Id}: клітинка зайнята");
        foreach (var x in l.Sensors)
        {
            if (!Inside(x.Col, x.Row) || l.Tile(x.Col, x.Row) != VohnykLevel.Air) e.Add($"кришталь {x.Id}: не в повітрі");
            if (!cells.Add((x.Col, x.Row))) e.Add($"кришталь {x.Id}: клітинка зайнята");
        }
        foreach (var x in l.Beams)
            if (!Inside(x.Col, x.Row) || l.Tile(x.Col, x.Row) != VohnykLevel.Air) e.Add($"ліхтар {x.Id}: не в повітрі");
        foreach (var p in l.Portals)
            foreach (var (pc, pr) in new[] { (p.ACol, p.ARow), (p.BCol, p.BRow) })
                if (!Inside(pc, pr) || !Inside(pc, pr + 1) || l.Tile(pc, pr) == VohnykLevel.Stone || l.Tile(pc, pr + 1) == VohnykLevel.Stone)
                    e.Add($"портал {p.Id}: кінець [{pc},{pr}] у камені чи поза рівнем");
        if (l.Levers.Length > 8) e.Add("важелів і дзеркал понад 8");
        if (l.Beams.Length > 4) e.Add("ліхтарів понад 4");
        if (l.Sensors.Length > 4) e.Add("кришталів понад 4");
        if (l.Portals.Length > 3) e.Add("порталів понад 3");
        if (l.Buttons.Length + l.Levers.Length + l.Sensors.Length > 30) e.Add("сигналів понад 30");
        foreach (var g in l.Gems)
            if (!Inside(g.Col, g.Row) || l.Tile(g.Col, g.Row) != VohnykLevel.Air) e.Add($"самоцвіт [{g.Col},{g.Row}]: не в повітрі");
        foreach (var b in l.Boxes)
            if (!Inside(b.Col, b.Row) || l.Tile(b.Col, b.Row) != VohnykLevel.Air) e.Add($"скриня [{b.Col},{b.Row}]: не в повітрі");
        if (l.Gems.Length > 31) e.Add("самоцвітів понад 31");
        if (l.Boxes.Length > 8) e.Add("скринь понад 8");
        if (l.Doors.Length > 8) e.Add("дверей понад 8");
        if (l.Lifts.Length > 4) e.Add("ліфтів понад 4");
        if (l.Buttons.Length > 8) e.Add("кнопок понад 8");
        if (l.Hints.Length > 4) e.Add("підказок понад 4");
        if (l.Hints.Length > 0 && l.N > 2 && l.N <= VohnykLevels.Cave1) e.Add("підказки — лише на рівнях 1–2 і в другій печері (де з'являється нова механіка)");
        if (l.Par <= 0) e.Add("par має бути додатним");
        foreach (var s in l.Solution.Concat(l.Solo?.Solution ?? []))
            if (s is not { Length: 3 } || s[0] < 1 || s[1] is not (0 or 1) || s[2] is < 0 or > 7) { e.Add("solution: запис має бути [крок ≥ 1, 0|1, 0..7]"); break; }
        return e;
    }
}
