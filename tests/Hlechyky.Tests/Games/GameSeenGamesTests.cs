using System.Text.Json;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «🧠 Пам'ять баченого між партіями» (пакет 1) у самих іграх: підсунута історія (<see cref="GameSeen.Seed"/>) → гра не
/// дає баченого, поки є небачене; без історії — вибір рівно як був.
/// </summary>
public class GameSeenGamesTests
{
    static readonly DateTimeOffset Yesterday = new(2026, 10, 18, 20, 0, 0, TimeSpan.Zero);

    /// <summary>60 слів: «слово00»…«слово59».</summary>
    static readonly string[] Bank = [.. Enumerable.Range(0, 60).Select(i => $"слово{i:00}")];

    static PictionaryWords Words() => new(Bank.Select(w => ("animals", w)));

    static IServiceProvider Services(TempDb? temp, Action<IServiceCollection>? more = null)
    {
        var s = new ServiceCollection();
        if (temp is not null) s.AddSingleton(temp.Db);
        more?.Invoke(s);
        return s.BuildServiceProvider();
    }

    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Влад"];

    // ---------- Позивні ----------

    static string[] PozyvniBoard(IServiceProvider services)
    {
        var h = new RoomHarness("pozyvni", seed: 7, services: services);
        foreach (var n in Nicks) h.Join(n);
        h.Start();
        return [.. h.View(null).GetProperty("board").EnumerateArray().Select(e => e.GetProperty("w").GetString()!)];
    }

    [Fact]
    public void Pozyvni_UnseenWordsGoFirst_AndBoardIsRemembered()
    {
        using var temp = new TempDb();
        // Оля вже бачила перші 35 слів (минулого вечора, за іншим столом) — свіжих рівно 25, на поле йдуть саме вони.
        new GameSeen(temp.Db).Seed("pozyvni", [GameSeen.NickKey("Оля")], Bank.Take(35), Yesterday);
        var board = PozyvniBoard(Services(temp, s => s.AddSingleton(Words())));
        Assert.Equal(Bank.Skip(35).Order(), board.Order());

        var peek = new GameSeen(temp.Db).Peek("pozyvni", [GameSeen.NickKey("Влад")]);
        Assert.Equal(board.Length, peek.Count);   // поле бачили всі за столом
    }

    [Fact]
    public void Pozyvni_EmptyHistoryDealsAsBefore()
    {
        using var temp = new TempDb();
        Assert.Equal(PozyvniBoard(Services(null, s => s.AddSingleton(Words()))),
            PozyvniBoard(Services(temp, s => s.AddSingleton(Words()))));
    }
}
