using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Ерудит. Половина тестів обходиться без кімнати взагалі: дошка (<see cref="ScrabbleBoard"/>) нічого не
/// знає ні про мішок, ні про черги, тому викладку й підрахунок очок можна перевіряти фішка за фішкою.
/// Друга половина грає через <see cref="RoomHarness"/> — черга, паси, кінець партії, приховані стійки.
///
/// Роздача випадкова, тому там, де потрібні конкретні літери, стан партії підмінюється через
/// <see cref="Rig(RoomHarness, string, string[])"/> (Save/Load гри) — інакше тест довелось би підбирати
/// під сід і він ламався б від будь-якої зміни в мішку.
/// </summary>
public class ScrabbleTests
{
    static readonly string[] Nicks = ["Оля", "Петро", "Іван", "Ганна"];

    // ------------------------------------------------------------------ дрібний інструмент

    static int C(int row, int col) => row * ScrabbleBoard.Size + col;

    static ScrabbleTile[] Across(int row, int col, string text) =>
        [.. text.Select((ch, i) => new ScrabbleTile(C(row, col + i), ch, false))];

    static ScrabbleTile[] Down(int row, int col, string text) =>
        [.. text.Select((ch, i) => new ScrabbleTile(C(row + i, col), ch, false))];

    /// <summary>Дошка, на якій уже щось лежить (кладемо повз перевірку — це вхідні дані тесту, а не хід).</summary>
    static ScrabbleBoard With(params ScrabbleTile[] tiles)
    {
        var board = new ScrabbleBoard();
        board.Apply(tiles);
        return board;
    }

    static Dictionary<string, object> Tile(int cell, char letter, bool blank = false) =>
        new() { ["cell"] = cell, ["letter"] = letter.ToString(), ["blank"] = blank };

    static RoomHarness Table(int players = 2, int seed = 1, IServiceProvider? services = null)
    {
        var h = new RoomHarness("scrabble", seed: seed, services: services);
        for (var i = 0; i < players; i++) h.Join(Nicks[i]);
        h.Start();
        return h;
    }

    static ActResult Play(RoomHarness h, int seat, int cell, string text, bool across = true)
    {
        var step = across ? 1 : ScrabbleBoard.Size;
        var tiles = text.Select((ch, i) => Tile(cell + i * step, ch)).ToArray();
        return h.Act(seat, "play", new { tiles });
    }

    static string Rack(RoomHarness h, int seat) =>
        string.Concat(h.View(seat).GetProperty("rack").EnumerateArray().Select(e => e.GetString()));

    static int Int(RoomHarness h, int? seat, string field) => h.View(seat).GetProperty(field).GetInt32();

    static int[] Ints(JsonElement e) => [.. e.EnumerateArray().Select(x => x.GetInt32())];

    /// <summary>Скільки фішок зараз у партії: мішок + усі стійки + те, що лежить на дошці.</summary>
    static int Tiles(RoomHarness h)
    {
        var view = h.View(null);
        return view.GetProperty("bag").GetInt32()
            + Ints(view.GetProperty("racks")).Sum()
            + view.GetProperty("board").GetString()!.Count(c => c != ScrabbleBoard.Free);
    }

    /// <summary>
    /// Підміна мішка й стійок через Save/Load: так тест дістається до кінцівок і до рідкісних літер,
    /// не граючи сорока ходів наосліп. Решта стану (дошка, очки, черга) лишається як була.
    /// </summary>
    static void Rig(RoomHarness h, string bag, params string[] racks)
    {
        var game = h.Room.Game;
        var node = JsonNode.Parse(game.Save()!)!.AsObject();
        node["Bag"] = bag;
        var list = new JsonArray();
        for (var i = 0; i < 4; i++) list.Add(i < racks.Length ? racks[i] : "");
        node["Racks"] = list;
        game.Load(node.ToJsonString());
    }

    /// <summary>Тимчасовий словник: або лише малі списки, або ще й маленький «великий» на SQLite.</summary>
    sealed class Dict : IDisposable
    {
        readonly string _dir;

        Dict(string dir, Words words)
        {
            _dir = dir;
            Words = words;
        }

        public Words Words { get; }

        public IServiceProvider Services => RoomHarness.WithService(Words);

        public static Dict Small(params string[] known) => Make(full: false, known);

        public static Dict Full(params string[] known) => Make(full: true, known);

        static Dict Make(bool full, string[] known)
        {
            var dir = Path.Combine(Path.GetTempPath(), "hlechyky-scrabble-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            // Малі списки потрібні, щоб Words вважав себе завантаженим; самі слова тут ні на що не впливають.
            File.WriteAllLines(Path.Combine(dir, "uk-5.txt"), ["книга"]);
            File.WriteAllLines(Path.Combine(dir, "uk-hangman.txt"), ["зброя"]);
            if (full) File.WriteAllLines(Path.Combine(dir, "uk-all.txt"), known);
            else File.WriteAllLines(Path.Combine(dir, "uk-guess.txt"), known);
            var words = new Words(dir);
            words.FullReady.GetAwaiter().GetResult();
            return new Dict(dir, words);
        }

        public void Dispose()
        {
            Words.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* хай лежить у temp */ }
        }
    }

    // ================================================================== мішок

    [Fact]
    public void The_bag_holds_a_hundred_and_four_tiles_worth_two_hundred_points()
    {
        Assert.Equal(104, ScrabbleBag.Total);
        Assert.Equal(104, ScrabbleBag.Table.Sum(t => t.Count));
        Assert.Equal(200, ScrabbleBag.Table.Sum(t => t.Count * t.Value));
        Assert.Equal(104, ScrabbleBag.Fresh(new Random(1)).Count);
    }

