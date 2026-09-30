namespace Hlechyky.Tests.Platform;

/// <summary>
/// Що потрапляє в запаску ефіру — список треків із кешу, який liquidsoap крутить сам, коли нове не вантажиться.
/// Спершу те, що люблять (❤ або грало ≥ 3 разів), а мало такого — докидається те, що хоч раз грало.
/// </summary>
public class SparePlanTests
{
    static SparePlan.Candidate C(string id, int plays = 0, int likes = 0) => new(id, $"cache/{id}.m4a", plays, likes);

    [Fact]
    public void Liked_and_often_played_tracks_come_first_the_most_liked_on_top()
    {
        var picked = SparePlan.Pick([C("once", plays: 1), C("often", plays: 5), C("liked", likes: 2), C("liked1", likes: 1, plays: 9)], 3);

        Assert.Equal(["liked", "liked1", "often"], picked.Select(c => c.TrackId));
    }

    [Fact]
    public void Few_favourites_are_topped_up_with_what_played_at_least_once_but_never_with_the_unplayed()
    {
        var picked = SparePlan.Pick([C("fav", likes: 1), C("twice", plays: 2), C("once", plays: 1), C("never")], 100);

        Assert.Equal(["fav", "twice", "once"], picked.Select(c => c.TrackId));
    }

    [Fact]
    public void Enough_favourites_leave_the_rest_out()
    {
        var favs = Enumerable.Range(0, SparePlan.MinGood).Select(i => C($"f{i:D2}", plays: 3));
        var picked = SparePlan.Pick([.. favs, C("once", plays: 1)], 1000);

        Assert.Equal(SparePlan.MinGood, picked.Count);
        Assert.DoesNotContain(picked, c => c.TrackId == "once");
    }

    [Fact]
    public void The_list_is_capped_and_a_track_is_listed_once()
    {
        var picked = SparePlan.Pick([C("a", likes: 1), C("a", likes: 1), C("b", likes: 1), C("c", likes: 1)], 2);

        Assert.Equal(2, picked.Count);
        Assert.Equal(picked.Count, picked.Select(c => c.TrackId).Distinct().Count());
    }
}
