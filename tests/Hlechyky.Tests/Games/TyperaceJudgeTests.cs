using Hlechyky.Games.Impl;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Суддя Клавоперегонів (spec §2.3): журнал натискань на фініші — чи надруковано по літері й чи людиною. Час він не
/// рахує (офіційний — серверний), лише перевіряє, у фіксованому порядку: bad-log → mismatch → clock → fast → script →
/// metronome → burst.
/// </summary>
public class TyperaceJudgeTests
{
    static TyperaceVerdict Check(int len, TyperaceLogs.Log log, long? serverMs = null) =>
        TyperaceJudge.Check(len, log.K, log.D, serverMs ?? log.Ms + 150);

    [Fact]
    public void Delta_encoding_round_trips_in_four_ms_units_and_clamps_at_16380()
    {
        foreach (var ms in new[] { 0, 1, 2, 3, 4, 6, 100, 255, 1000, 16_376, 16_380 })
        {
            var e = TyperaceJudge.Encode(ms);
            Assert.Equal(2, e.Length);
            Assert.Equal((int)Math.Round(ms / 4.0, MidpointRounding.AwayFromZero), TyperaceJudge.DecodeSteps(e[0], e[1]));
        }
        Assert.Equal("AA", TyperaceJudge.Encode(0));
        Assert.Equal("__", TyperaceJudge.Encode(16_380));
        Assert.Equal("__", TyperaceJudge.Encode(60_000));     // довша пауза — обрізана
        Assert.Equal("AA", TyperaceJudge.Encode(-50));
        Assert.Equal(-1, TyperaceJudge.DecodeSteps('*', 'A'));
        Assert.Equal(-1, TyperaceJudge.DecodeSteps('A', 'ї'));

        // три журнали, які склав кодувальник браузера на тих самих сценаріях, — побайтно те саме, що складає C#
        var scenarios = new[] { TyperaceLogs.ScenarioClean, TyperaceLogs.ScenarioErrors, TyperaceLogs.ScenarioPause };
        for (var i = 0; i < scenarios.Length; i++)
        {
            var log = TyperaceLogs.Encode(scenarios[i]);
            Assert.Equal(TyperaceLogs.FromJs[i].K, log.K);
            Assert.Equal(TyperaceLogs.FromJs[i].D, log.D);
        }
    }

    [Fact]
    public void The_three_browser_logs_replay_to_the_same_counts_in_the_judge()
    {
        var clean = TyperaceJudge.Check(26, TyperaceLogs.FromJs[0].K, TyperaceLogs.FromJs[0].D, TyperaceLogs.Encode(TyperaceLogs.ScenarioClean).Ms + 200);
        Assert.Null(clean.Flag);
        Assert.Equal((26, 0, 0), (clean.Correct, clean.Wrong, clean.Swallowed));
        Assert.Equal(TyperaceLogs.Encode(TyperaceLogs.ScenarioClean).Ms, clean.LogMs);

        var errors = TyperaceJudge.Check(26, TyperaceLogs.FromJs[1].K, TyperaceLogs.FromJs[1].D, TyperaceLogs.Encode(TyperaceLogs.ScenarioErrors).Ms + 200);
        Assert.Equal((27, 2, 1), (errors.Correct, errors.Wrong, errors.Swallowed));

        // пауза на стелі: журнал бачить менше часу, ніж сервер, але нижня межа тоді не діє; одна подія з 26 не від людини — 4 %, ще не скрипт
        var pause = TyperaceJudge.Check(26, TyperaceLogs.FromJs[2].K, TyperaceLogs.FromJs[2].D, 25_000);
        Assert.Null(pause.Flag);
        Assert.Equal(26, pause.Correct);
    }

    [Fact]
    public void A_clean_human_log_passes_with_speed_accuracy_and_zero_wrong()
    {
        var log = TyperaceLogs.Human(300, seed: 4);
        var v = Check(300, log);
        Assert.Null(v.Flag);
        Assert.True(v.Ok);
        Assert.Equal(300, v.Correct);
        Assert.Equal(0, v.Wrong);
        Assert.Equal(0, v.Swallowed);
        Assert.Equal(log.Ms, v.LogMs);
    }

