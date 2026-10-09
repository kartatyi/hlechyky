using System.Text.Json.Nodes;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Platform;

/// <summary>Файли й діагностика в записках «💡 Розробнику»: хто кидає, куди прив'язується, межі й прибирання.</summary>
public sealed class FeedbackFilesTests : IDisposable
{
    readonly TempDb _db = new();
    readonly FakeClock _clock = new();
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hlechyky-fbfiles-" + Guid.NewGuid().ToString("N"));
    readonly FeedbackStore _store;
    readonly Feedback _fb;
    readonly FeedbackFiles _files;

    public FeedbackFilesTests()
    {
        _store = new FeedbackStore(_db.Db);
        _fb = new Feedback(_store, _clock);
        _files = new FeedbackFiles(_store, _clock, new FeedbackFilesDir(_dir, 1L << 40));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static byte[] Png(int w, int h, byte salt = 0)
    {
        var b = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
        b[16] = (byte)(w >> 24); b[17] = (byte)(w >> 16); b[18] = (byte)(w >> 8); b[19] = (byte)w;
        b[20] = (byte)(h >> 24); b[21] = (byte)(h >> 16); b[22] = (byte)(h >> 8); b[23] = (byte)h;
        b[63] = salt;
        return b;
    }

    async Task<FeedbackFile> Up(string nick = "Оля", byte[]? data = null, string name = "скрін.png")
    {
        data ??= Png(1280, 720);
        var r = await _files.UploadAsync(nick, name, data.Length, new MemoryStream(data), CancellationToken.None);
        Assert.True(r.Ok, r.Message);
        return r.File!;
    }

    [Fact]
    public async Task A_screenshot_waits_and_then_sticks_to_the_note()
    {
        var f = await Up();
        Assert.Null(f.FeedbackId);
        Assert.Equal(ChatFileKind.Image, f.Type);
        Assert.Equal((1280, 720), (f.W, f.H));
        Assert.StartsWith($"{FeedbackFile.UrlPrefix}{f.Id}/{f.Key}/", f.Url);

        var r = _fb.Submit("Оля", "bug", "Кнопка скіпу не реагує", "#efir", null, null, files: [f.Id]);
        Assert.True(r.Ok, r.Message);

        var t = _fb.One(r.Id)!;
        var file = Assert.Single(t.Files!);
        Assert.Equal(r.Id, file.FeedbackId);
        Assert.Null(file.MsgId);                       // до самої записки, не до повідомлення
        Assert.NotNull(_files.Resolve(f.Id, f.Key));
    }

    [Fact]
    public async Task Wrong_key_shows_nothing()
    {
        var f = await Up();
        Assert.Null(_files.Resolve(f.Id, "0123456789abcdef01234567"));
        Assert.Null(_files.Resolve(f.Id, ""));
        Assert.Null(_files.Resolve(f.Id + 1, f.Key));
    }

    [Fact]
    public async Task Someone_elses_file_cannot_be_attached()
    {
        var petro = await Up("Петро");
        var r = _fb.Submit("Оля", "idea", "Ось що я бачу у Петра", null, null, null, files: [petro.Id]);
        Assert.False(r.Ok);
        Assert.Contains("загубився", r.Message);
        Assert.Empty(_fb.Mine("Оля"));
    }

    [Fact]
    public async Task A_file_attaches_only_once()
    {
        var f = await Up();
        Assert.True(_fb.Submit("Оля", "idea", "Перша записка зі скріном", null, null, null, files: [f.Id]).Ok);
        Assert.False(_fb.Submit("Оля", "idea", "Друга з тим самим скріном", null, null, null, files: [f.Id]).Ok);
    }

    [Fact]
    public async Task A_reply_can_be_just_a_file_from_either_side()
    {
        var id = _fb.Submit("Оля", "bug", "Кнопка скіпу не реагує", null, null, null).Id;
        var mine = await Up("Оля", Png(10, 10, 1));
        Assert.True(_fb.Say(id, "Оля", false, "", [mine.Id]).Ok);
        var dev = await Up("Влад", Png(10, 10, 2));
        Assert.True(_fb.Say(id, "Влад", true, "ось так тепер", [dev.Id]).Ok);

        var t = _fb.One(id)!;
        Assert.Equal(2, t.Msgs.Count);
        Assert.Equal(t.Msgs[0].Id, t.Files!.Single(x => x.Id == mine.Id).MsgId);
        Assert.Equal(t.Msgs[1].Id, t.Files!.Single(x => x.Id == dev.Id).MsgId);
        Assert.Equal("", t.Msgs[0].Text);
    }

    [Fact]
    public void Empty_reply_without_files_is_still_refused()
    {
        var id = _fb.Submit("Оля", "bug", "Кнопка скіпу не реагує", null, null, null).Id;
        Assert.False(_fb.Say(id, "Оля", false, "   ").Ok);
    }

    [Fact]
    public async Task No_more_than_five_files_at_once()
    {
        var ids = new List<long>();
        for (byte i = 0; i < Feedback.MaxFiles + 1; i++) ids.Add((await Up(data: Png(5, 5, i))).Id);
        var r = _fb.Submit("Оля", "idea", "Дуже багато скрінів", null, null, null, files: ids);
        Assert.False(r.Ok);
        Assert.True(_fb.Submit("Оля", "idea", "Рівно п'ять скрінів", null, null, null, files: ids.Take(Feedback.MaxFiles).ToList()).Ok);
    }

    [Theory]
    [InlineData("")]
    [InlineData("гість")]
    public async Task Nameless_cannot_upload(string nick)
    {
        var data = Png(5, 5);
        var r = await _files.UploadAsync(nick, "x.png", data.Length, new MemoryStream(data), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(FeedbackFiles.Nameless, r.Message);
    }

    [Fact]
    public async Task Named_guest_can_upload()
    {
        var f = await Up("гість Вася");
        Assert.True(_fb.Submit("гість Вася", "bug", "Ось скрін бага", null, null, null, files: [f.Id]).Ok);
    }

    [Fact]
    public async Task Too_big_is_refused_even_without_declared_length()
    {
        var big = new byte[FeedbackFiles.MaxBytes + 1];
        var r = await _files.UploadAsync("Оля", "video.mp4", null, new MemoryStream(big), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(FeedbackFiles.TooBig, r.Message);
        Assert.Empty(Directory.GetFiles(_dir));        // недокачане прибрано
    }

    [Fact]
    public async Task Hourly_budget_counts_files()
    {
        for (var i = 0; i < FeedbackFiles.HourFiles; i++) await Up(data: Png(5, 5, (byte)i));
        var data = Png(6, 6);
        var r = await _files.UploadAsync("Оля", "x.png", data.Length, new MemoryStream(data), CancellationToken.None);
        Assert.Equal(FeedbackFiles.TooMuch, r.Message);
        _clock.Advance(TimeSpan.FromHours(1.01));
        Assert.True((await _files.UploadAsync("Оля", "x.png", data.Length, new MemoryStream(data), CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task Files_that_never_got_a_note_are_swept_after_a_day()
    {
        var waiting = await Up(data: Png(5, 5, 1));
        var kept = await Up(data: Png(5, 5, 2));
        Assert.True(_fb.Submit("Оля", "idea", "Записка з одним скріном", null, null, null, files: [kept.Id]).Ok);

        _clock.Advance(FeedbackFiles.Wait + TimeSpan.FromMinutes(1));
        _files.Sweep(_clock.UtcNow);

        Assert.Null(_store.File(waiting.Id));
        Assert.Null(_files.Resolve(waiting.Id, waiting.Key));
        Assert.NotNull(_files.Resolve(kept.Id, kept.Key));
    }

    [Fact]
    public async Task The_same_picture_twice_is_one_copy_on_disk_and_sweeping_one_keeps_the_other()
    {
        var a = await Up();
        var b = await Up();
        Assert.Equal(a.Hash, b.Hash);
        Assert.Single(Directory.GetFiles(_dir));
        Assert.True(_fb.Submit("Оля", "idea", "Записка з другою копією", null, null, null, files: [b.Id]).Ok);

        _clock.Advance(FeedbackFiles.Wait + TimeSpan.FromMinutes(1));
        _files.Sweep(_clock.UtcNow);
        Assert.NotNull(_files.Resolve(b.Id, b.Key));   // файл на диску лишився — на нього посилається записка
    }

    [Fact]
    public void Diag_is_kept_as_json_and_oversized_is_dropped()
    {
        var diag = new JsonObject { ["dev"] = new JsonObject { ["os"] = "Android 14" } }.ToJsonString();
        var id = _fb.Submit("Оля", "bug", "Кнопка скіпу не реагує", null, null, null, diag).Id;
        Assert.Equal(diag, _fb.One(id)!.Item.Diag);

        var huge = "{\"x\":\"" + new string('а', Feedback.MaxDiag) + "\"}";
        var id2 = _fb.Submit("Оля", "bug", "Інший баг з купою подробиць", null, null, null, huge).Id;
        Assert.Null(_fb.One(id2)!.Item.Diag);
    }
}
