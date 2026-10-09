using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Зведення «Хто скільки» v3 (LitopysWire.cs): «🎯 Мої цілі» знають місце в «🏁 Гонці тижня», а «🔔 Нове» —
/// сигнатури 📖 Рекордів, 📈 Графіків і 🏺 Глека.
/// </summary>
public class LitopysWireTests
{
    /// <summary>Субота, 12:00 за Києвом: тиждень почався 20.09.</summary>
    static readonly DateTimeOffset Now = new(2026, 9, 26, 9, 0, 0, TimeSpan.Zero);

    static readonly GameInfo Ttt = EconomyRig.Info("ttt", "Хрестики-нолики", "хрестики-нолики");

    sealed class Rig : IDisposable
    {
        public readonly EconomyRig E = new();
        public readonly Litopys L;
        int _room;

        public Rig()
        {
            E.Clock.UtcNow = Now;
            E.Names.Learn(Ttt);
            L = new Litopys(E.Db, E.Names, E.Clock, new FixedOptions<SiteOptions>(new SiteOptions()));
        }

        public void Duel(string winner, string loser, DateTimeOffset at)
        {
            var room = "r" + ++_room;
            foreach (var (nick, outcome) in new[] { (winner, "win"), (loser, "loss") })
                E.Db.Exec("""
                    INSERT INTO game_results(room_id, game, round, nick_key, nick, outcome, score, created_at)
                    VALUES($r, 'ttt', 1, $k, $nick, $o, NULL, $at)
                    """, ("$r", room), ("$k", Auth.NickKey(nick)), ("$nick", nick), ("$o", outcome), ("$at", at.ToUniversalTime().ToString("o")));
        }

        public List<JsonElement> Goals(string nick, string kind) =>
            Views.Json(L.Goals(nick)).GetProperty("goals").EnumerateArray().Where(g => g.GetProperty("kind").GetString() == kind).ToList();

        public JsonElement Parts(string? nick) => Views.Json(L.Pulse(nick)).GetProperty("parts");

        public void Dispose() => E.Dispose();
    }

    static string Text(JsonElement parts) => string.Concat(parts.EnumerateArray().Select(p =>
        p.ValueKind == JsonValueKind.String ? p.GetString() : "[" + p.GetProperty("n").GetString() + "]"));

    [Fact]
    public void Race_goal_says_how_many_points_to_overtake_the_one_above()
    {
        using var rig = new Rig();
        // Петро: 2 перемоги й поразка = 3+3+1 = 7 очок; Оля: перемога й 2 поразки = 3+1+1 = 5
        rig.Duel("Петро", "Оля", Now.AddDays(-2));
        rig.Duel("Петро", "Оля", Now.AddDays(-2).AddMinutes(5));
        rig.Duel("Оля", "Петро", Now.AddDays(-1));

        var race = Assert.Single(rig.Goals("Оля", "race"));
        Assert.Contains("Ще 3 очки — і обженеш [Петро]", Text(race.GetProperty("parts")));
        Assert.Equal(3, race.GetProperty("need").GetDouble());
        Assert.Equal(8, race.GetProperty("of").GetDouble());

        var lead = Assert.Single(rig.Goals("Петро", "race"));
        Assert.Contains("Ти ведеш", Text(lead.GetProperty("parts")));
        Assert.Contains("[Оля] відстає лише на 2 очки", Text(lead.GetProperty("parts")));
    }

    [Fact]
    public void No_race_goal_without_points_this_week()
    {
        using var rig = new Rig();
        rig.Duel("Петро", "Оля", Now.AddDays(-20));   // минулий тиждень
        Assert.Empty(rig.Goals("Оля", "race"));
    }

    [Fact]
    public void Pulse_has_records_charts_and_glek_parts_and_charts_moves_with_my_place()
    {
        using var rig = new Rig();
        rig.Duel("Петро", "Оля", Now.AddDays(-2));
        var a = rig.Parts("Оля");
        Assert.False(string.IsNullOrEmpty(a.GetProperty("records").GetString()));
        Assert.False(string.IsNullOrEmpty(a.GetProperty("glek").GetString()));
        var charts = a.GetProperty("charts").GetString();

        rig.E.Clock.UtcNow = Now.AddMinutes(2);   // повз хвилинний кеш
        rig.Duel("Оля", "Петро", Now.AddMinutes(-30));
        rig.Duel("Оля", "Петро", Now.AddMinutes(-20));
        Assert.NotEqual(charts, rig.Parts("Оля").GetProperty("charts").GetString());   // Оля вийшла в лідери

        Assert.False(rig.Parts(null).TryGetProperty("glek", out _));   // без ніка — нема чиїх прожарок
    }
}
