using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Модерація Балачок (ChatModeration.cs): 🗑 прибрати репліку, 🧹 усе за людиною, 🔇 не пише, 🚫 без файлів (людині й
/// усім), 🐢 повільний режим, 📌 закріплене — і що все це переживає перезапуск.
/// </summary>
public sealed class ChatModerationTests : IDisposable
{
    const string Admin = "Влад";
    const string Ip = "93.170.1.2";

    readonly TempDb _t = new();
    readonly FakeClock _clock = new();   // 10.09.2026 12:00 UTC = 15:00 у Києві
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hlechyky-chatmod-" + Guid.NewGuid().ToString("N"));
    readonly ChatFiles _files;
    ChatModeration _mod;

    public ChatModerationTests()
    {
        _files = new ChatFiles(_t.Db, new ChatFlood(), _clock, new ChatFilesDir(_dir, 1L << 40));
        _mod = Fresh();
    }

    ChatModeration Fresh() => new(new ChatModStore(_t.Db), _t.Db, _clock, _files);

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* хай лежить у temp */ }
    }

    static byte[] Png()
    {
        var b = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 0, 9, 0, 0, 0, 9 }.CopyTo(b, 0);
        return b;
    }

    async Task<ChatMessage> Picture(string nick = "Оля")
    {
        var data = Png();
        var r = await _files.UploadAsync(nick, true, "фото.png", "", null, data.Length, new MemoryStream(data), CancellationToken.None);
        Assert.True(r.Ok, r.Message);
        return r.Line!;
    }

    // ---------- 🗑 і 🧹 ----------

    [Fact]
    public void A_deleted_message_disappears_from_history_and_everyone_is_told_its_id()
    {
        var keep = _t.Db.AddChat("Оля", "привіт", "chat");
        var bad = _t.Db.AddChat("Петро", "щось погане", "chat");

        var r = _mod.Delete(Admin, bad.Id);

        Assert.True(r.Ok);
        Assert.Equal([bad.Id], r.Deleted);
        Assert.Null(r.Line);                                   // прибирають тихо: рядка в Балачки нема
        Assert.Equal([keep.Id], _t.Db.RecentChat(10, 0).Select(m => m.Id));
        Assert.DoesNotContain(_t.Db.ChatBefore(bad.Id + 1, 10, log: false), m => m.Id == bad.Id);
        Assert.False(_mod.Delete(Admin, bad.Id).Ok);          // двічі не прибереш
        Assert.Contains("прибрав повідомлення Петра: «щось погане»", _mod.Overview().Log[0].Text);
    }

    [Fact]
    public void A_reply_to_a_deleted_message_quotes_it_as_deleted_and_it_can_no_longer_be_liked_or_answered()
    {
        var bad = _t.Db.AddChat("Петро", "щось погане", "chat");
        var re = _t.Db.AddChat("Оля", "фу", "chat", replyTo: bad.Id);
        _mod.Delete(Admin, bad.Id);

        var back = _t.Db.RecentChat(10, 0).Single(m => m.Id == re.Id);
        Assert.Equal(Db.DeletedQuote, back.ReplyText);
        Assert.Null(_t.Db.ToggleChatLike(bad.Id, "Оля"));
        Assert.Null(_t.Db.AddChat("Ганна", "що там було?", "chat", replyTo: bad.Id).ReplyTo);
    }

    [Fact]
    public void The_journal_cannot_be_deleted_from_the_chat()
    {
        var log = _t.Db.AddChat("Глечики", "Оля сіла грати", "system");
        Assert.False(_mod.Delete(Admin, log.Id).Ok);
    }

    [Fact]
    public async Task Deleting_a_file_message_wipes_the_file_unless_someone_else_threw_the_same_file()
    {
        var first = await Picture("Оля");
        var same = await Picture("Петро");                    // той самий файл — одна копія на диску
        var hash = first.File!.Hash;

        _mod.Delete(Admin, first.Id);
        Assert.NotNull(_files.Resolve(hash));                  // у Петра він ще живий

        _mod.Delete(Admin, same.Id);
        Assert.Null(_files.Resolve(hash));
    }

    [Fact]
    public void Clean_removes_only_that_persons_lines_within_the_period()
    {
        var old = _t.Db.AddChat("Петро", "давнє", "chat");
        _t.Db.With(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE chat SET created_at = $at WHERE id = $id";
            cmd.Parameters.AddWithValue("$at", _clock.UtcNow.AddHours(-5).ToString("o"));
            cmd.Parameters.AddWithValue("$id", old.Id);
            return cmd.ExecuteNonQuery();
        });
        var fresh1 = _t.Db.AddChat("Петро", "спам 1", "chat");
        var fresh2 = _t.Db.AddChat("петро", "спам 2", "dice");   // нік без регістру — та сама людина
        var other = _t.Db.AddChat("Оля", "я тут ні до чого", "chat");

        var r = _mod.Clean(Admin, "ПЕТРО", hours: 1);

        Assert.True(r.Ok);
        Assert.Equal([fresh1.Id, fresh2.Id], r.Deleted);
        Assert.Equal("Прибрано 2 повідомлення", r.Message);
        Assert.Equal([old.Id, other.Id], _t.Db.RecentChat(10, 0).Select(m => m.Id));

        Assert.Equal([old.Id], _mod.Clean(Admin, "Петро", hours: 0).Deleted);   // 0 — за весь час
        Assert.False(_mod.Clean(Admin, "Петро", hours: 0).Ok);                  // більше нема чого
    }

    [Fact]
    public void Clean_leaves_the_admins_own_moderation_lines()
    {
        _mod.Limit("Петро", "Оля", ChatModeration.Mute, 60);   // хай навіть «Петро» — адмін, його рядок 🔇 лишиться
        Assert.False(_mod.Clean(Admin, "Петро", hours: 0).Ok);
    }

    // ---------- 🔇 не пише ----------

    [Fact]
    public void A_muted_person_cannot_write_until_the_time_runs_out()
    {
        var r = _mod.Limit(Admin, "Оля", ChatModeration.Mute, 60);

        Assert.True(r.Ok);
        Assert.True(r.Changed);
        Assert.Equal("🔇 Олі заборонено писати на годину", r.Line!.Text);
        Assert.Equal("mod", r.Line.Kind);
        Assert.Equal("🔇 Адмін заборонив тобі писати до 16:00", _mod.WriteRefusal("оля", account: true, Ip));
        Assert.Null(_mod.WriteRefusal("Петро", account: true, Ip));
        Assert.Null(_mod.MediaRefusal("Оля", account: true, Ip));   // 🔇 — не 🚫: файли перевіряє окремо хаб

        var view = Assert.Single(_mod.State().Limits);
        Assert.Equal(("mute", "Оля"), (view.Kind, view.Nick));

        _clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Null(_mod.WriteRefusal("Оля", account: true, Ip));
        Assert.Empty(_mod.State().Limits);
    }

    [Fact]
    public void Forever_lasts_until_lifted()
    {
        Assert.Equal("🔇 Олі заборонено писати — поки адмін не зніме", _mod.Limit(Admin, "Оля", ChatModeration.Mute, 0).Line!.Text);
        _clock.Advance(TimeSpan.FromDays(400));
        Assert.Equal("🔇 Адмін заборонив тобі писати — поки не зніме", _mod.WriteRefusal("Оля", true, null));

        var lifted = _mod.Lift(Admin, "оля", ChatModeration.Mute);
        Assert.True(lifted.Ok);
        Assert.Equal("🔊 Олі знову можна писати", lifted.Line!.Text);
        Assert.Null(_mod.WriteRefusal("Оля", true, null));
        Assert.False(_mod.Lift(Admin, "Оля", ChatModeration.Mute).Ok);
    }

    [Fact]
    public void A_new_limit_replaces_the_old_one_of_the_same_kind()
    {
        _mod.Limit(Admin, "Оля", ChatModeration.Mute, 0);
        _mod.Limit(Admin, "Оля", ChatModeration.Mute, 10);
        Assert.Equal("10 хв", ChatModeration.Span(10));
        Assert.Single(_mod.State().Limits);
        _clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Null(_mod.WriteRefusal("Оля", true, null));
        Assert.Null(Fresh().WriteRefusal("Оля", true, null));   // і в базі стара «назавжди» вже не чинна
    }

    [Fact]
    public void Limits_and_switches_survive_a_restart()
    {
        _t.Db.AddChat("Оля", "читайте правила", "chat");
        var pin = _t.Db.AddChat("Оля", "📜 правила", "chat");
        _mod.Limit(Admin, "Петро", ChatModeration.Mute, 60);
        _mod.Limit(Admin, "Ганна", ChatModeration.Media, 0);
        _mod.MediaAll(Admin, 30);
        _mod.Slow(Admin, 30, 0);
        _mod.Pin(Admin, pin.Id);

        _mod = Fresh();

        Assert.NotNull(_mod.WriteRefusal("Петро", true, null));
        Assert.StartsWith("🚫 Адмін заборонив тобі кидати файли", _mod.MediaRefusal("Ганна", true, null));
        var s = _mod.State();
        Assert.Equal(pin.Id, s.Pinned!.Id);
        Assert.Equal(30, s.SlowSec);
        Assert.Null(s.SlowUntil);
        Assert.True(s.MediaOff);
        Assert.Equal(2, s.Limits.Count);
    }

    [Fact]
    public void A_guest_is_also_caught_by_address_but_an_account_on_the_same_address_is_not()
    {
        _mod.NoteIp("гість Вася", Ip);
        _mod.Limit(Admin, "гість Вася", ChatModeration.Mute, 60);

        Assert.NotNull(_mod.WriteRefusal("гість Петя", account: false, Ip));   // перейменувався — та сама адреса
        Assert.Null(_mod.WriteRefusal("гість Петя", account: false, "10.0.0.7"));
        Assert.Null(_mod.WriteRefusal("Оля", account: true, Ip));              // друг з акаунтом за тим самим роутером
        Assert.Single(_mod.Overview().Limits, l => l.Ip == Ip);
    }

    [Fact]
    public void The_address_is_learned_later_if_the_person_was_offline_when_limited()
    {
        _mod.Limit(Admin, "гість Вася", ChatModeration.Mute, 60);
        Assert.Null(_mod.WriteRefusal("гість Петя", false, Ip));

        _mod.NoteIp("ГІСТЬ ВАСЯ", Ip);
        Assert.NotNull(_mod.WriteRefusal("гість Петя", false, Ip));
        Assert.NotNull(Fresh().WriteRefusal("гість Петя", false, Ip));   // адреса лягла й у базу
    }

    [Fact]
    public void Loopback_is_not_an_address_to_ban_by()
    {
        _mod.NoteIp("гість Вася", "127.0.0.1");
        _mod.Limit(Admin, "гість Вася", ChatModeration.Mute, 60);
        Assert.Null(_mod.WriteRefusal("гість Петя", false, "127.0.0.1"));
    }

    [Fact]
    public void The_admin_cannot_limit_themselves_and_unknown_kinds_are_refused()
    {
        Assert.Equal("Себе не обмежиш", _mod.Limit(Admin, "влад", ChatModeration.Mute, 60).Message);
        Assert.False(_mod.Limit(Admin, "Оля", "ban", 60).Ok);
        Assert.False(_mod.Limit(Admin, "  ", ChatModeration.Mute, 60).Ok);
    }

    // ---------- 🚫 файли ----------

    [Fact]
    public void Media_can_be_closed_for_one_person_or_for_everyone()
    {
        Assert.Equal("🚫 Олі заборонено кидати файли на добу", _mod.Limit(Admin, "Оля", ChatModeration.Media, 1440).Line!.Text);
        Assert.NotNull(_mod.MediaRefusal("Оля", true, Ip));
        Assert.Null(_mod.WriteRefusal("Оля", true, Ip));                       // писати можна
        Assert.Null(_mod.MediaRefusal("Петро", true, Ip));

        var all = _mod.MediaAll(Admin, 60);
        Assert.Equal("🚫 Файли в Балачках вимкнено на годину", all.Line!.Text);
        Assert.Equal("🚫 Файли в Балачках вимкнено до 16:00", _mod.MediaRefusal("Петро", true, Ip));
        Assert.True(_mod.State().MediaOff);

        Assert.Equal("📎 Файли в Балачках знову можна", _mod.MediaAll(Admin, -1).Line!.Text);
        Assert.Null(_mod.MediaRefusal("Петро", true, Ip));
        Assert.False(_mod.MediaAll(Admin, -1).Ok);

        _mod.MediaAll(Admin, 10);
        _clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Null(_mod.MediaRefusal("Петро", true, Ip));
        Assert.False(_mod.State().MediaOff);
    }

    // ---------- 🐢 повільний режим ----------

    [Fact]
    public void Slow_mode_lets_one_message_through_per_interval()
    {
        Assert.Equal("🐢 Повільний режим: одне повідомлення на 30 с — на годину", _mod.Slow(Admin, 30, 60).Line!.Text);

        Assert.Null(_mod.SlowRefusal("Оля"));
        _mod.NoteSaid("Оля");
        _clock.Advance(TimeSpan.FromSeconds(18));
        Assert.Equal("🐢 Повільний режим: наступне — через 12 с", _mod.SlowRefusal("оля"));
        Assert.Null(_mod.SlowRefusal("Петро"));
        _clock.Advance(TimeSpan.FromSeconds(12));
        Assert.Null(_mod.SlowRefusal("Оля"));

        _mod.NoteSaid("Оля");
        _clock.Advance(TimeSpan.FromMinutes(60));               // строк вийшов — режим сам вимкнувся
        _mod.NoteSaid("Оля");
        Assert.Null(_mod.SlowRefusal("Оля"));
        Assert.Equal(0, _mod.State().SlowSec);
    }

    [Fact]
    public void Slow_mode_interval_is_kept_within_bounds_and_can_be_switched_off()
    {
        _mod.Slow(Admin, 1, 0);
        Assert.Equal(ChatModeration.SlowMin, _mod.State().SlowSec);
        Assert.Equal("🐇 Повільний режим вимкнено — пишіть скільки влізе", _mod.Slow(Admin, 0, 0).Line!.Text);
        Assert.Equal(0, _mod.State().SlowSec);
        Assert.False(_mod.Slow(Admin, 0, 0).Ok);
    }

    // ---------- 📌 ----------

    [Fact]
    public void Pinning_shows_the_message_to_everyone_and_deleting_it_unpins()
    {
        var m = _t.Db.AddChat("Оля", "📜 правила балачок", "chat");

        var r = _mod.Pin(Admin, m.Id);
        Assert.True(r.Ok);
        Assert.True(r.Changed);
        Assert.Null(r.Line);
        Assert.Equal("📜 правила балачок", _mod.State().Pinned!.Text);

        var gone = _mod.Delete(Admin, m.Id);
        Assert.True(gone.Changed);
        Assert.Null(_mod.State().Pinned);
        Assert.Null(Fresh().State().Pinned);
    }

    [Fact]
    public void Unpin_and_pinning_what_is_gone()
    {
        Assert.False(_mod.Pin(Admin, 0).Ok);
        Assert.False(_mod.Pin(Admin, 424242).Ok);
        var m = _t.Db.AddChat("Оля", "раз", "chat");
        _mod.Pin(Admin, m.Id);
        Assert.True(_mod.Pin(Admin, 0).Ok);
        Assert.Null(_mod.State().Pinned);
    }

    // ---------- слова ----------

    [Fact]
    public void Spans_and_deadlines_read_naturally()
    {
        Assert.Equal("годину", ChatModeration.Span(60));
        Assert.Equal("3 год", ChatModeration.Span(180));
        Assert.Equal("добу", ChatModeration.Span(1440));
        Assert.Equal("тиждень", ChatModeration.Span(10080));
        Assert.Equal("2 дн.", ChatModeration.Span(2880));

        var now = _clock.UtcNow;                                // 15:00 у Києві
        Assert.Equal("до 15:10", ChatModeration.UntilText(now.AddMinutes(10), now, "x"));
        Assert.Equal("до 11.09 15:00", ChatModeration.UntilText(now.AddDays(1), now, "x"));
        Assert.Equal("— поки не зніме", ChatModeration.UntilText(null, now, "поки не зніме"));
    }
}
