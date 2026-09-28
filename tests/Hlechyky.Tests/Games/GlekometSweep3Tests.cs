using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Глекомети, прохід №3: емоції над хатою, підсумок серії, снаряди-приколи, погода й мапи, «Залп».</summary>
public partial class GlekometTests
{
    // =============================================================================================
    // 100. Емоції над хатою
    // =============================================================================================

    [Fact]
    public void Emotion_flies_in_a_frame_to_everybody_and_is_throttled()
    {
        var h = Table(3);
        Ready(h);
        var other = (Game(h).Turn + 1) % 3;
        var mark = h.Outbox.Count;
        h.Input(other, "emo", new { e = 2 });
        h.Tick(1);
        var em = Frames(h, mark).Single(f => Views.Has(f, "em")).GetProperty("em");
        Assert.Equal($"[[{other},2]]", em.GetRawText());

        mark = h.Outbox.Count;
        h.Input(other, "emo", new { e = 1 });                            // одразу вдруге — мовчки ні
        h.Tick(1);
        Assert.DoesNotContain(Frames(h, mark), f => Views.Has(f, "em"));
        h.Tick(Glekomet.EmoGap);
        mark = h.Outbox.Count;
        h.Input(other, "emo", 1);                                        // голе число теж
        h.Tick(1);
        Assert.Contains(Frames(h, mark), f => Views.Has(f, "em"));
    }

    [Fact]
    public void Emotion_is_refused_for_bad_payload_and_does_not_touch_the_game()
    {
        var h = Table(2);
        Ready(h);
        var before = V(h).GetRawText();
        Assert.False(h.Act(1, "emo", new { e = 9 }).Ok);
        Assert.False(h.Act(1, "emo", new { x = 1 }).Ok);
        Assert.True(h.Act(1, "emo", new { e = 0 }).Ok);
        Assert.Equal(before, V(h).GetRawText());                         // вид той самий: емоція лише в кадрі
    }

    // =============================================================================================
    // 104. Підсумок серії
    // =============================================================================================

    [Fact]
    public void Series_sums_damage_and_keeps_the_best_shot_across_rematches()
    {
        var h = Table(2);
        Ready(h);
        Assert.Equal(JsonValueKind.Null, V(h).GetProperty("series").ValueKind);
        Flatten(h, 100, (0, 200), (1, 700));
        Core(h).Huts[1].Hp = 30;
        HitHut(h, 0, 1);                                                 // Оля розбиває Петра: 30
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var s = V(h).GetProperty("series");
        Assert.Equal(1, s.GetProperty("games").GetInt32());
        Assert.Equal(30, s.GetProperty("rows")[0].GetProperty("dmg").GetInt32());
        Assert.Equal("Оля", s.GetProperty("best").GetProperty("nick").GetString());

        Assert.True(h.Rematch().Ok, h.Reply.Message);                   // місця обернулись: Оля — 1
        Ready(h);
        Flatten(h, 100, (0, 200), (1, 700));
        Core(h).Huts[1].Hp = 20;
        if (Game(h).Turn != 0) ShootAway(h);
        HitHut(h, 0, 1);                                                 // Петро розбиває Олю: 20
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        s = V(h).GetProperty("series");
        Assert.Equal(2, s.GetProperty("games").GetInt32());
        Assert.Equal(30, s.GetProperty("rows")[1].GetProperty("dmg").GetInt32());   // Оля — уже на місці 1
        Assert.Equal(20, s.GetProperty("rows")[0].GetProperty("dmg").GetInt32());
        Assert.Equal(30, s.GetProperty("best").GetProperty("dmg").GetInt32());
    }
}