    [Fact]
    public void Errors_and_backspaces_replay_to_the_end_and_count_wrong_and_swallowed()
    {
        var log = TyperaceLogs.Sloppy(200, every: 10);
        var v = Check(200, log);
        Assert.Null(v.Flag);
        Assert.Equal(19, v.Wrong);                // на 10, 20, … 190
        Assert.Equal(9, v.Swallowed);             // на 20, 40, … 180
        Assert.Equal(202, v.Correct);             // 200 + два переписані після Backspace
    }

    [Fact]
    public void A_log_that_stops_short_of_the_text_is_a_mismatch()
    {
        var log = TyperaceLogs.Human(299);
        Assert.Equal(TyperaceJudge.Mismatch, Check(300, log).Flag);
        // і той, що довший за текст
        Assert.Equal(TyperaceJudge.Mismatch, Check(298, log).Flag);
        // закінчився з червоним
        var red = new TyperaceLogs.Builder();
        for (var i = 0; i < 10; i++) red.Add('c', 200);
        red.Add('x', 200);
        Assert.Equal(TyperaceJudge.Mismatch, Check(10, red.Build()).Flag);
    }

    [Fact]
    public void A_char_while_red_is_pending_must_be_swallowed_or_it_is_a_mismatch()
    {
        var b = new TyperaceLogs.Builder().Add('c', 300).Add('x', 200).Add('c', 200).Add('c', 200);
        Assert.Equal(TyperaceJudge.Mismatch, Check(3, b.Build()).Flag);
        var two = new TyperaceLogs.Builder().Add('c', 300).Add('x', 200).Add('x', 200);
        Assert.Equal(TyperaceJudge.Mismatch, Check(1, two.Build()).Flag);
        var ok = new TyperaceLogs.Builder().Add('c', 300).Add('x', 200).Add('s', 200).Add('b', 200).Add('c', 200).Add('c', 200);
        var v = Check(3, ok.Build());
        Assert.Null(v.Flag);
        Assert.Equal((3, 1, 1), (v.Correct, v.Wrong, v.Swallowed));
    }

    [Fact]
    public void Backspace_at_the_start_and_swallowed_without_red_are_mismatches()
    {
        var bs = new TyperaceLogs.Builder().Add('b', 300).Add('c', 200).Add('c', 200);
        Assert.Equal(TyperaceJudge.Mismatch, Check(2, bs.Build()).Flag);
        var sw = new TyperaceLogs.Builder().Add('c', 300).Add('s', 200).Add('c', 200);
        Assert.Equal(TyperaceJudge.Mismatch, Check(2, sw.Build()).Flag);
        // Backspace на правильному знаку й переписати — чесно
        var back = new TyperaceLogs.Builder().Add('c', 300).Add('c', 200).Add('b', 200).Add('c', 200);
        Assert.Null(Check(2, back.Build()).Flag);
    }

    [Fact]
    public void Malformed_logs_are_bad_log()
    {
        var good = TyperaceLogs.Human(20);
        Assert.Equal(TyperaceJudge.BadLog, TyperaceJudge.Check(20, "", "", 5000).Flag);
        Assert.Equal(TyperaceJudge.BadLog, TyperaceJudge.Check(20, null, good.D, 5000).Flag);
        Assert.Equal(TyperaceJudge.BadLog, TyperaceJudge.Check(20, good.K, null, 5000).Flag);
        Assert.Equal(TyperaceJudge.BadLog, TyperaceJudge.Check(20, good.K, good.D[..^1], 5000).Flag);
        Assert.Equal(TyperaceJudge.BadLog, TyperaceJudge.Check(20, good.K, good.D + "AA", 5000).Flag);
        Assert.Equal(TyperaceJudge.BadLog, TyperaceJudge.Check(20, good.K, "*" + good.D[1..], 5000).Flag);
        Assert.Equal(TyperaceJudge.BadLog, TyperaceJudge.Check(20, "q" + good.K[1..], good.D, 5000).Flag);
        Assert.Equal(TyperaceJudge.BadLog, TyperaceJudge.Check(20, "ї" + good.K[1..], good.D, 5000).Flag);
        // 1601 подія — понад стелю; 1600 — ще читається
        var big = TyperaceLogs.Human(1601, meanMs: 100);
        Assert.Equal(TyperaceJudge.BadLog, Check(1601, big).Flag);
        var max = TyperaceLogs.Human(1600, meanMs: 100);
        Assert.NotEqual(TyperaceJudge.BadLog, Check(1600, max).Flag);
    }

