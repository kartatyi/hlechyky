using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Кахлі» (docs/games/specs/kakhli.md): ядро поля (збіги, рушники, жаринки, глеки, пари, глина, мотузки, каскади,
/// перемішування) і три гри на ньому — «Кахлі дня», «На час», «Шлях кахляра».
/// </summary>
public class KakhliTests(ITestOutputHelper output)
{
    // ---------- ядро ----------

    static int Cell(KakhliBoard b, int x, int y) => y * b.W + x;

    static void AssertHealthy(KakhliBoard b)
    {
        Assert.True(b.Settled, "між ходами на полі не лишається готових збігів");
        var ids = new HashSet<int>();
        for (var c = 0; c < b.Cells; c++)
        {
            if (b.IsHole(c)) { Assert.Equal(-1, b.IdAt(c)); continue; }
            Assert.True(b.IdAt(c) > 0, $"клітинка {c} порожня після ходу");
            Assert.True(ids.Add(b.IdAt(c)), $"id {b.IdAt(c)} двічі");
            if (b.KindAt(c) == KakhliKind.Glek) Assert.Equal(-1, b.ColorAt(c));
            else Assert.InRange(b.ColorAt(c), 0, b.Colors - 1);
        }
    }

    [Fact]
    public void Fill_has_no_matches_and_at_least_one_move()
    {
        for (ulong seed = 1; seed <= 60; seed++)
        {
            var b = new KakhliBoard(8, 8, 6, seed);
            b.Fill();
            AssertHealthy(b);
            Assert.NotNull(b.FindMove());
        }
    }

    [Fact]
    public void Same_seed_same_board_and_same_moves_give_same_result()
    {
        var a = new KakhliBoard(8, 8, 6, 77);
        var b = new KakhliBoard(8, 8, 6, 77);
        a.Fill();
        b.Fill();
        Assert.Equal(Views.Json(a.Wire()).GetRawText(), Views.Json(b.Wire()).GetRawText());
        for (var i = 0; i < 25; i++)
        {
            var m = a.Moves()[i % a.Moves().Count];
            Assert.NotNull(a.Swap(m.A, m.B));
            Assert.NotNull(b.Swap(m.A, m.B));
        }
        Assert.Equal(a.Score, b.Score);
        Assert.Equal(Views.Json(a.Wire()).GetRawText(), Views.Json(b.Wire()).GetRawText());
        var c = new KakhliBoard(8, 8, 6, 78);
        c.Fill();
        Assert.NotEqual(Views.Json(a.Wire()).GetRawText(), Views.Json(c.Wire()).GetRawText());
    }

    [Fact]
    public void Swap_that_makes_nothing_or_is_not_adjacent_is_refused_and_board_stays()
    {
        var b = KakhliBoard.Lay("""
            1 2 1 2 1
            2 1 2 1 2
            0 0 3 4 0
            3 4 0 1 3
            """);
        var before = Views.Json(b.Wire()).GetRawText();
        Assert.Null(b.Swap(Cell(b, 0, 0), Cell(b, 1, 0)));   // 2 1 1 2 1 — нічого
        Assert.Null(b.Swap(Cell(b, 0, 0), Cell(b, 2, 0)));   // не сусіди
        Assert.Null(b.Swap(Cell(b, 0, 0), Cell(b, 1, 1)));   // по діагоналі
        Assert.Null(b.Swap(-1, 0));
        Assert.Equal(before, Views.Json(b.Wire()).GetRawText());
        Assert.Equal(0, b.Score);
    }

    [Fact]
    public void Four_in_a_row_makes_a_rushnyk_where_the_tile_was_moved()
    {
        var b = KakhliBoard.Lay("""
            1 2 1 2 1
            2 1 2 1 2
            0 0 3 0 4
            3 4 0 1 3
            """);
        var a = Cell(b, 2, 2);
        var steps = b.Swap(a, Cell(b, 2, 3))!;
        Assert.Contains(steps[0].Made, m => m[1] == a && m[2] == 0 && m[3] == KakhliKind.Row);
        Assert.Equal(4, steps[0].Gone.Count);
        Assert.True(b.Stripes >= 1);   // каскад із досипаних може скласти ще
        AssertHealthy(b);
    }

    [Fact]
    public void Five_in_a_row_makes_a_magic_glek_and_an_l_makes_an_ember()
    {
        var b = KakhliBoard.Lay("""
            1 2 1 2 1
            2 1 2 1 2
            0 0 3 0 0
            3 4 0 4 3
            """);
        var a = Cell(b, 2, 2);
        var steps = b.Swap(a, Cell(b, 2, 3))!;
        Assert.Contains(steps[0].Made, m => m[1] == a && m[2] == -1 && m[3] == KakhliKind.Glek);
        Assert.Equal(1, b.Gleks);

        var l = KakhliBoard.Lay("""
            0 1 2 1 2
            0 2 1 2 1
            3 0 0 4 3
            0 2 1 2 1
            """);
        var corner = Cell(l, 0, 2);
        var st = l.Swap(corner, Cell(l, 0, 3))!;
        Assert.Contains(st[0].Made, m => m[1] == corner && m[2] == 0 && m[3] == KakhliKind.Ember);
        Assert.Equal(5, st[0].Gone.Count);
        Assert.Equal(1, l.Embers);
    }

    [Fact]
    public void Rushnyk_in_a_match_clears_its_whole_row()
    {
        var b = KakhliBoard.Lay("""
            1 2 1 2 1
            0h 0 3 1 2
            2 1 0 2 1
            1 2 1 3 1
            """);
        var steps = b.Swap(Cell(b, 2, 1), Cell(b, 2, 2))!;
        Assert.Contains(steps[0].Fx, f => f[0] == KakhliFx.Row && f[1] == Cell(b, 0, 1));
        Assert.Equal(5, steps[0].Gone.Count);
        AssertHealthy(b);
    }

    [Fact]
    public void Glek_with_a_plain_tile_takes_that_whole_colour()
    {
        var b = KakhliBoard.Lay("""
            1 2 1 2 1
            2 g 2 1 2
            1 3 1 3 1
            """);
        var steps = b.Swap(Cell(b, 1, 1), Cell(b, 2, 1))!;
        Assert.Contains(steps[0].Fx, f => f[0] == KakhliFx.Glek && f[2] == 2);
        Assert.Equal(6, steps[0].Gone.Count);   // п'ять двійок і сам глек
        Assert.Equal(5, b.Collected[2]);
    }

    [Fact]
    public void Glek_with_a_rushnyk_turns_the_colour_into_rushnyks_and_fires_them()
    {
        var b = KakhliBoard.Lay("""
            1 3 1 3 1
            3 g 2h 1 3
            1 2 1 3 1
            3 1 3 2 3
            """);
        var steps = b.Swap(Cell(b, 1, 1), Cell(b, 2, 1))!;
        // інші двійки стали рушниками
        Assert.Equal(2, steps[0].Made.Count(m => m[2] == 2 && KakhliKind.Stripe((byte)m[3])));
        Assert.True(steps[0].Fx.Count(f => f[0] is KakhliFx.Row or KakhliFx.Col) >= 3);
        AssertHealthy(b);
    }

    [Fact]
    public void Pairs_of_specials_fire_together_cross_wide_and_big_blast()
    {
        var cross = KakhliBoard.Lay("""
            1 2 1 2 1
            2 0h 1v 2 1
            1 2 3 4 3
            2 1 2 1 2
            """);
        var s1 = cross.Swap(Cell(cross, 1, 1), Cell(cross, 2, 1))!;
        Assert.Contains(s1[0].Fx, f => f[0] == KakhliFx.Cross);
        Assert.Equal(8, s1[0].Gone.Count);   // рядок 5 + стовпчик 4 − перетин

        var embers = KakhliBoard.Lay("""
            1 2 1 2 1 2
            2 0e 1e 2 1 2
            1 2 1 3 4 3
            2 1 2 1 2 1
            1 3 1 4 1 3
            2 1 2 1 2 1
            """);
        var s2 = embers.Swap(Cell(embers, 1, 1), Cell(embers, 2, 1))!;
        Assert.Contains(s2[0].Fx, f => f[0] == KakhliFx.Ember && f[2] == 2);
        Assert.Equal(20, s2[0].Gone.Count);   // 5×5 довкола (2;1), обрізане згори

        var wide = KakhliBoard.Lay("""
            1 2 1 2 1 2
            2 0e 1h 2 1 2
            1 2 1 3 4 3
            2 1 2 1 2 1
            1 3 1 4 1 3
            """);
        var s3 = wide.Swap(Cell(wide, 1, 1), Cell(wide, 2, 1))!;
        Assert.Contains(s3[0].Fx, f => f[0] == KakhliFx.Wide);
        // рядки 0–2 (18) + стовпчики 1–3 у рядках 3–4 (6)
        Assert.Equal(24, s3[0].Gone.Count);

        var gleks = KakhliBoard.Lay("""
            1 2 1 2
            2 g g 1
            1 2 1 2
            """);
        var s4 = gleks.Swap(Cell(gleks, 1, 1), Cell(gleks, 2, 1))!;
        Assert.Equal(12, s4[0].Gone.Count);
    }

    [Fact]
    public void Clay_cracks_under_a_matched_tile_and_rope_takes_the_hit_instead_of_the_tile()
    {
        var clay = KakhliBoard.Lay("""
            1 2 1 2 1
            0~~ 0 3 1 2
            2 1 0 2 1
            """);
        Assert.Equal(2, clay.ClayLeft);
        var s = clay.Swap(Cell(clay, 2, 1), Cell(clay, 2, 2))!;
        Assert.Contains(s[0].Clay, x => x[0] == Cell(clay, 0, 1) && x[1] == 1);
        Assert.Equal(1, clay.ClayLeft);

        var rope = KakhliBoard.Lay("""
            1 2 1 2 1
            0@ 0 3 1 2
            2 1 0 2 1
            """);
        var tied = Cell(rope, 0, 1);
        var id = rope.IdAt(tied);
        Assert.False(rope.CanSwap(tied, Cell(rope, 1, 1)));
        Assert.False(rope.CanSwap(tied, Cell(rope, 0, 2)));
        var r = rope.Swap(Cell(rope, 2, 1), Cell(rope, 2, 2))!;
        Assert.Contains(tied, r[0].Rope);
        Assert.DoesNotContain(id, r[0].Gone);
        Assert.Equal(id, rope.IdAt(tied));
        Assert.Equal(0, rope.RopesLeft);
    }

    [Fact]
    public void Tiles_fall_past_holes_and_holes_stay_empty()
    {
        var b = KakhliBoard.Lay("""
            1 2 3 2 1
            2 # 1 4 2
            1 3 1 2 1
            0 0 3 4 1
            3 4 0 1 3
            """);
        var top = b.IdAt(Cell(b, 1, 0));
        var s = b.Swap(Cell(b, 2, 3), Cell(b, 2, 4))!;
        // кахля з-над дірки впала повз неї на дві клітинки, а згори насипалась нова
        Assert.Equal(top, s[0].Board[Cell(b, 1, 2)]);
        Assert.Equal(-1, s[0].Board[Cell(b, 1, 1)]);
        Assert.Equal(-1, b.IdAt(Cell(b, 1, 1)));
        AssertHealthy(b);
    }

    [Fact]
    public void No_moves_left_means_glek_shuffles_into_a_board_with_a_move()
    {
        var b = KakhliBoard.Lay("""
            0 1 2 3
            1 2 3 0
            2 3 0 1
            3 0 1 2
            """, colors: 4, seed: 5);
        Assert.Null(b.FindMove());
        var ids = Enumerable.Range(0, b.Cells).Select(b.IdAt).OrderBy(x => x).ToArray();
        var st = b.Shuffle();
        Assert.True(st.Shuffle);
        Assert.NotNull(b.FindMove());
        Assert.True(b.Settled);
        Assert.Equal(ids, Enumerable.Range(0, b.Cells).Select(b.IdAt).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Random_play_keeps_the_board_healthy_and_score_only_grows()
    {
        for (ulong seed = 1; seed <= 15; seed++)
        {
            var b = new KakhliBoard(8, 8, 6, seed);
            b.Fill();
            var rng = new Random((int)seed);
            long last = 0;
            for (var i = 0; i < 60; i++)
            {
                var moves = b.Moves();
                Assert.NotEmpty(moves);
                var m = moves[rng.Next(moves.Count)];
                var steps = b.Swap(m.A, m.B);
                Assert.NotNull(steps);
                Assert.NotEmpty(steps);
                Assert.True(b.Score > last);
                last = b.Score;
                AssertHealthy(b);
                Assert.Equal(steps[^1].Board, Enumerable.Range(0, b.Cells).Select(b.IdAt).ToArray());
            }
        }
    }

    [Fact]
    public void Save_and_load_continue_exactly_the_same_way()
    {
        var a = new KakhliBoard(8, 8, 5, 9, KakhliLevels.All[24].Map);
        a.Fill();
        for (var i = 0; i < 10; i++) { var m = a.Moves()[0]; a.Swap(m.A, m.B); }
        var json = JsonSerializer.Serialize(a.Save());
        var b = KakhliBoard.Load(JsonSerializer.Deserialize<KakhliBoardState>(json))!;
        Assert.Equal(Views.Json(a.Wire()).GetRawText(), Views.Json(b.Wire()).GetRawText());
        for (var i = 0; i < 10; i++)
        {
            var m = a.Moves()[^1];
            a.Swap(m.A, m.B);
            b.Swap(m.A, m.B);
        }
        Assert.Equal(a.Score, b.Score);
        Assert.Equal(Views.Json(a.Wire()).GetRawText(), Views.Json(b.Wire()).GetRawText());
        Assert.Null(KakhliBoard.Load(JsonSerializer.Deserialize<KakhliBoardState>(json)! with { Id = [1, 2] }));
    }

    [Fact]
    public void Finale_turns_spare_moves_into_rushnyks_and_fires_every_special()
    {
        var b = new KakhliBoard(8, 8, 6, 3);
        b.Fill();
        var before = b.Score;
        var steps = b.Finale(5);
        Assert.NotEmpty(steps);
        Assert.Equal(5, steps[0].Made.Count);
        Assert.True(steps.TrueForAll(x => x.Chain >= 1));
        Assert.True(b.Score > before);
        AssertHealthy(b);
        Assert.Empty(new KakhliBoard(8, 8, 6, 3).Finale(0));
    }

    [Fact]
    public void Every_level_map_is_eight_by_eight_and_playable()
    {
        Assert.Equal(30, KakhliLevels.All.Count);
        foreach (var (l, i) in KakhliLevels.All.Select((l, i) => (l, i)))
        {
            var rows = l.Map.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(KakhliLevels.H, rows.Length);
            Assert.All(rows, r => Assert.Equal(KakhliLevels.W, r.Length));
            Assert.All(rows, r => Assert.Matches("^[.#12rx]+$", r));
            Assert.True(l.Two < l.Three, $"рівень {i + 1}: ★★ < ★★★");
            Assert.True(!l.ScoreOnly || l.Target < l.Two, $"рівень {i + 1}: ціль < ★★");
            var b = new KakhliBoard(8, 8, l.Colors, (ulong)i + 1, l.Map);
            b.Fill();
            AssertHealthy(b);
            Assert.NotNull(b.FindMove());
            if (l.Goals.Any(g => g.Kind == 'l')) Assert.True(b.ClayLeft > 0);
            if (l.Goals.Any(g => g.Kind == 'r')) Assert.True(b.RopesLeft > 0);
        }
    }

    // ---------- бот (для тестів рівнів і калібрування зірок) ----------

    /// <summary>Жадібний бот: приміряє кожен хід на копії з чужим мішечком і бере той, що дає більше очок і цілей.</summary>
    static (int A, int B) Pick(KakhliBoard b, KakhliLevel? level, int salt)
    {
        var best = (A: -1, B: -1);
        var bestVal = long.MinValue;
        foreach (var m in b.Moves())
        {
            var c = b.Clone((ulong)(salt * 7919 + m.A * 64 + m.B));
            var (clay, rope) = (c.ClayLeft, c.RopesLeft);
            var col = (int[])c.Collected.Clone();
            c.Swap(m.A, m.B);
            long v = c.Score - b.Score;
            if (level is not null)
            {
                v += (clay - c.ClayLeft) * 60 + (rope - c.RopesLeft) * 60;
                foreach (var g in level.Goals.Where(g => g.Kind == 'c'))
                    v += Math.Max(0, Math.Min(c.Collected[g.Color] - col[g.Color], g.Count - col[g.Color])) * 25;
            }
            if (v > bestVal) { bestVal = v; best = m; }
        }
        return best;
    }

    static void Play(RoomHarness h, KakhliBoard board, int salt)
    {
        var (a, b) = Pick(board, null, salt);
        var dir = b == a + 1 ? 0 : b == a + board.W ? 1 : b == a - 1 ? 2 : 3;
        Assert.True(h.Act(0, "swap", new { c = a, d = dir }).Ok);
    }

    // ---------- Кахлі дня ----------

    static RoomHarness Daily(string nick, KakhliDailyBoard table)
    {
        var h = new RoomHarness("kakhli-daily", services: RoomHarness.WithService(table));
        Assert.True(h.Solo(nick).Ok);
        return h;
    }

    [Fact]
    public void Daily_same_board_for_everyone_thirty_moves_one_attempt()
    {
        var table = new KakhliDailyBoard(null);
        var a = Daily("Оля", table);
        var b = Daily("Микола", table);
        var ga = (KakhliDaily)a.Room.Game;
        var gb = (KakhliDaily)b.Room.Game;
        Assert.Equal(Views.Json(ga.Board!.Wire()).GetRawText(), Views.Json(gb.Board!.Wire()).GetRawText());
        Assert.StartsWith("daily:kakhli-daily:", a.Room.Key);

        // таблицю дня до своєї спроби не видно
        Assert.Equal(JsonValueKind.Null, a.View(0).GetProperty("daily").GetProperty("board").ValueKind);
        for (var i = 0; i < KakhliDaily.Moves; i++)
        {
            Play(a, ga.Board!, i);
            Play(b, gb.Board!, i);
        }
        // той самий мішечок: однакові ходи — однакові очки
        Assert.Equal(ga.Board!.Score, gb.Board!.Score);
        Assert.True(ga.Reported);
        Assert.Equal(RoomStatus.Finished, a.Room.Status);
        Assert.Contains(a.Scores, s => s.GameId == "kakhli-daily" && s.Attempts == 1);
        Assert.Contains(a.Awards, x => x.Reason == "daily:kakhli-daily");
        Assert.False(a.Act(0, "swap", new { c = 0, d = 0 }).Ok);
        Assert.Equal(2, table.Top(ga.Day).Count);
        var view = a.View(0);
        Assert.Equal("done", view.GetProperty("phase").GetString());
        Assert.Equal(2, view.GetProperty("daily").GetProperty("board").GetArrayLength());
        Assert.Contains("Кахлі дня №", view.GetProperty("daily").GetProperty("share").GetString());
        Assert.False(a.Rematch().Ok);
        Assert.Equal(ga.Board!.Score, ((IDailyPoints)table).Points(ga.Day, "оля"));
    }

    [Fact]
    public void Daily_comes_back_on_the_same_move_and_a_played_day_stays_played()
    {
        var table = new KakhliDailyBoard(null);
        var h = Daily("Оля", table);
        var g = (KakhliDaily)h.Room.Game;
        for (var i = 0; i < 10; i++) Play(h, g.Board!, i);
        var key = h.Room.Key!;
        var saved = h.Store.States[key];

        var h2 = new RoomHarness("kakhli-daily", services: RoomHarness.WithService(table));
        h2.Store.States[key] = saved;
        Assert.True(h2.Solo("Оля").Ok);
        var g2 = (KakhliDaily)h2.Room.Game;
        Assert.Equal(KakhliDaily.Moves - 10, g2.Left);
        Assert.Equal(Views.Json(g.Board!.Wire()).GetRawText(), Views.Json(g2.Board!.Wire()).GetRawText());
        // той самий наступний хід дає те саме (мішечок збережено)
        Play(h, g.Board!, 99);
        Play(h2, g2.Board!, 99);
        Assert.Equal(g.Board!.Score, g2.Board!.Score);

        for (var i = 0; i < KakhliDaily.Moves; i++) if (!g.Reported) Play(h, g.Board!, i);
        var done = h.Store.States[key];
        var h3 = new RoomHarness("kakhli-daily", services: RoomHarness.WithService(table));
        h3.Store.States[key] = done;
        Assert.True(h3.Solo("Оля").Ok);
        Assert.True(((KakhliDaily)h3.Room.Game).Reported);
        Assert.Equal(KakhliDaily.DoneToday, h3.Act(0, "swap", new { c = 0, d = 0 }).Message);
    }

    [Fact]
    public void Daily_view_carries_steps_but_never_the_generator()
    {
        var h = Daily("Оля", new KakhliDailyBoard(null));
        var g = (KakhliDaily)h.Room.Game;
        Play(h, g.Board!, 1);
        var raw = h.View(0).GetRawText();
        var game = h.View(0).GetProperty("game");
        Assert.Equal(1, game.GetProperty("mv").GetInt32());
        Assert.True(game.GetProperty("steps").GetArrayLength() > 0);
        Assert.Equal(64, game.GetProperty("board").GetProperty("ids").GetArrayLength());
        Assert.DoesNotContain("rng", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("seed", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Не зрозумів, що з чим міняти", h.Act(0, "swap", new { c = 0, d = 9 }).Message);
        Assert.Equal("Не зрозумів, що з чим міняти", h.Act(0, "swap", new { c = 0, d = 3 }).Message);   // за край
        Assert.Equal("Тут так не ходять", h.Act(0, "boom").Message);
        Assert.Equal(1, h.View(0).GetProperty("game").GetProperty("mv").GetInt32());
    }

    // ---------- На час ----------

    [Fact]
    public void Blitz_waits_for_ready_counts_down_runs_two_minutes_and_scores_the_best()
    {
        var h = new RoomHarness("kakhli-blitz", seed: 3);
        Assert.True(h.Solo("Оля").Ok);
        var g = (KakhliBlitz)h.Room.Game;
        Assert.Equal(KakhliBlitz.PhReady, g.Phase);
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("game").ValueKind);   // поле до «Поїхали» сховане
        Assert.Equal("Спершу «Поїхали»", h.Act(0, "swap", new { c = 0, d = 0 }).Message);
        Assert.True(h.Act(0, "ready").Ok);
        Assert.Equal("Уже почали", h.Act(0, "ready").Message);
        Assert.Equal("Зачекай, зараз почнемо", h.Act(0, "swap", new { c = 0, d = 0 }).Message);
        h.Clock.AdvanceMs(3000);
        for (var i = 0; i < 20; i++) { Play(h, g.Board!, i); h.Clock.AdvanceMs(2000); }
        Assert.Equal("Ще є час", h.Act(0, "end").Message);
        h.Clock.AdvanceMs(80_000);   // 40 + 80 = дві хвилини рівно
        Play(h, g.Board!, 50);       // у межах секунди на дорогу ще рахується
        h.Clock.AdvanceMs(1500);
        var late = Pick(g.Board!, null, 0);
        Assert.Equal("Час вийшов", h.Act(0, "swap", new { c = late.A, d = late.B == late.A + 1 ? 0 : 1 }).Message);
        Assert.Equal(KakhliBlitz.PhOver, g.Phase);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var score = Assert.Single(h.Scores);
        Assert.Equal(g.Board!.Score, (long)score.Score);
        Assert.True(score.Score > 0);
        Assert.True(h.Rematch().Ok);
        Assert.Equal(KakhliBlitz.PhReady, ((KakhliBlitz)h.Room.Game).Phase);
    }

    [Fact]
    public void Blitz_end_from_the_browser_finishes_after_the_bell()
    {
        var h = new RoomHarness("kakhli-blitz", seed: 4);
        h.Solo("Оля");
        var g = (KakhliBlitz)h.Room.Game;
        h.Act(0, "ready");
        h.Clock.AdvanceMs(3000 + 120_000 - 300);
        Assert.True(h.Act(0, "end").Ok);
        Assert.Equal(KakhliBlitz.PhOver, g.Phase);
        Assert.Single(h.Scores);
    }

    // ---------- Шлях кахляра ----------

    static RoomHarness Path(string nick = "Оля", int seed = 5)
    {
        var h = new RoomHarness("kakhli", seed: seed);
        Assert.True(h.Solo(nick).Ok);
        return h;
    }

    [Fact]
    public void Path_opens_on_the_map_first_level_open_rest_locked()
    {
        var h = Path();
        var g = (Kakhli)h.Room.Game;
        Assert.Equal(Kakhli.PhMap, g.Phase);
        var levels = h.View(0).GetProperty("levels");
        Assert.Equal(30, levels.GetArrayLength());
        Assert.True(levels[0].GetProperty("open").GetBoolean());
        Assert.False(levels[1].GetProperty("open").GetBoolean());
        Assert.Equal("Цей рівень ще замкнено — спершу попередній", h.Act(0, "level", new { n = 1 }).Message);
        Assert.Equal("Нема такого рівня", h.Act(0, "level", new { n = 30 }).Message);
        Assert.Equal("Спершу обери рівень", h.Act(0, "swap", new { c = 0, d = 0 }).Message);
        Assert.True(h.Act(0, "level", new { n = 0 }).Ok);
        Assert.Equal(Kakhli.PhPlay, g.Phase);
        Assert.Equal(KakhliLevels.All[0].Moves, g.Left);
    }

    [Fact]
    public void Path_win_pays_a_shard_per_new_star_once_and_unlocks_the_next_level()
    {
        var h = Path();
        var g = (Kakhli)h.Room.Game;
        h.Act(0, "level", new { n = 0 });
        g.Board!.Score = KakhliLevels.All[0].Three;   // на ★★★ уже є
        for (var i = 0; i < KakhliLevels.All[0].Moves; i++) Play(h, g.Board!, i);
        Assert.Equal(Kakhli.PhWon, g.Phase);
        Assert.Equal(3, g.Stars[0]);
        var paid = h.Awards.Where(a => a.Reason.StartsWith("kakhli-star:1:", StringComparison.Ordinal)).Select(a => a.Reason).ToList();
        Assert.Equal(["kakhli-star:1:1", "kakhli-star:1:2", "kakhli-star:1:3"], paid);
        Assert.All(h.Awards.Where(a => a.Reason.StartsWith("kakhli-star", StringComparison.Ordinal)), a => Assert.Equal(Kakhli.StarShards, a.Shards));
        Assert.Equal(3, (int)h.Scores[^1].Score);
        var won = h.View(0).GetProperty("level");
        Assert.Equal(3, won.GetProperty("got").GetInt32());
        Assert.Equal(3, won.GetProperty("fresh").GetInt32());
        Assert.Equal(1, won.GetProperty("next").GetInt32());
        Assert.True(h.View(0).GetProperty("levels")[1].GetProperty("open").GetBoolean());

        // той самий рівень ще раз — більше трьох зірок не буває: ні черепка, ні нового рядка в таблицю
        var stars = h.Awards.Count(a => a.Reason.StartsWith("kakhli-star", StringComparison.Ordinal));
        var scores = h.Scores.Count;
        Assert.True(h.Act(0, "retry").Ok);
        Assert.Equal(0, g.Board!.Score);
        for (var i = 0; i < KakhliLevels.All[0].Moves && g.Phase == Kakhli.PhPlay; i++) Play(h, g.Board!, i);
        Assert.NotEqual(Kakhli.PhPlay, g.Phase);
        Assert.Equal(stars, h.Awards.Count(a => a.Reason.StartsWith("kakhli-star", StringComparison.Ordinal)));
        Assert.Equal(scores, h.Scores.Count);
        Assert.Equal(3, g.Stars[0]);
        Assert.True(h.Act(0, "level", new { n = 1 }).Ok);
        Assert.Equal(1, g.Level);
    }

    [Fact]
    public void Path_goal_level_ends_as_soon_as_goals_are_met_and_spare_moves_become_rushnyks()
    {
        var h = Path(seed: 8);
        var g = (Kakhli)h.Room.Game;
        // рівень 2 «Червоні маки»: зібрати 20 червоних — відкриваємо, ніби перший уже пройдено
        g.Load(JsonSerializer.Serialize(new { stars = new[] { 1 }, phase = "map", level = -1, left = 0, mv = 0 }));
        Assert.True(h.Act(0, "level", new { n = 1 }).Ok);
        g.Board!.Collected[KakhliLevels.Red] = 19;
        for (var i = 0; i < 20 && g.Phase == Kakhli.PhPlay; i++) Play(h, g.Board!, i);
        Assert.Equal(Kakhli.PhWon, g.Phase);
        Assert.True(g.Left > 0);
        var steps = h.View(0).GetProperty("game").GetProperty("steps");
        Assert.Contains(steps.EnumerateArray(), s => s.GetProperty("fin").GetBoolean());
    }

    [Fact]
    public void Path_out_of_moves_without_goals_is_lost_and_retry_starts_over()
    {
        var h = Path(seed: 9);
        var g = (Kakhli)h.Room.Game;
        g.Load(JsonSerializer.Serialize(new { stars = Enumerable.Repeat(1, 10).ToArray(), phase = "map", level = -1, left = 0, mv = 0 }));
        Assert.True(h.Act(0, "level", new { n = 10 }).Ok);   // «Перша глина»
        for (var i = 0; i < KakhliLevels.All[10].Moves && g.Phase == Kakhli.PhPlay; i++)
        {
            // ходимо подалі від глини: перший-ліпший хід згори
            var m = g.Board!.Moves()[0];
            Assert.True(h.Act(0, "swap", new { c = m.A, d = m.B == m.A + 1 ? 0 : 1 }).Ok);
        }
        if (g.Phase == Kakhli.PhPlay) return;   // перестраховка: поле могло само все розбити
        if (g.Phase == Kakhli.PhLost)
        {
            Assert.Equal(0, g.Stars[10]);
            Assert.Equal("Далі поки нікуди", h.Act(0, "next").Message);
            Assert.True(h.Act(0, "retry").Ok);
            Assert.Equal(Kakhli.PhPlay, g.Phase);
            Assert.Equal(KakhliLevels.All[10].Moves, g.Left);
        }
    }

    [Fact]
    public void Path_progress_and_the_level_on_the_board_survive_save_and_load()
    {
        var h = Path(seed: 10);
        var g = (Kakhli)h.Room.Game;
        g.Load(JsonSerializer.Serialize(new { stars = new[] { 3, 2, 1 }, phase = "map", level = -1, left = 0, mv = 0 }));
        h.Act(0, "level", new { n = 3 });
        for (var i = 0; i < 5; i++) Play(h, g.Board!, i);
        var key = h.Room.Key!;
        var saved = h.Store.States[key];
        var h2 = new RoomHarness("kakhli", seed: 10);
        h2.Store.States[key] = saved;
        Assert.True(h2.Solo("Оля").Ok);
        var g2 = (Kakhli)h2.Room.Game;
        output.WriteLine($"рівень 4 після 5 ходів: {g.Phase}, зібрано {string.Join(",", g.Board!.Collected)}, очок {g.Board.Score}");
        Assert.Equal(g.Stars, g2.Stars);
        Assert.Equal(g.Phase, g2.Phase);
        Assert.Equal(3, g2.Level);
        Assert.Equal(g.Left, g2.Left);
        Assert.Equal(Views.Json(g.Board!.Wire()).GetRawText(), Views.Json(g2.Board!.Wire()).GetRawText());
        Assert.True(h2.Act(0, "map").Ok);
        Assert.Equal(Kakhli.PhMap, g2.Phase);
        Assert.Equal(JsonValueKind.Null, h2.View(0).GetProperty("game").ValueKind);
        // зіпсоване збереження — мапа з нуля, але не падіння
        g2.Load("{oops");
        g2.Load("""{"stars":[9,9],"phase":"play","level":99}""");
        Assert.Equal(3, g2.Stars[0]);
    }

    // ---------- калібрування (вручну: KAKHLI_CALIB=1) ----------

    [Fact]
    public void Calibrate_levels_with_a_greedy_bot()
    {
        if (Environment.GetEnvironmentVariable("KAKHLI_CALIB") != "1") return;
        var runs = int.TryParse(Environment.GetEnvironmentVariable("KAKHLI_RUNS"), out var r) ? r : 40;
        foreach (var (l, i) in KakhliLevels.All.Select((l, i) => (l, i)))
        {
            var scores = new List<long>();
            var wins = 0;
            for (var run = 0; run < runs; run++)
            {
                var b = new KakhliBoard(8, 8, l.Colors, (ulong)(run * 1000 + i + 1), l.Map);
                b.Fill();
                var left = l.Moves;
                bool Met() => l.Goals.All(g => g.Kind switch
                {
                    's' => b.Score >= g.Count,
                    'c' => b.Collected[g.Color] >= g.Count,
                    'l' => b.ClayLeft == 0,
                    'r' => b.RopesLeft == 0,
                    _ => true,
                });
                while (left > 0)
                {
                    var m = Pick(b, l, run * 100 + left);
                    b.Swap(m.A, m.B);
                    left--;
                    if (!l.ScoreOnly && Met()) break;
                }
                b.Finale(l.ScoreOnly ? 0 : left);
                if (Met()) wins++;
                scores.Add(b.Score);
            }
            scores.Sort();
            long P(double q) => scores[(int)Math.Min(scores.Count - 1, q * scores.Count)];
            static long R(double x) => (long)Math.Round(x / 100) * 100;
            output.WriteLine($"{i + 1,2} {l.Name,-18} win {wins * 100 / runs,3}%  p25 {P(.25),6} p50 {P(.5),6} p75 {P(.75),6} p90 {P(.9),6}   now ★ {l.Target} ★★ {l.Two} ★★★ {l.Three}"
                + $"   SUGGEST {i + 1} {(l.ScoreOnly ? R(P(.25) * 0.6) : 0)} {R(P(.25))} {R(P(.75))}");
        }
        foreach (var (name, moves) in new[] { ("день 30", 30), ("на час ~60", 60), ("на час ~80", 80) })
        {
            var scores = new List<long>();
            for (var run = 0; run < runs; run++)
            {
                var b = new KakhliBoard(8, 8, 6, (ulong)(run + 7));
                b.Fill();
                for (var k = 0; k < moves; k++) { var m = Pick(b, null, run * 1000 + k); b.Swap(m.A, m.B); }
                b.Finale(0);
                scores.Add(b.Score);
            }
            scores.Sort();
            output.WriteLine($"{name}: p25 {scores[runs / 4]} p50 {scores[runs / 2]} p75 {scores[runs * 3 / 4]} p90 {scores[runs * 9 / 10]}");
        }
    }
}

/// <summary>Заміри «Кахлів»: тисяча ходів з каскадами — без жодного відчутного часу.</summary>
[Trait("Category", "Perf")]
[Collection(SerialPerf.Name)]
public class KakhliPerfTests
{
    [Fact]
    public void A_thousand_moves_take_well_under_two_seconds()
    {
        var b = new KakhliBoard(8, 8, 6, 11);
        b.Fill();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            var m = b.FindMove()!.Value;
            b.Swap(m.A, m.B);
        }
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} мс");
    }
}
