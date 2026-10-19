using Hlechyky.Games.Economy;

namespace Hlechyky.Tests.Games;

/// <summary>Сила гравця в грі (пакет 1, 19.10): формула на вигаданих партіях і читання з тестової бази.</summary>
public class SkillBoardTests
{
    [Fact]
    public void Formula_counts_wins_against_expected_share()
    {
        // n=2: дві перемоги з двох → (2 − 1) / (2 + 4)
        Assert.Equal(1.0 / 6, SkillBoard.Formula([new("win", 2), new("win", 2)]), 9);
        // n=4: одна перемога з двох → (1 − 0,5) / (2 + 4)
        Assert.Equal(0.5 / 6, SkillBoard.Formula([new("win", 4), new("loss", 4)]), 9);
        // нічия — половина перемоги: на двох це рівно очікуване
        Assert.Equal(0, SkillBoard.Formula([new("draw", 2)]), 9);
        // поразки на двох — мінус
        Assert.Equal(-1.0 / 6, SkillBoard.Formula([new("loss", 2), new("loss", 2)]), 9);
        // новачок — 0; соло не рахується
        Assert.Equal(0, SkillBoard.Formula([]));
        Assert.Equal(0, SkillBoard.Formula([new("solo", 1)]));
    }

    [Fact]
    public void Formula_takes_only_last_window()
    {
        // спершу (найновіші) 40 поразок на двох, далі 100 перемог — старі не рахуються
        var games = Enumerable.Repeat(new SkillGame("loss", 2), SkillBoard.Window)
            .Concat(Enumerable.Repeat(new SkillGame("win", 2), 100));
        Assert.Equal(-20.0 / 44, SkillBoard.Formula(games), 9);
    }

    [Fact]
    public void Reload_reads_people_per_round_from_db()
    {
        var dir = Path.Combine(Path.GetTempPath(), "skill-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        try
        {
            var db = new Db(Path.Combine(dir, "t.db"));
            db.With(c =>
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO game_results(room_id, game, round, nick_key, nick, outcome, created_at) VALUES
                      ('r1', 'curve', 1, 'оля', 'Оля', 'win', '2026-10-01T10:00:00+00:00'),
                      ('r1', 'curve', 1, 'петро', 'Петро', 'loss', '2026-10-01T10:00:00+00:00'),
                      ('r1', 'curve', 1, 'іра', 'Іра', 'loss', '2026-10-01T10:00:00+00:00'),
                      ('r1', 'curve', 1, 'макс', 'Макс', 'loss', '2026-10-01T10:00:00+00:00'),
                      ('r2', 'curve', 1, 'оля', 'Оля', 'draw', '2026-10-01T11:00:00+00:00'),
                      ('r2', 'curve', 1, 'петро', 'Петро', 'draw', '2026-10-01T11:00:00+00:00'),
                      ('s1', 'curve', 0, 'оля', 'Оля', 'solo', '2026-10-01T12:00:00+00:00'),
                      ('r3', 'bomber', 1, 'оля', 'Оля', 'loss', '2026-10-01T12:00:00+00:00'),
                      ('r3', 'bomber', 1, 'петро', 'Петро', 'win', '2026-10-01T12:00:00+00:00')
                    """;
                cmd.ExecuteNonQuery();
            });
            var board = new SkillBoard(db, null);
            Assert.Equal(0, board.Strength("curve", "Оля"));   // ще не читали
            board.Reload();
            // Оля в Кривулі: перемога на чотирьох (1 − 0,25) + нічия на двох (0,5 − 0,5) = 0,75 / (2 + 4)
            Assert.Equal(0.75 / 6, board.Strength("curve", "Оля"), 9);
            Assert.Equal(0.75 / 6, board.Strength("curve", " оля "), 9);   // нік — за ключем
            Assert.Equal(-0.25 / 6, board.Strength("curve", "Петро"), 9);
            Assert.Equal(-0.5 / 5, board.Strength("bomber", "Оля"), 9);
            Assert.Equal(0, board.Strength("curve", "Новачок"));
            board.Reload("bomber");   // одна гра — інші лишаються
            Assert.Equal(0.75 / 6, board.Strength("curve", "Оля"), 9);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