    [Fact]
    public void A_log_claiming_more_time_than_the_server_saw_is_a_clock_flag()
    {
        var log = TyperaceLogs.Human(100, seed: 9);
        Assert.Equal(TyperaceJudge.Clock, Check(100, log, log.Ms - 501).Flag);
        Assert.Null(Check(100, log, log.Ms - 500).Flag);
    }

    [Fact]
    public void A_log_up_to_twenty_seconds_shorter_than_server_time_still_passes()
    {
        var log = TyperaceLogs.Human(100, seed: 10);
        // фініш дійшов на 12 с пізніше за останню літеру (зв'язок ліг і повернувся) — не бот, лише гірший час
        Assert.Null(Check(100, log, log.Ms + 12_000).Flag);
        Assert.Null(Check(100, log, log.Ms + 20_000).Flag);
        Assert.Equal(TyperaceJudge.Clock, Check(100, log, log.Ms + 20_001).Flag);
        // але якщо в журналі була пауза на стелі (людина відійшла) — скільки там насправді, журнал не знає
        var b = new TyperaceLogs.Builder();
        var rng = new Random(6);
        for (var i = 0; i < 50; i++) b.Add('c', i == 25 ? 30_000 : 150 + rng.Next(100));
        var paused = b.Build();
        Assert.Null(Check(50, paused, paused.Ms + 40_000).Flag);
    }

    [Fact]
    public void Over_1200_cpm_is_fast_even_with_a_perfect_log()
    {
        // 300 знаків за 14 с = 1286 зн/хв; журнал «ідеальний» — рівно те, що бачив сервер
        var b = new TyperaceLogs.Builder();
        var rng = new Random(1);
        for (var i = 0; i < 300; i++) b.Add('c', 28 + rng.Next(35));
        var log = b.Build();
        Assert.True(log.Ms < 14_500, $"журнал {log.Ms} мс");
        Assert.Equal(TyperaceJudge.Fast, Check(300, log, log.Ms).Flag);
        // 1200 рівно — ще людина: 300 знаків за 15 с
        Assert.NotEqual(TyperaceJudge.Fast, Check(300, log, 15_000).Flag);
        Assert.Equal(TyperaceJudge.Fast, TyperaceJudge.Check(1, "c", "AA", 0).Flag);
    }

    [Fact]
    public void Untrusted_events_above_ten_percent_are_a_script()
    {
        var human = TyperaceLogs.Human(100, seed: 12);
        string Upper(int n) => string.Concat(human.K.Select((ch, i) => i < n ? char.ToUpperInvariant(ch) : ch));
        Assert.Null(TyperaceJudge.Check(100, Upper(10), human.D, human.Ms + 100).Flag);
        Assert.Equal(TyperaceJudge.Script, TyperaceJudge.Check(100, Upper(11), human.D, human.Ms + 100).Flag);
        // великі літери програються так само, як малі
        var v = TyperaceJudge.Check(100, Upper(100), human.D, human.Ms + 100);
        Assert.Equal(100, v.Correct);
    }

    [Fact]
    public void Ten_equal_intervals_in_a_row_are_a_metronome()
    {
        TyperaceLogs.Log With(int run)
        {
            var b = new TyperaceLogs.Builder();
            var rng = new Random(2);
            // решта проміжків — або швидкі, або повільні, але ніколи не 160±4: щоб серія була рівно така, як задумано
            for (var i = 0; i < 60; i++) b.Add('c', i >= 20 && i < 20 + run ? 160 + (i % 2) * 4 : rng.Next(2) == 0 ? 80 + rng.Next(60) : 200 + rng.Next(100));
            return b.Build();
        }
        // run знаків підряд із проміжками 160/164 мс (у межах одного кроку) — це run рівних проміжків
        Assert.Equal(TyperaceJudge.Metronome, Check(60, With(10)).Flag);
        Assert.Null(Check(60, With(9)).Flag);
        Assert.Equal(TyperaceJudge.Metronome, Check(300, TyperaceLogs.Robot(300, 150)).Flag);
    }

