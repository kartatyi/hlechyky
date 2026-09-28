using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hlechyky.Games;

/// <summary>
/// Підключення ігрового каркаса (WP0): кімнати, реєстр, розсилка, тик. Program.cs кличе обидва методи;
/// їхній вміст — власність WP0. Сервіси економіки підключає EconomySetup.
/// </summary>
public static class GamesSetup
{
    public static IServiceCollection AddHlechykyGames(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<GameEvents>();
        // Явна фабрика: у реєстру є ще один, необов'язковий параметр (додаткові збірки для тестів),
        // і контейнер не вміє його вгадати.
        services.AddSingleton(sp => new Registry(sp.GetService<ILogger<Registry>>()));
        services.AddSingleton<Rooms>();
        services.AddSingleton<Calls>();                    // заклики за стіл: «📣 Покликати», /клич, «покликати ще раз»
        services.AddSingleton<Broadcaster>();
        services.AddSingleton<RateGate>();
        // Broadcaster знаходиться при першому Post, а не при побудові графа: інакше
        // Rooms → IStakes (економіка) → IOutbox → Broadcaster → Rooms замикає коло (див. DeferredOutbox).
        services.AddSingleton<IOutbox>(sp => new DeferredOutbox(sp.GetRequiredService<Broadcaster>));
        // Заглушки, щоб сервер піднімався без економіки. AddHlechykyGames кличеться раніше за
        // AddHlechykyEconomy, тому TryAdd тут завжди виграє — справжні реалізації WP1 ставить через
        // services.Replace(ServiceDescriptor.Singleton<IStakes, Economy>()).
        services.TryAddSingleton<IStakes, NoStakes>();
        services.TryAddSingleton<IGameStore, MemoryGameStore>();
        services.AddHostedService<TickEngine>();
        services.AddSingleton<GameAdded>();                // «🆕»: коли сервер уперше побачив гру
        services.AddSingleton<GameNews>();                 // «що нового»: яку версію оновлення гри нік уже бачив
        services.AddSingleton<Tournament>();
        services.AddHostedService(sp => sp.GetRequiredService<Tournament>());
        Impl.ClickerGuildSetup.AddClickerGuild(services);   // цех Гончарного кола: віз, дарунки, хата друга
        // «Вгадай мелодію»: один на всі столи — щоб пісню з добірки не качали двічі два столи водночас
        services.AddSingleton(sp => new Impl.MelodyLibrary(
            sp.GetService<Db>(), sp.GetService<Microsoft.Extensions.Options.IOptionsMonitor<YtDlpOptions>>(),
            sp.GetService<YtMusicClient>(), sp.GetService<YtDlpService>(), Impl.MelodyClassics.Default,
            sp.GetService<Microsoft.Extensions.Options.IOptionsMonitor<MelodyOptions>>(), sp.GetService<ILogger<Impl.MelodyLibrary>>()));
        services.AddSingleton<Impl.IMelodySource>(sp => sp.GetRequiredService<Impl.MelodyLibrary>());
        Impl.SvoyaSetup.AddSvoya(services);                 // «Своя гра»: пакети запитань
        Impl.RallySetup.AddRally(services);   // «Сільське ралі»: рекорди кіл трас
        Impl.DuelSetup.AddDuel(services);                   // Дуель: спільні рекорди реакції («найшвидша рука»)
        Impl.VohnykSetup.AddVohnyk(services);              // «Вогник і Крапля»: прогрес рівнів і найкращі часи пар
        Impl.DotepySetup.AddDotepy(services);               // «Дотепи»: голос Глека, голос публіки
        Impl.GeoSetup.AddGeo(services);   // «Де це?»: фото з Вікісховища в cache/geo
        Impl.BattleshipSetup.AddBattleship(services);       // Морський бій: гаманці шелягів для «⚓ Арсеналу»
        Impl.SkilkySetup.AddSkilky(services);   // «Скільки?»: фото «Якого року?» в cache/skilky, таблиця дня
        Impl.PictionarySetup.AddPictionary(services);       // Піктіонарі: публічний альбом і рекорди пар
        return services;
    }

    public static WebApplication MapHlechykyGames(this WebApplication app)
    {
        // Лобі будується з каталогу, а не з хардкоду в JS: додав клас гри — вона з'явилась на сайті.
        app.MapGet("/api/games/catalog", (Registry registry, FrontPrint front, GameAdded added) =>
            new Catalog(registry.Catalog, Rooms.Stakes, front.Games(), added.Map(registry.Catalog)));
        GameNews.Map(app);                                  // /api/games/news — «бачив що нового»
        Impl.ClickerGuildSetup.MapClickerGuild(app);        // /api/games/clicker/guild і /house
        Impl.MelodyClips.Map(app);                          // /api/games/melody/<токен>.mp3 — уривки «Вгадай мелодію»
        Impl.SvoyaSetup.MapSvoya(app);                      // /api/games/svoya/… — пакети «Своєї гри»
        Impl.VohnykSetup.MapVohnyk(app);                   // /api/games/vohnyk/best — таблиця рівня
        Impl.DotepySetup.MapDotepy(app);                    // /api/games/dotepy/jury — голос публіки «Дотепів»
        Impl.GeoSetup.MapGeo(app);   // /api/games/geo/<токен>.jpg
        Impl.SkilkySetup.MapSkilky(app);   // /api/games/skilky/photo/<токен>.jpg
        Impl.PictionarySetup.MapPictionary(app);            // /api/games/pictionary/… — альбом, 📌, реакції глядачів, пари
        return app;
    }
}
