using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Швидкодія «Своєї гри» на дев'ятьох (прохід 28.09): партія повним класичним пакетом — тики, вид кожного місця
/// (зібрати й серіалізувати, як розсилка), натиски й відповіді. Голос — підробка, готова одразу.
/// </summary>
[Collection(SerialPerf.Name)]
public class SvoyaPerfTests(ITestOutputHelper output)
{
    static readonly string[] Nine = ["Оля", "Петро", "Ганна", "Іван", "Марта", "Богдан", "Леся", "Остап", "Ніна"];

    [Fact]
    [Trait("Category", "Perf")]
    public void Nine_players_on_a_full_pack_stay_cheap()
    {
        var packs = new FakeSvoyaPacks();
        var full = SvoyaPackTests.FilledClassic();
        full.Id = "b_full";
        SvoyaBuiltin.Stamp(full, "full");
        packs.Packs["b_full"] = (full, "");
        var h = SvoyaTests.Table(nicks: Nine, voice: new FakeSvoyaVoice(), packs: packs, pack: "b_full");
        var game = h.Room.Game;

        var tick = new Stopwatch();
        var act = new Stopwatch();
        var view = new Stopwatch();
        int ticks = 0, acts = 0, views = 0, maxBytes = 0, boardBytes = 0;
        var k = 0;
        var key = "";
        for (var t = 0; t < 6000 && h.Room.Status == RoomStatus.Playing; t++)
        {
            var v = h.View(null);
            var phase = v.GetProperty("phase").GetString()!;
            var now = phase + ":" + v.GetProperty("cell").GetRawText() + ":" + v.GetProperty("answering").GetRawText();
            if (now != key)
            {
                key = now;
                // вид кожного місця й глядача на кожній новій фазі — рівно те, що робить розсилка
                view.Start();
                for (var s = -1; s < 9; s++)
                {
                    var raw = JsonSerializer.Serialize(game.View(s < 0 ? null : s), Wire);
                    maxBytes = Math.Max(maxBytes, raw.Length);
                    if (phase == Svoya.Board && s == 0) boardBytes = raw.Length;
                    views++;
                }
                view.Stop();
                k++;
                act.Start();
                acts += Drive(h, game, phase, v, k);
                act.Stop();
            }
            tick.Start();
            h.Tick();
            tick.Stop();
            ticks++;
        }
        var perTick = tick.Elapsed.TotalMilliseconds / ticks;
        var perView = view.Elapsed.TotalMilliseconds / views;
        var perAct = acts == 0 ? 0 : act.Elapsed.TotalMilliseconds / acts;
        output.WriteLine($"тиків {ticks}: {perTick * 1000:F1} мкс на тик; вид {perView * 1000:F1} мкс (поле {boardBytes} Б, найбільший {maxBytes} Б); хід {perAct * 1000:F1} мкс ({acts} ходів); фаза: {SvoyaTests.Phase(h)}");
        Assert.True(perTick < 0.25, $"тик {perTick:F3} мс");
        Assert.True(perView < 0.2, $"вид {perView:F3} мс");
        Assert.True(perAct < 0.2, $"хід {perAct:F3} мс");
        Assert.True(maxBytes < 6000, $"вид {maxBytes} Б");
    }

    static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    /// <summary>Один людський крок на нову фазу: обрати клітинку, натиснути, відповісти (то влучно, то мимо), кіт, аукціон, фінал.</summary>
    static int Drive(RoomHarness h, Game game, string phase, JsonElement v, int k)
    {
        int Do(int seat, string action, object? payload = null)
        {
            lock (h.Room.Sync) game.Act(seat, action, Views.Payload(payload));
            return 1;
        }
        var chooser = v.GetProperty("chooser").ValueKind == JsonValueKind.Number ? v.GetProperty("chooser").GetInt32() : 0;
        switch (phase)
        {
            case Svoya.Board:
                var board = v.GetProperty("board");
                for (var ti = 0; ti < board.GetArrayLength(); ti++)
                {
                    var cells = board[ti].GetProperty("cells");
                    for (var qi = 0; qi < cells.GetArrayLength(); qi++)
                        if (cells[qi].GetProperty("open").GetBoolean()) return Do(chooser, "pick", new { theme = ti, q = qi });
                }
                return 0;
            case Svoya.Buzz:
                return Do(k % 9, "buzz");
            case Svoya.Answering:
                var who = v.GetProperty("answering").GetInt32();
                var text = v.GetProperty("question").GetProperty("text").GetString()!.Replace("Запитання", "Відповідь");
                return Do(who, "answer", new { text = k % 3 == 0 ? "не знаю" : text });
            case Svoya.Cat:
                return Do(chooser, "give", new { seat = (chooser + 1) % 9 }) + Do((chooser + 1) % 9, "catPrice", new { max = true });
            case Svoya.Auction:
                var turn = v.GetProperty("auction").GetProperty("turn");
                return turn.ValueKind == JsonValueKind.Number ? Do(turn.GetInt32(), "pass") : 0;
            case Svoya.Strike:
                var f = v.GetProperty("final");
                var struck = f.GetProperty("struck").EnumerateArray().Select(x => x.GetInt32()).ToHashSet();
                var open = Enumerable.Range(0, f.GetProperty("themes").GetArrayLength()).First(i => !struck.Contains(i));
                return f.GetProperty("turn").ValueKind == JsonValueKind.Number ? Do(f.GetProperty("turn").GetInt32(), "strike", new { theme = open }) : 0;
            case Svoya.Bet:
                var n = 0;
                foreach (var s in v.GetProperty("final").GetProperty("finalists").EnumerateArray()) n += Do(s.GetInt32(), "bet", new { amount = 1 });
                return n;
            case Svoya.FinalQuestion:
                var m = 0;
                foreach (var s in v.GetProperty("final").GetProperty("finalists").EnumerateArray()) m += Do(s.GetInt32(), "answer", new { text = "фінал" });
                return m;
        }
        return 0;
    }
}
