using Hlechyky.Games;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hlechyky.Tests.Platform;

/// <summary>🖥 Віддалений екран (Screen.cs): зміни адміна, пресети, ключ пристрою, довге очікування.</summary>
public class ScreenTests
{
    static (Screen Screen, MemoryGameStore Store, FakeClock Clock) Make(MemoryGameStore? store = null)
    {
        store ??= new MemoryGameStore();
        var clock = new FakeClock();
        return (new Screen(store, clock, NullLogger<Screen>.Instance), store, clock);
    }

    [Fact]
    public void Starts_bright_white_free_and_with_a_key()
    {
        var (s, _, _) = Make();
        Assert.Equal(0, s.State.Rev);
        Assert.Equal(100, s.State.Brightness);
        Assert.Equal(0, s.State.Color);
        Assert.Null(s.State.Preset);
        Assert.False(s.State.Calibration);
        Assert.True(s.Key.Length >= 30);
        Assert.True(s.KeyOk(s.Key));
        Assert.False(s.KeyOk(""));
        Assert.False(s.KeyOk(null));
        Assert.False(s.KeyOk(s.Key + "x"));
    }

    [Fact]
    public void Patch_changes_only_what_was_sent_and_clamps()
    {
        var (s, _, _) = Make();
        Assert.Null(s.Apply(new ScreenPatch(Brightness: 150), "владік"));
        Assert.Equal(100, s.State.Brightness);
        Assert.Null(s.Apply(new ScreenPatch(Brightness: -5, Color: 5000), "владік"));
        Assert.Equal(0, s.State.Brightness);
        Assert.Equal(1023, s.State.Color);
        Assert.Null(s.Apply(new ScreenPatch(Calibration: true), "владік"));
        Assert.True(s.State.Calibration);
        Assert.Equal(0, s.State.Brightness);   // не чіпали — лишилось
        Assert.Equal(3, s.State.Rev);
        Assert.Equal("владік", s.State.By);
        Assert.Equal("Нічого не змінено", s.Apply(new ScreenPatch(), "владік"));
        Assert.Equal(3, s.State.Rev);
    }

    [Fact]
    public void Preset_holds_until_own_color_is_picked()
    {
        var (s, _, _) = Make();
        s.Apply(new ScreenPatch(Color: 512), "a");
        Assert.Null(s.Apply(new ScreenPatch(Preset: "Red"), "a"));
        Assert.Equal("red", s.State.Preset);
        Assert.Equal(512, s.State.Color);           // колір лишився — повернеться, коли пресет знімуть
        s.Apply(new ScreenPatch(Brightness: 40), "a");
        Assert.Equal("red", s.State.Preset);        // яскравість пресет не знімає
        s.Apply(new ScreenPatch(Color: 700), "a");
        Assert.Null(s.State.Preset);                 // свій колір — пресет знято
        Assert.Equal(700, s.State.Color);
        s.Apply(new ScreenPatch(Preset: "black"), "a");
        s.Apply(new ScreenPatch(Preset: ""), "a");
        Assert.Null(s.State.Preset);
        Assert.NotNull(s.Apply(new ScreenPatch(Preset: "pink"), "a"));
        Assert.Null(s.State.Preset);
    }

    [Fact]
    public void State_and_key_survive_restart()
    {
        var (s, store, _) = Make();
        s.Apply(new ScreenPatch(Brightness: 33, Preset: "green", Calibration: true), "a");
        var key = s.Key;
        var (again, _, _) = Make(store);
        Assert.Equal(1, again.State.Rev);
        Assert.Equal(33, again.State.Brightness);
        Assert.Equal("green", again.State.Preset);
        Assert.True(again.State.Calibration);
        Assert.Equal(key, again.Key);
    }

    [Fact]
    public void New_key_locks_out_the_old_one()
    {
        var (s, _, _) = Make();
        var old = s.Key;
        var fresh = s.NewDeviceKey();
        Assert.NotEqual(old, fresh);
        Assert.False(s.KeyOk(old));
        Assert.True(s.KeyOk(fresh));
    }

    [Fact]
    public async Task Waiting_screen_wakes_up_on_change()
    {
        var (s, _, _) = Make();
        var wait = s.WaitAsync(0, TimeSpan.FromSeconds(10), CancellationToken.None);
        await Task.Delay(50);
        Assert.False(wait.IsCompleted);
        s.Apply(new ScreenPatch(Brightness: 10), "a");
        var st = await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, st.Rev);
        Assert.Equal(10, st.Brightness);
    }

    [Fact]
    public async Task Waiting_returns_at_once_when_already_behind_and_times_out_otherwise()
    {
        var (s, _, _) = Make();
        s.Apply(new ScreenPatch(Color: 1), "a");
        var st = await s.WaitAsync(0, TimeSpan.FromSeconds(10), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, st.Rev);
        var same = await s.WaitAsync(1, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        Assert.Equal(1, same.Rev);
    }

    [Fact]
    public void Admins_hear_changes_and_the_screen_coming_online()
    {
        var (s, _, clock) = Make();
        var heard = 0;
        s.AdminChanged += _ => heard++;
        s.Apply(new ScreenPatch(Brightness: 5), "a");
        Assert.Equal(1, heard);
        s.Seen();                       // екран уперше — адмін дізнається
        Assert.Equal(2, heard);
        s.Seen();                       // і далі на зв'язку — без зайвих подій
        Assert.Equal(2, heard);
        clock.Advance(Screen.SeenFresh + TimeSpan.FromSeconds(1));
        s.Seen();                       // зникав і повернувся
        Assert.Equal(3, heard);
    }

    [Fact]
    public void Online_while_a_stream_is_open()
    {
        var (s, _, clock) = Make();
        static bool Online(object view)
        {
            var device = view.GetType().GetProperty("device")!.GetValue(view)!;
            return (bool)device.GetType().GetProperty("online")!.GetValue(device)!;
        }
        Assert.False(Online(s.AdminView(false)));
        var hold = s.Stream();
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(Online(s.AdminView(false)));
        hold.Dispose();
        Assert.True(Online(s.AdminView(false)));   // щойно пішов — ще «на зв'язку»
        clock.Advance(Screen.SeenFresh + TimeSpan.FromSeconds(1));
        Assert.False(Online(s.AdminView(false)));
        Assert.Null(s.AdminView(false).GetType().GetProperty("key")!.GetValue(s.AdminView(false)));
    }
}
