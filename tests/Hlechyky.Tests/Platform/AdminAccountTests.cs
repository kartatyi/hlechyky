using Hlechyky.Tests.Support;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Адмін — це акаунт (Auth:AdminNicks або accounts.role), а не кука: стара hlechyky_auth ролі не дає,
/// ?k= живе лише з непорожнім AdminKey (дев), «вийти всюди» робить старі сесії гостями.
/// </summary>
public class AdminAccountTests
{
    /// <summary>Сервер без сервера: Auth.Gate на DefaultHttpContext з базою, конфігом і одним захисником кук.</summary>
    sealed class Site(TempDb t, AuthOptions options)
    {
        readonly IServiceProvider services = new ServiceCollection()
            .AddSingleton(t.Db)
            .AddSingleton<IOptionsMonitor<AuthOptions>>(new FixedOptions<AuthOptions>(options))
            .AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider())
            .BuildServiceProvider();

        public HttpContext Request(string? cookies = null, string query = "")
        {
            var c = new DefaultHttpContext { RequestServices = services };
            c.Request.Path = "/";
            c.Request.QueryString = new QueryString(query);
            if (cookies is not null) c.Request.Headers.Cookie = cookies;
            Auth.Gate(c, () => Task.CompletedTask).GetAwaiter().GetResult();
            return c;
        }

        /// <summary>Сесійна кука, як її кладе вхід: «hlechyky_session=…» для заголовка Cookie.</summary>
        public string SessionFor(string nick)
        {
            var c = new DefaultHttpContext { RequestServices = services };
            Auth.SignIn(c, t.Db.FindAccount(nick)!);
            return Cookie(c, Auth.SessionCookie)!;
        }

