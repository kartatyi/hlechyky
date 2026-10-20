using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Мемологія»: мем як кадр із відео (<c>image.video</c> + <c>image.at</c>, specs/memy.md §4) і ввічливість до
/// «забагато запитів» (429). Без мережі: YouTube — фейковий <see cref="IMemyFrames"/>, сервер картинок — заглушка.
/// </summary>
public class MemyVideoTests
{
    static JsonObject Video(string id, string video, JsonNode? at, string cat = "ua")
    {
        var o = MemyCache.M(id, cat, "Мем " + id, [], null, [], 2020, 1, ["люди"]);
        o["image"] = new JsonObject { ["video"] = video, ["at"] = at, ["page"] = video };
        return o;
    }

    static MemyBank Bank(params JsonNode[] memes) => MemyBank.Parse(MemyCache.Json(memes), null);

    // ---------------------------------------------------------------- банк

    [Fact]
    public void Bank_takes_a_video_and_a_second_instead_of_url()
    {
        var bank = Bank(
            Video("u100", "https://www.youtube.com/watch?v=BpO7cy0PovU", 3),
            Video("u101", "https://youtube.com/watch?list=PL1&v=b66swPVumO4&t=40s", 6.25),
            MemyCache.M("w100", "world", "Звичайний", [], null, [], 2017, 1, ["люди"]));
        Assert.Empty(bank.Problems);
        var a = bank.Find("u100")!;
        Assert.True(a.IsVideo);
        Assert.Equal(("https://www.youtube.com/watch?v=BpO7cy0PovU", 3.0, ""), (a.Video, a.At, a.ImageUrl));
        Assert.Equal("https://www.youtube.com/watch?v=BpO7cy0PovU&t=3s", a.Source);
        // хвіст посилання (список, мітка часу) відкидається: ключ кешу від нього не стрибає
        var b = bank.Find("u101")!;
        Assert.Equal(("https://www.youtube.com/watch?v=b66swPVumO4", 6.25), (b.Video, b.At));
        var w = bank.Find("w100")!;
        Assert.False(w.IsVideo);
        Assert.Equal(w.ImageUrl, w.Source);
    }

    [Fact]
    public void Bank_drops_bad_video_records_one_by_one()
    {
        var both = Video("u105", "https://www.youtube.com/watch?v=BpO7cy0PovU", 3);
        both["image"]!["url"] = "https://i.example.com/u105.png";
        var atAlone = MemyCache.M("w106", "world", "Мем w106", [], null, [], 2020, 1, ["люди"]);
        atAlone["image"]!["at"] = 3;
        var bank = Bank(
            Video("u100", "https://www.youtube.com/watch?v=BpO7cy0PovU", 0),   // нульова секунда — можна
            Video("u101", "https://www.youtube.com/watch?v=BpO7cy0PovU", null),
            Video("u102", "https://www.youtube.com/watch?v=BpO7cy0PovU", -1),
            Video("u103", "https://www.youtube.com/watch?v=BpO7cy0PovU", "3"),
            Video("u104", "https://vimeo.com/12345", 3),
            both,
            atAlone,
            Video("u107", "http://www.youtube.com/watch?v=BpO7cy0PovU", 3),
            Video("u108", "https://www.youtube.com/watch?v=short", 3),
            Video("u109", "https://www.youtube.com/watch?v=BpO7cy0PovU", MemyBank.AtMax + 1));
        Assert.Equal(["u100"], bank.Memes.Select(m => m.Id));
        foreach (var (id, why) in new[]
                 {
                     ("u101", "image.at"), ("u102", "image.at"), ("u103", "image.at"), ("u104", "image.video"),
                     ("u105", "або url, або video"), ("w106", "image.at без image.video"), ("u107", "image.video"),
                     ("u108", "image.video"), ("u109", "image.at"),
                 })
            Assert.Contains(bank.Problems, p => p.StartsWith(id + ":", StringComparison.Ordinal) && p.Contains(why, StringComparison.Ordinal));
    }

    [Fact]
    public void Real_bank_reads_without_rejects()
    {
        var path = Paths.Resolve(MemyBank.FileName);
        if (!File.Exists(path)) return;   // банк не в цій збірці — нема що перевіряти
        var bank = MemyBank.Load(path, null);
        Assert.Empty(bank.Problems);
        Assert.Contains(bank.Memes, m => m.IsVideo);
    }

    [Fact]
    public void Cache_key_follows_video_second_and_crop()
    {
        var bank = Bank(
            Video("u100", "https://www.youtube.com/watch?v=BpO7cy0PovU", 3),
            Video("u101", "https://www.youtube.com/watch?v=BpO7cy0PovU&t=9s", 3),
            Video("u102", "https://www.youtube.com/watch?v=BpO7cy0PovU", 3.5));
        var key = MemyImages.KeyFor(bank.Find("u100")!);
        Assert.Equal(key, MemyImages.KeyFor(bank.Find("u101")!));      // те саме відео й секунда — той самий кадр
        Assert.NotEqual(key, MemyImages.KeyFor(bank.Find("u102")!));   // інша секунда — інший кадр
        Assert.NotEqual(key, MemyImages.KeyFor(bank.Find("u100")! with { Crop = [0, 0, 0.5, 0.5] }));
        Assert.DoesNotContain("BpO7cy0PovU", MemyImages.FileNameFor(bank.Find("u100")!, 0));
    }

