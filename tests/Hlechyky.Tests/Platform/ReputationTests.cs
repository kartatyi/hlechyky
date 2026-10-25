using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Етап 3 поліпшення порад Глека (25.10): репутація з наших програвань і обережність з новими артистами. Бектест на
/// бойовій копії (усе до 10.10 — навчання, після — перевірка, «живі» програвання): AUC «скіпнуть чи ні» в межах того
/// самого складу слухачів 0,675 (етап 2) → 0,707 з репутацією → 0,720 ще й з новизною; частка скіпів серед верхньої
/// третини за множником 19,8 % → 14,2 %.
/// </summary>
public class ReputationTests
{
    static IEnumerable<(string, string, bool)> Heard(string artist, string title, int plays, int skips) =>
        Enumerable.Range(0, plays).Select(i => (artist, title, i < skips));

    /// <summary>Тло: 200 звичайних програвань з часткою скіпів 10 %, щоб було від чого міряти.</summary>
    static IEnumerable<(string, string, bool)> Background() =>
        Enumerable.Range(0, 200).Select(i => ($"Артист {i % 40}", $"Пісня {i}", i % 10 == 0));

    [Fact]
    public void Song_the_room_keeps_listening_to_goes_up_and_the_one_it_skips_goes_down()
    {
        var r = Reputation.Build(Background().Concat(Heard("Radiohead", "Creep", 40, 0)).Concat(Heard("Alena Omargalieva", "Щось", 10, 7)), []);

        var loved = Reputation.Multiplier(r, "Radiohead", "Creep", null);
        var skipped = Reputation.Multiplier(r, "Alena Omargalieva", "Щось", null);
        var unseen = Reputation.Multiplier(r, "Нова Група", "Перша пісня", null);

        Assert.InRange(loved, 1.3, 1.6 * 1.25);
        Assert.InRange(skipped, 0.6 * 0.8, 0.75);
        Assert.Equal(1, unseen, 3);
    }

    [Fact]
    public void One_unlucky_skip_does_not_bury_a_song()
    {
        var r = Reputation.Build(Background().Concat(Heard("Курган & Agregat", "Пісня", 1, 1)).Concat(Heard("Alena Omargalieva", "Щось", 10, 7)), []);
        var once = Reputation.Multiplier(r, "Курган & Agregat", "Пісня", null);
        var often = Reputation.Multiplier(r, "Alena Omargalieva", "Щось", null);
        Assert.True(once > 0.5, $"один скіп ×{once:F2}");
        Assert.True(once > often + 0.05, $"один скіп ×{once:F2} проти семи з десяти ×{often:F2}");
    }

    [Fact]
    public void Artist_reputation_carries_over_to_their_other_songs_but_weaker()
    {
        var r = Reputation.Build(Background().Concat(Heard("Zwyntar", "Потяг", 20, 0)).Concat(Heard("Zwyntar", "Інша", 20, 0)), []);
        var newSong = Reputation.Multiplier(r, "Zwyntar", "Ще не грала", null);
        var old = Reputation.Multiplier(r, "Zwyntar", "Потяг", null);
        Assert.InRange(newSong, 1.05, 1.25);
        Assert.True(old > newSong);
    }

    [Fact]
    public void Liked_by_someone_listening_is_lifted_most()
    {
        var r = Reputation.Build(Background(), [("Jerry Heil", "Охрана, отмєна", "Smaug")]);

        Assert.Equal(1.5, Reputation.Multiplier(r, "Jerry Heil", "Охрана, отмєна", ["smaug", "владік"]), 3);
        Assert.Equal(1, Reputation.Multiplier(r, "Jerry Heil", "Охрана, отмєна", ["владік"]), 3);   // Smaug нема — це не його лайк
        Assert.Equal(1.25, Reputation.Multiplier(r, "Jerry Heil", "Охрана, отмєна", null), 3);   // нікого — лайк кімнати
    }

    [Fact]
    public void Uploader_channel_plays_count_for_the_real_artist()
    {
        var r = Reputation.Build(Background().Concat(Heard("jimmej955", "Bob Seger- Turn the Page", 15, 0)), []);
        Assert.True(Reputation.Multiplier(r, "Bob Seger", "Turn the Page", null) > 1.2);
    }

    [Fact]
    public void Empty_history_changes_nothing() =>
        Assert.Equal(1, Reputation.Multiplier(Reputation.Table.Empty, "Radiohead", "Creep", ["владік"]), 3);

    [Fact]
    public void Heard_plays_are_only_those_someone_listened_to()
    {
        using var db = new TempDb();
        var t = new TrackInfo("uCreep00001", "Creep", "Radiohead", 238, null, "https://music.youtube.com/watch?v=uCreep00001", null);
        db.Db.UpsertTrack(t);
        var heard = db.Db.StartPlay(t.Id, "autodj", null, null, null);
        db.Db.NotePlayListeners(heard, 1, ["владік"]);
        db.Db.EndPlay(heard, skipped: true, by: "владік");
        db.Db.EndPlay(db.Db.StartPlay(t.Id, "autodj", null, null, null), skipped: false); // у порожню кімнату

        var row = Assert.Single(db.Db.HeardPlays(DateTimeOffset.UtcNow.AddHours(-1)));
        Assert.Equal(("Radiohead", "Creep", true), row);
    }

    // ---------- нові артисти ----------

    static readonly AutoDjOptions O = new();

    [Theory]
    [InlineData(true, "cyr", "cyr", 1, 1.0)]     // кімната знає — без питань
    [InlineData(false, "lat", "lat", 1, 1.0)]    // новий латиничний від латиничного — як було
    [InlineData(false, "cyr", "cyr", 1, 0.6)]    // новий кириличний — найчастіше мимо (56 % скіпів)
    [InlineData(false, "cyr", "cyr", 2, 0.85)]   // але двоє джерел збіглися — довіряємо більше
    [InlineData(false, "cyr", "lat", 1, 0.5)]    // стрибок на інше письмо з новим артистом
    [InlineData(false, "lat", "cyr", 1, 0.3)]    // і те, і те
    [InlineData(false, "", "cyr", 1, 0.6)]       // письма сіда не видно — лише кирилиця
    public void New_artists_are_trusted_less_and_jumps_across_scripts_even_less(bool known, string seed, string cand, int sources, double expected) =>
        Assert.Equal(expected, AutoDj.Novelty(known, seed, cand, sources, O), 3);
}
