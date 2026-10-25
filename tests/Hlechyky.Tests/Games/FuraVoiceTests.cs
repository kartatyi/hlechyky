using System.Text.Json;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>Голос Глека-диспетчера (specs/fura.md, «1e»): книга фраз, прогрів, що віддається браузеру.</summary>
public sealed class FuraVoiceTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "fura-tts-" + Guid.NewGuid().ToString("N"));
    readonly FakeTtsEngine _engine = new();
    readonly TtsService _tts;

    public FuraVoiceTests() =>
        _tts = new TtsService(_engine, new FixedOptions<TtsOptions>(new TtsOptions { CacheDir = _dir }), NullLogger<TtsService>.Instance);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>Події, на які чекає браузер (web/games/fura/voice.js) — кожна має бути в книзі.</summary>
    static readonly string[] Events =
        ["hello", "take", "coupled", "fragile", "sturdy", "clink", "half", "near", "late", "done_good", "done_ok",
         "done_broken", "exact", "voided", "rescue", "idle", "cancel",
         // етап 2 — колона
         "join", "leave", "col_take", "col_join", "col_bonus"];

    [Fact]
    public void Book_has_every_event_with_variants_and_no_numbers()
    {
        var book = FuraVoice.Load();
        Assert.Equal("ostap", book.Voice);
        foreach (var e in Events)
        {
            Assert.True(book.Lines.TryGetValue(e, out var list), e);
            Assert.True(list!.Count >= 2, e + ": хоч два варіанти, щоб не повторювався");
            foreach (var t in list)
            {
                Assert.DoesNotContain(t, ch => char.IsDigit(ch));      // числа — на табличках, голосом не кажемо
                Assert.False(string.IsNullOrWhiteSpace(DotepyVoice.Clean(t)), t);
            }
        }
    }

    [Fact]
    public async Task Warm_voices_every_line_once_and_snapshot_gives_urls()
    {
        var voice = new FuraVoice(_tts);
        var book = FuraVoice.Load();
        var before = JsonSerializer.SerializeToElement(voice.Snapshot(book));
        Assert.True(before.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("lines").GetProperty("hello")[0].GetProperty("url").ValueKind);

        voice.Warm(book);
        voice.Warm(book);                                            // вдруге нічого не додає
        while (await _tts.StepAsync(CancellationToken.None)) { }
        var total = book.Lines.Values.Sum(x => x.Count);
        Assert.Equal(total, _engine.Said.Count);

        var after = JsonSerializer.SerializeToElement(voice.Snapshot(book));
        var first = after.GetProperty("lines").GetProperty("hello")[0];
        Assert.StartsWith(SvoyaVoice.UrlPrefix, first.GetProperty("url").GetString());
        Assert.EndsWith(".mp3", first.GetProperty("url").GetString());
        Assert.Equal(1.5, first.GetProperty("sec").GetDouble());
        Assert.Equal(book.Lines["hello"][0], first.GetProperty("text").GetString());
    }

    [Fact]
    public void Without_tts_snapshot_says_disabled_and_keeps_texts()
    {
        var off = new TtsService(_engine, new FixedOptions<TtsOptions>(new TtsOptions { CacheDir = _dir, Enabled = false }), NullLogger<TtsService>.Instance);
        var s = JsonSerializer.SerializeToElement(new FuraVoice(off).Snapshot());
        Assert.False(s.GetProperty("enabled").GetBoolean());
        var line = s.GetProperty("lines").GetProperty("near")[0];
        Assert.Equal(JsonValueKind.Null, line.GetProperty("url").ValueKind);
        Assert.False(string.IsNullOrEmpty(line.GetProperty("text").GetString()));
    }
}
