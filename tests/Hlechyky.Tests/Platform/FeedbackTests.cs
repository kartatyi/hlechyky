using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Platform;

/// <summary>«💡 Розробнику»: хто може написати, що саме, як часто, і як розробник відповідає.</summary>
public sealed class FeedbackTests : IDisposable
{
    readonly TempDb _db = new();
    readonly FakeClock _clock = new();
    readonly Feedback _fb;

    public FeedbackTests() => _fb = new Feedback(new FeedbackStore(_db.Db), _clock);

    public void Dispose() => _db.Dispose();

    [Fact]
    public void A_note_is_kept_and_shows_up_in_mine_as_new()
    {
        var r = _fb.Submit("Оля", "idea", "  Додайте темну тему для балачок  ", "#efir", "1366×768", "Mozilla/5.0");
        Assert.True(r.Ok, r.Message);
        Assert.True(r.Id > 0);

        var mine = _fb.Mine("оля");                        // нік без урахування регістру
        var item = Assert.Single(mine);
        Assert.Equal("Додайте темну тему для балачок", item.Text);   // краї обрізано
        Assert.Equal("idea", item.Kind);
        Assert.Equal("new", item.Status);
        Assert.Equal("#efir", item.Place);
        Assert.Empty(_fb.Mine("Петро"));
    }