    [Fact]
    public void The_bag_covers_the_whole_alphabet_and_two_blanks()
    {
        const string alphabet = "абвгґдеєжзиіїйклмнопрстуфхцчшщьюя";
        var letters = ScrabbleBag.Table.Select(t => t.Letter).ToArray();
        Assert.Equal(letters.Length, letters.Distinct().Count());
        foreach (var ch in alphabet) Assert.Contains(ch, letters);
        Assert.Equal(2, ScrabbleBag.Table.Single(t => t.Letter == ScrabbleBag.Blank).Count);
        // апострофа й дефіса в грі нема — їх нема й у мішку
        Assert.DoesNotContain('\'', letters);
        Assert.DoesNotContain('-', letters);
    }

    [Fact]
    public void A_fresh_bag_has_exactly_the_tiles_from_the_table()
    {
        var bag = ScrabbleBag.Fresh(new Random(7));
        foreach (var (letter, count, _) in ScrabbleBag.Table)
            Assert.Equal(count, bag.Count(c => c == letter));
    }

    [Fact]
    public void The_same_seed_shuffles_the_bag_the_same_way()
    {
        Assert.Equal(ScrabbleBag.Fresh(new Random(42)), ScrabbleBag.Fresh(new Random(42)));
        Assert.NotEqual(ScrabbleBag.Fresh(new Random(42)), ScrabbleBag.Fresh(new Random(43)));
    }

    [Fact]
    public void A_blank_is_worth_nothing_and_a_rare_letter_a_lot()
    {
        Assert.Equal(0, ScrabbleBag.Value(ScrabbleBag.Blank));
        Assert.Equal(0, ScrabbleBag.Value('І'));      // ВЕЛИКА на дошці — це порожня фішка
        Assert.Equal(1, ScrabbleBag.Value('о'));
        Assert.Equal(2, ScrabbleBag.Value('к'));
        Assert.Equal(8, ScrabbleBag.Value('щ'));
        Assert.Equal(10, ScrabbleBag.Value('ґ'));
    }

    // ================================================================== розкладка бонусів

    [Fact]
    public void The_bonus_layout_is_the_classic_one()
    {
        var b = ScrabbleBoard.Bonuses;
        Assert.Equal(ScrabbleBoard.Cells, b.Length);
        Assert.Equal(8, b.Count(c => c == 'T'));
        Assert.Equal(16, b.Count(c => c == 'D'));
        Assert.Equal(12, b.Count(c => c == 't'));
        Assert.Equal(24, b.Count(c => c == 'd'));
        Assert.Equal('*', b[ScrabbleBoard.Centre]);
        Assert.Equal('T', b[C(0, 0)]);
        Assert.Equal('T', b[C(0, 7)]);
        Assert.Equal('T', b[C(14, 14)]);
    }

    [Fact]
    public void The_bonus_layout_is_symmetric()
    {
        var b = ScrabbleBoard.Bonuses;
        for (var i = 0; i < ScrabbleBoard.Cells; i++)
            Assert.Equal(b[i], b[ScrabbleBoard.Cells - 1 - i]);
    }

    // ================================================================== викладка: що не можна

    [Fact]
    public void Nothing_placed_is_refused()
    {
        var (play, error) = new ScrabbleBoard().Check([]);
        Assert.Null(play);
        Assert.Equal("Поклади хоч одну фішку", error);
    }

    [Fact]
    public void More_than_seven_tiles_are_refused()
    {
        var (_, error) = new ScrabbleBoard().Check(Across(7, 4, "оаиентоа"));
        Assert.Equal("За хід кладуть щонайбільше сім фішок", error);
    }

    [Fact]
    public void A_cell_outside_the_board_is_refused()
    {
        Assert.Equal("Такої клітинки на дошці нема", new ScrabbleBoard().Check([new ScrabbleTile(225, 'к', false)]).Error);
        Assert.Equal("Такої клітинки на дошці нема", new ScrabbleBoard().Check([new ScrabbleTile(-1, 'к', false)]).Error);
    }

    [Fact]
    public void Two_tiles_on_one_cell_are_refused()
    {
        var (_, error) = new ScrabbleBoard().Check([new ScrabbleTile(112, 'к', false), new ScrabbleTile(112, 'і', false)]);
        Assert.Equal("Дві фішки на одну клітинку не кладуть", error);
    }

    [Fact]
    public void A_busy_cell_is_refused()
    {
        var board = With(Across(7, 6, "оса"));
        Assert.Equal("Ця клітинка вже зайнята", board.Check([new ScrabbleTile(C(7, 7), 'к', false)]).Error);
    }

    [Fact]
    public void Tiles_must_lie_in_one_line()
    {
        var (_, error) = new ScrabbleBoard().Check([new ScrabbleTile(C(7, 7), 'к', false), new ScrabbleTile(C(8, 8), 'і', false)]);
        Assert.Equal("Фішки мають лягти в один рядок або стовпець", error);
    }

    [Fact]
    public void A_hole_in_the_line_is_refused()
    {
        var (_, error) = new ScrabbleBoard().Check([new ScrabbleTile(C(7, 6), 'к', false), new ScrabbleTile(C(7, 8), 'т', false)]);
        Assert.Equal("У слові дірка", error);
    }

    [Fact]
    public void The_first_word_must_cross_the_centre()
    {
        Assert.Equal("Перше слово кладуть через центр", new ScrabbleBoard().Check(Across(7, 8, "кіт")).Error);
        Assert.Null(new ScrabbleBoard().Check(Across(7, 6, "кіт")).Error);
    }

    [Fact]
    public void A_word_of_one_letter_is_refused()
    {
        var (_, error) = new ScrabbleBoard().Check([new ScrabbleTile(ScrabbleBoard.Centre, 'к', false)]);
        Assert.Equal("Слово має бути щонайменше з двох літер", error);
    }

    [Fact]
    public void A_word_that_touches_nothing_is_refused()
    {
        var board = With(Across(7, 6, "оса"));
        Assert.Equal("Слово має торкатись того, що вже на дошці", board.Check(Across(0, 0, "кіт")).Error);
    }

    // ================================================================== підрахунок очок