    [Fact]
    public void A_rhythm_with_coefficient_of_variation_under_twelve_percent_is_a_metronome()
    {
        // проміжки 150–180 мс по черзі без серій: σ/μ ≈ 7 %
        TyperaceLogs.Log Even(int n)
        {
            var b = new TyperaceLogs.Builder();
            int[] cycle = [150, 170, 160, 180, 155, 175, 165, 150, 180, 160];
            for (var i = 0; i < n; i++) b.Add('c', cycle[(i * 7) % cycle.Length]);
            return b.Build();
        }
        Assert.Equal(TyperaceJudge.Metronome, Check(60, Even(60)).Flag);
        // лише від 40 проміжків: на 40 знаках (39 проміжків) ще не судимо
        Assert.Null(Check(40, Even(40)).Flag);
        // живий розкид — не метроном
        Assert.Null(Check(300, TyperaceLogs.Human(300, seed: 21)).Flag);
    }

    [Fact]
    public void Too_many_intervals_under_twenty_ms_are_a_burst()
    {
        TyperaceLogs.Log With(int quick)
        {
            var b = new TyperaceLogs.Builder();
            var rng = new Random(4);
            // «черга» — 8 і 16 мс навперемінно (2 і 4 кроки): швидко, але не рівно, тож метрономом це не стане
            for (var i = 0; i < 101; i++) b.Add('c', i > 0 && i <= quick ? (i % 2 == 0 ? 8 : 16) : 120 + rng.Next(200));
            return b.Build();
        }
        Assert.Null(Check(101, With(15)).Flag);       // 15 зі 100 — ще людина (перекочування пальців)
        Assert.Equal(TyperaceJudge.Burst, Check(101, With(16)).Flag);
    }

    [Fact]
    public void Held_key_autorepeat_on_a_red_letter_is_not_a_metronome()
    {
        // затиснута клавіша: помилка, а за нею двадцять проковтнутих рівно по 33 мс — людське, ОС так повторює
        var b = new TyperaceLogs.Builder();
        var rng = new Random(8);
        for (var i = 0; i < 80; i++)
        {
            if (i == 40)
            {
                b.Add('x', 150);
                for (var j = 0; j < 20; j++) b.Add('s', 33);
                b.Add('b', 400);
            }
            b.Add('c', 100 + rng.Next(200));
        }
        Assert.Null(Check(80, b.Build()).Flag);
    }

    [Fact]
    public void Flags_are_checked_in_the_declared_order()
    {
        // журнал не сходиться І швидший за людину — першим каже mismatch
        var robot = TyperaceLogs.Robot(100, 20);
        Assert.Equal(TyperaceJudge.Mismatch, Check(101, robot, 2000).Flag);
        // сходиться, але годинник бреше І швидкий — clock
        Assert.Equal(TyperaceJudge.Clock, Check(100, robot, 1000).Flag);
        // годинник у нормі, швидкий і метроном — fast
        Assert.Equal(TyperaceJudge.Fast, Check(100, robot, 2100).Flag);
        // повільний метроном усіма великими — script раніше за metronome
        var slow = TyperaceLogs.Robot(100, 200);
        Assert.Equal(TyperaceJudge.Script, TyperaceJudge.Check(100, slow.K.ToUpperInvariant(), slow.D, slow.Ms + 100).Flag);
        Assert.Equal(TyperaceJudge.Metronome, Check(100, slow).Flag);
        // причини людською мовою
        Assert.Equal("ритм метронома", TyperaceJudge.Reason(TyperaceJudge.Metronome));
        Assert.Equal("", TyperaceJudge.Reason(null));
        foreach (var f in new[] { "bad-log", "mismatch", "clock", "fast", "script", "metronome", "burst", "unseen" })
            Assert.NotEqual("щось не те", TyperaceJudge.Reason(f));
    }