    // ---------------------------------------------------------------- кадри з відео

    /// <summary>YouTube без YouTube: що віддати на (відео, секунда) і хто скільки разів питав.</summary>
    sealed class Frames(Func<string, double, string, (bool, bool, string)> answer) : IMemyFrames
    {
        public readonly List<(string Video, double At)> Calls = [];

        public Task<(bool Ok, bool Final, string Why)> GrabAsync(string video, double at, string to, CancellationToken ct)
        {
            Calls.Add((video, at));
            return Task.FromResult(answer(video, at, to));
        }
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

    static readonly Stub NoHttp = new(_ => throw new InvalidOperationException("до мережі справа дійти не мала"));

    MemyImages Images(MemyCache c, IMemyFrames? frames, string? ff = null, HttpMessageHandler? http = null, List<TimeSpan>? slept = null) =>
        new(c.Bank, c.Dir, c.Clock, ff ?? Path.Combine(c.Dir, "нема-ffmpeg.exe"), null, http ?? NoHttp, TimeSpan.Zero, online: false,
            frames: frames, sleep: (t, _) => { slept?.Add(t); c.Clock.Advance(t); return Task.CompletedTask; });

    [Fact]
    public async Task Hopeless_video_gives_up_at_once_and_flaky_one_after_three_tries()
    {
        var bank = Bank(Video("u100", "https://www.youtube.com/watch?v=BpO7cy0PovU", 3), Video("u101", "https://www.youtube.com/watch?v=b66swPVumO4", 6));
        using var c = new MemyCache(bank, _ => false);
        var frames = new Frames((v, _, _) => v.EndsWith("PovU", StringComparison.Ordinal)
            ? (false, true, "yt-dlp: відео 18+, YouTube віддає його тільки залогіненому акаунту")
            : (false, false, "yt-dlp: мережа впала"));
        var images = Images(c, frames);

        Assert.False(await images.FetchAsync(bank.Find("u100")!, default));
        Assert.True(images.Gone("u100"));
        Assert.Contains("18+", images.Failed["u100"]);

        var m = bank.Find("u101")!;
        Assert.False(await images.FetchAsync(m, default));
        Assert.False(await images.FetchAsync(m, default));
        Assert.False(images.Gone("u101"));
        Assert.False(await images.FetchAsync(m, default));
        Assert.True(images.Gone("u101"));

        // прохід завантажувача сплячих не чіпає — жодних нескінченних повторів
        var before = frames.Calls.Count;
        await images.PassAsync(default);
        Assert.Equal(before, frames.Calls.Count);
        Assert.Equal([("https://www.youtube.com/watch?v=BpO7cy0PovU", 3.0)], frames.Calls.Take(1));
        Assert.Empty(Directory.EnumerateFiles(c.Dir));   // жодних недописаних сирих файлів
    }

    [Fact]
    public async Task Without_a_grabber_video_memes_sleep_with_a_reason()
    {
        var bank = Bank(Video("u100", "https://www.youtube.com/watch?v=BpO7cy0PovU", 3));
        using var c = new MemyCache(bank, _ => false);
        var images = Images(c, null);
        Assert.False(await images.FetchAsync(bank.Find("u100")!, default));
        Assert.True(images.Gone("u100"));
        Assert.Contains("вимкнені", images.Failed["u100"]);
    }

    [Fact]
    public async Task Video_frame_goes_through_the_same_pipeline()
    {
        if (MemyTests.Ffmpeg() is not { } ff) return;   // на машині без ffmpeg — нема що перевіряти
        var cropped = Video("u101", "https://www.youtube.com/watch?v=b66swPVumO4", 6.5);
        cropped["crop"] = new JsonArray(0, 0, 0.5, 1);
        var bank = Bank(Video("u100", "https://www.youtube.com/watch?v=BpO7cy0PovU", 3), cropped);
        using var c = new MemyCache(bank, _ => false);
        var src = Path.Combine(c.Dir, "src");
        Directory.CreateDirectory(src);
        var png = MemyTests.Generate(ff, src, "f.png", "testsrc=size=1280x720:rate=1");
        Directory.Delete(src);
        var frames = new Frames((_, _, to) => { File.WriteAllBytes(to, png); return (true, false, ""); });
        var images = Images(c, frames, ff);

        await images.PassAsync(default);
        Assert.Equal(2, images.ReadyCount);
        Assert.Equal([("https://www.youtube.com/watch?v=BpO7cy0PovU", 3.0), ("https://www.youtube.com/watch?v=b66swPVumO4", 6.5)], frames.Calls);
        foreach (var (id, size) in new[] { ("u100", (960, 540)), ("u101", (640, 720)) })
            for (var f = 0; f < MemyImages.Frames; f++)
            {
                var bytes = File.ReadAllBytes(images.PathFor(bank.Find(id)!, f));
                Assert.Equal(size, MemyTests.Size(bytes));
                Assert.False(GeoImage.HasSegment(bytes, 0xFE));
            }
        Assert.Equal(2 * MemyImages.Frames, Directory.EnumerateFiles(c.Dir).Count());
    }