    [Fact]
    public void The_star_doubles_the_first_word()
    {
        var (play, error) = new ScrabbleBoard().Check(Across(7, 6, "кіт"));
        Assert.Null(error);
        var word = Assert.Single(play!.Words);
        Assert.Equal("кіт", word.Text);
        Assert.Equal(8, word.Score);       // (2 + 1 + 1) × 2
        Assert.Equal(8, play.Total);
        Assert.Equal([C(7, 6), C(7, 7), C(7, 8)], play.Cells);
    }

    [Fact]
    public void A_triple_word_multiplies_the_whole_word()
    {
        var board = With(new ScrabbleTile(C(7, 1), 'а', false));
        var (play, error) = board.Check([new ScrabbleTile(C(7, 0), 'т', false)]);
        Assert.Null(error);
        Assert.Equal("та", Assert.Single(play!.Words).Text);
        Assert.Equal(6, play.Total);       // (1 + 1) × 3
    }

    [Fact]
    public void A_triple_letter_multiplies_only_its_own_letter()
    {
        var board = With(new ScrabbleTile(C(5, 6), 'а', false));
        var (play, error) = board.Check([new ScrabbleTile(C(5, 5), 'к', false)]);
        Assert.Null(error);
        Assert.Equal(7, play!.Total);      // 2×3 + 1, слово не множиться
    }

    [Fact]
    public void Letter_bonuses_are_counted_before_the_word_bonus()
    {
        // «кіоба» лягає на потрійне слово (7,0) і подвійну літеру (7,3): 2 + 1 + 1 + 3×2 + 1 = 11, ×3 = 33
        var board = With(new ScrabbleTile(C(7, 4), 'а', false));
        var (play, error) = board.Check(Across(7, 0, "кіоб"));
        Assert.Null(error);
        Assert.Equal("кіоба", Assert.Single(play!.Words).Text);
        Assert.Equal(33, play.Total);
    }

    [Fact]
    public void Perpendicular_words_are_scored_too()
    {
        var board = With(Across(7, 6, "оса"));
        var (play, error) = board.Check(Across(6, 6, "та"));
        Assert.Null(error);
        Assert.Equal(["та", "то", "ас"], play!.Words.Select(w => w.Text).ToArray());
        Assert.Equal([3, 3, 2], play.Words.Select(w => w.Score).ToArray());
        Assert.Equal(8, play.Total);
    }

    [Fact]
    public void A_bonus_under_an_old_tile_does_not_pay_twice()
    {
        // «с» уже стоїть на зірці — слово, що йде крізь неї, вже не подвоюється
        var board = With(Across(7, 6, "оса"));
        var (play, error) = board.Check([new ScrabbleTile(C(6, 7), 'а', false), new ScrabbleTile(C(8, 7), 'а', false)]);
        Assert.Null(error);
        Assert.Equal("аса", Assert.Single(play!.Words).Text);
        Assert.Equal(3, play.Total);
    }

    [Fact]
    public void A_word_multiplier_doubles_the_perpendicular_word_too()
    {
        // нова «с» лягає на ×2 слова (4,4) — подвоюється і головне слово, і те, що склалось упоперек
        var board = With(new ScrabbleTile(C(4, 5), 'а', false), new ScrabbleTile(C(5, 4), 'т', false));
        var (play, error) = board.Check([new ScrabbleTile(C(4, 3), 'о', false), new ScrabbleTile(C(4, 4), 'с', false)]);
        Assert.Null(error);
        Assert.Equal(["оса", "ст"], play!.Words.Select(w => w.Text).ToArray());
        Assert.Equal([6, 4], play.Words.Select(w => w.Score).ToArray());   // (1+1+1)×2 і (1+1)×2
        Assert.Equal(10, play.Total);
    }

    [Fact]
    public void Seven_tiles_at_once_add_fifty()
    {
        var (play, error) = new ScrabbleBoard().Check(Across(7, 4, "оаиеноа"));
        Assert.Null(error);
        Assert.Equal(14, Assert.Single(play!.Words).Score);
        Assert.Equal(64, play.Total);
    }

    [Fact]
    public void A_blank_holds_a_letter_but_scores_nothing()
    {
        var board = new ScrabbleBoard();
        ScrabbleTile[] tiles =
        [
            new(C(7, 6), 'к', false),
            new(C(7, 7), 'і', true),
            new(C(7, 8), 'т', false),
        ];
        var (play, error) = board.Check(tiles);
        Assert.Null(error);
        Assert.Equal("кіт", Assert.Single(play!.Words).Text);
        Assert.Equal(6, play.Total);       // (2 + 0 + 1) × 2

        board.Apply(tiles);
        Assert.Equal('І', board.Text[ScrabbleBoard.Centre]);   // порожня фішка на дошці — ВЕЛИКА літера
        Assert.Equal('к', board.Text[C(7, 6)]);
    }

    [Fact]
    public void A_single_tile_can_finish_two_words_at_once()
    {
        // «о» стоїть вертикально, «а» — горизонтально; одна фішка замикає обидва слова
        var board = With(new ScrabbleTile(C(6, 7), 'т', false), new ScrabbleTile(C(7, 6), 'а', false));
        var (play, error) = board.Check([new ScrabbleTile(C(7, 7), 'о', false)]);
        Assert.Null(error);
        Assert.Equal(["ао", "то"], play!.Words.Select(w => w.Text).ToArray());
    }

    [Fact]
    public void The_board_reads_as_two_hundred_twenty_five_characters()
    {
        var board = new ScrabbleBoard();
        Assert.True(board.IsEmpty);
        Assert.Equal(new string('.', 225), board.Text);
        board.Apply(Across(7, 6, "кіт"));
        Assert.False(board.IsEmpty);
        Assert.Equal("кіт", board.Text.Substring(C(7, 6), 3));
        board.Clear([C(7, 6), C(7, 7), C(7, 8)]);
        Assert.True(board.IsEmpty);
    }

    // ================================================================== стіл: роздача й черга

    [Fact]
    public void Everyone_gets_seven_tiles_and_the_bag_shrinks()
    {
        var h = Table();
        Assert.Equal(7, Rack(h, 0).Length);
        Assert.Equal(7, Rack(h, 1).Length);
        Assert.Equal(104 - 14, Int(h, 0, "bag"));
        Assert.Equal([7, 7, 0, 0], Ints(h.View(0).GetProperty("racks")));
        Assert.Equal(0, Int(h, 0, "turn"));
        Assert.Equal(2, Int(h, 0, "players"));
    }

