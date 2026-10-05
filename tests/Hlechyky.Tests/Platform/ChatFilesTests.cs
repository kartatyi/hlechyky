using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Platform;

/// <summary>Файли в Балачках: хто може кидати, межі, що показується в рядку, а що лише скачується, і чистка теки.</summary>
public sealed class ChatFilesTests : IDisposable
{
    readonly TempDb _t = new();
    readonly FakeClock _clock = new();
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hlechyky-chatfiles-" + Guid.NewGuid().ToString("N"));

    ChatFiles Files(long maxTotal = 1L << 40) => new(_t.Db, new ChatFlood(), _clock, new ChatFilesDir(_dir, maxTotal));

    static byte[] Png(int w, int h)
    {
        var b = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
        b[16] = (byte)(w >> 24); b[17] = (byte)(w >> 16); b[18] = (byte)(w >> 8); b[19] = (byte)w;
        b[20] = (byte)(h >> 24); b[21] = (byte)(h >> 16); b[22] = (byte)(h >> 8); b[23] = (byte)h;
        return b;
    }

    Task<ChatFileReply> Send(ChatFiles f, byte[] data, string name = "фото.png", string caption = "", bool user = true, string nick = "Оля", long? replyTo = null) =>
        f.UploadAsync(nick, user, name, caption, replyTo, data.Length, new MemoryStream(data), CancellationToken.None);

    [Fact]
    public async Task A_picture_lands_in_the_chat_with_its_size_and_dimensions()
    {
        var f = Files();
        var r = await Send(f, Png(800, 600), caption: "дивіться, який глек");

        Assert.True(r.Ok, r.Message);
        var file = r.Line!.File!;
        Assert.Equal(ChatFileKind.Image, file.Type);
        Assert.Equal((800, 600), (file.W, file.H));
        Assert.Equal("фото.png", file.Name);
        Assert.Equal("дивіться, який глек", r.Line.Text);
        Assert.StartsWith(ChatFiles.UrlPrefix + file.Hash + "/", file.Url);
        Assert.NotNull(f.Resolve(file.Hash));

        var back = _t.Db.RecentChat(10, 0).Single();
        Assert.Equal(file, back.File);
    }

    [Fact]
    public async Task Guests_cannot_throw_files()
    {
        var r = await Send(Files(), Png(10, 10), user: false, nick: "гість Вася");
        Assert.False(r.Ok);
        Assert.Equal(ChatFiles.NeedAccount, r.Message);
        Assert.Empty(_t.Db.RecentChat(10, 0));
    }

    [Fact]
    public async Task Over_32_megabytes_is_refused_even_when_the_browser_lies_about_the_length()
    {
        var f = Files();
        var big = new byte[ChatFiles.MaxBytes + 1];
        Assert.Equal(ChatFiles.TooBig, (await f.UploadAsync("Оля", true, "x.bin", "", null, big.Length, new MemoryStream(big), default)).Message);
        Assert.Equal(ChatFiles.TooBig, (await f.UploadAsync("Оля", true, "x.bin", "", null, null, new MemoryStream(big), default)).Message);
        Assert.Empty(Directory.GetFiles(_dir));          // і недокачаного не лишилось
        Assert.True((await Send(f, new byte[ChatFiles.MaxBytes], "рівно.bin")).Ok);
    }

