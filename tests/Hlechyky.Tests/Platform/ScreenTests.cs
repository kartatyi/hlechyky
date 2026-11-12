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

    static object? Prop(object o, string name) => o.GetType().GetProperty(name)!.GetValue(o);

    [Fact]
    public async Task Measure_request_goes_to_the_screen_and_the_answer_clears_it()
    {
        var (s, _, _) = Make();
        s.Apply(new ScreenPatch(Brightness: 40, Preset: "white"), "a");
        var wait = s.WaitAsync(s.State.Rev, TimeSpan.FromSeconds(10), CancellationToken.None);
        var id = s.AskMeasure("владік");
        var st = await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, id);
        Assert.Equal(id, st.Measure!.Id);
        var device = Screen.DeviceView(st);
        Assert.Equal(id, Prop(Prop(device, "measure")!, "id"));

        var rev = s.State.Rev;
        Assert.Null(s.Report(new ScreenReport(Measure: new ScreenMeasureReport(id, 0.3127, 0.329, 6504))));
        Assert.Null(s.State.Measure);                 // відповідь дійшла — запит знято
        Assert.Equal(rev + 1, s.State.Rev);           // і екран про це дізнається
        Assert.Null(Prop(Screen.DeviceView(s.State), "measure"));
        var measures = (Array)Prop(s.AdminView(false), "measures")!;
        var first = measures.GetValue(0)!;
        Assert.Equal(6504.0, Prop(first, "k"));
        Assert.Equal(0.3127, Prop(first, "x"));
        Assert.Equal(40, Prop(first, "brightness"));  // що екран тоді показував
        Assert.Equal("white", Prop(first, "preset"));
    }

    [Fact]
    public void Measure_without_id_answers_the_pending_one_and_old_ids_do_not_clear_the_new()
    {
        var (s, _, _) = Make();
        var first = s.AskMeasure("a");
        var second = s.AskMeasure("a");
        Assert.Null(s.Report(new ScreenReport(Measure: new ScreenMeasureReport(first, 0.3, 0.3, 6000))));
        Assert.Equal(second, s.State.Measure!.Id);    // запізніла відповідь на старий — новий ще чекає
        Assert.Null(s.Report(new ScreenReport(Measure: new ScreenMeasureReport(null, 0.31, 0.32, 6500))));
        Assert.Null(s.State.Measure);
        Assert.Null(s.Report(new ScreenReport(Measure: new ScreenMeasureReport(null, 0.31, 0.32, 6500))));   // «сам», без запиту
        Assert.Equal(3, ((Array)Prop(s.AdminView(false), "measures")!).Length);
        Assert.NotNull(s.Report(new ScreenReport(Measure: new ScreenMeasureReport(99, 0.3, 0.3, 6000))));
    }

    [Fact]
    public void Long_measure_keeps_waiting_and_remembers_what_the_screen_showed_when_asked()
    {
        var (s, _, clock) = Make();
        s.Apply(new ScreenPatch(Brightness: 1, Color: 300), "a");
        var id = s.AskMeasure("a");
        clock.Advance(TimeSpan.FromMinutes(30));      // на яскравості 1 міряє довго — запит не згорає
        Assert.Equal(id, s.State.Measure!.Id);
        s.Apply(new ScreenPatch(Brightness: 50), "a");   // поки міряло, підкрутили
        Assert.Equal(id, s.State.Measure!.Id);           // запит від цього не зникає
        Assert.Null(s.Report(new ScreenReport(Measure: new ScreenMeasureReport(id, 0.3, 0.3, 6000))));
        var m = ((Array)Prop(s.AdminView(false), "measures")!).GetValue(0)!;
        Assert.Equal(1, Prop(m, "brightness"));         // вимір — про те, що було в мить запиту
        Assert.Equal(300, Prop(m, "color"));
        Assert.Equal(true, Prop(m, "changed"));
        Assert.NotNull(Prop(m, "askedAt"));

        var id2 = s.AskMeasure("a");
        s.Report(new ScreenReport(Measure: new ScreenMeasureReport(id2, 0.3, 0.3, 6000)));
        var m2 = ((Array)Prop(s.AdminView(false), "measures")!).GetValue(0)!;
        Assert.Equal(false, Prop(m2, "changed"));
        Assert.Equal(50, Prop(m2, "brightness"));
    }

    [Fact]
    public async Task Cancel_tells_the_screen_and_a_late_answer_is_still_kept()
    {
        var (s, _, _) = Make();
        Assert.False(s.CancelMeasure());
        var id = s.AskMeasure("a");
        var wait = s.WaitAsync(s.State.Rev, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.True(s.CancelMeasure());
        var st = await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(st.Measure);                       // екран бачить: можна перервати
        var rev = s.State.Rev;
        Assert.Null(s.Report(new ScreenReport(Measure: new ScreenMeasureReport(id, 0.3, 0.3, 6000))));
        Assert.Equal(rev, s.State.Rev);                // запиту вже нема — стан не чіпаємо
        Assert.Single((Array)Prop(s.AdminView(false), "measures")!);
    }

    [Fact]
    public void Bad_reports_are_refused()
    {
        var (s, _, _) = Make();
        Assert.NotNull(s.Report(new ScreenReport()));
        Assert.NotNull(s.Report(new ScreenReport(Temperature: double.NaN)));
        Assert.NotNull(s.Report(new ScreenReport(Temperature: 1000)));
        Assert.NotNull(s.Report(new ScreenReport(Measure: new ScreenMeasureReport(null, 0.3, null, 6000))));
        Assert.NotNull(s.Report(new ScreenReport(Measure: new ScreenMeasureReport(null, 1.3, 0.3, 6000))));
        Assert.NotNull(s.Report(new ScreenReport(Measure: new ScreenMeasureReport(null, 0.3, 0.3, 0))));
        Assert.Null(Prop(s.AdminView(false), "temperature"));
        Assert.Empty((Array)Prop(s.AdminView(false), "measures")!);
    }

    [Fact]
    public void Temperature_is_shown_to_admins_without_touching_the_screen_state()
    {
        var (s, _, clock) = Make();
        var heard = 0;
        s.AdminChanged += _ => heard++;
        var rev = s.State.Rev;
        Assert.Null(s.Report(new ScreenReport(Temperature: 41.5)));
        Assert.Equal(rev, s.State.Rev);               // екрану перемальовувати нічого
        var t = Prop(s.AdminView(false), "temperature")!;
        Assert.Equal(41.5, Prop(t, "value"));
        Assert.Equal(1, heard);
        s.Report(new ScreenReport(Temperature: 41.5));   // те саме одразу — адмінам не сиплемо
        Assert.Equal(1, heard);
        s.Report(new ScreenReport(Temperature: 42));     // змінилась — одразу
        Assert.Equal(2, heard);
        clock.Advance(16);
        s.Report(new ScreenReport(Temperature: 42));     // те саме, але давно — «ще жива»
        Assert.Equal(3, heard);
    }

    [Fact]
    public void Measures_and_their_numbering_survive_restart()
    {
        var (s, store, _) = Make();
        var id = s.AskMeasure("a");
        s.Report(new ScreenReport(Measure: new ScreenMeasureReport(id, 0.3, 0.31, 5000)));
        s.AskMeasure("a");                              // висить без відповіді
        var (again, _, _) = Make(store);
        Assert.Equal(2, again.State.Measure!.Id);
        Assert.Single((Array)Prop(again.AdminView(false), "measures")!);
        Assert.Equal(3, again.AskMeasure("a"));
        for (var i = 0; i < Screen.MaxMeasures + 5; i++) again.Report(new ScreenReport(Measure: new ScreenMeasureReport(null, 0.3, 0.3, 6000)));
        Assert.Equal(Screen.MaxMeasures, ((Array)Prop(again.AdminView(false), "measures")!).Length);
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
