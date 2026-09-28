using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hlechyky.Tests.Games;

/// <summary>Прохід №3 (29.09): «💬 Наші балачки» в Клавоперегонах — фільтр реплік, підпис, мікс і пояснення «замало».</summary>
public class TyperaceChatTests
{
    [Theory]
    [InlineData("я мирний, хто хоче зі мною дружити?", "я мирний, хто хоче зі мною дружити?")]
    [InlineData("все інше ліпиться довго 🔥🔥 тому норм кількість", "все інше ліпиться довго тому норм кількість")]
    [InlineData("дивись тут – усе \"по-нашому\" … правда", "дивись тут — усе «по-нашому» ... правда")]
    public void Clean_keeps_ordinary_lines_and_tidies_them(string raw, string want) => Assert.Equal(want, TyperaceChat.Clean(raw));

    [Theory]
    [InlineData("коротко")]                                                       // замало
    [InlineData("глянь https://hlechyky.pp.ua там усе написано")]                 // посилання, латиниця
    [InlineData("мій номер 067 123 45 67 дзвони ввечері")]                        // телефон
    [InlineData("пиши мені на пошту ivan@example.com завтра")]                    // пошта
    [InlineData("живу на вулиці Шевченка біля магазину, заходь")]                 // адреса
    [InlineData("та йди ти в сраку зі своїм радіо, чесне слово")]                  // лайка
    [InlineData("я, курва, зробив тобі смак кращим")]                               // лайка
    [InlineData("это вообще не по-нашему написано брат")]                           // «э ы» — не друкується
    [InlineData("/roll 20 кубик на удачу для всіх")]                              // команда
    public void Clean_drops_private_rude_and_untypeable_lines(string raw) => Assert.Null(TyperaceChat.Clean(raw));

    [Fact]
    public void Clean_does_not_trip_on_innocent_words_like_sky() =>
        Assert.NotNull(TyperaceChat.Clean("сьогодні небо чисте, їдемо на ралі ввечері"));

    static List<TyperaceChatLine> Lines(int n) =>
        [.. Enumerable.Range(1, n).Select(i => new TyperaceChatLine(i, i % 2 == 0 ? "Smaug" : "владік", "26.09",
            $"репліка номер {Word(i)} про глечики й ралі"))];

    static string Word(int i) => new[] { "один", "два", "три", "чотири", "п’ять", "шість", "сім", "вісім", "дев’ять", "десять" }[i % 10] + new string('а', i / 10);

    static RoomHarness Table(TyperaceChat chat, string source)
    {
        var sp = new ServiceCollection().AddSingleton(chat).BuildServiceProvider();
        var h = new RoomHarness("typerace", options: new { source, length = "short" }, seed: 4, services: sp);
        Assert.True(h.Join("Оля").Ok);
        Assert.True(h.Join("Петро").Ok);
        Assert.True(h.Start().Ok);
        return h;
    }

    [Fact]
    public void Chat_source_gives_a_text_from_the_lines_signed_with_nick_and_date()
    {
        var chat = new TyperaceChat(null, new FakeClock());
        chat.Set(Lines(20));
        var h = Table(chat, TyperaceChat.Chat);
        var v = h.View(0);
        var src = v.GetProperty("src");
        Assert.Equal("chat", src.GetProperty("kind").GetString());
        Assert.StartsWith("— ", src.GetProperty("title").GetString());
        Assert.Contains("26.09", src.GetProperty("title").GetString());
        Assert.Contains("репліка номер", v.GetProperty("text").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, v.GetProperty("srcNote").ValueKind);
    }

    [Fact]
    public void Too_few_lines_fall_back_to_classic_and_say_why()
    {
        var chat = new TyperaceChat(null, new FakeClock());
        chat.Set(Lines(3));
        var h = Table(chat, TyperaceChat.Chat);
        var v = h.View(0);
        Assert.Equal("classic", v.GetProperty("src").GetProperty("kind").GetString());
        Assert.Contains("замало", v.GetProperty("srcNote").GetString());
    }

    [Fact]
    public void Signature_names_three_and_counts_the_rest()
    {
        var sig = TyperaceChat.Signature(Lines(5));
        Assert.Equal("— владік, 26.09 · Smaug, 26.09 · владік, 26.09 і ще 2", sig);
    }

    [Fact]
    public void Default_source_stays_all_and_mix_is_an_option()
    {
        var opt = Hlechyky.Games.Impl.TyperaceOptions.All.Single(o => o.Key == "source");
        Assert.Equal("all", opt.Default);
        Assert.Contains(opt.Values, v => v.Value == TyperaceChat.Mix);
        Assert.Contains(opt.Values, v => v.Value == TyperaceChat.Chat);
    }
}