    [Fact]
    public void Four_players_take_turns_round_the_table()
    {
        var h = Table(players: 4);
        Assert.Equal(4, Int(h, 0, "players"));
        Assert.Equal(104 - 28, Int(h, 0, "bag"));
        foreach (var expected in new[] { 1, 2, 3, 0 })
        {
            Assert.True(h.Act(Int(h, 0, "turn"), "pass").Ok);
            Assert.Equal(expected, Int(h, 0, "turn"));
        }
    }

    [Fact]
    public void Playing_out_of_turn_is_refused_and_the_board_stays_clean()
    {
        var h = Table();
        var before = Views.Text(h.Room.Game.View(null));
        Assert.Equal("Зараз не твій хід", Play(h, 1, C(7, 6), "кіт").Message);
        Assert.Equal(before, Views.Text(h.Room.Game.View(null)));
        Assert.Equal(0, h.Room.Moves);
    }

    [Fact]
    public void An_unknown_action_is_refused()
    {
        var h = Table();
        Assert.Equal("Тут так не ходять", h.Act(0, "jump").Message);
    }

    [Fact]
    public void A_move_after_the_game_is_over_is_refused()
    {
        var h = Table();
        Rig(h, "", "а", "аа");
        for (var i = 0; i < 6; i++) Assert.True(h.Act(i % 2, "pass").Ok);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal("Партію зіграно, тисни «Ще раз»", h.Act(0, "pass").Message);
    }

    // ================================================================== стіл: викладка

    [Fact]
    public void A_word_from_the_rack_lands_on_the_board_and_scores()
    {
        var h = Table();
        Rig(h, "оаиеноаоаи", "кітоаие", "оса");
        Assert.Equal("+8 очок", Play(h, 0, C(7, 6), "кіт").Message);

        Assert.Equal("кіт", h.View(0).GetProperty("board").GetString()!.Substring(C(7, 6), 3));
        Assert.Equal(8, Ints(h.View(0).GetProperty("scores"))[0]);
        Assert.Equal(1, Int(h, 0, "turn"));
        Assert.Equal(7, Rack(h, 0).Length);      // добрав з мішка
        var last = h.View(0).GetProperty("last");
        Assert.Equal(0, last.GetProperty("seat").GetInt32());
        Assert.Equal(8, last.GetProperty("total").GetInt32());
        Assert.Equal("кіт", last.GetProperty("words")[0].GetProperty("word").GetString());
        Assert.Equal([C(7, 6), C(7, 7), C(7, 8)], Ints(last.GetProperty("cells")));
    }

    [Fact]
    public void Tiles_you_do_not_have_are_refused()
    {
        var h = Table();
        Rig(h, "оаиеноаоаи", "кіт", "оса");
        Assert.Equal("Таких фішок у тебе на стійці нема", Play(h, 0, C(7, 6), "оса").Message);
        Assert.True(h.View(0).GetProperty("board").GetString()!.All(c => c == '.'));
    }

    [Fact]
    public void A_latin_letter_is_refused()
    {
        var h = Table();
        Assert.Equal("Це не українська літера", h.Act(0, "play", new { tiles = new[] { Tile(112, 'k') } }).Message);
    }

    [Fact]
    public void A_blank_without_a_letter_is_refused()
    {
        var h = Table();
        var tiles = new[] { new Dictionary<string, object> { ["cell"] = 112, ["letter"] = "", ["blank"] = true } };
        Assert.Equal("Скажи, яка це літера на порожній фішці", h.Act(0, "play", new { tiles }).Message);
    }

    [Fact]
    public void The_play_payload_is_exactly_what_the_module_sends()
    {
        // Модуль шле { tiles: [{ cell, letter, blank }] } — будь-яка інша форма має відпадати вголос,
        // а не мовчки губити хід (той самий урок, що з { col } у «Чотирьох у ряд»).
        var h = Table();
        Rig(h, "оаиеноаоаи", "кітоаие", "оса");
        Assert.Equal("Не зрозумів, що ти кладеш", h.Act(0, "play", new { cells = new[] { 112 } }).Message);
        Assert.Equal("Не зрозумів, куди ти кладеш", h.Act(0, "play", new { tiles = new[] { new Dictionary<string, object> { ["letter"] = "к" } } }).Message);
        Assert.True(Play(h, 0, C(7, 6), "кіт").Ok);
    }

    [Fact]
    public void A_blank_played_from_the_rack_takes_the_letter_it_was_given()
    {
        var h = Table();
        Rig(h, "оаиеноаоаи", "к*тоаие", "оса");
        var tiles = new[] { Tile(C(7, 6), 'к'), Tile(C(7, 7), 'і', blank: true), Tile(C(7, 8), 'т') };
        Assert.Equal("+6 очок", h.Act(0, "play", new { tiles }).Message);
        Assert.Equal('І', h.View(0).GetProperty("board").GetString()![ScrabbleBoard.Centre]);
    }

    [Fact]
    public void Seven_tiles_at_once_are_a_bingo()
    {
        var h = Table();
        Rig(h, "оаиеноаоаи", "оаиеноа", "оса");
        Assert.Equal("Бінго! +64 очок", Play(h, 0, C(7, 4), "оаиеноа").Message);
        Assert.Equal(64, Ints(h.View(0).GetProperty("scores"))[0]);
    }

