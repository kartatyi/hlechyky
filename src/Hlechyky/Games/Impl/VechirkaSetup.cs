using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Вечірка поза столом (§22): дані дошки клієнтові (карта + тексти, кеш за версією), корона «🏆 Голова вечірки»
/// (§10) — пишемо за подією кінця партії, не з гри: гра крутиться під замком кімнати без бази.
/// </summary>
public static class VechirkaSetup
{
    public const string CrownKey = "vechirka:crown";

    public static IServiceCollection AddVechirka(this IServiceCollection services)
    {
        services.AddHostedService(sp => new CrownKeeper(sp.GetRequiredService<GameEvents>(), sp.GetRequiredService<IGameStore>()));
        return services;
    }

    public static WebApplication MapVechirka(this WebApplication app)
    {
        // Карта й тексти одним запитом; клієнт кешує за ?v= (версія карти у виді).
        app.MapGet("/api/games/vechirka/data", (string? map) =>
        {
            VechirkaMap m;
            try { m = VechirkaMap.Load(string.IsNullOrEmpty(map) ? "selo" : map); }
            catch (Exception) { return Results.NotFound(); }
            var texts = Texts();
            var json = $"{{\"map\":{m.Json},\"texts\":{texts}}}";
            return Results.Text(json, "application/json; charset=utf-8");
        });
        app.MapGet("/api/games/vechirka/crown", (IGameStore store) =>
            Results.Text(store.LoadState(CrownKey) ?? "{\"nicks\":[]}", "application/json; charset=utf-8"));
        return app;
    }

    static string Texts()
    {
        try
        {
            var path = Path.Combine(Paths.Root, "data", "vechirka", "texts.json");
            if (File.Exists(path)) return JsonNode.Parse(File.ReadAllText(path))!.ToJsonString();
        }
        catch (Exception) { /* кривий файл — клієнт обійдеться вбудованими назвами */ }
        return "{}";
    }

    /// <summary>Корона — переможцям-людям вечірки, де людей було ≥ 3, до наступної такої вечірки.</summary>
    sealed class CrownKeeper(GameEvents events, IGameStore store) : IHostedService
    {
        public Task StartAsync(CancellationToken ct) { events.RoomFinished += On; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) { events.RoomFinished -= On; return Task.CompletedTask; }

        void On(RoomFinishedEvent e)
        {
            if (e.GameId != "vechirka" || e.Result.Scores is not { Count: >= 3 } || e.Result.Winners.Length == 0) return;
            var nicks = e.Result.Winners.Where(s => s >= 0 && s < e.Seats.Count && e.Seats[s] is not null).Select(s => e.Seats[s]!).ToArray();
            if (nicks.Length == 0) return;
            store.SaveState(CrownKey, JsonSerializer.Serialize(new { nicks, at = e.FinishedAt }));
        }
    }
}
