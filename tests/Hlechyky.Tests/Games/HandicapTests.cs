using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;

namespace Hlechyky.Tests.Games;

/// <summary>«⚖ Фора» (пакет 3, 20.10): множник від сили, межі, дробовий залишок, вимикачі, підпис.</summary>
public class HandicapTests
{
    sealed class FixedSkills(Dictionary<string, double> map) : ISkills
    {
        public double Strength(string gameId, string? nick) => map.TryGetValue(nick ?? "", out var s) ? s : 0;
    }

    sealed class Services(ISkills? skills) : IServiceProvider
    {
        public object? GetService(Type t) => t == typeof(ISkills) ? skills : null;
    }

    sealed class Ctx(Dictionary<string, string> options, ISkills? skills, params string?[] nicks) : IRoomContext
    {
        public string RoomId => "t";
        public int Players => nicks.Count(n => n is not null);
        public int Round => 1;
        public Random Rng { get; } = new(1);
        public IClock Clock => throw new NotSupportedException();
        public IReadOnlyDictionary<string, string> Options => options;
        public IServiceProvider Services { get; } = new Services(skills);
        public string? NickOf(int seat) => seat >= 0 && seat < nicks.Length ? nicks[seat] : null;
        public bool Seated(int seat) => NickOf(seat) is not null;
        public int? HostSeat => 0;
        public void Finish(int[] winners, string log, IReadOnlyDictionary<int, long>? scores = null, string? verdict = null) { }
        public void Log(string text) { }
        public void Say(string text) { }
        public void Score(int seat, double value, int? attempts = null) { }
        public void Award(int seat, int shards, string reason) { }
    }

    // Сили як у Цеглинах на проді 20.10: Микола +0,40, владік −0,24, Smaug −0,09, третій −0,17.
    static readonly Dictionary<string, double> Bricks = new()
    {
        ["Микола"] = 0.40, ["владік"] = -0.24, ["Smaug"] = -0.09, ["Іван"] = -0.17,
    };

    static Ctx Table(Dictionary<string, string>? options = null) =>
        new(options ?? [], new FixedSkills(Bricks), "Микола", "владік", "Smaug", "Іван");

    [Fact]
    public void The_strong_get_less_and_the_weak_get_more_around_the_table_mean()
    {
        var f = Handicap.ForSeats(Table(), "bricks", [0, 1, 2, 3]);
        Assert.InRange(f[0], 0.6, 0.7);     // Микола: 1 − 0,8·(0,40 + 0,025) ≈ 0,66
        Assert.InRange(f[1], 1.15, 1.2);    // владік ≈ 1,17
        Assert.True(f[2] > 1 && f[2] < f[1]);
        Assert.Equal(4, f.Count);
    }

    [Fact]
    public void Factor_is_clamped_and_equal_players_get_one()
    {
        Assert.Equal(Handicap.Min, Handicap.Factor(5, 0));
        Assert.Equal(Handicap.Max, Handicap.Factor(-5, 0));
        Assert.Equal(1, Handicap.Factor(0.3, 0.3));
    }

    [Fact]
    public void Off_staked_rated_or_alone_means_no_handicap()
    {
        Assert.Empty(Handicap.ForSeats(Table(new() { [Handicap.Key] = Handicap.Off }), "bricks", [0, 1]));
        Assert.Empty(Handicap.ForSeats(Table(new() { ["stake"] = "50" }), "bricks", [0, 1]));
        Assert.Empty(Handicap.ForSeats(Table(), "bricks", [0, 1], rated: true));
        Assert.Empty(Handicap.ForSeats(Table(), "bricks", [0]));
        Assert.Empty(Handicap.ForSeats(new Ctx([], null, "Микола", "владік"), "bricks", [0, 1]));
        Assert.NotEmpty(Handicap.ForSeats(Table(new() { ["stake"] = "0" }), "bricks", [0, 1]));
    }

    [Fact]
    public void Scale_keeps_the_fraction_so_the_whole_match_gets_exactly_the_factor()
    {
        double carry = 0;
        var sent = Enumerable.Range(0, 10).Sum(_ => Handicap.Scale(1, 0.7, ref carry));
        Assert.Equal(7, sent);
        carry = 0;
        Assert.Equal(5, Handicap.Scale(4, 1.25, ref carry));
        Assert.Equal(0, Handicap.Scale(0, 1.4, ref carry));
    }

    [Fact]
    public void Note_names_only_visible_handicaps_weakest_bonus_last()
    {
        var ctx = Table();
        var note = Handicap.Note(ctx, Handicap.ForSeats(ctx, "bricks", [0, 1, 2, 3]));
        Assert.NotNull(note);
        Assert.StartsWith("⚖ Фора: Микола ×0,6", note);
        Assert.Contains("владік ×1,1", note);
        Assert.Null(Handicap.Note(ctx, new Dictionary<int, double> { [0] = 1.02, [1] = 0.98 }));
    }
}
