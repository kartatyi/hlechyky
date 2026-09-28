using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using static Hlechyky.Tests.Games.DotepyTests;

namespace Hlechyky.Tests.Games;

/// <summary>Прохід №3 (29.09): теми партії, завдання «про нас» із ніками, смайл-ребуси, «📌 В альбом» і сам альбом.</summary>
public sealed class DotepyPass3Tests
{
    /// <summary>Банк із темами: по 12 звичайних на тему, 4 фінальні, «про нас» і ребуси.</summary>
    static IReadOnlyList<DotepyPrompt> Themed() =>
    [
        .. Enumerable.Range(1, 12).Select(i => new DotepyPrompt($"s{i:00}", $"Сільське завдання номер {i}", ["село"], false)),
        .. Enumerable.Range(1, 12).Select(i => new DotepyPrompt($"r{i:00}", $"Робоче завдання номер {i}", ["робота"], false)),
        .. Enumerable.Range(1, 4).Select(i => new DotepyPrompt($"f{i:00}", $"Фінальне сільське номер {i}", ["село"], true)),
        .. Enumerable.Range(1, 10).Select(i => new DotepyPrompt($"n{i:00}", $"Що {{нік}} робить уночі, варіант {i}", [Dotepy.TagNas], false)),
        .. Enumerable.Range(1, 10).Select(i => new DotepyPrompt($"b{i:00}", $"Що тут сталося? 🐐🚜🌧 варіант {i}", [Dotepy.TagRebus], false)),
    ];

    static List<string[]> RoundsOf(RoomHarness h)
    {
        var rounds = new List<string[]>();
        var last = -1;
        for (var guard = 0; guard < 20_000 && h.Room.Status == RoomStatus.Playing; guard++)
        {
            var v = V(h);
            var phase = v.GetProperty("phase").GetString();
            if (phase == "write")
            {
                var r = v.GetProperty("round").GetInt32();
                if (r != last) { last = r; rounds.Add([.. v.GetProperty("prompts").EnumerateArray().Select(p => p.GetString()!)]); }
                WriteAll(h);
            }
            else if (phase == "vote" && Pending(h)) VoteAll(h, s => [FirstOther(h, s)]);
            h.Tick();
        }
        return rounds;
    }

    [Fact]
    public void Theme_option_keeps_the_party_inside_the_chosen_tags()
    {
        var h = Table(4, options: new { themes = "selo" }, bank: Themed());
        var rounds = RoundsOf(h);
        Assert.Equal(3, rounds.Count);
        Assert.All(rounds.SelectMany(r => r), p => Assert.StartsWith(p.Contains("Фінальне") ? "Фінальне сільське" : "Сільське", p));
    }

    [Fact]
    public void Unknown_theme_or_theme_without_prompts_falls_back_to_the_whole_bank()
    {
        var h = Table(3, options: new { themes = "glek" }, bank: Themed());
        Assert.Equal("write", Phase(h));
        Assert.NotEmpty(V(h).GetProperty("prompts").EnumerateArray());
    }

    [Fact]
    public void All_themes_bring_one_nick_prompt_each_round_and_a_rebus_in_round_two()
    {
        var h = Table(4, bank: Themed());
        var rounds = RoundsOf(h);
        var nicks = Names.Take(4).ToArray();
        // раунди 1–2 (по два завдання на чотирьох): «про нас» — у першому, ребус — у другому
        Assert.Contains(rounds[0], p => nicks.Any(n => p.StartsWith("Що " + n + " робить")));
        Assert.Contains(rounds[1], p => p.Contains("🐐"));
        Assert.DoesNotContain(rounds.SelectMany(r => r), p => p.Contains(Dotepy.NickSlot));
    }

    [Fact]
    public void In_a_duel_the_hero_of_the_nick_prompt_is_not_one_of_its_two_authors()
    {
        for (var seed = 1; seed <= 12; seed++)
        {
            var h = Table(6, seed: seed, bank: Themed());
            var saw = false;
            for (var s = 0; s < 6; s++)
                foreach (var (_, prompt) in Tasks(h, s))
                    if (prompt.Contains(" робить уночі"))
                    {
                        saw = true;
                        Assert.DoesNotContain(Names[s], prompt);
                    }
            Assert.True(saw, $"сід {seed}: нема завдання «про нас»");
        }
    }

    [Fact]
    public void Nick_prompt_says_the_nick_without_the_guest_prefix()
    {
        var bank = new List<DotepyPrompt> { new("n1", "Що {нік} робить уночі", [Dotepy.TagNas], false) };
        bank.AddRange(Enumerable.Range(1, 6).Select(i => new DotepyPrompt($"x{i}", $"Звичайне завдання {i}", ["тест"], i > 4)));
        var h = new RoomHarness("dotepy", new { themes = "nas" }, 3, RoomHarness.WithService(new DotepyPrompts(bank)));
        foreach (var n in new[] { "гість Оля", "гість Петро", "гість Ганна" }) Assert.True(h.Join(n).Ok, h.Reply.Message);
        Assert.True(h.Start().Ok, h.Reply.Message);
        var p = V(h).GetProperty("prompts").EnumerateArray().Select(x => x.GetString()!).First(x => x.Contains("уночі"));
        Assert.DoesNotContain("гість", p);
        Assert.Matches("^Що (Оля|Петро|Ганна) робить уночі$", p);
    }

