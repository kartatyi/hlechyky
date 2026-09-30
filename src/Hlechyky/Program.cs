using Hlechyky;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Mcp;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

var root = Paths.Root;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = root,
    WebRootPath = Path.Combine(root, "web"),
});
builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

var cfg = builder.Configuration;
builder.Services.Configure<SiteOptions>(cfg.GetSection("Site"));
builder.Services.Configure<AuthOptions>(cfg.GetSection("Auth"));
builder.Services.Configure<GoogleOptions>(cfg.GetSection("Google"));
builder.Services.Configure<YtDlpOptions>(cfg.GetSection("YtDlp"));
builder.Services.Configure<VoiceOptions>(cfg.GetSection("Voice"));
builder.Services.Configure<LiquidsoapOptions>(cfg.GetSection("Liquidsoap"));
builder.Services.Configure<LastFmOptions>(cfg.GetSection("LastFm"));
builder.Services.Configure<AutoDjOptions>(cfg.GetSection("AutoDj"));
builder.Services.Configure<DjBotOptions>(cfg.GetSection("DjBot"));
builder.Services.Configure<DeployOptions>(cfg.GetSection("Deploy"));
builder.Services.Configure<MelodyOptions>(cfg.GetSection("Melody"));
builder.Services.Configure<CurfewOptions>(cfg.GetSection("Curfew"));
builder.Services.Configure<VoiceChatOptions>(cfg.GetSection("VoiceChat"));   // Посиденьки: голос (VoiceChat.cs)

Console.OutputEncoding = System.Text.Encoding.UTF8;
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Paths.Resolve("data/keys")));
builder.Services.AddSignalR();
builder.Services.AddSingleton(_ => new Db(Paths.Resolve("data/hlechyky.db")));
builder.Services.AddSingleton(_ => new FrontPrint(Path.Combine(root, "web")));   // відбиток web/ (Front.cs): /api/front, ?v= модулів ігор
builder.Services.AddSingleton<Accounts>();
builder.Services.AddSingleton<IGoogleVerifier, GoogleVerifier>();
builder.Services.AddSingleton<YtMusicClient>();
builder.Services.AddSingleton<Albums>();
builder.Services.AddSingleton<YtDlpService>();
builder.Services.AddSingleton<VoiceService>();
builder.Services.AddSingleton<LastFmClient>();
builder.Services.AddSingleton<LiquidsoapClient>();
builder.Services.AddSingleton<MusicBrainzClient>();
builder.Services.AddSingleton<RoomTaste>();
builder.Services.AddSingleton<ArtistQuality>();
builder.Services.AddSingleton<AutoDj>();
builder.Services.AddSingleton<Presence>();
builder.Services.AddSingleton<Curfew>();      // нічний відбій для окремих гравців (Curfew.cs)
builder.Services.AddSingleton<ChatFlood>();   // один лічильник флуду на Балачки, столи й агентів
builder.Services.AddSingleton<VoiceChat>();   // Посиденьки: хто де говорить, листи між браузерами (VoiceChat.cs)
builder.Services.AddSingleton<RadioEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RadioEngine>());
builder.Services.AddSingleton<IOnAir>(sp => sp.GetRequiredService<RadioEngine>());
builder.Services.AddSingleton<TrackBans>();
builder.Services.AddSingleton<TrackCache>();
builder.Services.AddSingleton<SpareList>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SpareList>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<TrackCache>());
builder.Services.AddSingleton<DjBrain>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DjBrain>());
// Підстраховка автодеплою: підбирає зелену збірку, вебхук про яку не дійшов (Deploy.cs)
builder.Services.AddHostedService(sp => new DeployWatch(
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<DeployOptions>>(), sp.GetRequiredService<ILogger<DeployWatch>>()));
builder.Services.AddHlechykyGames();
builder.Services.AddHlechykyEconomy();
builder.Services.AddHlechykyWords();
builder.Services.AddHlechykyMcp();   // аі-агенти за столом: POST /mcp
builder.Services.AddHlechykyFeedback();   // «💡 Розробнику»: пропозиції й баги (Feedback.cs)
builder.Services.AddHlechykyLavka();      // «Лавка Дядька Глека»: вигляд профілю, подарунки, присвята й феєрверк (Lavka.cs)

var port = cfg.GetValue<int?>("Site:ListenPort") ?? 8080;
builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(port));

var app = builder.Build();
// За Caddy (той самий хост): X-Forwarded-Proto робить Request.IsHttps правдивим, X-Forwarded-For віддає IP слухача
app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto });
app.Logger.LogInformation("Глечики: root={Root}, port={Port}", root, port);
// Голос столу стежить за розсилкою: мафія заснула, хтось устав — хто кого чує, треба перерахувати.
app.Services.GetRequiredService<Broadcaster>().Flushed += app.Services.GetRequiredService<VoiceChat>().OnFlushed;

app.UseHlechykyAuth();
app.UseDefaultFiles();
var front = app.Services.GetRequiredService<FrontPrint>();
app.UseStaticFiles(new StaticFileOptions
{
    // Типово no-cache: web/ віддається наживо і міняється деплоєм. Але модулі ігор core.js тягне з ?v=<відбиток>
    // (каталог роздає відбитки, Front.cs), і такий файл браузер може тримати назавжди — нова версія прийде під
    // новою адресою. Раніше кожне відкриття «Ігор» перепитувало сервер про ~95 файлів.
    OnPrepareResponse = ctx =>
    {
        var req = ctx.Context.Request;
        var immutable = req.Query.TryGetValue("v", out var v) && front.Matches(req.Path.Value ?? "", v.ToString());
        ctx.Context.Response.Headers.CacheControl = immutable ? "public, max-age=31536000, immutable" : "no-cache";
    },
});
app.MapHlechyky();
app.MapPeople();   // люди й статистика: картка людини, «Хто скільки», історія, свій гаманець, «Часто граємо»
app.MapHlechykyGames();
app.MapHlechykyEconomy();
app.MapHlechykyMcp();
app.MapHlechykyFeedback();
app.MapHlechykyLavka();
app.MapFront();    // відбиток web/: відкрита сторінка сама бачить, що після деплою змінилось (Front.cs)
app.MapHub<RadioHub>("/hub");
app.Run();