    [Fact]
    public void Accuracy_counts_retyped_correct_chars_and_ignores_swallowed()
    {
        var b = new TyperaceLogs.Builder();
        var rng = new Random(5);
        for (var i = 0; i < 50; i++) b.Add('c', 150 + rng.Next(100));
        b.Add('b', 300).Add('b', 300).Add('c', 200).Add('c', 200);     // стер два, переписав
        b.Add('x', 200);
        for (var i = 0; i < 5; i++) b.Add('s', 100 + rng.Next(80));
        b.Add('b', 300);
        for (var i = 0; i < 50; i++) b.Add('c', 150 + rng.Next(100));
        var v = Check(100, b.Build());
        Assert.Null(v.Flag);
        Assert.Equal(102, v.Correct);
        Assert.Equal(1, v.Wrong);
        Assert.Equal(5, v.Swallowed);
        // так рахує гра: 100 × 102 / 103 = 99 %
        Assert.Equal(99, (int)Math.Round(100.0 * v.Correct / (v.Correct + v.Wrong), MidpointRounding.AwayFromZero));
    }

    // ------------------------------------------------------------------------------------ після рецензій

    /// <summary>Миті ¼, ½, ¾ із журналу + затримка — те, що сервер бачив би з pos у чесного гравця.</summary>
    static long[] SeenFor(TyperaceLogs.Log log, int len, long lagMs = 150)
    {
        var seen = new long[TyperaceJudge.SeenMarks];
        Array.Fill(seen, -1L);
        var q = 0;
        foreach (var (_, ms) in TyperaceLogs.Marks(log, len)) seen[q++] = ms + lagMs;
        return seen;
    }

    [Fact]
    public void A_finish_the_server_never_saw_being_typed_is_unseen()
    {
        var log = TyperaceLogs.Human(300, seed: 8);
        var ms = log.Ms + 150;
        // чесний: pos на ¼, ½, ¾ ішли слідом за натисками
        Assert.Null(TyperaceJudge.Check(300, log.K, log.D, ms, SeenFor(log, 300)).Flag);
        // фініш «з нуля» з консолі: жодного pos за весь заїзд
        var none = new long[] { -1, -1, -1 };
        Assert.Equal(TyperaceJudge.Unseen, TyperaceJudge.Check(300, log.K, log.D, ms, none).Flag);
        // pos є, але всі прийшли наприкінці — журнал каже, що чверть була хвилину тому
        Assert.Equal(TyperaceJudge.Unseen, TyperaceJudge.Check(300, log.K, log.D, ms, new[] { ms - 100, ms - 80, ms - 60 }).Flag);
        // pos «випередили» журнал більш ніж на 1,5 с — теж не те
        var early = SeenFor(log, 300, lagMs: -2_000);
        Assert.Equal(TyperaceJudge.Unseen, TyperaceJudge.Check(300, log.K, log.D, ms, early).Flag);
        // зв'язок кліпнув на 12 с якраз на половині — ще людське
        var lag = SeenFor(log, 300);
        lag[1] += 12_000;
        Assert.Null(TyperaceJudge.Check(300, log.K, log.D, ms, lag).Flag);
        // зв'язок ліг під кінець: ¾ сервер не бачив — рахується миттю фінішу, і це близько
        var tail = SeenFor(log, 300);
        tail[2] = -1;
        Assert.Null(TyperaceJudge.Check(300, log.K, log.D, ms, tail).Flag);
        // …а чверть мусить прийти справжнім pos
        var noQuarter = SeenFor(log, 300);
        noQuarter[0] = -1;
        Assert.Equal(TyperaceJudge.Unseen, TyperaceJudge.Check(300, log.K, log.D, ms, noQuarter).Flag);
        // порожній seen — перевірку пропускаємо (чистий суддя)
        Assert.Null(TyperaceJudge.Check(300, log.K, log.D, ms).Flag);
        Assert.Equal("сервер не бачив самого друку", TyperaceJudge.Reason(TyperaceJudge.Unseen));
    }

