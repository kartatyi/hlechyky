using System.Text;
using System.Text.Json;
using Hlechyky.Games.Impl;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Програти записаний журнал вводу на свіжому світі (так само рахує й сервер, і сценарій паритету в браузері)
/// та переписати файл рівня з новим <c>solution</c>/<c>check</c> — у читабельному вигляді: рядок мапи на рядок.
/// </summary>
public static class VohnykRecord
{
    /// <summary>Результат прогону журналу: крок, на якому зараховано вихід (0 — не зараховано), хеші, самоцвіти, смерть.</summary>
    public sealed record Run(int ClearedAt, int Steps, int Hash, int[] Hashes, int Gems, int DiedAt);

    public const int Every = 100;

    /// <summary>
    /// Грати журнал до «пройдено» (або до maxSteps). Журнал: [крок (1-based), герой, k]; k діє з цього кроку.
    /// </summary>
    public static Run Replay(VohnykLevel level, int[][] solution, int maxSteps = 20000, bool solo = false)
    {
        var w = new VohnykWorld(level) { Solo = solo };
        var k = new int[2];
        var p = 0;
        var hashes = new List<int>();
        var ordered = solution.OrderBy(e => e[0]).ToArray();
        for (var s = 1; s <= maxSteps; s++)
        {
            while (p < ordered.Length && ordered[p][0] <= s) { k[ordered[p][1]] = ordered[p][2]; p++; }
            w.Step(k[0], k[1]);
            if (s % Every == 0) hashes.Add((int)w.Hash());
            if (w.AnyDied) return new Run(0, s, (int)w.Hash(), [.. hashes], w.Gems, s);
            if (w.Cleared != 0) return new Run(s, s, (int)w.Hash(), [.. hashes], w.Gems, 0);
        }
        return new Run(0, maxSteps, (int)w.Hash(), [.. hashes], w.Gems, 0);
    }

    public static string LevelPath(int n) => Path.Combine(Paths.Resolve(VohnykLevels.Dir), $"{n:00}.json");

    /// <summary>Переписати проходження сам за двох у файлі рівня n, зберігши решту як була.</summary>
    public static void SaveSolo(int n, int[][] solution, VohnykCheck check)
    {
        var path = LevelPath(n);
        var file = JsonSerializer.Deserialize<VohnykLevelFile>(File.ReadAllText(path), VohnykLevels.Json)!;
        file.Solo = new VohnykSoloRun { Solution = solution, Check = check };
        File.WriteAllText(path, Format(file), new UTF8Encoding(false));
    }

    /// <summary>Переписати solution і check у файлі рівня n, зберігши решту як була.</summary>
    public static void Save(int n, int[][] solution, VohnykCheck check, int? par = null)
    {
        var path = LevelPath(n);
        var file = JsonSerializer.Deserialize<VohnykLevelFile>(File.ReadAllText(path), VohnykLevels.Json)!;
        file.Solution = solution;
        file.Check = check;
        if (par is { } p) file.Par = p;
        File.WriteAllText(path, Format(file), new UTF8Encoding(false));
    }

    static string J(object? o) => JsonSerializer.Serialize(o, Opts);

