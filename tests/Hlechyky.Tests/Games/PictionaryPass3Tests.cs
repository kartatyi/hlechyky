using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Піктіонарі, прохід №3 (specs/pictionary.md, «Прохід №3»): пропустити слово, слова компанії, реакції, удвох
/// «Скільки встигнемо», галерея з ❤ і «Митець партії», публічний альбом, малюнок на дроті рядком.
/// </summary>
public class PictionaryPass3Tests
{
    static readonly int RevealTicks = Pictionary.RevealMs / Pictionary.TickMs;
    static readonly string[] Many = ["кіт", "пес", "їжак", "сова", "риба", "жаба", "вовк", "лис", "кінь", "бик", "заєць", "ведмідь"];

    static IServiceProvider Services(PictionaryStore? store = null, params string[] words)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton(new PictionaryWords((words.Length > 0 ? words : Many).Select(w => ("animals", w))));
        sc.AddSingleton(store ?? new PictionaryStore(null));
        return sc.BuildServiceProvider();
    }

    static RoomHarness Lobby(object? options, IServiceProvider services, params string[] nicks)
    {
        var h = new RoomHarness("pictionary", options: options, seed: 7, services: services);
        foreach (var nick in nicks) h.Join(nick);
        return h;
    }

    static RoomHarness Table(object? options = null, IServiceProvider? services = null, params string[] nicks)
    {
        var h = Lobby(options, services ?? Services(), nicks.Length > 0 ? nicks : ["Оля", "Петро", "Ганна"]);
        Assert.True(h.Start().Ok);
        return h;
    }

    static string Phase(RoomHarness h) => h.View(null).GetProperty("phase").GetString()!;
    static int Drawer(RoomHarness h) => h.View(null).GetProperty("drawer").GetInt32();
    static string[] Choices(RoomHarness h, int seat) => [.. h.View(seat).GetProperty("choices").EnumerateArray().Select(e => e.GetString()!)];
    static string Word(RoomHarness h) => h.View(Drawer(h)).GetProperty("word").GetString()!;

    static ActResult Guess(RoomHarness h, int seat, string text)
    {
        h.Clock.AdvanceMs(Pictionary.GuessEveryMs);
        return h.Act(seat, "guess", new { text });
    }

    static JsonElement LastFrame(RoomHarness h) =>
        Views.Json(h.Outbox.OfType<RoomFrame>().Last(f => f.RoomId == h.RoomId).Frame);

    /// <summary>Один хід компанією: художник обирає, малює штрих, усі вгадують, розкриття минає.</summary>
    static void PlayTurn(RoomHarness h, int seats)
    {
        var d = Drawer(h);
        Assert.True(h.Act(d, "pick", new { i = 0 }).Ok);
        h.Input(d, "draw", new { s = 1, c = 1, w = 8, p = new[] { 10, 10, 200, 200 } });
        var word = Word(h);
        for (var s = 0; s < seats; s++) if (s != d) Guess(h, s, word);
        h.Tick(RevealTicks + 1);
    }

    // ---------------------------------------------------------------- п. 32: малюнок на дроті

    [Fact]
    public void Wire_packs_and_unpacks_the_same_ops_and_is_much_smaller_than_json()
    {
        var rng = new Random(3);
        var ops = new List<int[]>();
        for (var k = 0; k < 60; k++)
        {
            var n = rng.Next(1, 120);
            var op = new int[4 + 2 * n];
            op[0] = 0; op[1] = k + 1; op[2] = rng.Next(20); op[3] = rng.Next(1, 61);
            int x = rng.Next(1001), y = rng.Next(751);
            for (var i = 0; i < n; i++)
            {
                x = Math.Clamp(x + rng.Next(-12, 13), 0, 1000);
                y = Math.Clamp(y + rng.Next(-12, 13), 0, 750);
                op[4 + 2 * i] = x; op[5 + 2 * i] = y;
            }
            ops.Add(op);
        }
        ops.Add([1, 99, 5, 0, 1000, 750]);

        var z = SketchWire.Pack(ops);
        var back = SketchWire.Unpack(z)!;
        Assert.Equal(ops.Count, back.Count);
        for (var i = 0; i < ops.Count; i++) Assert.Equal(ops[i], back[i]);

        var json = JsonSerializer.Serialize(ops).Length;
        Assert.True(json > z.Length * 2.5, $"json {json} Б, рядок {z.Length} Б");
        Assert.Equal(ops.Skip(10).Count(), SketchWire.Unpack(SketchWire.Pack(ops, 10))!.Count);
    }

    [Fact]
    public void Broken_wire_strings_are_refused()
    {
        Assert.Null(SketchWire.Unpack("не base64"));
        Assert.Null(SketchWire.Unpack(Convert.ToBase64String([0, 1, 1])));            // обірвано посеред операції
        Assert.Null(SketchWire.Unpack(Convert.ToBase64String([0, 1, 25, 8, 1, 5, 5])));  // нема такого кольору
        Assert.Empty(SketchWire.Unpack("")!);
    }

    // ---------------------------------------------------------------- п. 28: пропустити слово

    [Fact]
    public void The_drawer_can_take_three_new_words_once_per_game_and_the_clock_keeps_going()
    {
        var h = Table();
        var d = Drawer(h);
        var until = h.View(d).GetProperty("until").GetDateTimeOffset();
        var before = Choices(h, d);
        Assert.True(h.View(d).GetProperty("reroll").GetBoolean());
        Assert.False(h.View((d + 1) % 3).GetProperty("reroll").GetBoolean());

        Assert.False(h.Act((d + 1) % 3, "reroll").Ok);
        Assert.True(h.Act(d, "reroll").Ok);
        var after = Choices(h, d);
        Assert.Empty(before.Intersect(after));
        Assert.Equal(until, h.View(d).GetProperty("until").GetDateTimeOffset());
        Assert.False(h.View(d).GetProperty("reroll").GetBoolean());
        Assert.False(h.Act(d, "reroll").Ok);

        // наступного разу той самий художник уже не міняє
        PlayTurn(h, 3); PlayTurn(h, 3); PlayTurn(h, 3);
        Assert.Equal(d, Drawer(h));
        Assert.False(h.Act(d, "reroll").Ok);
    }

    // ---------------------------------------------------------------- п. 25: слова компанії

    [Fact]
    public void Company_words_go_in_the_lobby_and_are_drawn_by_others_only()
    {
        var h = Lobby(null, Services(), "Оля", "Петро");
        Assert.True(h.Act(0, "home", new { text = "  кумів   трактор " }).Ok);
        Assert.False(h.Act(0, "home", new { text = "кумів трактор" }).Ok);   // уже є
        Assert.False(h.Act(1, "home", new { text = "<script>" }).Ok);
        Assert.False(h.Act(1, "home", new { text = "ы" }).Ok);
        Assert.True(h.Act(0, "home", new { text = "Глек на ярмарку" }).Ok);
        Assert.True(h.Act(0, "home", new { text = "Мар’янин борщ" }).Ok);
        Assert.False(h.Act(0, "home", new { text = "четверте" }).Ok);         // більше трьох — ні

        Assert.Equal(3, h.View(0).GetProperty("home").GetProperty("mine").GetArrayLength());
        Assert.Equal(0, h.View(1).GetProperty("home").GetProperty("mine").GetArrayLength());
        Assert.Equal(3, h.View(1).GetProperty("home").GetProperty("n").GetInt32());
        Assert.DoesNotContain("трактор", h.View(1).GetRawText());
        Assert.True(h.Act(0, "unhome", new { text = "Мар'янин борщ" }).Ok);

        Assert.True(h.Start().Ok);
        Assert.False(h.Act(1, "home", new { text = "запізно" }).Ok);

        // Оля (автор) малює першою — її слів у виборі нема; Петро малює другим — одне з трьох «наше»
        Assert.Equal(0, Drawer(h));
        Assert.DoesNotContain(true, h.View(0).GetProperty("choicesHome").EnumerateArray().Select(e => e.GetBoolean()));
        PlayTurn(h, 2);
        Assert.Equal(1, Drawer(h));
        var home = h.View(1).GetProperty("choicesHome").EnumerateArray().Select(e => e.GetBoolean()).ToArray();
        var i = Array.IndexOf(home, true);
        Assert.True(i >= 0);
        Assert.Contains(Choices(h, 1)[i], new[] { "кумів трактор", "Глек на ярмарку" });
        Assert.True(h.Act(1, "pick", new { i }).Ok);
        Assert.Equal("Оля", h.View(1).GetProperty("homeBy").GetString());
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("homeBy").ValueKind);   // Оля вгадує — автор слова їй не підказка, але й слово не видно
    }

    // ---------------------------------------------------------------- п. 27: реакції

    [Fact]
    public void Reactions_come_from_those_who_guessed_and_fly_in_the_frame()
    {
        var h = Table();
        var d = Drawer(h);
        Assert.True(h.Act(d, "pick", new { i = 0 }).Ok);
        var word = Word(h);
        int g = (d + 1) % 3, other = (d + 2) % 3;

        h.Input(g, "react", new { e = 0 });
        h.Input(d, "react", new { e = 1 });
        h.Tick();
        Assert.Equal(0, h.View(null).GetProperty("reacts")[0].GetInt32());

        Assert.True(Guess(h, g, word).Ok);
        h.Input(g, "react", new { e = 0 });
        h.Input(g, "react", new { e = 2 });   // занадто швидко — мовчки ні
        var room = h.Room;
        lock (room.Sync) Assert.True(((Pictionary)room.Game).FanReact("глядач", 1).Ok);
        h.Tick();
        Assert.Equal([0, 1], LastFrame(h).GetProperty("re").EnumerateArray().Select(e => e.GetInt32()).ToArray());
        h.Tick();
        Assert.Equal(1, h.View(other).GetProperty("reacts")[1].GetInt32());
    }

    // ---------------------------------------------------------------- п. 26: галерея, ❤, «Митець партії», альбом

    static RoomHarness ToGallery(PictionaryStore? store = null)
    {
        var h = Table(new { rounds = "1" }, Services(store), "Оля", "Петро", "Ганна");
        PlayTurn(h, 3); PlayTurn(h, 3); PlayTurn(h, 3);
        return h;
    }

    [Fact]
    public void After_the_last_drawing_comes_the_gallery_and_the_best_gets_the_artist_award()
    {
        var h = ToGallery();
        Assert.Equal("vote", Phase(h));
        var gallery = h.View(null).GetProperty("gallery");
        Assert.Equal(3, gallery.GetArrayLength());
        Assert.Equal(JsonValueKind.Null, gallery[0].GetProperty("hearts").ValueKind);   // ❤ до кінця не видно

        Assert.False(h.Act(0, "vote", new { t = 1 }).Ok);   // свій малюнок (Оля малювала перший хід)
        Assert.True(h.Act(0, "vote", new { t = 2 }).Ok);
        Assert.True(h.Act(1, "vote", new { t = 1 }).Ok);
        Assert.True(h.Act(1, "vote", new { t = 3 }).Ok);   // передумав
        Assert.Equal(3, h.View(1).GetProperty("myVote").GetInt32());
        Assert.True(h.Act(2, "vote", new { t = 2 }).Ok);   // усі — кінець

        Assert.Equal("done", Phase(h));
        Assert.Equal([1], h.View(null).GetProperty("artists").EnumerateArray().Select(e => e.GetInt32()).ToArray());
        Assert.Equal(2, h.View(null).GetProperty("gallery")[1].GetProperty("hearts").GetInt32());
        Assert.Contains(h.Awards, a => a.Nick == "Петро" && a.Shards == Pictionary.ArtistShards);
        Assert.Contains("Митець партії — Петро", h.Finished.Single().Result.Text);
    }

    [Fact]
    public void The_gallery_ends_by_the_clock_too()
    {
        var h = ToGallery();
        h.Tick(Pictionary.VoteMs / Pictionary.TickMs + 1);
        Assert.Equal("done", Phase(h));
        Assert.Empty(h.Awards);
        Assert.Single(h.Finished);
    }

    [Fact]
    public void Players_pin_drawings_into_the_public_album_after_the_game()
    {
        var store = new PictionaryStore(null);
        var h = ToGallery(store);
        var game = (Pictionary)h.Room.Game;
        lock (h.Room.Sync) Assert.NotNull(game.Pin("Оля", 2).Error);   // ще голосують
        h.Tick(Pictionary.VoteMs / Pictionary.TickMs + 1);

        lock (h.Room.Sync)
        {
            var (art, error) = game.Pin("Оля", 2);
            Assert.Null(error);
            Assert.Equal("Петро", art!.Author);
            Assert.NotNull(game.Pin("Петро", 2).Error);    // уже в альбомі
            Assert.NotNull(game.Pin("Хтось", 1).Error);    // не грав
            Assert.Null(game.Pin("оля", 1).Error);         // той самий нік
            Assert.Null(game.Pin("Оля", 3).Error);
            Assert.Single(SketchWire.Unpack(art.Z)!);
        }
        Assert.Equal([1, 2, 3], h.View(null).GetProperty("pinned").EnumerateArray().Select(e => e.GetInt32()).ToArray());
    }

    [Fact]
    public void The_album_pages_newest_first_deletes_and_keeps_within_its_size()
    {
        var store = new PictionaryStore(null);
        var at = DateTimeOffset.Parse("2026-09-29T12:00:00Z");
        for (var i = 0; i < 30; i++) store.Add($"слово{i}", "Оля", "Петро", i % 3, false, at, "AAEBCAEBAQ==");
        var (first, more) = store.Page(0, 24);
        Assert.Equal(24, first.Count);
        Assert.True(more);
        Assert.Equal("слово29", first[0].Word);
        var (rest, more2) = store.Page(first[^1].Id, 24);
        Assert.Equal(6, rest.Count);
        Assert.False(more2);
        Assert.True(store.Delete(first[0].Id));
        Assert.False(store.Delete(first[0].Id));
        Assert.Equal(29, store.Count);

        var big = new string('A', PictionaryStore.MaxArtChars);
        for (var i = 0; i < PictionaryStore.MaxTotalChars / PictionaryStore.MaxArtChars + 5; i++) store.Add("x", "a", "b", 0, false, at, big);
        Assert.True(store.TotalChars <= PictionaryStore.MaxTotalChars);
    }

    [Fact]
    public void The_album_lives_in_the_database()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pc-album-{Guid.NewGuid():N}.db");
        try
        {
            var store = new PictionaryStore(new Db(path));
            var id = store.Add("кумів трактор", "Оля", "Петро", 2, true, DateTimeOffset.UtcNow, "AAEBCAEBAQ==");
            Assert.True(store.RecordPair(["Оля", "Петро"], 5, DateTimeOffset.UtcNow));
            store.Flush();

            var again = new PictionaryStore(new Db(path));   // рестарт
            var (items, _) = again.Page(0, 10);
            Assert.Equal(id, items.Single().Id);
            Assert.True(items[0].Home);
            Assert.Equal(5, again.Pair(PictionaryStore.PairKey(["петро", "оля"]))!.Best);
            Assert.True(again.Delete(id));
            Assert.Equal(0, again.Count);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    // ---------------------------------------------------------------- п. 29: удвох «Скільки встигнемо»

    [Fact]
    public void Duo_needs_exactly_two()
    {
        var h = Lobby(new { mode = "duo" }, Services(), "Оля", "Петро", "Ганна");
        Assert.False(h.Start().Ok);
        h.Leave("Ганна");
        Assert.True(h.Start().Ok);
        Assert.Equal("draw", Phase(h));   // без вибору: слово одразу
    }

    [Fact]
    public void Duo_counts_words_together_takes_turns_and_keeps_the_pair_record()
    {
        var store = new PictionaryStore(null);
        var h = Table(new { mode = "duo" }, Services(store), "Оля", "Петро");
        var total = h.View(0).GetProperty("duo").GetProperty("totalMs").GetInt32();
        Assert.Equal(Pictionary.DuoMs, total);

        for (var k = 0; k < 3; k++)
        {
            var d = Drawer(h);
            Assert.Equal(k % 2, d);
            Assert.True(Guess(h, 1 - d, Word(h)).Ok);
            h.Tick(Pictionary.DuoRevealMs / Pictionary.TickMs + 1);
        }
        // пропустити слово удвох — скільки завгодно, малює той самий
        var dd = Drawer(h);
        var w = Word(h);
        Assert.True(h.Act(dd, "reroll").Ok);
        Assert.NotEqual(w, Word(h));
        Assert.Equal(dd, Drawer(h));

        Assert.Equal(3, h.View(null).GetProperty("duo").GetProperty("count").GetInt32());
        h.Tick(Pictionary.DuoMs / Pictionary.TickMs);
        Assert.Equal("done", Phase(h));
        Assert.True(h.View(null).GetProperty("duo").GetProperty("record").GetBoolean());
        Assert.Equal(3, store.Pair(PictionaryStore.PairKey(["Оля", "Петро"]))!.Best);
        Assert.Contains("встигли 3 слова", h.Finished.Single().Result.Text);

        // друга партія: старий рекорд видно, менше — не рекорд
        Assert.True(h.Rematch().Ok);
        Assert.Equal(3, h.View(null).GetProperty("duo").GetProperty("best").GetInt32());
        h.Tick(Pictionary.DuoMs / Pictionary.TickMs + 1);
        Assert.Equal("done", Phase(h));
        Assert.False(h.View(null).GetProperty("duo").GetProperty("record").GetBoolean());
        Assert.Equal(3, store.Pair(PictionaryStore.PairKey(["Оля", "Петро"]))!.Best);
    }
}
