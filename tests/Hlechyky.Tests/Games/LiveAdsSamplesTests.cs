using System.Text;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>Факт, що біжить лише з LIVEADS_SAMPLES=1 (справжній edge-tts і ffmpeg, копія прод-бази), інакше — Skip.</summary>
public sealed class LiveAdsSamplesFactAttribute : FactAttribute
{
    public LiveAdsSamplesFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("LIVEADS_SAMPLES") != "1")
            Skip = "зразки живої реклами — лише з LIVEADS_SAMPLES=1";
    }
}

/// <summary>
/// Зразки живої реклами для людини: справжні mp3 у <c>qa/samples/</c> і <c>samples.md</c> з текстами. База — копія
/// прод-бази в <c>qa/prod.db</c> (прод лише читали), годинник — вечір, коли в базі цілий день гри. Запуск:
/// <c>LIVEADS_SAMPLES=1 HLECHYKY_TTS_PYTHON=&lt;python&gt; dotnet test --filter Make_samples</c>. Ролики не програються.
/// </summary>
public sealed class LiveAdsSamplesTests
{
    static string Slug(string nick) => Auth.NickKey(nick) switch
    {
        "владік" => "vladik",
        "smaug" => "smaug",
        "микола ( справжній )" => "mykola",
        "назар" => "nazar",
        var k => new string(k.Where(char.IsAsciiLetterOrDigit).ToArray()),
    };

