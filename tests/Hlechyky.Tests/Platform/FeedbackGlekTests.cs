using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Дядько Глек у записках «💡 Розробнику»: розробник пише від його імені й дає автору черепки за баг чи добру ідею.
/// Лише розробник, лише в межах <see cref="Feedback.MaxGift"/>, без подвійного подарунка на подвійний клік.
/// </summary>
public sealed class FeedbackGlekTests : IDisposable
{
    readonly TempDb _db = new();
    readonly FakeClock _clock = new();
    readonly FakeStakes _purse = new();
    readonly List<(string Nick, int Unread)> _authors = [];
    readonly Feedback _fb;

    public FeedbackGlekTests() => _fb = new Feedback(new FeedbackStore(_db.Db), _clock, new Wire(_authors), _purse);

    public void Dispose() => _db.Dispose();

    sealed class Wire(List<(string, int)> authors) : IFeedbackWire
    {
        public void Author(string nick, int unread) => authors.Add((nick, unread));
        public void Dev(FeedbackCount count) { }
    }

    long Note(string nick = "Smaug", string kind = "bug", string text = "Збирач не рахує Цзиндечжень") =>
        _fb.Submit(nick, kind, text, null, null, null).Id;

    IReadOnlyList<FeedbackMsg> Msgs(long id) => _fb.One(id)!.Msgs;

    [Fact]
    public void Developer_writes_as_Glek_and_the_note_shows_him()
    {
        var id = Note();
        Assert.True(_fb.Say(id, "владік", true, "дякую, пофіксимо", glek: true).Ok);
        var m = Assert.Single(Msgs(id));
        Assert.True(m.Dev);
        Assert.Equal(Feedback.Glek, m.Nick);
        Assert.Equal("text", m.Kind);
        Assert.Equal(1, _fb.Unread("Smaug"));
        Assert.Equal("seen", _fb.One(id)!.Item.Status);
    }

    [Fact]
    public void Glek_gives_shards_to_the_author_with_a_line_in_the_thread()
    {
        var id = Note();
        var r = _fb.Say(id, "владік", true, "тримай за баг", glek: true, shards: 250);
        Assert.True(r.Ok, r.Message);
        Assert.Contains("+250", r.Message);
        Assert.Equal(250, _purse.Balance("Smaug"));
        Assert.Equal($"{Feedback.GiftReason}:{id}", Assert.Single(_purse.Reasons.Values));
        var msgs = Msgs(id);
        Assert.Equal(["text", "shards"], msgs.Select(x => x.Kind));
        Assert.All(msgs, x => Assert.Equal(Feedback.Glek, x.Nick));
        Assert.Equal("250", msgs[1].Text);
        Assert.Contains(_authors, a => a.Nick == "Smaug" && a.Unread == 1);
    }

    [Fact]
    public void Shards_alone_without_words_are_a_reply_too()
    {
        var id = Note();
        Assert.True(_fb.Say(id, "владік", true, "", shards: 100).Ok);
        var m = Assert.Single(Msgs(id));
        Assert.Equal("shards", m.Kind);
        Assert.Equal(Feedback.Glek, m.Nick);   // черепки дає завжди Глек, хоч би відповідь була й від розробника
        Assert.Equal(100, _purse.Balance("Smaug"));
    }

    [Fact]
    public void Double_click_does_not_pay_twice()
    {
        var id = Note();
        Assert.True(_fb.Say(id, "владік", true, null, shards: 300).Ok);
        _clock.Advance(TimeSpan.FromSeconds(2));
        var again = _fb.Say(id, "владік", true, null, shards: 300);
        Assert.False(again.Ok);
        Assert.Equal(300, _purse.Balance("Smaug"));
        Assert.Single(Msgs(id));
        // а свідомий другий подарунок згодом — можна
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True(_fb.Say(id, "владік", true, null, shards: 300).Ok);
        Assert.Equal(600, _purse.Balance("Smaug"));
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(Feedback.MaxGift + 1)]
    public void Gift_out_of_bounds_is_refused_and_nothing_is_written(int shards)
    {
        var id = Note();
        var r = _fb.Say(id, "владік", true, "тримай", glek: true, shards: shards);
        Assert.False(r.Ok);
        Assert.Empty(Msgs(id));
        Assert.Equal(0, _purse.Balance("Smaug"));
    }

    [Fact]
    public void Author_cannot_speak_as_Glek_or_pay_himself()
    {
        var id = Note();
        Assert.False(_fb.Say(id, "Smaug", false, "я Глек", glek: true).Ok);
        Assert.False(_fb.Say(id, "Smaug", false, "собі", shards: 500).Ok);
        Assert.Empty(Msgs(id));
        Assert.Equal(0, _purse.Balance("Smaug"));
    }

    [Fact]
    public void Without_wallets_shards_are_refused_but_words_go()
    {
        var fb = new Feedback(new FeedbackStore(_db.Db), _clock);
        var id = fb.Submit("Вовочка", "idea", "Колесо зі ставками", null, null, null).Id;
        Assert.False(fb.Say(id, "владік", true, "тримай", glek: true, shards: 100).Ok);
        Assert.Empty(fb.One(id)!.Msgs);
        Assert.True(fb.Say(id, "владік", true, "тримай", glek: true).Ok);
    }
}
