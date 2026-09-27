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

/// <summary>Важіль: клітинка й початковий стан.</summary>
public sealed record VohnykLeverDef(string Id, int Col, int Row, int Init);

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

/// <summary>Розібраний рівень. Плитки: 0 повітря, 1 камінь, 2 вода, 3 лава, 4 болото.</summary>
public sealed class VohnykLevel
{
    public const byte Air = 0, Stone = 1, Water = 2, Lava = 3, Mud = 4;

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

public sealed class VohnykHintFile : VohnykAtFile
{
    public int W { get; set; } = 6;
    public string Text { get; set; } = "";
}

/// <summary>Усі 15 рівнів, раз на процес. Кривий файл — виняток з іменем файла при першому зверненні.</summary>
public static class VohnykLevels
{
    public const int Count = 15;
    public const string Dir = "data/vohnyk/levels";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    static readonly Lazy<VohnykLevel[]> Cached = new(() => LoadDir(Paths.Resolve(Dir)));

    /// <summary>Рівні 1..15 (індекс = n − 1).</summary>
    public static VohnykLevel[] All => Cached.Value;

    /// <summary>Чи лежать рівні на диску — без винятку (для Configure, щоб не валити сервер).</summary>
    public static bool Available
    {
        get
        {
            try { return All.Length == Count; }
            catch (Exception) { return false; }
        }
    }

    public static VohnykLevel Get(int n) => All[n - 1];

    public static VohnykLevel[] LoadDir(string dir)
    {
        var list = new VohnykLevel[Count];
        for (var n = 1; n <= Count; n++)
        {
            var path = Path.Combine(dir, $"{n:00}.json");
            list[n - 1] = LoadFile(path);
            if (list[n - 1].N != n) throw new InvalidDataException($"{path}: n = {list[n - 1].N}, а має бути {n}");
        }
        return list;
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
                    _ => VohnykLevel.Air,
                };
        }
        var signals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var b in f.Buttons) signals.TryAdd(b.Id, signals.Count);
        foreach (var l in f.Levers) signals.TryAdd(l.Id, signals.Count);
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
            Levers = [.. f.Levers.Select(l => { var (c, r) = At(l.At, l.Id); return new VohnykLeverDef(l.Id, c, r, l.Init); })],
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
                if ("#.WLM".IndexOf(ch) < 0) { e.Add($"рядок {r}: символ «{ch}»"); break; }
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
            else if (l.Tile(c, r + 1) != VohnykLevel.Stone && l.Tile(c, r + 1) != Own(who)) e.Add($"спавн {who}: під ногами нема опори");
            var (ec, er) = l.Exits[who];
            if (!Inside(ec, er) || l.Tile(ec, er) != VohnykLevel.Air || l.Tile(ec, er + 1) != VohnykLevel.Air) e.Add($"вихід {who}: не два повітря");
            else if (l.Tile(ec, er + 2) != VohnykLevel.Stone) e.Add($"вихід {who}: не на камені");
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in l.Buttons.Select(b => b.Id).Concat(l.Levers.Select(x => x.Id)).Concat(l.Doors.Select(d => d.Id)).Concat(l.Lifts.Select(x => x.Id)))
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
            if (x.Init is not (0 or 1)) e.Add($"важіль {x.Id}: init = {x.Init}");
        foreach (var g in l.Gems)
            if (!Inside(g.Col, g.Row) || l.Tile(g.Col, g.Row) != VohnykLevel.Air) e.Add($"самоцвіт [{g.Col},{g.Row}]: не в повітрі");
        foreach (var b in l.Boxes)
            if (!Inside(b.Col, b.Row) || l.Tile(b.Col, b.Row) != VohnykLevel.Air) e.Add($"скриня [{b.Col},{b.Row}]: не в повітрі");
        if (l.Gems.Length > 31) e.Add("самоцвітів понад 31");
        if (l.Boxes.Length > 8) e.Add("скринь понад 8");
        if (l.Doors.Length > 8) e.Add("дверей понад 8");
        if (l.Lifts.Length > 4) e.Add("ліфтів понад 4");
        if (l.Buttons.Length > 8) e.Add("кнопок понад 8");
        if (l.Levers.Length > 4) e.Add("важелів понад 4");
        if (l.Hints.Length > 4) e.Add("підказок понад 4");
        if (l.Hints.Length > 0 && l.N > 2) e.Add("підказки — лише на рівнях 1–2");
        if (l.Par <= 0) e.Add("par має бути додатним");
        foreach (var s in l.Solution.Concat(l.Solo?.Solution ?? []))
            if (s is not { Length: 3 } || s[0] < 1 || s[1] is not (0 or 1) || s[2] is < 0 or > 7) { e.Add("solution: запис має бути [крок ≥ 1, 0|1, 0..7]"); break; }
        return e;
    }
}