    [Fact]
    public void A_word_of_thirty_points_asks_for_the_achievement()
    {
        using var dict = Dict.Full("ґща");
        var h = Table(services: dict.Services);
        Rig(h, "оаиеноаоаи", "ґщаоаие", "оса");
        Assert.True(Play(h, 0, C(7, 6), "ґща").Ok);       // (10 + 8 + 1) × 2 = 38

        var award = Assert.Single(h.Awards);
        Assert.Equal("ach:scrabble-30", award.Reason);
        Assert.Equal(0, award.Shards);
        Assert.Equal("Оля", award.Nick);
        Assert.Contains("38 очок", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void In_small_dictionary_mode_the_achievement_waits_for_the_challenge_window()
    {
        using var dict = Dict.Small("оса");
        var h = Table(services: dict.Services);
        Rig(h, "оаиеноаоаи", "ґщаоаие", "осаоаие");
        Assert.True(Play(h, 0, C(7, 6), "ґща").Ok);
        Assert.Empty(h.Awards);                          // слово ще можуть зняти з дошки

        Assert.True(h.Act(1, "pass").Ok);                // вікно закрилось — слово лишилось
        Assert.Equal("ach:scrabble-30", Assert.Single(h.Awards).Reason);
    }

    [Fact]
    public void A_challenged_word_takes_its_achievement_with_it()
    {
        using var dict = Dict.Small("оса");
        var h = Table(services: dict.Services);
        Rig(h, "оаиеноаоаи", "ґщаоаие", "осаоаие");
        Assert.True(Play(h, 0, C(7, 6), "ґща").Ok);
        Assert.True(h.Act(1, "challenge").Ok);

        Assert.Empty(h.Awards);                          // очки відкотились — і нагорода разом з ними
        Assert.Equal(0, Ints(h.View(0).GetProperty("scores"))[0]);
    }

    [Fact]
    public void A_cheap_word_asks_for_nothing()
    {
        var h = Table();
        Rig(h, "оаиеноаоаи", "кітоаие", "оса");
        Assert.True(Play(h, 0, C(7, 6), "кіт").Ok);
        Assert.Empty(h.Awards);
    }

    // ================================================================== паси й обмін

    [Fact]
    public void A_pass_hands_the_turn_over_and_counts()
    {
        var h = Table();
        Assert.Equal("Пас", h.Act(0, "pass").Message);
        Assert.Equal(1, Int(h, 0, "passes"));
        Assert.Equal(1, Int(h, 0, "turn"));
    }

    [Fact]
    public void A_word_resets_the_pass_counter()
    {
        var h = Table();
        Rig(h, "оаиеноаоаи", "кітоаие", "оса");
        h.Act(0, "pass");
        h.Act(1, "pass");
        Assert.Equal(2, Int(h, 0, "passes"));
        Assert.True(Play(h, 0, C(7, 6), "кіт").Ok);
        Assert.Equal(0, Int(h, 0, "passes"));
    }

    [Fact]
    public void Six_passes_end_the_game_and_the_racks_are_subtracted()
    {
        var h = Table();
        Rig(h, "", "а", "аа");
        for (var i = 0; i < 6; i++) Assert.True(h.Act(i % 2, "pass").Ok);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        var result = h.View(0).GetProperty("result");
        Assert.Equal("passes", result.GetProperty("reason").GetString());
        Assert.Equal([-1, -2, 0, 0], Ints(result.GetProperty("scores")));
        Assert.Equal(0, result.GetProperty("winner").GetInt32());
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("turn").ValueKind);
    }

    [Fact]
    public void Equal_scores_are_a_draw()
    {
        var h = Table();
        Rig(h, "", "а", "а");
        for (var i = 0; i < 6; i++) h.Act(i % 2, "pass");

        Assert.True(h.Room.Result!.Draw);
        Assert.Contains("нічия", h.Outbox.OfType<Journal>().Last().Text);
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("result").GetProperty("winner").ValueKind);
    }

    [Fact]
    public void A_swap_keeps_the_bag_the_same_size_and_never_gives_back_what_was_handed_in()
    {
        var h = Table();
        Rig(h, "оаиеноаоаи", "*кітоса", "оса");
        Assert.Equal("Обмін", h.Act(0, "swap", new { letters = new[] { "*" } }).Message);

        Assert.Equal(10, Int(h, 0, "bag"));
        Assert.Equal(7, Rack(h, 0).Length);
        Assert.DoesNotContain("*", Rack(h, 0));
        Assert.Equal(1, Int(h, 0, "passes"));
        Assert.Equal(1, Int(h, 0, "turn"));
    }

    [Fact]
    public void A_swap_with_an_almost_empty_bag_is_refused()
    {
        var h = Table();
        Rig(h, "оаи", "кітоаие", "оса");
        Assert.Equal("У мішку замало фішок для обміну", h.Act(0, "swap", new { letters = new[] { "к" } }).Message);
    }

    [Fact]
    public void A_swap_needs_seven_tiles_in_the_bag_and_seven_is_enough()
    {
        var enough = Table();
        Rig(enough, "оаиеноа", "кітоаие", "оса");        // рівно сім — межа проходить
        Assert.True(enough.Act(0, "swap", new { letters = new[] { "к" } }).Ok);
        Assert.Equal(7, Int(enough, 0, "bag"));

        var scarce = Table();
        Rig(scarce, "оаиено", "кітоаие", "оса");         // шість — уже ні
        Assert.Equal("У мішку замало фішок для обміну", scarce.Act(0, "swap", new { letters = new[] { "к" } }).Message);
    }

    [Fact]
    public void Passes_and_swaps_count_into_the_same_six()
    {
        var h = Table();
        Rig(h, "оаиеноаоаи", "кітоаие", "осаоаие");
        for (var i = 0; i < 6; i++)
        {
            var seat = i % 2;
            var step = seat == 0
                ? h.Act(seat, "swap", new { letters = new[] { Rack(h, seat)[..1] } })
                : h.Act(seat, "pass");
            Assert.True(step.Ok);
        }

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal("passes", h.View(0).GetProperty("result").GetProperty("reason").GetString());
    }

    [Fact]
    public void A_swap_of_tiles_you_do_not_have_is_refused()
    {
        var h = Table();
        Rig(h, "оаиеноаоаи", "кітоаие", "оса");
        Assert.Equal("Таких фішок у тебе на стійці нема", h.Act(0, "swap", new { letters = new[] { "ґ" } }).Message);
    }