    static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Файл рівня так, як його зручно читати й правити руками.</summary>
    public static string Format(VohnykLevelFile f)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append($"  \"n\": {f.N}, \"name\": {J(f.Name)}, \"par\": {f.Par},\n");
        sb.Append($"  \"w\": {f.W}, \"h\": {f.H},\n");
        sb.Append("  \"rows\": [\n");
        for (var i = 0; i < f.Rows.Length; i++)
            sb.Append("    ").Append(J(f.Rows[i])).Append(i + 1 < f.Rows.Length ? ",\n" : "\n");
        sb.Append("  ],\n");
        sb.Append($"  \"spawn\": {{ \"fire\": {J(f.Spawn.Fire)}, \"water\": {J(f.Spawn.Water)} }},\n");
        List("gems", f.Gems.Select(g => $"{{ \"who\": {J(g.Who)}, \"at\": {J(g.At)} }}"));
        sb.Append($"  \"exits\": {{ \"fire\": {J(f.Exits.Fire)}, \"water\": {J(f.Exits.Water)} }},\n");
        List("buttons", f.Buttons.Select(b => $"{{ \"id\": {J(b.Id)}, \"at\": {J(b.At)} }}"));
        List("levers", f.Levers.Select(l => $"{{ \"id\": {J(l.Id)}, \"at\": {J(l.At)}, \"init\": {l.Init} }}"));
        List("doors", f.Doors.Select(d => $"{{ \"id\": {J(d.Id)}, \"at\": {J(d.At)}, \"h\": {d.H}, \"by\": {J(d.By)}, \"mode\": {J(d.Mode)}, \"inv\": {J(d.Inv)} }}"));
        List("lifts", f.Lifts.Select(l => $"{{ \"id\": {J(l.Id)}, \"at\": {J(l.At)}, \"w\": {l.W}, \"to\": {J(l.To)}, \"by\": {J(l.By)}, \"mode\": {J(l.Mode)}, \"inv\": {J(l.Inv)} }}"));
        List("boxes", f.Boxes.Select(b => $"{{ \"at\": {J(b.At)} }}"));
        List("hints", f.Hints.Select(h => $"{{ \"at\": {J(h.At)}, \"w\": {h.W}, \"text\": {J(h.Text)} }}"));
        // друга печера — лише там, де є
        if (f.Mirrors.Length > 0) List("mirrors", f.Mirrors.Select(m => $"{{ \"id\": {J(m.Id)}, \"at\": {J(m.At)}, \"init\": {m.Init}, \"fixed\": {J(m.Fixed)} }}"));
        if (f.Beams.Length > 0) List("beams", f.Beams.Select(b => $"{{ \"id\": {J(b.Id)}, \"at\": {J(b.At)}, \"dir\": {J(b.Dir)}, \"who\": {J(b.Who)}, \"by\": {J(b.By)}, \"mode\": {J(b.Mode)}, \"inv\": {J(b.Inv)} }}"));
        if (f.Sensors.Length > 0) List("sensors", f.Sensors.Select(s => $"{{ \"id\": {J(s.Id)}, \"at\": {J(s.At)} }}"));
        if (f.Portals.Length > 0) List("portals", f.Portals.Select(p => $"{{ \"id\": {J(p.Id)}, \"a\": {J(p.A)}, \"b\": {J(p.B)}, \"by\": {J(p.By)}, \"mode\": {J(p.Mode)}, \"inv\": {J(p.Inv)} }}"));
        sb.Append("  \"solution\": ");
        Log(f.Solution, "  ");
        if (f.Check is { } c)
        {
            sb.Append(",\n  \"check\": ");
            Check(c, "  ");
        }
        if (f.Solo is { } so)
        {
            sb.Append(",\n  \"solo\": {\n    \"solution\": ");
            Log(so.Solution, "    ");
            if (so.Check is { } sc)
            {
                sb.Append(",\n    \"check\": ");
                Check(sc, "    ");
            }
            sb.Append("\n  }");
        }
        sb.Append("\n}\n");
        return sb.ToString();

        void Log(int[][] log, string pad)
        {
            sb.Append('[');
            for (var i = 0; i < log.Length; i++)
            {
                if (i % 10 == 0) sb.Append('\n').Append(pad).Append("  ");
                sb.Append(J(log[i])).Append(i + 1 < log.Length ? ", " : "");
            }
            sb.Append(log.Length > 0 ? "\n" + pad + "]" : "]");
        }

        void Check(VohnykCheck ch, string pad) =>
            sb.Append($"{{ \"steps\": {ch.Steps}, \"hash\": {ch.Hash}, \"every\": {ch.Every},\n{pad}  \"hashes\": {J(ch.Hashes)} }}");

        void List(string name, IEnumerable<string> items)
        {
            var all = items.ToList();
            if (all.Count == 0) { sb.Append($"  \"{name}\": [],\n"); return; }
            sb.Append($"  \"{name}\": [\n");
            for (var i = 0; i < all.Count; i++) sb.Append("    ").Append(all[i]).Append(i + 1 < all.Count ? ",\n" : "\n");
            sb.Append("  ],\n");
        }
    }
}