    [LiveAdsSamplesFact]
    public async Task Make_samples()
    {
        var qa = LiveAdsTests.RepoFile("qa");
        var src = Path.Combine(qa, "prod.db");
        var dbPath = Path.Combine(qa, "samples-db.db");
        File.Copy(src, dbPath, overwrite: true);
        var outDir = Path.Combine(qa, "samples");
        Directory.CreateDirectory(outDir);
        var cache = Path.Combine(qa, "samples-cache");
        Directory.CreateDirectory(cache);
        var ffDir = LiveAdsTests.FfmpegDir() ?? throw new InvalidOperationException("нема ffmpeg");
        var python = Environment.GetEnvironmentVariable("HLECHYKY_TTS_PYTHON") ?? @"C:\Users\Ya\AppData\Local\Python\pythoncore-3.14-64\python.exe";

        var db = new Db(dbPath);
        _ = new LavkaStore(db);
        var clock = new FakeClock { UtcNow = new DateTimeOffset(2026, 9, 30, 20, 50, 0, TimeSpan.Zero) };   // 23:50 Києва, 30.09
        var names = new GameNames(new Registry());
        var econStore = new EconomyStore(db);
        var outbox = new FakeOutbox();
        var economy = new Economy(econStore, names, clock, new FixedOptions<EconomyOptions>(new EconomyOptions()), outbox, NullLogger<Economy>.Instance);
        var presence = new Presence();
        foreach (var n in new[] { "владік", "Smaug", "микола ( справжній )", "Назар", "Mariana Matviienko" })
        {
            presence.Set("c-" + n, n);
            presence.SetListening("c-" + n, true);
        }
        var yt = new FixedOptions<YtDlpOptions>(new YtDlpOptions { FfmpegDir = ffDir, CacheDir = cache });
        var ttsO = new FixedOptions<TtsOptions>(new TtsOptions { Python = python });
        var liveO = new LiveAdsOptions { BedsDir = LiveAdsTests.RepoFile("data/liveads/beds") };
        using var tts = new EdgeTtsEngine(ttsO, yt, NullLogger<EdgeTtsEngine>.Instance);
        var renderer = new FfmpegLiveRenderer(tts, new FixedOptions<LiveAdsOptions>(liveO), ttsO, yt, NullLogger<FfmpegLiveRenderer>.Instance,
            new FixedOptions<AdOptions>(new AdOptions()));
        var store = new LiveAdsStore(db);
        var facts = new LiveFacts(db, names, clock);
        var live = new LiveAds(store, facts, renderer, economy, presence, outbox, clock, new FixedOptions<LiveAdsOptions>(liveO),
            new FixedOptions<CurfewOptions>(new CurfewOptions()), yt, NullLogger<LiveAds>.Instance)
        {
            CacheDir = cache, Rng = new Random(2909), Lines = LiveAdsTests.Bank.Value,
        };

        var md = new StringBuilder();
        md.AppendLine("# Зразки живої реклами (30.09.2026, з лором і гучніші)");
        md.AppendLine();
        md.AppendLine("Згенеровано тестом `LiveAdsSamplesTests.Make_samples` на копії прод-бази (прод лише читали), годинник — 30.09 23:50 за Києвом, ");
        md.AppendLine("онлайн і слухають: владік, Smaug, микола ( справжній ), Назар, Мар'яна. Голоси — edge-tts (Остап = Глек, Поліна), темп −4 %, ");
        md.AppendLine("спортивний блок новин +12 %. «[ба-дум-тсс]» — місце, де звучить удар.");
        md.AppendLine();

        async Task Save(string file, string title, LiveScript script, string? note = null)
        {
            var path = Path.Combine(outDir, file);
            var sec = await renderer.RenderAsync(script, path, default);
            md.AppendLine($"## {title} — `{file}`");
            md.AppendLine();
            md.AppendLine($"Підкладка: `{script.Style}` · факти: {string.Join(", ", script.Facts)} · тривалість: {(sec is { } s ? $"{s:0.0} с" : "НЕ ЗІБРАЛОСЬ")}");
            if (note is not null) md.AppendLine($"_{note}_");
            md.AppendLine();
            foreach (var l in script.Lines)
                md.AppendLine($"- **{(l.Voice == LiveLines.Polina ? "Поліна" : "Глек")}**{(l.Rate != liveO.Rate ? $" ({l.Rate})" : "")}: {l.Text}{(l.Rim ? " [ба-дум-тсс]" : "")}");
            md.AppendLine($"- **Глек**: {liveO.Sign}");
            md.AppendLine();
        }

        // ---- по три живі прожарки на чотирьох гравців: наступна — вже з кулдауном фактів після попередньої ----
        foreach (var nick in new[] { "владік", "Smaug", "микола ( справжній )", "Mariana Matviienko" })
        {
            var all = facts.For(nick, presence.Online);
            md.AppendLine($"<!-- {nick}: {string.Join("; ", all.Select(f => $"{f.Kind} {f.Juice:0.00}"))} -->");
            for (var k = 1; k <= 3; k++)
            {
                var script = live.Roast(nick, "roast", new Dictionary<string, string> { ["target"] = live.Lines.Say(nick) }, 2, false)
                    ?? live.Roast(nick, "roast", new Dictionary<string, string> { ["target"] = live.Lines.Say(nick) }, 2, true);
                if (script is null) { md.AppendLine($"## {nick} #{k}: нема що сказати"); continue; }
                await Save($"roast-{Slug(nick)}-{k}.mp3", $"Жива прожарка: {nick} #{k}", script);
                var id = store.Add("roast", nick, null, false, 0, "queued", clock.UtcNow);
                store.Ready(id, script.Text, string.Join(',', script.Facts), "voice-live-sample", 20, clock.UtcNow);
                store.Move(id, "ready", "sent", clock.UtcNow);
            }
        }

        // ---- замовлена: Назар замовляє Smaug через справжній шлях (списання → кухня → файл) ----
        var order = live.Order("Назар", true, "Smaug", false);
        Assert.True(order.Ok, order.Message);
        await live.TickAsync(default);
        var row = store.Get(order.Id!.Value)!;
        if (row.File is { } f && File.Exists(Path.Combine(cache, f + ".mp3")))
        {
            File.Copy(Path.Combine(cache, f + ".mp3"), Path.Combine(outDir, "order-smaug-from-nazar.mp3"), true);
            md.AppendLine("## Замовлена прожарка: Smaug від Назара — `order-smaug-from-nazar.mp3`");
            md.AppendLine();
            md.AppendLine($"Статус: {row.Status} · факти: {row.Facts} · тривалість: {row.Seconds} с");
            md.AppendLine();
            md.AppendLine(row.Text);
            md.AppendLine();
        }
        else md.AppendLine($"## Замовлена прожарка не вийшла: {row.Status}");

        // ---- новини дня ----
        if (live.NewsScript() is { } news) await Save("news.mp3", "Новини Глечиків", news);

        // ---- реакція на подію: найдовша серія поразок дня ----
        var streak = facts.News(new HashSet<string>()).Select(x => x.Fact).FirstOrDefault(x => x.Kind == "lose_streak");
        var ev = streak is not null
            ? LiveFact.Of("lose_streak", 1, ("nick", streak.Values["nick"]), ("n", streak.Values["n"]), ("game", streak.Values["game"]))
            : LiveFact.Of("big_buy", 1, ("nick", "Смауг"), ("n", 800), ("item", "Зорепад"));
        if (live.EventScript(ev.Kind, ev) is { } evScript) await Save("event.mp3", $"Реакція на подію ({ev.Kind})", evScript);

        await File.WriteAllTextAsync(Path.Combine(outDir, "samples.md"), md.ToString());
        Assert.True(Directory.GetFiles(outDir, "*.mp3").Length >= 8);
    }
}