    [Fact]
    public void An_empty_swap_is_refused()
    {
        var h = Table();
        Assert.Equal("Обери, які фішки міняти", h.Act(0, "swap", new { letters = Array.Empty<string>() }).Message);
        Assert.Equal("Не зрозумів, що міняти", h.Act(0, "swap", new { tiles = 1 }).Message);
    }

    // ================================================================== кінець за фішками

    [Fact]
    public void Playing_the_last_tile_with_an_empty_bag_ends_the_game()
    {
        var h = Table();
        Rig(h, "", "кіт", "оса");
        Assert.True(Play(h, 0, C(7, 6), "кіт").Ok);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var result = h.View(0).GetProperty("result");
        Assert.Equal("out", result.GetProperty("reason").GetString());
        Assert.Equal([11, -3, 0, 0], Ints(result.GetProperty("scores")));   // 8 + чужа стійка «оса» = 3
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Contains("перемога: Оля", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void The_finish_hands_the_scores_to_the_platform()
    {
        var h = Table();
        Rig(h, "", "кіт", "оса");
        Play(h, 0, C(7, 6), "кіт");

        var finished = Assert.Single(h.Finished);
        Assert.Equal("scrabble", finished.GameId);
        Assert.Equal(11L, finished.Result.Scores![0]);
        Assert.Equal(-3L, finished.Result.Scores[1]);
    }

    // ================================================================== словник

    [Fact]
    public void Without_a_dictionary_the_game_still_plays_and_says_so()
    {
        var h = Table();     // сервісів нема взагалі — Words не знайдено
        Assert.True(h.View(0).GetProperty("smallDict").GetBoolean());
        Rig(h, "оаиеноаоаи", "кітоаие", "оса");
        Assert.True(Play(h, 0, C(7, 6), "кіт").Ok);
    }

    [Fact]
    public void With_the_big_dictionary_a_known_word_is_accepted()
    {
        using var dict = Dict.Full("кіт");
        Assert.True(dict.Words.FullLoaded);
        var h = Table(services: dict.Services);
        Assert.False(h.View(0).GetProperty("smallDict").GetBoolean());
        Rig(h, "оаиеноаоаи", "кітоаие", "оса");
        Assert.True(Play(h, 0, C(7, 6), "кіт").Ok);
    }

    [Fact]
    public void With_the_big_dictionary_an_unknown_word_is_refused_by_name()
    {
        using var dict = Dict.Full("оса");
        var h = Table(services: dict.Services);
        Rig(h, "оаиеноаоаи", "кітоаие", "оса");
        Assert.Equal("Такого слова нема: кіт", Play(h, 0, C(7, 6), "кіт").Message);
        Assert.True(h.View(0).GetProperty("board").GetString()!.All(c => c == '.'));
        Assert.Equal(0, h.Room.Moves);
    }

    [Fact]
    public void With_the_big_dictionary_a_perpendicular_word_must_be_known_too()
    {
        using var dict = Dict.Full("оса", "та", "то");
        var h = Table(services: dict.Services);
        Rig(h, "оаиеноаоаи", "осаоаие", "таоаиен");
        Assert.True(Play(h, 0, C(7, 6), "оса").Ok);
        // «та» і «то» словник знає, а «ас» — ні
        Assert.Equal("Такого слова нема: ас", Play(h, 1, C(6, 6), "та").Message);
    }

    [Fact]
    public void In_small_dictionary_mode_any_word_is_accepted()
    {
        using var dict = Dict.Small("оса");
        var h = Table(services: dict.Services);
        Assert.True(h.View(0).GetProperty("smallDict").GetBoolean());
        Rig(h, "оаиеноаоаи", "кітоаие", "осаоаие");
        Assert.True(Play(h, 0, C(7, 6), "кіт").Ok);
    }

    [Fact]
    public void A_challenge_takes_the_word_off_the_board_and_the_score_with_it()
    {
        using var dict = Dict.Small("оса");
        var h = Table(services: dict.Services);
        Rig(h, "оаиеноаоаи", "кітоаие", "осаоаие");
        Assert.True(Play(h, 0, C(7, 6), "кіт").Ok);
        Assert.Equal(8, Ints(h.View(0).GetProperty("scores"))[0]);

        Assert.Equal("«кіт» знято з дошки", h.Act(1, "challenge").Message);
        Assert.True(h.View(0).GetProperty("board").GetString()!.All(c => c == '.'));
        Assert.Equal(0, Ints(h.View(0).GetProperty("scores"))[0]);
        Assert.Equal("кітоаие", Rack(h, 0));
        Assert.Equal(10, Int(h, 0, "bag"));
        Assert.Equal(1, Int(h, 0, "turn"));            // хід лишається за тим, хто оскаржив
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("last").ValueKind);
    }

    [Fact]
    public void A_challenge_against_a_word_from_the_lists_fails()
    {
        using var dict = Dict.Small("оса");
        var h = Table(services: dict.Services);
        Rig(h, "оаиеноаоаи", "осаоаие", "кітоаие");
        Assert.True(Play(h, 0, C(7, 6), "оса").Ok);

        Assert.Equal("Таке слово в словнику є", h.Act(1, "challenge").Message);
        Assert.Equal("оса", h.View(0).GetProperty("board").GetString()!.Substring(C(7, 6), 3));
        Assert.Equal(6, Ints(h.View(0).GetProperty("scores"))[0]);
    }

    [Fact]
    public void A_challenge_works_only_once_and_only_against_someone_else()
    {
        using var dict = Dict.Small("оса");
        var h = Table(services: dict.Services);
        Rig(h, "оаиеноаоаи", "кітоаие", "осаоаие");
        Assert.Equal("Нема чого оскаржувати", h.Act(0, "challenge").Message);

        Assert.True(Play(h, 0, C(7, 6), "кіт").Ok);
        Assert.Equal("Зараз не твій хід", h.Act(0, "challenge").Message);
        Assert.True(h.Act(1, "challenge").Ok);
        Assert.Equal("Нема чого оскаржувати", h.Act(1, "challenge").Message);
    }

    [Fact]
    public void A_pass_closes_the_challenge_window()
    {
        using var dict = Dict.Small("оса");
        var h = Table(services: dict.Services);
        Rig(h, "оаиеноаоаи", "кітоаие", "осаоаие");
        Assert.True(Play(h, 0, C(7, 6), "кіт").Ok);
        Assert.True(h.Act(1, "pass").Ok);
        Assert.True(h.Act(0, "pass").Ok);
        Assert.Equal("Нема чого оскаржувати", h.Act(1, "challenge").Message);
    }

    [Fact]
    public void With_the_big_dictionary_there_is_nothing_to_challenge()
    {
        using var dict = Dict.Full("кіт");
        var h = Table(services: dict.Services);
        Rig(h, "оаиеноаоаи", "кітоаие", "осаоаие");
        Assert.True(Play(h, 0, C(7, 6), "кіт").Ok);
        Assert.Equal("Тут повний словник — оскаржувати нема потреби", h.Act(1, "challenge").Message);
        Assert.False(h.View(1).GetProperty("canChallenge").GetBoolean());
    }

    [Fact]
    public void A_word_nobody_challenged_stays_on_the_board()
    {
        // у малому режимі слово лягає без суду; якщо суперник змовчав — воно там і лишається
        using var dict = Dict.Small("оса");
        var h = Table(services: dict.Services);
        Rig(h, "оаиеноаоаи", "кітоаие", "осаоаие");
        Assert.True(Play(h, 0, C(7, 6), "кіт").Ok);
        Assert.True(h.Act(1, "pass").Ok);
        Assert.Equal("кіт", h.View(0).GetProperty("board").GetString()!.Substring(C(7, 6), 3));
        Assert.Equal(8, Ints(h.View(0).GetProperty("scores"))[0]);
    }

    [Fact]
    public void A_word_that_already_stood_on_the_board_survives_a_challenge()
    {
        using var dict = Dict.Small("оса");
        var h = Table(services: dict.Services);
        Rig(h, "оаиеноаоаи", "атаоаие", "осаоаие");
        Assert.True(Play(h, 0, C(7, 7), "ат").Ok);       // «ат» словник не знає, але ніхто не оскаржив
        Assert.True(h.Act(1, "pass").Ok);

        // те саме «ат», тепер згори вниз: слово вже стояло на дошці, отже законне (spec, рядок 6)
        Assert.True(Play(h, 0, C(6, 8), "а").Ok);
        Assert.Equal("Таке слово в словнику є", h.Act(1, "challenge").Message);
    }

    [Fact]
    public void The_view_says_when_the_challenge_button_makes_sense()
    {
        using var dict = Dict.Small("оса");
        var h = Table(services: dict.Services);
        Rig(h, "оаиеноаоаи", "кітоаие", "осаоаие");
        Assert.False(h.View(0).GetProperty("canChallenge").GetBoolean());    // ще нічого не викладено
        Assert.True(Play(h, 0, C(7, 6), "кіт").Ok);

        Assert.True(h.View(1).GetProperty("canChallenge").GetBoolean());
        Assert.False(h.View(0).GetProperty("canChallenge").GetBoolean());    // своє слово не оскаржують
        Assert.False(h.View(null).GetProperty("canChallenge").GetBoolean()); // глядач і поготів

        Assert.True(h.Act(1, "pass").Ok);
        Assert.True(h.Act(0, "pass").Ok);
        // хід знову за Петром і слово Олі на дошці — але вікно вже закрите, кнопці нема чого світитись
        Assert.False(h.View(1).GetProperty("canChallenge").GetBoolean());
    }

    // ================================================================== приховане

    [Fact]
    public void Each_player_sees_only_his_own_rack()
    {
        var h = Table();
        Rig(h, "оаиеноаоаи", "кітоаие", "осаоаие");
        Assert.Equal("кітоаие", Rack(h, 0));
        Assert.Equal("осаоаие", Rack(h, 1));
        // на дроті стійка — це масив рядків; у чужому виді такого масиву не має бути взагалі
        static string Wire(string rack) => Views.Text(rack.Select(c => c.ToString()).ToArray());
        Assert.DoesNotContain(Wire("осаоаие"), Views.Text(h.Room.Game.View(0)));
        Assert.DoesNotContain(Wire("кітоаие"), Views.Text(h.Room.Game.View(1)));
        Assert.Contains(Wire("кітоаие"), Views.Text(h.Room.Game.View(0)));
    }

    [Fact]
    public void A_watcher_sees_the_board_but_no_rack_at_all()
    {
        var h = Table();
        var view = h.View(null);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("rack").ValueKind);
        Assert.Equal(225, view.GetProperty("board").GetString()!.Length);
        Assert.Equal([7, 7, 0, 0], Ints(view.GetProperty("racks")));
    }

