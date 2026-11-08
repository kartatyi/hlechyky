using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games.Impl;
using Microsoft.AspNetCore.Http;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Мемологія», 09.11.2026: кліп із відеофайла (mp4 з 404.com.ua, звідки брали мем) — банк, кеш і нарізка ffmpeg-ом,
/// токен і роздача <c>.mp4</c> з Range, вид розкриття й підсумку. Кліп не тримає гру: не готовий — картинка й 6 с.
/// </summary>
public class MemyClipFileTests
{
    const string File404 = "https://404.com.ua/rails/active_storage/blobs/proxy/abc/yarik-bachok.mp4";

    static JsonObject WithClip(JsonObject m, object clip)
    {
        m["clip"] = JsonSerializer.SerializeToNode(clip);
        return m;
    }

    static MemyBank FileBank()
    {
        var ten = MemyCache.Ten();
        foreach (var m in ten) WithClip(m, new { file = File404, from = 3, to = 11 });
        return MemyBank.Parse(MemyCache.Json(ten), null);
    }

    sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(answer(request));
        }
    }

    [Fact]
    public void Bank_reads_file_clip_and_refuses_both_or_bad_address()
    {
        var ten = MemyCache.Ten();
        WithClip(ten[0], new { file = File404, from = 3, to = 11 });
        WithClip(ten[1], new { file = File404, video = "https://www.youtube.com/watch?v=EUAIulcmNYI", from = 3, to = 11 });   // обидва
        WithClip(ten[2], new { file = "http://404.com.ua/x.mp4", from = 3, to = 11 });                                       // не https
        WithClip(ten[3], new { file = File404, from = 0, to = 40 });                                                         // задовгий
        var b = MemyBank.Parse(MemyCache.Json(ten), null);
        Assert.Equal(10, b.Memes.Count);
        Assert.Equal(new MemyClip(null, 3, 11, File404), b.Find("w001")!.Clip);
        Assert.True(b.Find("w001")!.Clip!.IsFile);
        Assert.Null(b.Find("w002")!.Clip);
        Assert.Null(b.Find("w003")!.Clip);
        Assert.Null(b.Find("w004")!.Clip);
        Assert.Equal(3, b.Problems.Count(p => p.Contains("clip", StringComparison.Ordinal)));
    }

    [Fact]
    public void Clip_file_name_changes_with_bounds_and_is_null_for_youtube()
    {
        var b = FileBank();
        var name = MemyImages.ClipFileNameFor(b.Find("w001")!);
        Assert.Matches("^[0-9a-f]{32}\\.mp4$", name);
        var moved = MemyBank.Parse(MemyCache.Json([WithClip(MemyCache.Ten()[0], new { file = File404, from = 4, to = 11 })]), null);
        Assert.NotEqual(name, MemyImages.ClipFileNameFor(moved.Find("w001")!));
        var yt = MemyBank.Parse(MemyCache.Json([WithClip(MemyCache.Ten()[0], new { video = "https://www.youtube.com/watch?v=EUAIulcmNYI", from = 4, to = 11 })]), null);
        Assert.Null(MemyImages.ClipFileNameFor(yt.Find("w001")!));
    }

    [Fact]
    public void Ready_clip_gets_a_token_and_is_served_as_mp4_with_range()
    {
        using var c = new MemyCache(FileBank());
        var m = c.Bank.Find("w001")!;
        Assert.False(c.Images.ClipReady("w001"));
        Assert.Null(c.Images.IssueClip("w001"));
        File.WriteAllBytes(c.Images.ClipPathFor(m)!, new byte[2048]);
        c.Images.Rescan();
        Assert.True(c.Images.ClipReady("w001"));
        var token = c.Images.IssueClip("w001")!;

        var ok = new DefaultHttpContext();
        var res = MemySetup.Serve(token + ".mp4", c.Images, ok);
        var file = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.PhysicalFileHttpResult>(res);
        Assert.Equal("video/mp4", file.ContentType);
        Assert.True(file.EnableRangeProcessing);
        Assert.Equal("nosniff", ok.Response.Headers["X-Content-Type-Options"]);

        // той самий токен як .jpg — 404: розширення мусить збігтися з файлом
        Assert.IsAssignableFrom<IStatusCodeHttpResult>(MemySetup.Serve(token + ".jpg", c.Images, new DefaultHttpContext()));
        Assert.Equal(404, ((IStatusCodeHttpResult)MemySetup.Serve(token + ".jpg", c.Images, new DefaultHttpContext())).StatusCode);
        // токен кадру як .mp4 — теж 404
        var frame = c.Images.Issue("w001", MemyImages.Clear);
        Assert.Equal(404, ((IStatusCodeHttpResult)MemySetup.Serve(frame + ".mp4", c.Images, new DefaultHttpContext())).StatusCode);

        // прибирання не чіпає кліп банку, а чужий mp4 — геть
        var stray = Path.Combine(c.Dir, new string('a', 32) + ".mp4");
        File.WriteAllBytes(stray, [1]);
        c.Images.Sweep();
        Assert.True(File.Exists(c.Images.ClipPathFor(m)!));
        Assert.False(File.Exists(stray));
    }

    [Fact]
    public void Reveal_plays_own_clip_when_ready_and_falls_back_to_picture_when_not()
    {
        using var c = new MemyCache(FileBank());
        foreach (var m in c.Bank.Memes) File.WriteAllBytes(c.Images.ClipPathFor(m)!, new byte[16]);
        c.Images.Rescan();
        var h = MemyTests.Table(c, 2);
        MemyTests.Until(h, Memy.PhaseReveal);
        var clip = h.View(0).GetProperty("answer").GetProperty("clip");
        Assert.Matches("^/api/games/memy/[0-9a-f]{24}\\.mp4$", clip.GetProperty("src").GetString());
        Assert.Equal((0, 8), (clip.GetProperty("from").GetInt32(), clip.GetProperty("to").GetInt32()));
        Assert.Equal(8_000 + Memy.RevealClipLead, h.View(null).GetProperty("totalMs").GetInt32());
        Assert.NotNull(c.Images.Resolve(MemyTests.Token(clip.GetProperty("src").GetString()!)));

        using var none = new MemyCache(FileBank());   // кліпи ще не вирізані
        var h2 = MemyTests.Table(none, 2);
        MemyTests.Until(h2, Memy.PhaseReveal);
        Assert.Equal(JsonValueKind.Null, h2.View(0).GetProperty("answer").GetProperty("clip").ValueKind);
        Assert.Equal(Memy.RevealMs, h2.View(null).GetProperty("totalMs").GetInt32());
    }

    [Fact]
    public async Task Not_a_video_or_404_gives_up_at_once_and_500_after_three_tries()
    {
        using var c = new MemyCache(FileBank());
        var ff = Path.Combine(c.Dir, "нема-ffmpeg.exe");
        var m = c.Bank.Find("w001")!;
        var html = new MemyImages(c.Bank, c.Dir, c.Clock, ff, null,
            new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html/>", System.Text.Encoding.UTF8, "text/html") }), TimeSpan.Zero, online: false);
        Assert.False(await html.FetchClipAsync(m, default));
        Assert.Contains("не відео", html.ClipFailed["w001"]);

        var flakyStub = new Stub(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var flaky = new MemyImages(c.Bank, c.Dir, c.Clock, ff, null, flakyStub, TimeSpan.Zero, online: false);
        for (var i = 0; i < 3; i++) Assert.False(await flaky.FetchClipAsync(m, default));
        Assert.Equal(3, flakyStub.Calls);
        Assert.Contains("500", flaky.ClipFailed["w001"]);
        Assert.DoesNotContain(Directory.EnumerateFiles(c.Dir), f => f.EndsWith(".tmp", StringComparison.Ordinal) || f.EndsWith(".tmp.mp4", StringComparison.Ordinal));
        Assert.False(flaky.ClipReady("w001"));
        Assert.True(flaky.Ready("w001"));                     // картинки мему кліп не чіпає
    }

    /// <summary>Справжній ffmpeg: з п'ятнадцятисекундного testsrc зі звуком — кліп рівно 3–11 с, H.264 + AAC, faststart.</summary>
    [Fact]
    public async Task Real_ffmpeg_cuts_the_moment_into_a_small_mp4()
    {
        if (MemyTests.Ffmpeg() is not { } ff) return;   // на машині без ffmpeg — нема що перевіряти
        using var c = new MemyCache(FileBank());
        var src = Path.Combine(c.Dir, "src.mp4");
        Run(ff, "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc=size=1280x720:rate=25",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100", "-t", "15", "-c:v", "libx264", "-preset", "ultrafast",
            "-c:a", "aac", "-shortest", src);
        var body = await File.ReadAllBytesAsync(src);
        File.Delete(src);
        var stub = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) { Headers = { ContentType = new("video/mp4") } } });
        var images = new MemyImages(c.Bank, c.Dir, c.Clock, ff, null, stub, TimeSpan.Zero, online: false);
        var m = c.Bank.Find("w001")!;
        Assert.True(await images.FetchClipAsync(m, default), images.ClipFailed.GetValueOrDefault("w001"));
        Assert.True(images.ClipReady("w001"));
        var path = images.ClipPathFor(m)!;
        var probe = Run(Path.Combine(Path.GetDirectoryName(ff)!, "ffprobe.exe"), "-v", "error", "-show_entries",
            "format=duration:stream=codec_name,width,height", "-of", "default=nw=1", path);
        Assert.Contains("codec_name=h264", probe);
        Assert.Contains("codec_name=aac", probe);
        Assert.Contains("width=854", probe);
        var dur = double.Parse(probe.Split('\n').First(l => l.StartsWith("duration=", StringComparison.Ordinal))["duration=".Length..].Trim(),
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(dur, 7.5, 8.5);
        // faststart: moov перед mdat
        var bytes = await File.ReadAllBytesAsync(path);
        var text = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
        Assert.Contains("moov", text);
        Assert.Empty(Directory.EnumerateFiles(c.Dir, "*.tmp*"));
    }

    static string Run(string exe, params string[] args)
    {
        if (!File.Exists(exe)) return "";
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, e);
        return o.Result;
    }
}