    [Fact]
    public void A_backspace_dip_across_a_mark_that_pos_never_saw_still_passes()
    {
        // чверть тексту (25 з 100): дописав, помилився, довго виправляв і ще й стер знак перед нею — ямку назад
        // на 150 мс pos (раз на 200 мс) не побачив, тож сервер пам'ятає перший перетин, а журнал — останній
        var b = new TyperaceLogs.Builder();
        var rng = new Random(3);
        for (var i = 0; i < 25; i++) b.Add('c', 150 + rng.Next(120));
        b.Add('x', 180).Add('s', 300).Add('s', 400).Add('b', 900).Add('b', 700).Add('c', 150);
        for (var i = 0; i < 75; i++) b.Add('c', 150 + rng.Next(120));
        var log = b.Build();
        var marks = TyperaceLogs.Marks(log, 100).ToArray();
        var firstQuarter = marks[0].Ms - 180 - 300 - 400 - 900 - 700 - 150;   // коли чверть перетнули вперше
        var seen = new[] { firstQuarter + 120, marks[1].Ms + 120, marks[2].Ms + 120 };
        Assert.Null(TyperaceJudge.Check(100, log.K, log.D, log.Ms + 150, seen).Flag);
    }

    [Fact]
    public void A_sloppy_long_run_fits_the_journal_once_the_client_caps_swallowed_keys()
    {
        // 760 знаків, 70 помилок (точність 92 %), і на кожну — 12 натисків, поки людина помітила червоне
        var raw = TyperaceLogs.SloppyRaw(760, errors: 70, swallows: 12);
        var all = new TyperaceLogs.Builder();
        foreach (var (kind, ms) in raw) all.Add(kind, ms);
        var uncapped = all.Build();
        Assert.True(uncapped.K.Length > TyperaceJudge.MaxEvents);      // так клієнт писав до виправлення — і людину звали 🤖
        Assert.Equal(TyperaceJudge.BadLog, TyperaceJudge.Check(760, uncapped.K, uncapped.D, uncapped.Ms + 150).Flag);

        var log = TyperaceLogs.AsClientWrites(raw, 760);
        Assert.True(log.K.Length <= TyperaceJudge.MaxEvents);
        Assert.InRange(Math.Abs(uncapped.Ms - log.Ms), 0, 4L * raw.Count);   // час той самий (з точністю до округлення кроків)
        var v = TyperaceJudge.Check(760, log.K, log.D, log.Ms + 150, SeenFor(log, 760));
        Assert.Null(v.Flag);
        Assert.Equal(70, v.Wrong);
        Assert.Equal(760, v.Correct);
        Assert.Equal(70 * TyperaceLogs.ClientSwallowsPerRed, v.Swallowed);

        // ще неохайніше: 200 помилок по 12 — проковтнуті зникають зовсім, коли журнал підходить до стелі
        var worse = TyperaceLogs.AsClientWrites(TyperaceLogs.SloppyRaw(760, errors: 200, swallows: 12), 760);
        Assert.True(worse.K.Length <= TyperaceJudge.MaxEvents);
        Assert.Null(TyperaceJudge.Check(760, worse.K, worse.D, worse.Ms + 150).Flag);
    }

    [Fact]
    public void The_client_journal_cap_is_the_same_in_the_browser_and_in_cs()
    {
        var log = TyperaceLogs.AsClientWrites(TyperaceLogs.ScenarioSwallows(), 40);
        Assert.Equal(TyperaceLogs.FromJsCapped.K, log.K);
        Assert.Equal(TyperaceLogs.FromJsCapped.D, log.D);
        var v = TyperaceJudge.Check(40, log.K, log.D, log.Ms + 150);
        Assert.Null(v.Flag);
        Assert.Equal((4, 4 + 2 + 4 + 4), (v.Wrong, v.Swallowed));   // 6 і 9 проковтнутих стали по 4
    }

    [Fact]
    public void Misses_mark_the_letters_where_red_hung()
    {
        var b = new TyperaceLogs.Builder();
        b.Add('c', 300).Add('x', 200).Add('b', 300).Add('c', 200).Add('c', 200).Add('x', 150).Add('s', 90).Add('b', 300).Add('c', 200);
        var miss = new bool[4];
        TyperaceJudge.Misses(b.Build().K, 4, miss);
        Assert.Equal([false, true, false, true], miss);
    }
}