    [Fact]
    public void An_empty_seat_has_no_rack()
    {
        var h = Table();
        var view = Views.Json(h.Room.Game.View(3));
        Assert.Equal(JsonValueKind.Null, view.GetProperty("rack").ValueKind);
    }

    [Fact]
    public void View_matches_the_shape_the_client_expects()
    {
        var h = Table();
        var view = h.View(0);
        foreach (var name in new[] { "board", "bonuses", "turn", "players", "scores", "racks", "rack", "bag", "last", "passes", "smallDict", "canChallenge", "moves", "result" })
            Assert.True(Views.Has(view, name), $"у виді нема поля {name}");

        Assert.Equal(225, view.GetProperty("bonuses").GetString()!.Length);
        Assert.Equal(ScrabbleBoard.Bonuses, view.GetProperty("bonuses").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("last").ValueKind);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("result").ValueKind);
        Assert.Equal(0, view.GetProperty("passes").GetInt32());
        Assert.Empty(view.GetProperty("moves").EnumerateArray());
    }

    [Fact]
    public void The_journal_of_moves_grows_with_the_game()
    {
        var h = Table();
        Rig(h, "оаиеноаоаи", "кітоаие", "осаоаие");
        Play(h, 0, C(7, 6), "кіт");
        h.Act(1, "pass");

        var moves = h.View(0).GetProperty("moves").EnumerateArray().ToArray();
        Assert.Equal(2, moves.Length);
        Assert.Equal(0, moves[0].GetProperty("seat").GetInt32());
        Assert.Contains("кіт", moves[0].GetProperty("text").GetString());
        Assert.Equal("пас", moves[1].GetProperty("text").GetString());
    }

    // ================================================================== вихід гравця

    [Fact]
    public void A_player_who_leaves_returns_his_tiles_to_the_bag_and_the_rest_play_on()
    {
        var h = Table(players: 3);
        Assert.Equal(104 - 21, Int(h, 0, "bag"));
        Assert.True(h.Leave("Петро").Ok);

        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(2, Int(h, 0, "players"));
        Assert.Equal(104 - 14, Int(h, 0, "bag"));
        Assert.True(h.Act(0, "pass").Ok);
        Assert.Equal(2, Int(h, 0, "turn"));       // черга перескочила порожнє місце
    }

    [Fact]
    public void The_turn_moves_on_when_the_one_who_was_to_play_leaves()
    {
        var h = Table(players: 3);
        h.Act(0, "pass");
        Assert.Equal(1, Int(h, 0, "turn"));
        Assert.True(h.Leave("Петро").Ok);
        Assert.Equal(2, Int(h, 2, "turn"));
    }

    [Fact]
    public void A_player_who_leaves_inside_the_challenge_window_does_not_lose_his_tiles()
    {
        using var dict = Dict.Small("оса");
        var h = Table(players: 3, services: dict.Services);
        Assert.Equal(ScrabbleBag.Total, Tiles(h));

        var letters = Rack(h, 0).Replace("*", "");           // порожню фішку сюди класти нічим
        Assert.True(Play(h, 0, C(7, 7), letters[..2]).Ok);   // слово лягло, вікно оскарження відкрите
        Assert.True(h.Leave("Іван").Ok);                     // встає ТРЕТІЙ, не той, хто ходив

        // Знімок у вікні пам'ятає мішок без фішок Івана — тому вихід вікно й закриває.
        Assert.Equal("Нема чого оскаржувати", h.Act(1, "challenge").Message);
        Assert.False(h.View(1).GetProperty("canChallenge").GetBoolean());
        Assert.Equal(ScrabbleBag.Total, Tiles(h));           // усі 104 фішки на місці
    }

    [Fact]
    public void When_only_one_player_is_left_the_game_is_his()
    {
        var h = Table();
        Assert.True(h.Leave("Петро").Ok);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Equal("left", h.View(0).GetProperty("result").GetProperty("reason").GetString());
        Assert.Contains("лишилась за Оля", h.Outbox.OfType<Journal>().Last().Text);
    }

    // ================================================================== рематч, детермінізм, стан

    [Fact]
    public void Rematch_deals_a_clean_board_and_swaps_the_seats()
    {
        var h = Table();
        Rig(h, "", "а", "аа");
        for (var i = 0; i < 6; i++) h.Act(i % 2, "pass");
        Assert.True(h.Rematch("Оля").Ok);

        Assert.Equal("Петро", h.Room.Seats[0]);
        Assert.True(h.View(0).GetProperty("board").GetString()!.All(c => c == '.'));
        Assert.Equal([0, 0, 0, 0], Ints(h.View(0).GetProperty("scores")));
        Assert.Equal(104 - 14, Int(h, 0, "bag"));
        Assert.Equal(0, Int(h, 0, "turn"));
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("result").ValueKind);
        Assert.Empty(h.View(0).GetProperty("moves").EnumerateArray());
    }

    [Fact]
    public void The_same_seed_deals_the_same_tiles()
    {
        Assert.Equal(Rack(Table(seed: 42), 0), Rack(Table(seed: 42), 0));
        Assert.NotEqual(Rack(Table(seed: 42), 0), Rack(Table(seed: 43), 0));
    }

    [Fact]
    public void The_same_moves_from_the_same_seed_give_the_same_view()
    {
        static string Round(int seed)
        {
            var h = Table(seed: seed);
            h.Act(0, "pass");
            h.Act(1, "pass");
            return Views.Text(h.Room.Game.View(null));
        }
        Assert.Equal(Round(11), Round(11));
    }

    [Fact]
    public void Load_of_Save_gives_the_same_game()
    {
        var h = Table();
        Rig(h, "оаиеноаоаи", "кітоаие", "осаоаие");
        Play(h, 0, C(7, 6), "кіт");
        h.Act(1, "pass");

        var copy = new Scrabble();
        copy.Load(h.Room.Game.Save()!);
        foreach (var seat in new int?[] { 0, 1, null })
            Assert.Equal(Views.Text(h.Room.Game.View(seat)), Views.Text(copy.View(seat)));
    }

    [Fact]
    public void Broken_saved_state_does_not_break_the_view()
    {
        var copy = new Scrabble();
        copy.Load("null");
        Assert.Equal(225, Views.Json(copy.View(null)).GetProperty("board").GetString()!.Length);
    }

    [Fact]
    public void The_game_is_in_the_catalog_as_the_spec_says()
    {
        var game = Assert.Single(new Registry().Catalog, g => g.Id == "scrabble");
        Assert.Equal("Ерудит", game.Title);
        Assert.Equal("ерудит", game.Accusative);
        Assert.Equal("board", game.Group);
        Assert.Equal("byHost", game.Start);
        Assert.True(game.Hidden);
        Assert.False(game.Rated);          // ставок тут нема: за столом може бути четверо
        Assert.False(game.Private);
        Assert.Equal(2, game.MinPlayers);
        Assert.Equal(4, game.MaxPlayers);
        Assert.Equal(0, game.TickMs);
        Assert.Equal("scrabble", game.Module);
        Assert.NotEmpty(game.Hint);
    }

    [Fact]
    public void Seat_names_are_ordinals()
    {
        var h = Table(players: 4);
        Assert.Equal(["перший", "другий", "третій", "четвертий"], h.Room.Summary().SeatNames);
    }
}
