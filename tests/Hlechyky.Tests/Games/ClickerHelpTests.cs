using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Допомога друзям, 12-те дошліфування (ClickerGuildHelp.cs, ClickerGuild.cs, ClickerTolokaHelp.cs): картка друга
/// (залишок стелі гостинця, до котрої продовжиться підмайстер), бафи складаються зі стелями, «Подякувати», стрічка
/// цеху, дзвоник пошти, «Село» з першого обпалу, наступний етап толоки, старі збереження.
/// </summary>
public class ClickerHelpTests
{
    sealed class Bell : IClickerGuildWire
    {
        public List<(string Nick, string Kind, string From)> Rung { get; } = [];
        public HashSet<string> Here { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Online(string nick) => Here.Contains(nick);
        public void Mail(string nick, string kind, string from) => Rung.Add((nick, kind, from));
    }

    sealed class Tsekh
    {
        public FakeStore Store { get; } = new();
        public FakeClock Clock { get; } = new();
        public Bell Bell { get; } = new();
        public ClickerGuildService Svc { get; }
        public Tsekh() => Svc = new ClickerGuildService(Store, Clock) { GuildWire = Bell };

        public RoomHarness Potter(string nick)
        {
            var h = new RoomHarness("clicker", services: RoomHarness.WithService(Svc));
            h.Solo(nick);
            Assert.True(h.Act(0, "look").Ok);
            Publish(h, nick);
            return h;
        }

        /// <summary>Збереження кола — у сховище, як його пише кімната після дії (цех читає звідти бафи й толоку).</summary>
        public void Publish(RoomHarness h, string nick)
        {
            lock (h.Room.Sync) Store.SaveState("clicker:" + ClickerGuildService.Key(nick), h.Room.Game.Save()!);
        }
    }

