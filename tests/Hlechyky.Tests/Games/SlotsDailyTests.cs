using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Пакет 5 «Азарт чесно»: Скарбничка 3 % / межа 25 000, «Глек докидає щодня» (<see cref="SlotsOptions.JackpotDaily"/>)
/// і чесне сальдо у виді автомата. specs/slots.md «Пакет 5».
/// </summary>
public class SlotsDailyTests
{
    static SlotsBank Bank(FakeStore store, FakeClock clock, SlotsOptions? opts = null)
    {
        var o = opts ?? new SlotsOptions();
        return new SlotsBank(new FakeStakes(), store, () => o, clock, defer: a => a());
    }

    [Fact]
    public void Defaults_are_three_percent_limit_25k_and_1000_a_day()
    {
        var o = new SlotsOptions();
        Assert.Equal(3, o.JackpotPct);
        Assert.Equal(10_000, o.JackpotSeed);
        Assert.Equal(25_000, o.JackpotMustHit);
        Assert.Equal(1000, o.JackpotDaily);
        Assert.Equal(1 / (1 + Math.Log(2.5)), o.SeedShare, 12);   // ≈ 0,522
    }

    [Fact]
    public void New_bank_starts_tomorrow_then_adds_once_per_kyiv_day()
    {
        var clock = new FakeClock();   // 10.09 15:00 за Києвом
        var bank = Bank(new FakeStore(), clock);
        Assert.Equal(10_000, bank.Pot);
        clock.Advance(TimeSpan.FromHours(8));   // 23:00 — та сама доба
        Assert.Equal(10_000, bank.Pot);
        clock.Advance(TimeSpan.FromHours(2));   // 01:00 наступної доби
        Assert.Equal(11_000, bank.Pot);
        Assert.Equal(11_000, bank.Pot);         // друге звернення тієї ж доби — нічого
        Assert.Equal(0, bank.Reserve);          // докладання — у суму, не в запас
        clock.Advance(TimeSpan.FromDays(3));
        Assert.Equal(14_000, bank.Pot);         // три пропущені доби — за кожну
    }

    [Fact]
    public void Reload_does_not_add_twice()
    {
        var clock = new FakeClock();
        var store = new FakeStore();
        var bank = Bank(store, clock);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(11_000, bank.Pot);
        bank.FlushNow();
        var again = Bank(store, clock);
        Assert.Equal(11_000, again.Pot);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(12_000, again.Pot);
    }

    [Fact]
    public void Old_state_without_a_day_gets_today_once()
    {
        var clock = new FakeClock();
        var store = new FakeStore();
        store.SaveState(SlotsBank.StoreKey, """{"pot":12345.5,"reserve":0}""");
        var bank = Bank(store, clock);
        Assert.Equal(13_345, bank.Pot);
        bank.FlushNow();
        Assert.Equal(13_345, Bank(store, clock).Pot);
    }

    [Fact]
    public void Missed_days_are_capped_at_seven()
    {
        var clock = new FakeClock();
        var bank = Bank(new FakeStore(), clock);
        clock.Advance(TimeSpan.FromDays(30));
        Assert.Equal(10_000 + SlotsOptions.DailyMaxDays * 1000, bank.Pot);
    }

    [Fact]
    public void Clock_going_back_adds_nothing()
    {
        var clock = new FakeClock();
        var bank = Bank(new FakeStore(), clock);
        clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(12_000, bank.Pot);
        clock.Advance(TimeSpan.FromDays(-1));
        Assert.Equal(12_000, bank.Pot);
        clock.Advance(TimeSpan.FromDays(1));   // знову та сама доба, що вже докладена
        Assert.Equal(12_000, bank.Pot);
    }

    [Fact]
    public void Off_does_not_catch_up_when_turned_back_on()
    {
        var clock = new FakeClock();
        var opts = new SlotsOptions { JackpotDaily = 0 };
        var bank = Bank(new FakeStore(), clock, opts);
        clock.Advance(TimeSpan.FromDays(3));
        Assert.Equal(10_000, bank.Pot);
        opts.JackpotDaily = 1000;
        Assert.Equal(10_000, bank.Pot);   // ця доба вже «прожита» вимкненою
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(11_000, bank.Pot);
    }

    [Fact]
    public void Daily_top_up_past_the_limit_is_dropped_by_the_next_spin()
    {
        var clock = new FakeClock();
        var store = new FakeStore();
        store.SaveState(SlotsBank.StoreKey, """{"pot":24500,"reserve":0,"dailyDay":"2026-09-09"}""");
        var bank = Bank(store, clock);
        Assert.Equal(25_500, bank.Pot);                // доклав за 10.09 — понад межу
        var won = bank.Feed(10, new ScriptRng());      // таємничий шанс не спрацьовує — зриває межа
        Assert.Equal(25_000, won);
        var add = 10 * 0.03 * (1 - new SlotsOptions().SeedShare);
        Assert.InRange(bank.Pot, 10_500, 10_500 + add + 1);   // надлишок понад межу — у нову суму
    }

    [Fact]
    public void Feed_by_spin_also_adds_the_daily_first()
    {
        var clock = new FakeClock();
        var bank = Bank(new FakeStore(), clock);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, bank.Feed(100, new ScriptRng()));   // перше звернення доби — оберт
        Assert.InRange(bank.Pot, 11_001, 11_002);           // 1000 + 3 × (1 − 0,522) ≈ 1,43
    }

    sealed class FixedSaldo(Saldo s) : IAzartSaldo
    {
        public string? Kind;
        public Saldo Get(string kind, string? nick) { Kind = kind; return s; }
    }

    [Fact]
    public void Slot_view_carries_saldo_only_with_the_service()
    {
        var stakes = new FakeStakes();
        stakes.Set("Оля", 1000);
        var bank = new SlotsBank(stakes, new FakeStore(), () => new SlotsOptions(), new FakeClock(), defer: a => a());
        var saldo = new FixedSaldo(new Saldo(1200, 950, 5000, 5100));
        var sp = new ServiceCollection().AddSingleton(bank).AddSingleton<IAzartSaldo>(saldo).BuildServiceProvider();
        var h = new RoomHarness("slot-glek", services: sp, seed: 3);
        Assert.True(h.Solo("Оля").Ok);
        var v = h.View(0).GetProperty("saldo");
        Assert.Equal("slot", saldo.Kind);
        Assert.Equal(1200, v.GetProperty("day").GetProperty("bet").GetInt32());
        Assert.Equal(950, v.GetProperty("day").GetProperty("win").GetInt32());
        Assert.Equal(5100, v.GetProperty("all").GetProperty("win").GetInt32());
        Assert.Equal(1000, h.View(0).GetProperty("daily").GetInt32());

        var plain = new SlotsKit();   // без сервісу сальдо — поля нема (null)
        Assert.True(!plain.View.TryGetProperty("saldo", out var none) || none.ValueKind is System.Text.Json.JsonValueKind.Null);
    }
}