    [Fact]
    public void Glek_reads_a_rebus_without_emoji_and_says_it_is_on_screen()
    {
        var h = Table(3, options: new { themes = "rebus", rounds = "short" }, bank: Themed());
        WriteAll(h);
        UntilPhase(h, "vote");
        var say = Say(h);
        Assert.Contains("Що тут сталося? Смайл-ребус на екрані.", say);
        Assert.DoesNotContain("🐐", say);
        Assert.Contains("🐐", Card(h).GetProperty("prompt").GetString());
    }

    [Fact]
    public void Pin_takes_a_best_joke_once_only_from_those_who_played_and_at_most_two_each()
    {
        var h = Table(3, options: new { rounds = "short" });
        Assert.Equal("В альбом закидають після партії", Game(h).Pin("Оля", 0).Error);
        PlayMatch(h, s => [FirstOther(h, s)]);
        var best = V(h).GetProperty("result").GetProperty("best");
        Assert.True(best.GetArrayLength() >= 2);

        Assert.Equal("Закидають ті, хто грав", Game(h).Pin("Чужий", 0).Error);
        Assert.Equal("Нема такого дотепу", Game(h).Pin("Оля", 9).Error);
        var (item, error) = Game(h).Pin("Оля", 0);
        Assert.Null(error);
        Assert.Equal(best[0].GetProperty("text").GetString(), item!.Text);
        Assert.Equal(best[0].GetProperty("prompt").GetString(), item.Prompt);
        Assert.Equal(Names[best[0].GetProperty("seat").GetInt32()], item.Author);
        Assert.Equal("Оля", item.By);
        Assert.Equal("Цей уже в альбомі 📌", Game(h).Pin("Петро", 0).Error);
        Assert.Equal([0], V(h).GetProperty("result").GetProperty("pinned").EnumerateArray().Select(x => x.GetInt32()));

        Assert.Null(Game(h).Pin("оля", 1).Error);                     // той самий нік іншим регістром — той самий гравець
        if (best.GetArrayLength() > 2) Assert.StartsWith("Ти вже закинув 2", Game(h).Pin("Оля", 2).Error);
    }

    [Fact]
    public void Album_in_memory_pages_newest_first_likes_toggle_and_admin_deletes()
    {
        var album = new DotepyAlbum(null);
        var at = DateTimeOffset.UtcNow;
        var ids = Enumerable.Range(1, 5).Select(i => album.Add($"Завдання {i}", $"дотеп {i}", "Оля", "Петро", 100 * i, at)).ToArray();
        var (page, more) = album.Page(0, 3, null);
        Assert.True(more);
        Assert.Equal(ids.Reverse().Take(3), page.Select(x => x.Item.Id));
        var (rest, more2) = album.Page(page[^1].Item.Id, 3, null);
        Assert.False(more2);
        Assert.Equal(2, rest.Count);

        Assert.Equal((1, true), album.Like(ids[0], "оля"));
        Assert.Equal((2, true), album.Like(ids[0], "петро"));
        Assert.Equal((1, false), album.Like(ids[0], "оля"));
        Assert.Null(album.Like(999, "оля"));
        Assert.True(album.Page(0, 30, "петро").Items.Single(x => x.Item.Id == ids[0]).Liked);

        Assert.True(album.Delete(ids[0]));
        Assert.False(album.Delete(ids[0]));
        Assert.Equal(4, album.Count);
    }

    [Fact]
    public void Album_lives_in_the_database_with_likes_and_trims_the_oldest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dt-album-{Guid.NewGuid():N}.db");
        try
        {
            var album = new DotepyAlbum(new Db(path));
            var id = album.Add("Що Оля робить уночі", "Рахує черепки", "Петро", "Оля", 300, DateTimeOffset.UtcNow);
            Assert.Equal((1, true), album.Like(id, "ганна"));
            Assert.Equal((0, false), album.Like(id, "ганна"));   // зняла ❤

            var again = new DotepyAlbum(new Db(path));   // рестарт
            var (items, _) = again.Page(0, 10, "ганна");
            var got = Assert.Single(items);
            Assert.Equal("Рахує черепки", got.Item.Text);
            Assert.Equal(300, got.Item.Points);
            Assert.Equal(0, got.Item.Likes);
            Assert.False(got.Liked);
            Assert.Equal((1, true), again.Like(id, "ганна"));
            Assert.True(again.Page(0, 10, "ганна").Items[0].Liked);
            Assert.True(again.Delete(id));
            Assert.Equal(0, again.Count);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    [Fact]
    public void Real_bank_has_the_new_themes_with_enough_prompts_and_well_formed_templates()
    {
        var bank = DotepyBank.All;
        foreach (var (key, _, tags) in Dotepy.Themes)
            Assert.True(bank.Count(p => p.Tags.Any(tags.Contains)) >= 25, key);
        var nas = bank.Where(p => p.Tags.Contains(Dotepy.TagNas)).ToList();
        Assert.InRange(nas.Count, 30, 60);
        Assert.All(nas, p => Assert.Equal(1, p.Text.Split(Dotepy.NickSlot).Length - 1));
        Assert.True(nas.Count(p => p.Final) >= 4);
        var rebus = bank.Where(p => p.Tags.Contains(Dotepy.TagRebus)).ToList();
        Assert.True(rebus.Count >= 30);
        Assert.All(rebus, p => Assert.Contains(p.Text, c => char.IsSurrogate(c)));
        Assert.DoesNotContain(bank.Where(p => !p.Tags.Contains(Dotepy.TagNas)), p => p.Text.Contains('{'));
    }
}