    static JsonElement G(RoomHarness h) => h.View(0).GetProperty("guild");
    static ActResult Guild(RoomHarness h, object payload) => h.Act(0, "guild", payload);
    static JsonElement Json(object? o) => JsonSerializer.SerializeToElement(o, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    static JsonElement Look(Tsekh g, string me, string nick) => Json(g.Svc.Look(me, nick, g.Clock.UtcNow));

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    static DateTimeOffset Until(RoomHarness h, string kind) => G(h).GetProperty("buffs").GetProperty(kind).GetProperty("until").GetDateTimeOffset();

    [Fact]
    public void The_giver_sees_how_many_treat_minutes_the_friend_can_still_take()
    {
        var g = new Tsekh();
        var ola = g.Potter("Оля");
        g.Potter("Петро");
        Patch(ola, s => s["pots"] = 1e15);
        Assert.Equal(ClickerGuildService.TreatCapMinutes, Look(g, "Оля", "Петро").GetProperty("treatLeft").GetInt32());
        Assert.True(Guild(ola, new { op = "treat", to = "Петро", minutes = 30 }).Ok);
        Assert.Equal(ClickerGuildService.TreatCapMinutes - 60, Look(g, "Оля", "Петро").GetProperty("treatLeft").GetInt32());
        // Те саме — у списку цеху: картка друга живе й там.
        var row = Json(g.Svc.Roster("Оля")).GetProperty("potters").EnumerateArray().First(p => p.GetProperty("nick").GetString() == "Петро");
        Assert.Equal(ClickerGuildService.TreatCapMinutes - 60, row.GetProperty("look").GetProperty("treatLeft").GetInt32());
        // Себе в списку не розглядаєш.
        var me = Json(g.Svc.Roster("Оля")).GetProperty("potters").EnumerateArray().First(p => p.GetProperty("me").GetBoolean());
        Assert.Equal(JsonValueKind.Null, me.GetProperty("look").ValueKind);
    }

    [Fact]
    public void A_second_apprentice_extends_the_stay_and_the_giver_sees_until_when()
    {
        var g = new Tsekh();
        var ola = g.Potter("Оля");
        var mykola = g.Potter("Микола");
        var petro = g.Potter("Петро");
        var now = petro.Clock.UtcNow;
        var look = Look(g, "Оля", "Петро").GetProperty("lend");
        Assert.True(look.GetProperty("can").GetBoolean());
        Assert.Equal(JsonValueKind.Null, look.GetProperty("until").ValueKind);
        Assert.Equal(now.AddHours(24), look.GetProperty("after").GetDateTimeOffset());

        Assert.True(Guild(ola, new { op = "lend", to = "Петро" }).Ok);
        // Ще в скриньці — а Микола вже бачить, що його підмайстер продовжить до +48 год.
        var m = Look(g, "Микола", "Петро").GetProperty("lend");
        Assert.Equal(now.AddHours(24), m.GetProperty("until").GetDateTimeOffset());
        Assert.Equal(now.AddHours(48), m.GetProperty("after").GetDateTimeOffset());
        Assert.True(Guild(mykola, new { op = "lend", to = "Петро" }).Ok);

        Assert.True(petro.Act(0, "look").Ok);
        Assert.Equal(now.AddHours(48), Until(petro, "lend"));
        Assert.Equal("Оля, Микола", G(petro).GetProperty("buffs").GetProperty("lend").GetProperty("from").GetString());
    }

    [Fact]
    public void Apprentices_ahead_stop_at_three_days()
    {
        var g = new Tsekh();
        var ola = g.Potter("Оля");
        var petro = g.Potter("Петро");
        var now = petro.Clock.UtcNow;
        // Уже гостюють 60 годин: наступна доба обріжеться стелею — і дарувальник бачить саме 72.
        Patch(petro, s => ((JsonObject)s["guild"]!)["buffs"] = new JsonObject { ["lend"] = now.AddHours(60).ToString("O"), ["lendFrom"] = "Ярко" });
        g.Publish(petro, "Петро");
        Assert.Equal(now.AddHours(72), Look(g, "Оля", "Петро").GetProperty("lend").GetProperty("after").GetDateTimeOffset());
        Assert.True(Guild(ola, new { op = "lend", to = "Петро" }).Ok);
        Assert.True(petro.Act(0, "look").Ok);
        Assert.Equal(now.AddHours(72), Until(petro, "lend"));
        g.Publish(petro, "Петро");

        // Повна стеля — більше не приймаємо: підмайстер дарувальника не згорить даремно.
        var mykola = g.Potter("Микола");
        var full = Look(g, "Микола", "Петро").GetProperty("lend");
        Assert.True(full.GetProperty("full").GetBoolean());
        Assert.Equal(JsonValueKind.Null, full.GetProperty("after").ValueKind);
        Assert.Equal($"У Петро підмайстри вже розписані на {ClickerGuildService.LendCapHours} год наперед — пришли пізніше",
            Guild(mykola, new { op = "lend", to = "Петро" }).Message);
        Assert.True(Json(g.Svc.Help("микола", g.Clock.UtcNow)).GetProperty("lendLeft").GetBoolean());

        // Правлена руками база не розтягне понад стелю.
        Patch(petro, s => ((JsonObject)s["guild"]!)["buffs"] = new JsonObject { ["lend"] = "2099-01-01T00:00:00+00:00" });
        Assert.Equal(petro.Clock.UtcNow.AddHours(ClickerGuildService.LendCapHours), Until(petro, "lend"));
    }

    [Fact]
    public void Cheers_add_an_hour_each_up_to_four()
    {
        var g = new Tsekh();
        var petro = g.Potter("Петро");
        var now = petro.Clock.UtcNow;
        foreach (var nick in new[] { "А", "Б", "В", "Г" })
            Assert.True(Guild(g.Potter(nick), new { op = "cheer", to = "Петро" }).Ok);
        var d = g.Potter("Д");
        Assert.True(Look(g, "Д", "Петро").GetProperty("cheer").GetProperty("full").GetBoolean());
        Assert.StartsWith("Петро уже нахвалили на 4 год наперед", Guild(d, new { op = "cheer", to = "Петро" }).Message);
        Assert.True(petro.Act(0, "look").Ok);
        Assert.Equal(now.AddMinutes(ClickerGuildService.CheerCapMinutes), Until(petro, "cheer"));
        // Множник — той самий +10 %, не 4 × 10 %: складається час, а не сила.
        petro.Clock.Advance(TimeSpan.FromHours(3.5));
        Assert.Equal(JsonValueKind.Object, G(petro).GetProperty("buffs").GetProperty("cheer").ValueKind);
    }

    [Fact]
    public void Mail_rings_the_receiver_and_any_action_takes_it()
    {
        var g = new Tsekh();
        var ola = g.Potter("Оля");
        var petro = g.Potter("Петро");
        Assert.True(Guild(ola, new { op = "lend", to = "Петро" }).Ok);
        Assert.Equal(("Петро", "lend", "Оля"), g.Bell.Rung.Single());
        Assert.True(g.Svc.HasMail("петро"));
        // Вид пошти не забирає (вид не зберігається) — забирає дія, яку клієнт шле на дзвоник.
        Assert.Equal(JsonValueKind.Null, G(petro).GetProperty("buffs").GetProperty("lend").ValueKind);
        Assert.True(Guild(petro, new { op = "mail" }).Ok);
        Assert.False(g.Svc.HasMail("петро"));
        Assert.Equal(JsonValueKind.Object, G(petro).GetProperty("buffs").GetProperty("lend").ValueKind);
        var got = G(petro).GetProperty("got").EnumerateArray().Single();
        Assert.Equal("lend", got.GetProperty("kind").GetString());
        Assert.Equal("Оля", got.GetProperty("from").GetString());

        // Онлайн — з дзвоника; хто тут, видно в картці.
        g.Bell.Here.Add("петро");
        Assert.True(Look(g, "Оля", "Петро").GetProperty("online").GetBoolean());
    }

    [Fact]
    public void Thanks_cheer_back_first_then_say_a_free_word_once_a_day()
    {
        var g = new Tsekh();
        var ola = g.Potter("Оля");
        var petro = g.Potter("Петро");
        // Дякувати нема за що — відмова.
        Assert.StartsWith("Оля тобі останнім часом нічого не надсилав", Guild(petro, new { op = "thank", to = "Оля" }).Message);

        Assert.True(Guild(ola, new { op = "lend", to = "Петро" }).Ok);
        Assert.True(Look(g, "Петро", "Оля").GetProperty("thank").GetBoolean());
        var r = Guild(petro, new { op = "thank", to = "Оля" });
        Assert.True(r.Ok, r.Message);
        Assert.StartsWith("👏", r.Message);
        Assert.Equal("Ти вже подякував(ла) Оля сьогодні", Guild(petro, new { op = "thank", to = "Оля" }).Message);
        Assert.False(Look(g, "Петро", "Оля").GetProperty("thank").GetBoolean());
        Assert.True(Guild(ola, new { op = "mail" }).Ok);
        Assert.Equal(JsonValueKind.Object, G(ola).GetProperty("buffs").GetProperty("cheer").ValueKind);

        // Микола: Петра він сьогодні вже хвалив, тож дякує словом — без бафа.
        var mykola = g.Potter("Микола");
        Assert.True(Guild(petro, new { op = "cheer", to = "Микола" }).Ok);
        Assert.True(Guild(mykola, new { op = "mail" }).Ok);
        Assert.True(Guild(mykola, new { op = "cheer", to = "Петро" }).Ok);
        Assert.True(Guild(petro, new { op = "mail" }).Ok);
        var cheerUntil = Until(petro, "cheer");
        var w = Guild(mykola, new { op = "thank", to = "Петро" });
        Assert.True(w.Ok, w.Message);
        Assert.StartsWith("💛", w.Message);
        Assert.True(Guild(petro, new { op = "mail" }).Ok);
        Assert.Equal(cheerUntil, Until(petro, "cheer"));
        Assert.Equal("thanks", G(petro).GetProperty("got")[0].GetProperty("kind").GetString());
        Assert.Contains("thanks", g.Bell.Rung.Select(x => x.Kind));
        Assert.Equal(["петро"], G(mykola).GetProperty("thanked").EnumerateArray().Select(x => x.GetString()).ToArray());
    }

    [Fact]
    public void The_feed_lists_todays_deeds_newest_first()
    {
        var g = new Tsekh();
        var ola = g.Potter("Оля");
        g.Potter("Петро");
        Assert.True(Guild(ola, new { op = "lend", to = "Петро" }).Ok);
        Assert.True(Guild(ola, new { op = "cheer", to = "Петро" }).Ok);
        var feed = Json(g.Svc.Roster("Петро")).GetProperty("feed").EnumerateArray().ToList();
        Assert.Equal(["cheer", "lend"], feed.Select(x => x.GetProperty("kind").GetString()).ToArray());
        Assert.Equal("Оля", feed[0].GetProperty("from").GetString());
        Assert.Equal("Петро", feed[0].GetProperty("to").GetString());
        // «Нещодавно взаємодіяв» — для порядку друзів у картці.
        Assert.Equal(JsonValueKind.String, Look(g, "Петро", "Оля").GetProperty("lastAt").ValueKind);
        // Завтра стрічка порожня (лише сьогоднішні справи).
        g.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Empty(Json(g.Svc.Roster("Петро")).GetProperty("feed").EnumerateArray());
    }

    [Fact]
    public void The_village_opens_after_the_first_firing()
    {
        var g = new Tsekh();
        var h = g.Potter("Новачок");
        Assert.False(G(h).GetProperty("open").GetBoolean());
        Patch(h, s => s["craft"]!["firedBy"] = new JsonObject { ["pot"] = 1 });
        Assert.True(G(h).GetProperty("open").GetBoolean());
    }

    [Fact]
    public void Help_from_a_friend_opens_the_village_too_and_is_remembered_after_reload()
    {
        var g = new Tsekh();
        var ola = g.Potter("Оля");
        var petro = g.Potter("Петро");
        Assert.True(Guild(ola, new { op = "cheer", to = "Петро" }).Ok);
        Assert.True(petro.Act(0, "look").Ok);
        Assert.True(G(petro).GetProperty("open").GetBoolean());
        var before = G(petro).GetProperty("got").GetRawText();
        Patch(petro, _ => { });
        Assert.Equal(before, G(petro).GetProperty("got").GetRawText());
        // Старе збереження без got — порожньо, без помилок.
        Patch(petro, s => ((JsonObject)s["guild"]!).Remove("got"));
        Assert.Empty(G(petro).GetProperty("got").EnumerateArray());
    }

    [Fact]
    public void An_old_service_state_without_deeds_and_thanks_still_opens()
    {
        var store = new FakeStore();
        var old = new JsonObject
        {
            ["potters"] = new JsonObject { ["оля"] = new JsonObject { ["nick"] = "Оля", ["rank"] = 0, ["seen"] = "2026-09-10T12:00:00+00:00" } },
            ["helps"] = new JsonObject { ["оля"] = new JsonObject { ["day"] = "2026-09-10", ["lend"] = true, ["cheer"] = new JsonArray("петро") } },
        };
        store.SaveState(ClickerGuildService.StoreKey, old.ToJsonString());
        var clock = new FakeClock();
        var svc = new ClickerGuildService(store, clock);
        var help = svc.Help("оля", clock.UtcNow);
        Assert.Empty(help.Thanked!);
        Assert.Empty(Json(svc.Roster("Оля")).GetProperty("feed").EnumerateArray());
        Assert.Equal("Петро тобі останнім часом нічого не надсилав(ла) — дякувати поки нема за що", svc.Thank("оля", "Оля", "Петро", clock.UtcNow).Error);
    }

    // ---------- толока: наступний етап ----------

    static void Laid(RoomHarness h, int stage, DateTimeOffset at, string[]? built = null) => Patch(h, s =>
    {
        s["pots"] = 1e15;
        s["total"] = 2e15;
        s["toloka"] = new JsonObject
        {
            ["built"] = new JsonArray((built ?? []).Select(x => (JsonNode)x).ToArray()),
            ["stage"] = stage,
            ["laidAt"] = at.ToString("O"),
        };
    });

    [Fact]
    public void While_a_stage_builds_the_next_one_is_shown_with_what_you_have()
    {
        var g = new Tsekh();
        var ola = g.Potter("Оля");
        Patch(ola, s => { s["pots"] = 1e15; s["total"] = 2e15; });
        // Етап ще збирають — наступного не показуємо.
        Assert.Equal(JsonValueKind.Null, ola.View(0).GetProperty("toloka").GetProperty("next").ValueKind);
        Laid(ola, 0, ola.Clock.UtcNow);
        Patch(ola, s => s["craft"]!["items"] = new JsonObject { ["jug||1"] = 7 });
        var next = ola.View(0).GetProperty("toloka").GetProperty("next");
        Assert.Equal("well", next.GetProperty("building").GetString());
        Assert.Equal(1, next.GetProperty("index").GetInt32());
        Assert.Equal("Дубовий зруб", next.GetProperty("name").GetString());
        Assert.Equal(4.4e15, next.GetProperty("pay").GetDouble());
        var jug = next.GetProperty("needs").EnumerateArray().First(n => n.GetProperty("ware").GetString() == "jug");
        Assert.Equal(20, jug.GetProperty("n").GetInt32());
        Assert.Equal(7, jug.GetProperty("have").GetInt32());

        // Останній етап будови — наступне вже перший етап наступної будови.
        Laid(ola, 2, ola.Clock.UtcNow);
        next = ola.View(0).GetProperty("toloka").GetProperty("next");
        Assert.NotEqual("well", next.GetProperty("building").GetString());
        Assert.Equal(0, next.GetProperty("index").GetInt32());
    }

    [Fact]
    public void A_friend_sees_my_next_stage_in_the_card_and_the_house()
    {
        var g = new Tsekh();
        g.Potter("Оля");
        var petro = g.Potter("Петро");
        Laid(petro, 0, petro.Clock.UtcNow);
        g.Publish(petro, "Петро");
        var house = Json(g.Svc.House("Петро", "Оля"));
        var next = house.GetProperty("toloka").GetProperty("next");
        Assert.Equal("Дубовий зруб", next.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, next.GetProperty("needs")[0].GetProperty("have").ValueKind);
        Assert.Equal("Дубовий зруб", house.GetProperty("look").GetProperty("toloka").GetProperty("next").GetProperty("name").GetString());
        // Хата без того, хто дивиться (старий клієнт), — як і була, без картки.
        Assert.False(Json(g.Svc.House("Петро")).TryGetProperty("look", out _));
        // Збирають — наступного нема.
        Laid(petro, 0, default);
        g.Publish(petro, "Петро");
        Assert.Equal(JsonValueKind.Null, Json(g.Svc.House("Петро", "Оля")).GetProperty("toloka").GetProperty("next").ValueKind);
    }

    [Fact]
    public void Toloka_and_gifts_land_in_the_got_list_and_the_feed()
    {
        var g = new Tsekh();
        var ola = g.Potter("Оля");
        var petro = g.Potter("Петро");
        Laid(petro, 0, default);
        g.Publish(petro, "Петро");
        Patch(ola, s => s["craft"]!["items"] = new JsonObject { ["pot||1"] = 3, ["bowl||1"] = 1 });
        var r = Guild(ola, new { op = "toloka", to = "Петро", key = "pot||1", n = 2 });
        Assert.True(r.Ok, r.Message);
        Assert.True(Guild(ola, new { op = "gift", nick = "Петро", key = "bowl||1" }).Ok);
        Assert.Equal(["toloka", "gift"], g.Bell.Rung.Select(x => x.Kind).ToArray());
        Assert.True(Guild(petro, new { op = "mail" }).Ok);
        var kinds = G(petro).GetProperty("got").EnumerateArray().Select(x => x.GetProperty("kind").GetString()).ToList();
        Assert.Contains("toloka", kinds);
        Assert.Contains("gift", kinds);
        var feed = Json(g.Svc.Roster("Оля")).GetProperty("feed").EnumerateArray().Select(x => x.GetProperty("kind").GetString()).ToArray();
        Assert.Equal(["gift", "toloka"], feed);
    }
}