    [Fact]
    public async Task Html_named_like_a_photo_is_only_a_download()
    {
        var r = await Send(Files(), "<html><script>alert(1)</script></html>"u8.ToArray(), "фото.jpg");
        Assert.True(r.Ok);
        Assert.Equal(ChatFileKind.Other, r.Line!.File!.Type);
        Assert.False(ChatFileKind.Sniff("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8).Inline);
    }

    [Theory]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x10, 0, 0x20, 0 }, "image", "image/gif")]
    [InlineData(new byte[] { 0, 0, 0, 0x20, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D }, "video", "video/mp4")]
    [InlineData(new byte[] { 0, 0, 0, 0x20, 0x66, 0x74, 0x79, 0x70, 0x4D, 0x34, 0x41, 0x20 }, "audio", "audio/mp4")]
    [InlineData(new byte[] { 0, 0, 0, 0x20, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, 0x63 }, "file", "application/octet-stream")]
    [InlineData(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }, "video", "video/webm")]
    [InlineData(new byte[] { 0x49, 0x44, 0x33, 4 }, "audio", "audio/mpeg")]
    [InlineData(new byte[] { 0x4F, 0x67, 0x67, 0x53 }, "audio", "audio/ogg")]
    [InlineData(new byte[] { 0x50, 0x4B, 3, 4 }, "file", "application/octet-stream")]
    public void Kind_comes_from_the_bytes(byte[] head, string type, string mime)
    {
        var k = ChatFileKind.Sniff(head);
        Assert.Equal((type, mime), (k.Type, k.Mime));
    }

    [Fact]
    public async Task The_same_file_twice_is_one_copy_on_disk()
    {
        var f = Files();
        var a = await Send(f, Png(5, 5), "a.png");
        _clock.Advance(10);
        var b = await Send(f, Png(5, 5), "b.png");
        Assert.Equal(a.Line!.File!.Hash, b.Line!.File!.Hash);
        Assert.Single(Directory.GetFiles(_dir));
        Assert.Equal("b.png", b.Line.File.Name);
    }

    [Fact]
    public async Task Over_the_folder_limit_the_oldest_files_go()
    {
        var f = Files(maxTotal: 250);
        var first = await Send(f, Enumerable.Repeat((byte)1, 100).ToArray(), "1.bin");
        File.SetLastWriteTimeUtc(f.Resolve(first.Line!.File!.Hash)!, DateTime.UtcNow.AddMinutes(-5));
        _clock.Advance(10);
        var second = await Send(f, Enumerable.Repeat((byte)2, 100).ToArray(), "2.bin");
        _clock.Advance(10);
        var third = await Send(f, Enumerable.Repeat((byte)3, 100).ToArray(), "3.bin");

        Assert.True(third.Ok, third.Message);
        Assert.Null(f.Resolve(first.Line.File.Hash));            // найстаріше стерлося
        Assert.NotNull(f.Resolve(second.Line!.File!.Hash));
        Assert.NotNull(f.Resolve(third.Line!.File!.Hash));
        Assert.Equal(3, _t.Db.RecentChat(10, 0).Count);           // а рядки в Балачках лишились
    }

    [Fact]
    public async Task A_reply_to_a_file_without_caption_quotes_its_name()
    {
        var f = Files();
        var pic = await Send(f, Png(3, 3), "кіт.png");
        var re = _t.Db.AddChat("Петро", "ого", "chat", replyTo: pic.Line!.Id);
        Assert.Equal("📎 кіт.png", re.ReplyText);
        Assert.Equal("📎 кіт.png", _t.Db.RecentChat(10, 0).Single(m => m.Id == re.Id).ReplyText);
    }

    [Fact]
    public async Task An_hourly_cap_per_person_keeps_the_disk_for_everyone()
    {
        var f = Files();
        for (var i = 0; i < ChatFiles.HourFiles; i++)
        {
            Assert.True((await Send(f, BitConverter.GetBytes(i), $"{i}.bin")).Ok);
            _clock.Advance(2);
        }
        Assert.Equal(ChatFiles.TooMuch, (await Send(f, [42], "ще.bin")).Message);
        Assert.True((await Send(f, [42], "ще.bin", nick: "Петро")).Ok);
        _clock.Advance(TimeSpan.FromHours(1));
        Assert.True((await Send(f, [43], "ще.bin")).Ok);
    }

    [Theory]
    [InlineData("C:\\Users\\Оля\\Desktop\\звіт.pdf", "звіт.pdf")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("  \"a<b>\".txt ", "ab.txt")]
    [InlineData("", "файл")]
    public void Names_are_cleaned(string raw, string clean) =>
        Assert.Equal(clean, ChatFiles.CleanName(raw, ChatFileKind.Download));

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
