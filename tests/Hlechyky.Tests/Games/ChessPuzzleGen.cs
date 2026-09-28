using System.Collections.Concurrent;
using Hlechyky.Games.Impl;
using Xunit;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Генератор банку «Задачі дня» (ChessPuzzles.cs). Звичайний прогін його пропускає: запускати вручну,
/// <c>CHESS_GEN=шлях_до_файла dotnet test --filter ChessPuzzleGen</c>. Ставить випадкові ендшпільні позиції
/// з королем захисту ближче до краю й лишає лише ті, де мат за N єдиним першим ходом і нема мату швидше.
/// </summary>
public class ChessPuzzleGen(ITestOutputHelper output)
{
    [Fact]
    public void Generate()
    {
        var path = Environment.GetEnvironmentVariable("CHESS_GEN");
        if (string.IsNullOrEmpty(path)) return;
        var want2 = int.Parse(Environment.GetEnvironmentVariable("CHESS_GEN_M2") ?? "50");
        var want3 = int.Parse(Environment.GetEnvironmentVariable("CHESS_GEN_M3") ?? "15");
        var seconds = int.Parse(Environment.GetEnvironmentVariable("CHESS_GEN_SEC") ?? "300");
        var found = new ConcurrentBag<string>();
        int m2 = 0, m3 = 0, tried = 0;
        var until = DateTime.UtcNow.AddSeconds(seconds);
        Parallel.For(0, 4, w =>
        {
            var rng = new Random(1000 + w);
            while (DateTime.UtcNow < until && (Volatile.Read(ref m2) < want2 || Volatile.Read(ref m3) < want3))
            {
                Interlocked.Increment(ref tried);
                var fen = RandomFen(rng, Volatile.Read(ref m3) < want3 && rng.Next(3) == 0);
                ChessCore c;
                try { c = ChessCore.FromFen(fen); } catch { continue; }
                if (c.KingAttacked(false) || c.KingAttacked(true)) continue;
                if (c.Legal().Count < 3) continue;
                if (ChessEngine.ForcedMate(c, 1)) continue;
                if (Volatile.Read(ref m2) < want2 && ChessEngine.ForcedMate(c, 2))
                {
                    var keys = ChessEngine.MateKeys(c, 2);
                    if (keys.Count == 1 && !c.IsCapture(keys[0]) && Replies(c, keys[0]) >= 2)
                    { found.Add($"{fen}|2|{Uci(keys[0])}|{Checks(c, keys[0])}"); Interlocked.Increment(ref m2); }
                    continue;
                }
                if (Volatile.Read(ref m3) < want3 && CountPieces(c) <= 7 && ChessEngine.ForcedMate(c, 3))
                {
                    if (ChessEngine.ForcedMate(c, 2)) continue;
                    var keys = ChessEngine.MateKeys(c, 3);
                    if (keys.Count == 1 && Replies(c, keys[0]) >= 2)
                    { found.Add($"{fen}|3|{Uci(keys[0])}|{Checks(c, keys[0])}"); Interlocked.Increment(ref m3); }
                }
            }
        });
        File.WriteAllLines(path, found);
        output.WriteLine($"tried {tried}, m2 {m2}, m3 {m3}");
    }

    static int CountPieces(ChessCore c) { var n = 0; for (var i = 0; i < 64; i++) if (c.PieceAt(i) != 0) n++; return n; }

    static int Checks(ChessCore c, ChessMove key)
    {
        c.Make(key, out var u);
        var n = c.InCheck() ? 1 : 0;
        c.Unmake(key, u);
        return n;
    }

    static int Replies(ChessCore c, ChessMove key)
    {
        c.Make(key, out var u);
        var n = c.Legal().Count;
        c.Unmake(key, u);
        return n;
    }

    public static string Uci(ChessMove m) => ChessCore.Name(m.From) + ChessCore.Name(m.To) + (m.Promo == 0 ? "" : " pnbrqk"[m.Promo].ToString());

    static string RandomFen(Random rng, bool small)
    {
        var b = new char[64];
        Array.Fill(b, '.');
        int Free(Func<int, bool> ok)
        {
            for (var t = 0; t < 200; t++) { var s = rng.Next(64); if (b[s] == '.' && ok(s)) return s; }
            return -1;
        }
        // Король захисту — біля краю (там мати живуть), король нападу — не впритул.
        var bk = Free(s => rng.Next(10) < 7 ? (s >> 3) <= 1 || (s & 7) is 0 or 7 : true);
        b[bk] = 'k';
        var wk = Free(s => Math.Max(Math.Abs((s & 7) - (bk & 7)), Math.Abs((s >> 3) - (bk >> 3))) >= 2);
        b[wk] = 'K';
        string[] att = small ? ["Q", "R", "RB", "RN", "QN", "BN", "RP", "QB", "RR", "NNB"]
            : ["Q", "R", "QR", "QB", "QN", "RR", "RB", "RN", "BBN", "RBN", "QP", "RPP", "QNP", "RBP", "NNBP"];
        string[] def = small ? ["", "p", "pp", "n", "b", "r", "pn"] : ["", "p", "pp", "ppp", "n", "b", "r", "pn", "pb", "ppr", "rn", "q", "ppb"];
        foreach (var ch in att[rng.Next(att.Length)] + def[rng.Next(def.Length)])
        {
            var pawn = ch is 'P' or 'p';
            // Фігури нападу — ближче до короля захисту: так мат трапляється частіше.
            var s = Free(q => (!pawn || ((q >> 3) is > 0 and < 7))
                && (char.IsLower(ch) || Math.Max(Math.Abs((q & 7) - (bk & 7)), Math.Abs((q >> 3) - (bk >> 3))) <= 4));
            if (s < 0) continue;
            b[s] = ch;
        }
        var rows = new List<string>();
        for (var r = 0; r < 8; r++)
        {
            var row = ""; var empty = 0;
            for (var f = 0; f < 8; f++)
            {
                var ch = b[r * 8 + f];
                if (ch == '.') { empty++; continue; }
                if (empty > 0) { row += empty; empty = 0; }
                row += ch;
            }
            if (empty > 0) row += empty;
            rows.Add(row);
        }
        return string.Join('/', rows) + " w - - 0 1";
    }
}
