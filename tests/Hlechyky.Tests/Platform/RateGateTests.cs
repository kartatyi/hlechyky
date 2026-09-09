using Hlechyky.Games;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Квота хаба (ARCHITECTURE §9). Живе окремим класом саме заради цих тестів: хаб у тестах не піднімається,
/// а обіцянка «10 дій і 30 вводів на секунду» має ламатись голосно, а не непомітно.
/// </summary>
public class RateGateTests
{
    [Fact]
    public void Ten_actions_a_second_pass_and_the_eleventh_does_not()
    {
        var gate = new RateGate();
        for (var i = 0; i < RateGate.ActsPerSecond; i++)
            Assert.True(gate.Allow("conn", input: false, second: 100), $"дія {i + 1} мала пройти");

        Assert.False(gate.Allow("conn", input: false, second: 100));
    }

    [Fact]
    public void Thirty_inputs_a_second_pass_and_the_thirty_first_does_not()
    {
        var gate = new RateGate();
        for (var i = 0; i < RateGate.InputsPerSecond; i++)
            Assert.True(gate.Allow("conn", input: true, second: 100));

        Assert.False(gate.Allow("conn", input: true, second: 100));
    }

    [Fact]
    public void A_new_second_starts_the_count_over()
    {
        var gate = new RateGate();
        for (var i = 0; i < RateGate.ActsPerSecond; i++) gate.Allow("conn", input: false, second: 100);
        Assert.False(gate.Allow("conn", input: false, second: 100));

        Assert.True(gate.Allow("conn", input: false, second: 101));
    }

    [Fact]
    public void Actions_and_inputs_have_their_own_quotas()
    {
        var gate = new RateGate();
        for (var i = 0; i < RateGate.ActsPerSecond; i++) gate.Allow("conn", input: false, second: 7);
        Assert.False(gate.Allow("conn", input: false, second: 7));

        Assert.True(gate.Allow("conn", input: true, second: 7));   // ввід рахується окремо
    }

    [Fact]
    public void Connections_do_not_share_a_quota_and_a_gone_one_is_forgotten()
    {
        var gate = new RateGate();
        for (var i = 0; i < RateGate.ActsPerSecond; i++) gate.Allow("перша", input: false, second: 5);
        Assert.False(gate.Allow("перша", input: false, second: 5));
        Assert.True(gate.Allow("друга", input: false, second: 5));

        Assert.Equal(2, gate.Tracked);
        gate.Forget("перша");
        gate.Forget("друга");
        Assert.Equal(0, gate.Tracked);
    }
}
