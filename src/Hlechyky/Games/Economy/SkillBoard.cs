using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hlechyky.Games.Economy;

/// <summary>Сила гравця в грі: близько 0 — середній чи новачок, плюс — сильніший, мінус — слабший. Лише з пам'яті.</summary>
public interface ISkills
{
    double Strength(string gameId, string? nick);
}

/// <summary>Одна партія ніка для формули сили: чим скінчилась і скільки людей сиділо за столом.</summary>
public readonly record struct SkillGame(string Outcome, int People);

/// <summary>
/// Сила гравців у кожній грі з <c>game_results</c> (пакет 1, 19.10): для командних ігор «за силою», далі — «фора»
/// (пакет 3). Базу читає лише фоном — на старті сервера, раз на <see cref="Stale"/> і невдовзі після кінця партії
/// (<see cref="GameEvents.RoomFinished"/>); під замком кімнати <see cref="Strength"/> — тільки читання словника.
/// </summary>
public sealed class SkillBoard(Db? db, GameEvents? events, ILogger<SkillBoard>? log = null) : ISkills, IHostedService
{
    /// <summary>Скільки останніх партій ніка в грі беремо.</summary>
    public const int Window = 40;
    /// <summary>«Уявні» середні партії в знаменнику: новачок з однією перемогою не стає одразу асом.</summary>
    public const double Prior = 4;
    static readonly TimeSpan Stale = TimeSpan.FromMinutes(10);
    /// <summary>Пауза після кінця партії: результати пише <c>Rewards</c> тим самим подієм, даємо йому дописати.</summary>
    static readonly TimeSpan AfterFinish = TimeSpan.FromSeconds(3);

    /// <summary>Гра → нік-ключ → сила. Словник гри підміняється цілком, тож читачам замок не потрібен.</summary>
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, double>> _byGame = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, bool> _pending = new(StringComparer.Ordinal);
    CancellationTokenSource? _cts;

    /// <summary>
    /// Формула сили (чиста, для тестів і «фори»): <c>(Σ win − Σ 1/n) / (партій + Prior)</c>, де n — людей у партії,
    /// нічия — половина перемоги, соло-рядки не рахуються. Беремо перші <see cref="Window"/> (найновіші — першими).
    /// </summary>
    public static double Formula(IEnumerable<SkillGame> games)
    {
        double won = 0, expected = 0;
        var count = 0;
        foreach (var g in games)
        {
            if (g.Outcome == "solo" || g.People < 2) continue;
            if (count == Window) break;
            count++;
            won += g.Outcome switch { "win" => 1, "draw" => 0.5, _ => 0 };
            expected += 1.0 / g.People;
        }
        return count == 0 ? 0 : (won - expected) / (count + Prior);
    }

    public double Strength(string gameId, string? nick) =>
        _byGame.TryGetValue(gameId, out var map) && map.TryGetValue(EconomyStore.Key(nick), out var s) ? s : 0;

    /// <summary>Перечитати силу з бази (усі ігри або одну). Синхронно — кличеться фоном і з тестів.</summary>
    public void Reload(string? gameId = null)
    {
        if (db is null) return;
        var rows = db.With(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"""
                SELECT g.game, g.nick_key, g.outcome,
                       (SELECT COUNT(*) FROM game_results x WHERE x.room_id = g.room_id AND x.round = g.round)
                FROM game_results g
                WHERE g.outcome <> 'solo' {(gameId is null ? "" : "AND g.game = $g")}
                ORDER BY g.id DESC
                """;
            if (gameId is not null) cmd.Parameters.AddWithValue("$g", gameId);
            using var r = cmd.ExecuteReader();
            var list = new List<(string Game, string Nick, SkillGame G)>();
            while (r.Read()) list.Add((r.GetString(0), r.GetString(1), new SkillGame(r.GetString(2), r.GetInt32(3))));
            return list;
        });
        var games = rows.GroupBy(x => x.Game, StringComparer.Ordinal);
        foreach (var g in games)
            _byGame[g.Key] = g.GroupBy(x => x.Nick, StringComparer.Ordinal)
                .ToDictionary(n => n.Key, n => Formula(n.Select(x => x.G)), StringComparer.Ordinal);
        if (gameId is not null && !games.Any()) _byGame.TryRemove(gameId, out _);
    }

    void OnFinished(RoomFinishedEvent e)
    {
        if (db is null || !_pending.TryAdd(e.GameId, true)) return;
        var ct = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(AfterFinish, ct);
                _pending.TryRemove(e.GameId, out _);
                Reload(e.GameId);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _pending.TryRemove(e.GameId, out _); log?.LogWarning(ex, "сила гравців: не перечитав {Game}", e.GameId); }
        }, CancellationToken.None);
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (db is null) return Task.CompletedTask;
        _cts = new CancellationTokenSource();
        if (events is not null) events.RoomFinished += OnFinished;
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try { Reload(); }
                catch (Exception ex) { log?.LogWarning(ex, "сила гравців: не прочитав game_results"); }
                try { await Task.Delay(Stale, token); } catch (OperationCanceledException) { break; }
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        if (events is not null) events.RoomFinished -= OnFinished;
        _cts?.Cancel();
        return Task.CompletedTask;
    }
}
