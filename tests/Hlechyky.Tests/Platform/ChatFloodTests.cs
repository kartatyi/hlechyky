namespace Hlechyky.Tests.Platform;

/// <summary>Захист від флуду: п'ять повідомлень за п'ять секунд і без однакового поспіль.</summary>
public class ChatFloodTests
{
    static readonly DateTimeOffset T0 = new(2026, 9, 23, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Five_messages_in_a_burst_pass_and_the_sixth_waits()
    {
        var f = new ChatFlood();
        for (var i = 0; i < ChatFlood.Burst; i++) Assert.Null(f.Check("Оля", $"репліка {i}", T0.AddMilliseconds(i * 100)));

        Assert.Equal(ChatFlood.TooFast, f.Check("Оля", "і ще одна", T0.AddSeconds(1)));
        Assert.Null(f.Check("Оля", "і ще одна", T0.Add(ChatFlood.Window).AddMilliseconds(1)));
    }

    [Fact]
    public void The_same_line_twice_in_a_row_is_refused_but_not_forever()
    {
        var f = new ChatFlood();
        Assert.Null(f.Check("Оля", "Привіт", T0));

        Assert.Equal(ChatFlood.Repeat, f.Check("Оля", "  привіт ", T0.AddSeconds(10)));
        Assert.Null(f.Check("Оля", "привіт", T0.Add(ChatFlood.RepeatWindow)));
    }

    [Fact]
    public void The_same_line_in_another_chat_is_not_a_repeat()
    {
        var f = new ChatFlood();
        Assert.Null(f.Check("Оля", "я за Петра", T0, "table:abc"));
        Assert.Null(f.Check("Оля", "я за Петра", T0.AddSeconds(6), "chat"));
        Assert.Equal(ChatFlood.Repeat, f.Check("Оля", "я за Петра", T0.AddSeconds(12), "table:abc"));
    }

    [Fact]
    public void Short_answers_can_be_given_twice_in_a_row()
    {
        var f = new ChatFlood();
        Assert.Null(f.Check("Оля", "так", T0));
        Assert.Null(f.Check("Оля", "так", T0.AddSeconds(3)));
        Assert.Null(f.Check("Оля", "+", T0.AddSeconds(6)));
        Assert.Null(f.Check("Оля", "+", T0.AddSeconds(9)));
    }

    [Fact]
    public void Dice_can_be_rolled_twice_in_a_row()
    {
        var f = new ChatFlood();
        Assert.Null(f.Check("Оля", "/кубик", T0));
        Assert.Null(f.Check("Оля", "/кубик", T0.AddSeconds(2)));
    }

    [Fact]
    public void Nicks_do_not_share_the_limit_and_case_does_not_make_a_new_nick()
    {
        var f = new ChatFlood();
        for (var i = 0; i < ChatFlood.Burst; i++) Assert.Null(f.Check("Оля", $"р{i}", T0));

        Assert.Null(f.Check("Петро", "а я тільки зайшов", T0));
        Assert.Equal(ChatFlood.TooFast, f.Check("ОЛЯ", "це теж я", T0));
    }

    [Fact]
    public void A_refused_line_does_not_count_against_the_next_window()
    {
        var f = new ChatFlood();
        for (var i = 0; i < ChatFlood.Burst; i++) f.Check("Оля", $"р{i}", T0);
        for (var i = 0; i < 20; i++) f.Check("Оля", $"спам {i}", T0.AddSeconds(1));

        Assert.Null(f.Check("Оля", "вже можна?", T0.Add(ChatFlood.Window).AddSeconds(1)));
    }
}
