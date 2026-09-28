namespace Hlechyky.Games.Impl;

/// <summary>Задача: позиція (FEN), мат за скільки ходів і єдиний перший хід («ключ», координатами: e2e4, e7e8q).</summary>
public sealed record ChessPuzzle(string Fen, int N, string Key)
{
    public bool WhiteToMove => Fen.Split(' ')[1] == "w";
}

/// <summary>
/// Банк «Задачі дня»: власний, згенерований рушієм (<see cref="ChessEngine"/>) з випадкових ендшпільних позицій —
/// тож жодних чужих авторських задач і ліцензій. Кожна перевірена тестом ChessDailyTests: мат справді за N,
/// перший хід єдиний, швидшого мату нема. Половину віддзеркалено — там починають і матують чорні.
/// Генератор — tests/Hlechyky.Tests/Games/ChessPuzzleGen.cs.
/// </summary>
public static class ChessPuzzles
{
    /// <summary>З якого дня рахуємо задачі по колу: перший день — перша задача банку.</summary>
    static readonly DateOnly Epoch = new(2026, 9, 29);

    public static readonly ChessPuzzle[] Bank =
    [
        new("3Q4/3K4/kN6/4p3/8/2P5/8/3n4 w - - 0 1", 2, "d7c6"),
        new("7N/8/4q3/8/1k6/4P3/2Kp4/8 b - - 0 1", 3, "e6e3"),
        new("1K1N3k/8/3Q4/6n1/8/8/8/8 w - - 0 1", 2, "d6g6"),
        new("8/3r4/8/K7/2b3k1/4n1N1/8/8 b - - 0 1", 2, "e3d5"),
        new("1R6/k1B1Pp2/8/6pK/8/8/8/8 w - - 0 1", 2, "e7e8q"),
        new("4r3/K7/1r6/k7/8/8/8/3N4 b - - 0 1", 2, "e8b8"),
        new("8/1p4P1/5N1B/8/7k/5K2/8/5N2 w - - 0 1", 2, "g7g8q"),
        new("8/8/2P2pb1/3P4/6k1/6n1/4n2K/8 b - - 0 1", 2, "g6e4"),
        new("6k1/2R3p1/3PP3/6K1/8/3p1p2/8/8 w - - 0 1", 2, "g5g6"),
        new("8/8/5P2/2b5/7P/5kr1/7p/3K4 b - - 0 1", 2, "g3g2"),
        new("K7/8/4p3/7k/3n2R1/8/8/6R1 w - - 0 1", 3, "g4d4"),
        new("8/8/7N/7q/8/k1r5/8/4K3 b - - 0 1", 2, "c3c2"),
        new("8/8/8/3Q4/k2P4/2K5/8/7n w - - 0 1", 2, "d5c5"),
        new("8/2P5/8/k7/6b1/6p1/N2n4/4n2K b - - 0 1", 2, "d2f3"),
        new("8/3B4/3Q3p/7k/4p3/bK6/8/8 w - - 0 1", 2, "d6g3"),
        new("7K/7n/8/6q1/8/8/2k5/7N b - - 0 1", 2, "h7f6"),
        new("8/2p5/K7/8/6p1/3QN3/8/k7 w - - 0 1", 2, "d3b3"),
        new("5k2/8/5q2/7B/8/1K2n3/p7/8 b - - 0 1", 2, "a2a1q"),
        new("5nk1/K7/5B2/2Q5/8/8/8/8 w - - 0 1", 3, "c5e7"),
        new("3qb2K/4k3/8/8/8/P2B2P1/8/8 b - - 0 1", 3, "e7f8"),
        new("8/k2KB3/8/8/4R3/2N5/8/8 w - - 0 1", 2, "d7c7"),
        new("8/8/8/8/7k/4Pp2/1r6/3K4 b - - 0 1", 3, "f3f2"),
        new("8/2P5/8/4R3/8/k2K3p/8/8 w - - 0 1", 3, "c7c8q"),
        new("8/5K2/4r3/6P1/1rk5/8/8/8 b - - 0 1", 3, "e6a6"),
        new("4k3/8/5p1R/8/3Q4/4n3/8/4K3 w - - 0 1", 3, "d4a7"),
        new("4n3/6q1/3P4/8/6pK/4k3/8/8 b - - 0 1", 2, "e3f4"),
        new("8/8/8/7p/5R2/8/p6R/2K3k1 w - - 0 1", 3, "h2a2"),
        new("8/8/2q5/8/2b3k1/K7/8/2N5 b - - 0 1", 2, "c6b5"),
        new("4K3/2p5/7k/4p3/3RQ3/8/8/8 w - - 0 1", 2, "e8f7"),
        new("8/3N4/8/5n2/6k1/3q4/P7/6K1 b - - 0 1", 2, "g4g3"),
        new("8/4b3/8/3P4/N7/p6K/2Q5/k7 w - - 0 1", 2, "a4c3"),
        new("3r4/8/8/4r3/P1P4K/8/4k3/8 b - - 0 1", 2, "e2f3"),
        new("K7/3p4/6R1/8/8/3B3k/8/4N3 w - - 0 1", 2, "e1f3"),
        new("6K1/1P6/4P2k/1P2b3/5q2/8/8/8 b - - 0 1", 2, "f4f6"),
        new("B7/k7/3Q4/p1K5/6p1/8/2b5/8 w - - 0 1", 2, "a8d5"),
        new("8/8/8/1P6/3P4/4K3/2qP4/n3k3 b - - 0 1", 2, "c2f5"),
        new("8/8/8/8/8/2K2p2/Q1B5/4k3 w - - 0 1", 2, "c2d3"),
        new("8/4k3/7K/8/8/3q4/P2p4/7N b - - 0 1", 2, "e7f6"),
        new("4k3/8/P3K3/4Q3/8/4r3/8/8 w - - 0 1", 3, "e5e3"),
        new("6k1/8/8/4q2n/1P6/6p1/8/7K b - - 0 1", 2, "h5f4"),
        new("8/6Qp/8/8/8/7k/8/6BK w - - 0 1", 2, "g1f2"),
        new("8/8/1Pk5/6P1/7K/8/3q2p1/8 b - - 0 1", 2, "g2g1q"),
        new("5k2/2pP4/8/8/p2N3Q/6K1/8/8 w - - 0 1", 2, "h4h7"),
        new("3N1n2/8/7K/4rk2/8/8/8/8 b - - 0 1", 3, "e5e7"),
        new("3K4/8/p7/3p4/3R4/6Q1/2p5/5k2 w - - 0 1", 2, "d4d2"),
        new("8/8/1k6/1r6/P7/pP6/K1p5/8 b - - 0 1", 3, "c2c1q"),
        new("8/8/5Q2/8/2K5/8/6Nk/8 w - - 0 1", 3, "f6f3"),
        new("K7/1r2r3/8/8/2P1P1k1/8/8/8 b - - 0 1", 2, "b7d7"),
        new("3Q4/3b3k/8/8/8/6B1/8/6K1 w - - 0 1", 3, "d8f6"),
        new("8/1r6/2P5/7N/K7/8/1kp5/4b3 b - - 0 1", 2, "c2c1q"),
        new("4k3/1QK4n/5p2/6R1/8/8/8/8 w - - 0 1", 2, "c7d6"),
        new("8/8/8/3k1Pp1/8/8/3q4/6K1 b - - 0 1", 3, "d5e4"),
        new("3R4/1pK5/k7/8/8/3R4/8/n7 w - - 0 1", 2, "d3d5"),
        new("1K6/1p6/2k5/2q5/8/2P4P/6P1/8 b - - 0 1", 2, "c6b6"),
        new("8/5K2/8/n7/1B6/2N5/3P4/k3N3 w - - 0 1", 2, "b4a3"),
        new("K1k5/8/4p3/b7/1r6/8/2N5/8 b - - 0 1", 2, "b4a4"),
        new("7k/8/1p6/4K2B/1p4Q1/8/8/8 w - - 0 1", 2, "e5f6"),
        new("8/3k4/K7/2p5/8/1q6/8/8 b - - 0 1", 2, "d7c6"),
        new("4k2N/8/4K3/B7/1n3R2/8/8/8 w - - 0 1", 2, "h8g6"),
        new("3K4/8/1n6/8/5qk1/6B1/8/8 b - - 0 1", 2, "f4f7"),
        new("8/3QN2k/8/8/8/4K3/8/8 w - - 0 1", 2, "d7g4"),
        new("8/1P6/4n3/4P1q1/8/3k4/7K/8 b - - 0 1", 2, "e6f4"),
        new("8/1b6/3p4/3N4/1Q6/8/5K2/k7 w - - 0 1", 2, "d5c3"),
        new("8/8/4P2K/8/k3qr2/8/8/8 b - - 0 1", 2, "f4g4"),
        new("1k6/5B2/1pK5/1p6/2P1R3/8/8/8 w - - 0 1", 3, "c4b5"),
        new("8/8/2k5/8/P3q2P/1P6/3b4/K7 b - - 0 1", 2, "e4c2"),
        new("8/8/2P5/k6p/2K5/3Q1p2/8/1b6 w - - 0 1", 3, "d3b1"),
        new("8/6k1/8/8/1r5N/8/r7/2K5 b - - 0 1", 3, "b4h4"),
        new("k7/B7/8/8/Q6K/5b2/8/8 w - - 0 1", 3, "a4a6"),
        new("8/8/8/2P1P3/pnk5/4P3/8/Knb5 b - - 0 1", 2, "b1c3"),
        new("1k4K1/8/3N4/2p5/2Q3p1/8/2p5/8 w - - 0 1", 2, "c4f7"),
        new("1k6/8/8/pP6/8/1P6/1K1p4/2r5 b - - 0 1", 3, "d2d1q"),
    ];

    /// <summary>Задача на день «рррр-мм-дд»: по колу за банком, одна на всіх.</summary>
    public static ChessPuzzle ForDay(string day)
    {
        var d = DateOnly.TryParseExact(day, "yyyy-MM-dd", out var x) ? x : Epoch;
        var i = (d.DayNumber - Epoch.DayNumber) % Bank.Length;
        return Bank[i < 0 ? i + Bank.Length : i];
    }

    /// <summary>Номер задачі в банку для «Задача №…» (з одиниці).</summary>
    public static int NumberOf(ChessPuzzle p) => Array.IndexOf(Bank, p) + 1;
}