    [Fact]
    public void Bug_gets_its_own_thanks()
    {
        Assert.Contains("Баг", _fb.Submit("Оля", "bug", "Кнопка скіпу не реагує", null, null, null).Message);
        Assert.DoesNotContain("Баг", _fb.Submit("Оля", "change", "Хай черга буде ширша", null, null, null).Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("гість")]
    [InlineData("ГІСТЬ")]
    public void Nameless_guest_cannot_write(string nick)
    {
        var r = _fb.Submit(nick, "idea", "Додайте щось класне", null, null, null);
        Assert.False(r.Ok);
        Assert.Contains("назвись", r.Message);
    }

    [Fact]
    public void Named_guest_can_write()
    {
        Assert.True(_fb.Submit("гість Вася", "idea", "Додайте щось класне", null, null, null).Ok);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wish")]
    public void Unknown_kind_is_refused(string? kind)
    {
        Assert.False(_fb.Submit("Оля", kind, "Додайте щось класне", null, null, null).Ok);
    }

    [Fact]
    public void Kind_is_case_insensitive()
    {
        Assert.True(_fb.Submit("Оля", " BUG ", "Кнопка скіпу не реагує", null, null, null).Ok);
        Assert.Equal("bug", _fb.Mine("Оля")[0].Kind);
    }

    [Fact]
    public void Too_short_and_too_long_are_refused()
    {
        Assert.False(_fb.Submit("Оля", "idea", "ой", null, null, null).Ok);
        Assert.False(_fb.Submit("Оля", "idea", "    ", null, null, null).Ok);
        Assert.False(_fb.Submit("Оля", "idea", new string('а', Feedback.MaxText + 1), null, null, null).Ok);
        Assert.True(_fb.Submit("Оля", "idea", new string('а', Feedback.MaxText), null, null, null).Ok);
    }

    [Fact]
    public void Control_characters_go_but_line_breaks_stay()
    {
        Assert.True(_fb.Submit("Оля", "bug", "Що робив:\nнатиснув скіп\u0007\u0000", null, null, null).Ok);
        Assert.Equal("Що робив:\nнатиснув скіп", _fb.Mine("Оля")[0].Text);
    }

    [Fact]
    public void The_same_note_twice_in_half_an_hour_is_a_double_click()
    {
        Assert.True(_fb.Submit("Оля", "idea", "Додайте темну тему", null, null, null).Ok);
        var again = _fb.Submit("Оля", "idea", "Додайте темну тему", null, null, null);
        Assert.False(again.Ok);
        Assert.Contains("вже тяпнуто", again.Message);

        // той самий текст від іншої людини — окрема думка
        Assert.True(_fb.Submit("Петро", "idea", "Додайте темну тему", null, null, null).Ok);

        _clock.Advance(TimeSpan.FromMinutes(31));
        Assert.True(_fb.Submit("Оля", "idea", "Додайте темну тему", null, null, null).Ok);
    }

    [Fact]
    public void Five_notes_an_hour_then_wait()
    {
        for (var i = 0; i < Feedback.PerHour; i++)
            Assert.True(_fb.Submit("Оля", "idea", "Думка номер " + i, null, null, null).Ok);
        var sixth = _fb.Submit("Оля", "idea", "Ще одна думка", null, null, null);
        Assert.False(sixth.Ok);
        Assert.Contains("годину", sixth.Message);
        Assert.True(_fb.Submit("Петро", "idea", "Моя думка", null, null, null).Ok);   // ліміт — на ніка, не на всіх

        _clock.Advance(TimeSpan.FromMinutes(61));
        Assert.True(_fb.Submit("Оля", "idea", "Ще одна думка", null, null, null).Ok);
    }

    [Fact]
    public void Twenty_notes_a_day_then_tomorrow()
    {
        for (var i = 0; i < Feedback.PerDay; i++)
        {
            Assert.True(_fb.Submit("Оля", "idea", "Думка номер " + i, null, null, null).Ok);
            if (i % Feedback.PerHour == Feedback.PerHour - 1) _clock.Advance(TimeSpan.FromMinutes(61));
        }
        var more = _fb.Submit("Оля", "idea", "Ще одна думка", null, null, null);
        Assert.False(more.Ok);
        Assert.Contains("сьогодні", more.Message);

        _clock.Advance(TimeSpan.FromHours(24));
        Assert.True(_fb.Submit("Оля", "idea", "Ще одна думка", null, null, null).Ok);
    }

    [Fact]
    public void Admin_sees_everyone_filtered_by_status_with_counts()
    {
        var a = _fb.Submit("Оля", "idea", "Додайте темну тему", null, null, null).Id;
        _fb.Submit("Петро", "bug", "Скіп не працює", null, null, null);
        _fb.Update(a, "done", null);

        var (all, counts) = _fb.List(null);
        Assert.Equal(2, all.Count);
        Assert.Equal("Петро", all[0].Nick);                 // свіжіші згори
        Assert.Equal(1, counts["new"]);
        Assert.Equal(1, counts["done"]);
        Assert.Equal(1, _fb.NewCount());

        var (done, _) = _fb.List("done");
        Assert.Equal(a, Assert.Single(done).Id);
        var (bogus, _) = _fb.List("whatever");             // невідомий фільтр — усе
        Assert.Equal(2, bogus.Count);
    }

    [Fact]
    public void Update_changes_status_and_an_old_style_reply_becomes_a_message()
    {
        var id = _fb.Submit("Оля", "idea", "Додайте темну тему", null, null, null).Id;
        _clock.Advance(TimeSpan.FromMinutes(5));

        // старий клієнт (кілька хвилин деплою) ще шле reply через PATCH — це просто повідомлення в переписку
        Assert.True(_fb.Update(id, "planned", "  Беремо в наступне оновлення  ", "Влад").Ok);
        var item = _fb.Mine("Оля")[0];
        Assert.Equal("planned", item.Status);
        Assert.Null(item.Reply);                            // старе поле більше не пишеться
        Assert.True(item.UpdatedAt > item.CreatedAt);
        var msgs = _fb.One(id)!.Msgs;
        Assert.Equal(["text:Беремо в наступне оновлення", "status:planned"], msgs.Select(m => m.Kind + ":" + m.Text));
        Assert.All(msgs, m => Assert.True(m.Dev));

        // стан без відповіді — переписка лишається, стан лягає рядком
        Assert.True(_fb.Update(id, "done", null).Ok);
        Assert.Equal(3, _fb.One(id)!.Msgs.Count);

        // порожня відповідь нічого не стирає і нічого не додає
        Assert.True(_fb.Update(id, null, "").Ok);
        Assert.Equal(3, _fb.One(id)!.Msgs.Count);
        Assert.Equal("done", _fb.Mine("Оля")[0].Status);
    }

    [Fact]
    public void Update_refuses_unknown_status_and_missing_note()
    {
        var id = _fb.Submit("Оля", "idea", "Додайте темну тему", null, null, null).Id;
        Assert.False(_fb.Update(id, "maybe", null).Ok);
        Assert.Equal("new", _fb.Mine("Оля")[0].Status);
        Assert.False(_fb.Update(id + 100, "done", null).Ok);
    }

    [Fact]
    public void Long_context_fields_are_cut()
    {
        Assert.True(_fb.Submit("Оля", "bug", "Скіп не працює", new string('#', 500), new string('1', 80), new string('M', 900)).Ok);
        var item = _fb.Mine("Оля")[0];
        Assert.Equal(200, item.Place!.Length);
        Assert.Equal(40, item.Screen!.Length);
        Assert.Equal(300, item.Ua!.Length);
    }
}