    [Fact]
    public void Youtube_refusals_are_sorted_into_hopeless_and_worth_retrying()
    {
        Assert.True(MemyVideoFrames.Hopeless("ERROR: [youtube] BpO7cy0PovU: Sign in to confirm your age. This video may be inappropriate for some users."));
        Assert.True(MemyVideoFrames.Hopeless("ERROR: [youtube] xxxxxxxxxxx: Video unavailable. This video has been removed by the uploader"));
        Assert.True(MemyVideoFrames.Hopeless("ERROR: [youtube] xxxxxxxxxxx: Private video. Sign in if you've been granted access"));
        Assert.False(MemyVideoFrames.Hopeless("ERROR: [youtube] xxxxxxxxxxx: Sign in to confirm you’re not a bot."));
        Assert.False(MemyVideoFrames.Hopeless("ERROR: Unable to download API page: [WinError 10054] connection reset"));
        Assert.False(MemyVideoFrames.Hopeless("ERROR: [youtube] xxxxxxxxxxx: Requested format is not available."));
    }

    // ---------------------------------------------------------------- 429

    static HttpResponseMessage Busy(TimeSpan? after = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        if (after is { } a) r.Headers.RetryAfter = new RetryConditionHeaderValue(a);
        return r;
    }

    [Fact]
    public async Task Too_many_requests_waits_as_asked_and_does_not_burn_tries()
    {
        using var c = new MemyCache(ready: _ => false);
        var n = 0;
        var stub = new Stub(_ => ++n switch
        {
            1 => Busy(TimeSpan.FromSeconds(7)),   // сервер сказав скільки
            2 => Busy(),                          // не сказав — 5 → 10 с з наростанням
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var slept = new List<TimeSpan>();
        var images = Images(c, null, http: stub, slept: slept);
        Assert.False(await images.FetchAsync(c.Bank.Find("w001")!, default));
        Assert.Equal(3, stub.Calls);   // той самий мем, ще двічі — уже після пауз
        Assert.Equal([TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(10)], slept);
        Assert.True(images.Gone("w001"));   // 404 після 429 — уже справжня відповідь

        // 429 без кінця: після BusyTries спроб мем лишається на наступний прохід — не спить і невдач не лічить
        var always = new Stub(_ => Busy());
        var patient = Images(c, null, http: always, slept: slept);
        var m = c.Bank.Find("w002")!;
        for (var i = 0; i < MemyImages.FailsBeforeSleep + 1; i++) Assert.False(await patient.FetchAsync(m, default));
        Assert.False(patient.Gone("w002"));
        Assert.Contains("429", patient.Failed["w002"]);
        Assert.Equal((MemyImages.FailsBeforeSleep + 1) * MemyImages.BusyTries, always.Calls);
        Assert.All(slept, t => Assert.True(t <= MemyImages.BusyMax));
        Assert.Empty(Directory.EnumerateFiles(c.Dir));
    }

    [Fact]
    public async Task Long_retry_after_skips_the_host_till_the_next_pass()
    {
        using var c = new MemyCache(ready: _ => false);
        var stub = new Stub(_ => Busy(TimeSpan.FromHours(1)));
        var slept = new List<TimeSpan>();
        var images = Images(c, null, http: stub, slept: slept);
        Assert.False(await images.FetchAsync(c.Bank.Find("w001")!, default));
        Assert.Equal(1, stub.Calls);
        // інший мем з того ж хоста — без запиту: хост просив годину, посеред проходу стільки не чекаємо
        Assert.False(await images.FetchAsync(c.Bank.Find("w002")!, default));
        Assert.Equal(1, stub.Calls);
        Assert.Empty(slept);
        Assert.False(images.Gone("w002"));
        Assert.Contains("наступним проходом", images.Failed["w002"]);
        // минула година — пробуємо знову
        c.Clock.Advance(TimeSpan.FromHours(1));
        Assert.False(await images.FetchAsync(c.Bank.Find("w002")!, default));
        Assert.Equal(2, stub.Calls);
    }

    [Fact]
    public void Backoff_grows_and_stops_at_the_ceiling()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), MemyImages.Backoff(1));
        Assert.Equal(TimeSpan.FromSeconds(10), MemyImages.Backoff(2));
        Assert.Equal(TimeSpan.FromSeconds(80), MemyImages.Backoff(5));
        Assert.Equal(MemyImages.BusyMax, MemyImages.Backoff(6));
        Assert.Equal(MemyImages.BusyMax, MemyImages.Backoff(1000));
    }
}
