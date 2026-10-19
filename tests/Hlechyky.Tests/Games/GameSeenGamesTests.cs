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

    // ---------- Піктіонарі ----------

    static (RoomHarness H, string[] Choices) PictionaryOffer(IServiceProvider services)
    {
        var h = new RoomHarness("pictionary", seed: 7, services: services);
        foreach (var n in Nicks) h.Join(n);
        h.Start();
        var drawer = h.View(null).GetProperty("drawer").GetInt32();
        return (h, [.. h.View(drawer).GetProperty("choices").EnumerateArray().Select(e => e.GetString()!)]);
    }

    [Fact]
    public void Pictionary_OffersUnseenWordsFirst_AndRemembersThemForTheArtist()
    {
        using var temp = new TempDb();
        // Оля бачила 57 слів із 60 — художнику пропонують саме три небачені.
        new GameSeen(temp.Db).Seed("pictionary", [GameSeen.NickKey("Оля")], Bank.Take(57), Yesterday);
        var (h, choices) = PictionaryOffer(Services(temp, s => s.AddSingleton(Words())));
        Assert.Equal(Bank.Skip(57).Order(), choices.Order());

        var drawer = h.View(null).GetProperty("drawer").GetInt32();
        var artist = new GameSeen(temp.Db).Peek("pictionary", [GameSeen.NickKey(Nicks[drawer])]);
        Assert.All(choices, w => Assert.True(artist.ContainsKey(w)));   // запропоноване бачив художник
        var other = Nicks.First(n => n != Nicks[drawer] && n != "Оля");
        Assert.Empty(new GameSeen(temp.Db).Peek("pictionary", [GameSeen.NickKey(other)]));   // а решта — ще ні
    }

    [Fact]
    public void Pictionary_EmptyHistoryOffersAsBefore()
    {
        using var temp = new TempDb();
        Assert.Equal(PictionaryOffer(Services(null, s => s.AddSingleton(Words()))).Choices,
            PictionaryOffer(Services(temp, s => s.AddSingleton(Words()))).Choices);
    }

    // ---------- Вгадай мелодію ----------

    static MelodyTrack Song(int i) => new($"t{i}", $"Пісня {i}", $"Гурт {i}", 200, null, "/dev/null");

    [Fact]
    public void Melody_UnheardSongsGoFirstInEachCategory()
    {
        // «Ті, що ми слухаємо»: 10 пісень різних гуртів, 7 з них Оля вже чула — партія на 3 бере саме нечуті.
        var rows = Enumerable.Range(0, 10).Select(i => new MelodyLibrary.Row(Song(i), 5, "")).ToList();
        var seen = Enumerable.Range(0, 7).ToDictionary(i => MelodyLibrary.SeenKey(Song(i)), _ => new GameSeen.Seen(1, Yesterday));
        var pools = MelodyLibrary.Pools(rows, new HashSet<string>(), ["fav"], MelodyClassics.Empty, new Random(1), seen);
        var picked = MelodyLibrary.Choose(MelodyLibrary.Interleave(pools), 3, new Random(1), shuffle: false);
        Assert.Equal(new[] { "t7", "t8", "t9" }, picked.Select(t => t.Id).Order());

        // Без історії — тасування рівно як було.
        var none = MelodyLibrary.Pools(rows, new HashSet<string>(), ["fav"], MelodyClassics.Empty, new Random(1));
        var empty = MelodyLibrary.Pools(rows, new HashSet<string>(), ["fav"], MelodyClassics.Empty, new Random(1), new Dictionary<string, GameSeen.Seen>());
        Assert.Equal(none[0], empty[0]);
    }

    [Fact]
    public void Melody_TablePassesWhatSeatedHeard_AndRemembersTheRoundTrack()
    {
        using var temp = new TempDb();
        new GameSeen(temp.Db).Seed("melody", [GameSeen.NickKey("Оля")], [MelodyLibrary.SeenKey(Song(0))], Yesterday);
        var source = new FakeMelodySource([.. Enumerable.Range(0, 6).Select(Song)]);
        var h = new RoomHarness("melody", options: new { rounds = "3" }, seed: 3,
            services: Services(temp, s => s.AddSingleton<IMelodySource>(source)));
        foreach (var n in new[] { "Оля", "Петро" }) h.Join(n);
        h.Start();
        Assert.NotNull(source.Seen);
        Assert.Equal(1, source.Seen![MelodyLibrary.SeenKey(Song(0))].By);

        for (var i = 0; i < 400 && h.View(null).GetProperty("phase").GetString() != "play"; i++) { h.Tick(); Thread.Sleep(2); }
        Assert.Equal("play", h.View(null).GetProperty("phase").GetString());
        var heard = new GameSeen(temp.Db).Peek("melody", [GameSeen.NickKey("Петро")]);
        Assert.Single(heard);   // трек першого раунду тепер «чули» всі за столом
    }

    // ---------- Додепи ----------

    static readonly DotepyPrompt[] Prompts =
    [
        .. Enumerable.Range(1, 40).Select(i => new DotepyPrompt($"n{i:000}", $"Звичайне завдання номер {i}", ["тест"], false)),
        .. Enumerable.Range(1, 12).Select(i => new DotepyPrompt($"f{i:000}", $"Фінальне завдання номер {i}", ["тест"], true)),
    ];

    /// <summary>Стіл Додепів на чотирьох; повертає id завдань першого раунду (зі свіжого кільця процесу).</summary>
    static string[] DotepyFirstRound(TempDb? temp)
    {
        var ring = new DotepySeen(Dotepy.SeenRing);
        var h = new RoomHarness("dotepy", null, 7, Services(temp, s =>
        {
            s.AddSingleton(new DotepyPrompts(Prompts));
            s.AddSingleton(ring);
        }));
        foreach (var n in Nicks) Assert.True(h.Join(n).Ok);
        Assert.True(h.Start().Ok);
        return [.. ring.Snapshot().Keys.Order(StringComparer.Ordinal)];
    }

    [Fact]
    public void Dotepy_UnseenPromptsGoFirst_EvenAfterRestart()
    {
        using var temp = new TempDb();
        // Кільце процесу свіже (наче після деплою), а база пам'ятає: Оля бачила n001…n035 — раунд бере з n036…n040.
        new GameSeen(temp.Db).Seed("dotepy", [GameSeen.NickKey("Оля")], Prompts.Take(35).Select(p => p.Id), Yesterday);
        var picked = DotepyFirstRound(temp);
        Assert.NotEmpty(picked);
        Assert.All(picked, id => Assert.Contains(id, Prompts.Skip(35).Take(5).Select(p => p.Id)));

        var all = new GameSeen(temp.Db).Peek("dotepy", [GameSeen.NickKey("Петро")]);
        Assert.Equal(picked, all.Keys.Order(StringComparer.Ordinal));   // показане бачили всі за столом
    }

    [Fact]
    public void Dotepy_EmptyHistoryPicksAsBefore()
    {
        using var temp = new TempDb();
        Assert.Equal(DotepyFirstRound(null), DotepyFirstRound(temp));
    }
}
