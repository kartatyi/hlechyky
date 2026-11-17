using Hlechyky.Games;

namespace Hlechyky.Tests.Platform;

/// <summary>🌦 Погода: розбір відповіді Open-Meteo, обласні центри, слово Глека й година, коли він його каже.</summary>
public sealed class WeatherTests
{
    // У формі справжньої відповіді /v1/forecast (current + daily, forecast_days=1), скорочено.
    const string Json = """
        {"latitude":50.25,"longitude":28.6875,"timezone":"Europe/Kyiv",
         "current":{"time":"2026-11-17T08:15","interval":900,"temperature_2m":3.24,"weather_code":61,"cloud_cover":96,"wind_speed_10m":14.3},
         "daily":{"time":["2026-11-17"],"weather_code":[63],"temperature_2m_max":[5.1],"temperature_2m_min":[1.2],
                  "precipitation_sum":[12.4],"precipitation_probability_max":[95],"wind_gusts_10m_max":[38.5]}}
        """;

    static readonly DateTimeOffset At = new(2026, 11, 17, 6, 15, 0, TimeSpan.Zero);

    [Fact]
    public void Parses_current_weather_and_the_day_for_a_known_oblast()
    {
        var p = Weather.Parse(Json, "Житомирська", At);
        Assert.NotNull(p);
        var (now, day) = p.Value;
        Assert.Equal("житомирська", now.Oblast);
        Assert.Equal("Житомир", now.Name);
        Assert.Equal(3.2, now.Temp);
        Assert.Equal(61, now.Code);
        Assert.Equal(96, now.Cover);
        Assert.Equal(At, now.At);
        Assert.NotNull(day);
        Assert.Equal(12.4, day.PrecipMm);
        Assert.Equal(95, day.PrecipChance);
    }

    [Theory]
    [InlineData("""{"error":true,"reason":"Too many requests"}""")]
    [InlineData("""{"current":{"weather_code":3}}""")]
    [InlineData("<html>502</html>")]
    [InlineData("")]
    public void Garbage_is_not_weather(string json) => Assert.Null(Weather.Parse(json, "житомирська", At));

    [Fact]
    public void Unknown_oblast_has_no_weather_and_kyiv_city_is_kyiv()
    {
        Assert.Null(Weather.Parse(Json, "марсіанська", At));
        Assert.Equal("Київ", Weather.Parse(Json, "м. київ", At)!.Value.Now.Name);
    }

    [Fact]
    public void Every_oblast_of_the_alarm_map_has_a_centre()
    {
        // Ключі областей — ті самі, що в web/static/tryvoga-map.json: небо питає погоду саме за ними.
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "web", "static", "tryvoga-map.json"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "web", "static", "tryvoga-map.json")));
        var keys = doc.RootElement.GetProperty("oblasts").EnumerateArray().Select(o => o.GetProperty("k").GetString()!).ToList();
        Assert.NotEmpty(keys);
        Assert.All(keys, k => Assert.True(Weather.Centers.ContainsKey(k), "нема обласного центру для «" + k + "»"));
    }

    static WeatherDay Day(int code = 3, double max = 12, double min = 6, double mm = 0, int chance = 0, double gust = 20) => new(code, max, min, mm, chance, gust);

    [Fact]
    public void Glek_keeps_quiet_on_an_ordinary_day()
    {
        Assert.Null(Weather.GlekLine(Day(), "Житомир", 1));
        Assert.Null(Weather.GlekLine(Day(code: 61, mm: 0.6, chance: 40), "Житомир", 1));   // покрапає — не привід
    }

    [Theory]
    [InlineData(63, 12, 6, 12.4, 95, 20, "☔")]     // злива
    [InlineData(61, 12, 6, 3.0, 80, 20, "🌧")]      // просто дощ
    [InlineData(73, 0, -3, 4.0, 90, 20, "❄")]       // сніг — навіть коли його багато, це не «злива»
    [InlineData(67, 1, -2, 6.0, 90, 20, "🧊")]      // крижаний дощ
    [InlineData(95, 24, 16, 15.0, 90, 50, "⛈")]    // гроза головніша за зливу
    [InlineData(3, -6, -14, 0, 0, 20, "🥶")]        // мороз
    [InlineData(1, 34, 21, 0, 0, 20, "🥵")]         // спека
    [InlineData(3, 12, 6, 0, 0, 72, "💨")]          // вітрюган
    public void Glek_speaks_up_when_the_weather_matters(int code, double max, double min, double mm, int chance, double gust, string sign)
    {
        var line = Weather.GlekLine(Day(code, max, min, mm, chance, gust), "Житомир", 0);
        Assert.NotNull(line);
        Assert.StartsWith(sign, line);
        Assert.Contains("Житомир", line);
    }

    [Fact]
    public void Heavy_rain_reminds_about_the_umbrella_whichever_phrase_is_picked()
    {
        for (var pick = -3; pick < 6; pick++)
            Assert.Contains("парасольк", Weather.GlekLine(Day(code: 65, mm: 22, chance: 100), "Київ", pick), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("2026-11-17T06:10:00Z", null, true)]            // 08:10 за Києвом, ще не казав
    [InlineData("2026-11-17T06:10:00Z", "2026-11-17", false)]   // сьогодні вже казав
    [InlineData("2026-11-17T06:10:00Z", "2026-11-16", true)]    // казав учора
    [InlineData("2026-11-17T05:50:00Z", null, false)]           // 07:50 — зарано
    [InlineData("2026-11-17T09:30:00Z", null, false)]           // 11:30 — вікно минуло, зранку сервер проспав
    public void Glek_speaks_once_a_day_in_the_morning_window(string utc, string? said, bool due)
    {
        Assert.Equal(due, Weather.Due(DateTimeOffset.Parse(utc), 8, said, out var today));
        Assert.Equal("2026-11-17", today);
    }
}
