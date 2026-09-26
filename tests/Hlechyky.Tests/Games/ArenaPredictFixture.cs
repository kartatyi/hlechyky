using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games.Impl;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Спільний «журнал вводу» для перевірки, що передбачення в браузері (icefloe.js, hockey.js) рахує рівно те
/// саме, що сервер. Журнали генеруються сідованим генератором, прогоняються через серверні функції
/// (<see cref="IcefloeCore.Glide"/>, <see cref="HockeyCore.StepPad"/>), а результати лежать у
/// <c>tests/Hlechyky.Tests/Fixtures/arena-predict.json</c>. Тест звіряє C# із файлом, а скрипт
/// <c>docs/games/dev/arena-predict.py</c> у headless Chrome — JS із тим самим файлом. Рівні обидва — рівні між собою.
/// Перегенерувати файл: <c>ARENA_FIXTURE_WRITE=1 dotnet test --filter Prediction_matches</c>.
/// </summary>
public static class ArenaPredictFixture
{
    public static string Path => Hlechyky.Paths.Resolve("tests/Hlechyky.Tests/Fixtures/arena-predict.json");

    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = false };

    /// <summary>Журнали Крижини: тіло з початковим станом і пачки підкроків [скільки, сектор, mu, тяга].</summary>
    public static JsonArray IcefloeLogs()
    {
        var rng = new Random(20260927);
        var logs = new JsonArray();
        for (var n = 0; n < 6; n++)
        {
            var start = new JsonArray(1300.0 + rng.Next(-400, 400), 1300.0 + rng.Next(-400, 400), (double)rng.Next(-900, 900), (double)rng.Next(-900, 900));
            var runs = new JsonArray();
            for (var r = 0; r < 14; r++)
            {
                var want = rng.Next(-1, 16);
                var mu = rng.Next(4) == 0 ? IcefloeCore.SpikeMu : IcefloeCore.Mu;
                var thrust = rng.Next(4) == 0 ? IcefloeCore.JugThrust : IcefloeCore.Thrust;
                runs.Add(new JsonArray(1 + rng.Next(40), want, mu, thrust));
            }
            logs.Add(new JsonObject { ["start"] = start, ["runs"] = runs });
        }
        return logs;
    }

    /// <summary>Прогін одного журналу Крижини: стан після кожної пачки [x, y, vx, vy].</summary>
    public static List<double[]> RunIcefloe(JsonNode log)
    {
        var s = log["start"]!.AsArray();
        var b = new ArenaBody(s[0]!.GetValue<double>(), s[1]!.GetValue<double>(), IcefloeCore.BodyR, 1)
        {
            Vx = s[2]!.GetValue<double>(),
            Vy = s[3]!.GetValue<double>(),
        };
        var outp = new List<double[]>();
        foreach (var run in log["runs"]!.AsArray())
        {
            var r = run!.AsArray();
            var count = r[0]!.GetValue<int>();
            var want = r[1]!.GetValue<int>();
            var mu = r[2]!.GetValue<double>();
            var thrust = r[3]!.GetValue<double>();
            for (var i = 0; i < count; i++) IcefloeCore.Glide(ref b, want, mu, thrust);
            outp.Add([b.X, b.Y, b.Vx, b.Vy]);
        }
        return outp;
    }

    /// <summary>Журнали Аерохокею: команда, старт біти й пачки підкроків [скільки, aim, tx, ty, dx, dy].</summary>
    public static JsonArray HockeyLogs()
    {
        var rng = new Random(7202609);
        var logs = new JsonArray();
        for (var n = 0; n < 6; n++)
        {
            var team = n % 2;
            var x0 = team == 0 ? 25.0 + rng.Next(60) : 115.0 + rng.Next(60);
            var start = new JsonArray(x0, 10.0 + rng.Next(100));
            var runs = new JsonArray();
            for (var r = 0; r < 16; r++)
            {
                var aim = rng.Next(3) != 0;
                // Цілі й поза своєю половиною, і дробові — так, як їх шле палець.
                var tx = Math.Round(rng.NextDouble() * 220 - 10, 3);
                var ty = Math.Round(rng.NextDouble() * 140 - 10, 3);
                runs.Add(new JsonArray(1 + rng.Next(60), aim ? 1 : 0, tx, ty, rng.Next(-1, 2), rng.Next(-1, 2)));
            }
            logs.Add(new JsonObject { ["team"] = team, ["start"] = start, ["runs"] = runs });
        }
        return logs;
    }

    /// <summary>Прогін журналу Аерохокею: позиція біти після кожної пачки [x, y].</summary>
    public static List<double[]> RunHockey(JsonNode log)
    {
        var team = log["team"]!.GetValue<int>();
        var s = log["start"]!.AsArray();
        double x = s[0]!.GetValue<double>(), y = s[1]!.GetValue<double>();
        var outp = new List<double[]>();
        foreach (var run in log["runs"]!.AsArray())
        {
            var r = run!.AsArray();
            var count = r[0]!.GetValue<int>();
            var aim = r[1]!.GetValue<int>() == 1;
            double tx = r[2]!.GetValue<double>(), ty = r[3]!.GetValue<double>();
            HockeyCore.ClampAim(team, ref tx, ref ty);
            var dx = r[4]!.GetValue<int>();
            var dy = r[5]!.GetValue<int>();
            for (var i = 0; i < count; i++) HockeyCore.StepPad(ref x, ref y, team, aim, tx, ty, dx, dy);
            outp.Add([x, y]);
        }
        return outp;
    }

    /// <summary>Файл-еталон: журнали й очікувані стани. Із ARENA_FIXTURE_WRITE=1 — перезаписати з C#.</summary>
    public static JsonNode Load()
    {
        if (Environment.GetEnvironmentVariable("ARENA_FIXTURE_WRITE") == "1")
        {
            var ice = IcefloeLogs();
            foreach (var log in ice) log!["expect"] = ToJson(RunIcefloe(log));
            var hk = HockeyLogs();
            foreach (var log in hk) log!["expect"] = ToJson(RunHockey(log));
            var root = new JsonObject
            {
                ["about"] = "Журнали вводу й очікувані стани передбачення (C# = JS). Генерує ArenaPredictFixture.",
                ["icefloe"] = ice,
                ["hockey"] = hk,
            };
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, root.ToJsonString(Pretty));
        }
        return JsonNode.Parse(File.ReadAllText(Path))!;
    }

    static JsonArray ToJson(List<double[]> rows)
    {
        var a = new JsonArray();
        foreach (var r in rows)
        {
            var row = new JsonArray();
            foreach (var v in r) row.Add(v);
            a.Add(row);
        }
        return a;
    }

    /// <summary>Порівняти до біта: double з файла читається рівно тим самим числом, яким його записали.</summary>
    public static void AssertSame(List<double[]> got, JsonNode expect, string what)
    {
        var e = expect.AsArray();
        Assert.Equal(e.Count, got.Count);
        for (var i = 0; i < got.Count; i++)
        {
            var row = e[i]!.AsArray();
            for (var k = 0; k < got[i].Length; k++)
                Assert.True(BitConverter.DoubleToInt64Bits(row[k]!.GetValue<double>()) == BitConverter.DoubleToInt64Bits(got[i][k]),
                    $"{what}: пачка {i}, поле {k}: файл {row[k]!.GetValue<double>():R}, C# {got[i][k]:R}");
        }
    }
}