        public string OldAdminCookie() =>
            $"{Auth.CookieName}={services.GetRequiredService<IDataProtectionProvider>().CreateProtector("hlechyky.auth").Protect("admin")}";
    }

    /// <summary>«ім'я=значення» з Set-Cookie відповіді; null — такої куки не клали.</summary>
    static string? Cookie(HttpContext c, string name) =>
        c.Response.Headers.SetCookie.Select(v => v!.Split(';')[0]).FirstOrDefault(v => v.StartsWith(name + "="));

    static void AddGoogleAccount(TempDb t, string nick) => t.Db.AddAccount(nick, "", Auth.NewSalt(), "sub-" + nick);

    [Fact]
    public void A_nick_from_AdminNicks_is_admin_regardless_of_case_and_others_are_not()
    {
        using var t = new TempDb();
        AddGoogleAccount(t, "владік");
        AddGoogleAccount(t, "Оля");
        var site = new Site(t, new AuthOptions { AdminNicks = ["Владік"] });

        Assert.Equal("member", t.Db.FindAccount("владік")!.Role);   // у базі не адмін — адміном робить конфіг
        Assert.True(Auth.IsAdmin(site.Request(site.SessionFor("владік"))));
        Assert.False(Auth.IsAdmin(site.Request(site.SessionFor("Оля"))));
        Assert.False(Auth.IsAdmin(site.Request()));
    }

    [Fact]
    public void Role_admin_in_accounts_is_admin_without_any_list()
    {
        using var t = new TempDb();
        AddGoogleAccount(t, "Оля");
        var site = new Site(t, new AuthOptions());
        Assert.False(Auth.IsAdmin(site.Request(site.SessionFor("Оля"))));
        t.Db.SetAccountRole("оля", "admin");
        Assert.True(Auth.IsAdmin(site.Request(site.SessionFor("Оля"))));
    }

    [Fact]
    public void Sign_in_sets_the_role_for_the_same_request()
    {
        using var t = new TempDb();
        AddGoogleAccount(t, "владік");
        var site = new Site(t, new AuthOptions { AdminNicks = ["владік"] });
        var c = site.Request();
        Assert.False(Auth.IsAdmin(c));
        Auth.SignIn(c, t.Db.FindAccount("владік")!);
        Assert.True(Auth.IsAdmin(c));   // /api/account/google відповідає role одразу після входу
    }

    [Fact]
    public void The_old_admin_cookie_gives_nothing_and_is_wiped()
    {
        using var t = new TempDb();
        var site = new Site(t, new AuthOptions { AdminKey = "dev" });
        var c = site.Request(site.OldAdminCookie());
        Assert.False(Auth.IsAdmin(c));
        Assert.False(Auth.IsUser(c));
        Assert.Equal(Auth.CookieName + "=", Cookie(c, Auth.CookieName));   // порожня й протухла — браузер стирає
        Assert.Contains(c.Response.Headers.SetCookie, v => v!.StartsWith(Auth.CookieName + "=") && v.Contains("1970"));

        // Навіть у парі з акаунтом-не-адміном вона нічого не додає.
        AddGoogleAccount(t, "Оля");
        Assert.False(Auth.IsAdmin(site.Request(site.SessionFor("Оля") + "; " + site.OldAdminCookie())));
    }

    [Theory]
    [InlineData("?k=dev")]
    [InlineData("?k=")]
    [InlineData("?k")]
    public void With_an_empty_AdminKey_k_only_leads_home(string query)
    {
        using var t = new TempDb();
        var site = new Site(t, new AuthOptions { AdminKey = "" });
        var c = site.Request(query: query);
        Assert.Equal(302, c.Response.StatusCode);
        Assert.Equal("/", c.Response.Headers.Location.ToString());
        Assert.Null(Cookie(c, Auth.SessionCookie));
        Assert.Null(t.Db.FindAccount(Auth.DevAdmin));
    }

    [Fact]
    public void On_dev_k_brings_a_guest_into_the_admin_account_and_a_wrong_key_does_nothing()
    {
        using var t = new TempDb();
        var site = new Site(t, new AuthOptions { AdminKey = "dev" });

        var wrong = site.Request(query: "?k=dex");
        Assert.Null(Cookie(wrong, Auth.SessionCookie));
        Assert.Null(t.Db.FindAccount(Auth.DevAdmin));

        var c = site.Request(query: "?k=dev");
        Assert.Equal("/", c.Response.Headers.Location.ToString());
        var session = Cookie(c, Auth.SessionCookie);
        Assert.NotNull(session);
        var next = site.Request(session);
        Assert.Equal(Auth.DevAdmin, Auth.Nick(next));
        Assert.True(Auth.IsAdmin(next));
    }

    [Fact]
    public void On_dev_k_makes_the_signed_in_account_admin()
    {
        using var t = new TempDb();
        AddGoogleAccount(t, "Оля");
        var site = new Site(t, new AuthOptions { AdminKey = "dev" });
        var session = site.SessionFor("Оля");
        site.Request(session, "?k=dev");
        var next = site.Request(session);
        Assert.Equal("Оля", Auth.Nick(next));
        Assert.True(Auth.IsAdmin(next));
    }

    [Theory]
    [InlineData("dev", "dev", true)]
    [InlineData("dev", "Dev", false)]
    [InlineData("dev", "dev ", false)]
    [InlineData("", "", false)]
    [InlineData(null, "", false)]
    public void Admin_key_matches_exactly_and_never_when_empty(string? given, string key, bool expected) =>
        Assert.Equal(expected, Auth.AdminKeyMatches(given, key));

    [Fact]
    public void Sign_out_everywhere_turns_an_old_google_session_into_a_guest()
    {
        using var t = new TempDb();
        AddGoogleAccount(t, "владік");
        var site = new Site(t, new AuthOptions { AdminNicks = ["владік"] });
        var stolen = site.SessionFor("владік");
        Assert.True(Auth.IsAdmin(site.Request(stolen)));

        var r = new Accounts(t.Db, new FixedOptions<SiteOptions>(new SiteOptions())).SignOutEverywhere(t.Db.FindAccount("владік")!, null);
        Assert.NotNull(r.Account);

        var old = site.Request(stolen);
        Assert.False(Auth.IsUser(old));
        Assert.False(Auth.IsAdmin(old));
        Assert.StartsWith(Auth.Guest, Auth.Nick(old));
        // А свіжа сесія (та, що Signed кладе цій вкладці) — знову адмін.
        Assert.True(Auth.IsAdmin(site.Request(site.SessionFor("владік"))));
    }

    [Fact]
    public void Sign_out_everywhere_with_a_password_asks_for_it_and_keeps_it()
    {
        using var t = new TempDb();
        t.Db.AddAccount("Оля", Auth.HashPassword("секрет123", out var salt), salt);
        var site = new Site(t, new AuthOptions());
        var accounts = new Accounts(t.Db, new FixedOptions<SiteOptions>(new SiteOptions()));
        var old = site.SessionFor("Оля");

        var miss = accounts.SignOutEverywhere(t.Db.FindAccount("Оля")!, "не той");
        Assert.Equal(401, miss.Status);
        Assert.True(Auth.IsUser(site.Request(old)));   // не той пароль — нічого не змінилось

        Assert.NotNull(accounts.SignOutEverywhere(t.Db.FindAccount("Оля")!, "секрет123").Account);
        Assert.False(Auth.IsUser(site.Request(old)));
        Assert.NotNull(accounts.Login("Оля", "секрет123").Account);   // пароль той самий, лише сіль нова
    }
}
