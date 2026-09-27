using System.Collections.Concurrent;

namespace Hlechyky.Games.Economy;

/// <summary>Одна ачівка з каталогу. <see cref="Hidden"/> — не показувати, поки не здобув.</summary>
public sealed record Achievement(string Key, string Title, string Text, string Icon, int Reward, bool Hidden = false);

/// <summary>Каталог ачівок (ARCHITECTURE §8). Ключі стабільні: вони лежать у базі.</summary>
public static class AchievementCatalog
{
    public static readonly IReadOnlyList<Achievement> All =
    [
        new("first-game",   "Перший крок",     "Дограти першу партію з людиною", "🚪", 5),
        new("first-win",    "Перша перемога",  "Вхопити першу перемогу", "🥇", 10),
        new("ten-wins",     "Десятка",         "Десять перемог", "🔟", 25),
        new("chess-5",      "Шахіст",          "П'ять перемог у шахах", "♟", 30),
        new("checkers-5",   "Шашист",          "П'ять перемог у шашках", "⛀", 30),
        new("all-boards",   "Настільний",      "Перемога в кожній настільній грі", "🎲", 50),
        new("streak-3",     "Серія",           "Три перемоги поспіль", "🔥", 15),
        new("scrabble-30",  "Ерудит",          "Бахнути слово на 30 очок і більше", "🔤", 20),
        new("mines-fast",   "Сапер",           "Сапер дня швидше за хвилину", "💣", 20),
        new("wordle-2",     "З двох спроб",    "Глек-слово з двох спроб", "🎯", 20),
        new("wordle-7",     "Тиждень слів",    "Сім днів Глек-слова поспіль", "📅", 30),
        new("duel-fast",    "Швидка рука",     "Постріл швидше за 200 мс", "🤠", 15),
        new("duel-10",      "Ковбой",          "Десять перемог у дуелі", "🔫", 20),
        new("mafia-win",    "Мафіозі",         "Перемога за мафію", "🕶", 15),
        new("sheriff",      "Комісар",         "Спіймати мафію на перевірці", "🔎", 15),
        new("mafia-maniac", "Маньяк",          "Пересидіти в мафії і село, і мафію", "🔪", 25),
        // Конкурс реклами прибрано 26.09.2026, і нових «Голосів села» вже не буде. Рядок лишається: здобуті лежать
        // у базі, а профіль показує лише те, що є в каталозі. Hidden — щоб недосяжну ачівку ніде не обіцяли.
        new("ad-winner",    "Голос села",      "Виграти конкурс реклами", "📢", 25, Hidden: true),
        new("svoya-win",    "Знавець",         "Перемога у «Своїй грі» на двох і більше", "🎓", 15),
        new("pozyvni-4",    "Одним словом",    "Підказка на чотири слова, і команда взяла всі", "🗝", 25),
        new("pozyvni-edge", "На волосині",     "Виграли в позивні останньою дозволеною здогадкою", "🪢", 20),
        new("potter-1k",    "Гончар",          "Тисяча глеків у гончарному колі", "🏺", 10),
        new("potter-100k",  "Майстер-гончар",  "Сто тисяч глеків", "🏆", 30),
        new("potter-1m",    "Мільйон глеків",  "Мільйон глеків за весь час", "🧺", 15),
        new("potter-1b",    "Глечаний магнат", "Мільярд глеків за весь час", "💎", 40),
        new("potter-1t",    "Цар-гончар",      "Трильйон глеків за весь час", "👑", 60),
        new("potter-fire",  "Перший обпал",    "Обпалив майстерню за клейма майстра", "🔥", 20),
        new("potter-golden","Ловець розписних","Впіймав 50 розписних глеків", "🎨", 25),
        new("potter-museum","Музей у хаті",    "Зібрав усі розписи глеків", "🖼", 50),
        new("potter-grab",  "Спритні руки",    "Спіймав 100 глеків, що падали з полиці", "🤲", 30),
        new("potter-streak","Жодного розбитого","Десять глеків з полиці поспіль", "🧤", 25),
        // Сьоме оновлення «Ремесло» (docs/games/specs/clicker-v7.md): гра сама видає їх через Ctx.Award(0, 0, "ach:<key>").
        new("potter-ware-1",      "Перший виріб",      "Виліпив на колі перший виріб", "🏺", 10),
        new("potter-ware-1k",     "Тисяча виробів",    "Виліпив тисячу виробів", "🧱", 30),
        new("potter-kiln-perfect","Дзвінке горно",     "Усі вироби в горні вийшли дзвінкими", "🔔", 25),
        new("potter-album-row",   "Уся колекція виробу","Один виріб у всіх розписах", "📒", 25),
        new("potter-album-all",   "Альбом майстра",    "Заповнив увесь альбом виробів", "📚", 80),
        new("potter-museum-shards","Трипільський музей","Знайшов у глинищі всі трипільські речі", "🏺", 40),
        new("potter-stove",       "Кахляна піч",       "Склав кахляну піч у хаті", "🧱", 40),
        new("potter-mastery",     "Золоті руки",       "Майстерність десятого рівня в одному виробі", "🖐", 30),
        new("potter-rep",         "Шана в селі",       "Найвища репутація в одному з гончарних сіл", "🤝", 30),
        new("potter-guest",       "Гостинна хата",     "Прийняв двадцять гостей", "🚪", 20),
        new("potter-wagon",       "Віз на ярмарок",    "Разом із цехом відправив денний віз", "🛒", 30),
        new("potter-gift",        "Щедра душа",        "Подарував друзям десять виробів", "🎁", 20),
        new("potter-rank",        "Цехмістр",          "Дійшов до найвищого цехового рангу", "🎖", 60),
        // Дев'яте оновлення «Округа» (docs/games/specs/clicker-v9.md).
        new("potter-eye",         "Майстер кивнув",    "Десять пройдених полиць Ока майстра", "👁", 20),
        new("potter-streak-50",   "П'ятдесят поспіль", "П'ятдесят глеків з полиці без жодного розбитого", "🏅", 40),
        new("potter-lucky",       "Щасливий",          "Сто щасливих кліків", "✨", 20),
        new("potter-cat",         "Кіт-приблуда",      "Погладив двадцять п'ять котів-мандрівників", "🐈", 20),
        new("potter-q4",          "Розкішний виріб",   "Перший розкішний виріб із горна", "💠", 30),
        new("potter-stoker",      "Палій не спить",    "Сто партій обпалив палій", "🕯", 25),
        new("potter-tech-all",    "Усі техніки",       "Відкрив усі вісім технік розпису", "🖌", 40),
        new("potter-ware-15",     "Усі вироби",        "Виліпив кожен із п'ятнадцяти виробів", "🫙", 40),
        new("potter-album-stars", "Дзвінкий альбом",   "Кожна клітинка альбому з зіркою", "🌟", 100),
        new("potter-stove-ring",  "Дзвінка піч",       "Уся кахляна піч із дзвінких кахель", "🔔", 50),
        new("potter-rep10",       "Шана на всю округу","Десятий рівень шани в одному з сіл", "🏘", 60),
        new("potter-treat",       "Хлібосол",          "Десять гостинців друзям", "🍞", 30),
        new("potter-elder",       "Старійшина",        "Найвищий ранг цеху — старійшина", "🎖", 80),
        new("potter-wonder",      "Дивовижа",          "Перша дивовижа в хаті", "🔮", 15),
        new("potter-wonders",     "Кунсткамера",       "Усі шістнадцять дивовиж", "🏛", 80),
        new("potter-look",        "Хата під себе",     "Перша оздоба хати за клейма", "🎨", 10),
        // Десяте оновлення «Глек на весь світ» (docs/games/specs/clicker-v10.md §12).
        new("potter-marks-40",    "Сорок віх",         "Сорок віх у майстерні водночас", "🪧", 30),
        new("potter-wheel-all",   "Коло без меж",      "Усі вісім віх «Швидшого кола»", "🌀", 60),
        new("potter-hryvnia",     "Гетьманська гривня","Наліпити за весь час на гривню — квадрильйон глеків", "📜", 20),
        new("potter-world",       "Навколо світу",     "Вирушити в кругосвітнє плавання", "🧭", 40),
        new("potter-paris",       "Золота медаль",     "Показати глеки на Всесвітній виставці в Парижі", "🏅", 60),
        new("potter-gold",        "Червоний золотий",  "Наліпити за весь час на червоний золотий", "💰", 60),
        new("potter-opishnia",    "Гончарна столиця",  "Дійти до останнього щабля — Опішні", "🏺", 100),
        // «potter-guest» уже зайнятий «Гостинною хатою» (гості села) — ключі в базі не міняють.
        new("potter-guest-first", "Перший гість",      "Виконати замовлення заморських гостей", "🏛", 30),
        new("potter-guest-max",   "Шанований у світі", "Шана одних заморських гостей — рівень 10", "🎖", 60),
        new("potter-guests-all",  "Свій у всьому світі","Усі шість заморських гостей — рівень 5 і вище", "🌍", 100),
        new("high-roller",  "Ставка",          "Виграв партію зі ставкою 25", "💰", 15),
        new("listener-10h", "Слухач",          "Десять годин тусні на сайті", "🎧", 15),
        new("listener-100h","Меломан",         "Сто годин тусні на сайті", "📻", 50),
        new("rich-100",     "Сотня",           "Сто черепків на балансі", "🏦", 10),
        // хвиля 2: bricks — гра просить сама через Ctx.Award(seat, 0, "ach:<key>")
        new("bricks-four",      "Четвірка",       "Закрив чотири ряди одним ударом у Цеглинах", "🧱", 15),
        new("bricks-sprint-2m", "Швидкий муляр",  "Сорок рядів швидше за дві хвилини", "⏱", 25),
        // хвиля 2: glekomet
        new("glekomet-sniper", "Далекобійник",  "Глекомети: пряме влучання в чужу хату з пів села", "🎯", 20),
        new("glekomet-clean",  "Ні подряпини",  "Глекомети: перемога з цілою хатою — усі 100 здоров'я", "🏠", 25),
        // хвиля 2: crowd
        new("crowd-eye",   "Око-алмаз",       "Юрма: влучив у гравця першим камінцем раунду", "🎯", 15),
        new("crowd-quiet", "Тихий покупець",  "Юрма: виграв раунд покупками, не стрельнувши жодного разу", "🧺", 15),
        // хвиля 2: dice
        new("dice-exact",    "Точно!",            "Вгадав ставку рівно — і повернув кісточку", "🎯", 15),
        new("dice-comeback", "З однієї кісточки", "Виграв партію «Під глеком», побувавши на одній кісточці", "🏺", 25),
        // хвиля 2: runner (Стрибозаври, Забіг дня, Лелеки) — гра просить їх сама через Ctx.Award(seat, 0, "ach:<key>")
        new("dino-far",     "Далекий забіг",   "Два кілометри від лавини за один забіг", "🦖", 20),
        new("dino-snow",    "Сніжкою в спину", "Твоя сніжка збила з ніг того, хто попереду", "❄", 10),
        new("storks-clean", "Чисте небо",      "Хвилина польоту в Лелеках без жодного зачепу", "🪽", 15),
        // хвиля 2: сільське ралі
        new("rally-win3",   "Перший на селі",  "Переміг у «Сільському ралі», де на старті було троє й більше", "🏁", 15),
        new("rally-record", "Рекорд траси",    "Найкраще коло траси за весь час — перебив чужий рекорд", "⏱", 20),
        // хвиля 2: icefloe
        new("icefloe-dry",     "Сухі валянки",  "Виграв партію в Крижині, жодного разу не шубовснувши", "🥾", 20),
        new("icefloe-push5",   "Штовхач",       "П'ятеро випханих у воду за одну партію Крижини", "💨", 15),
        // хвиля 2: hockey
        new("hockey-dry",      "Сухий рахунок", "Виграв аерохокей, не пропустивши жодного гола", "🧤", 20),
        new("hockey-comeback", "Камбек",        "Виграв аерохокей, програючи три голи", "🔁", 25),
        // хвиля 2: spy — гра просить сама через Ctx.Award(seat, 0, "ach:<key>")
        new("spy-guess",    "Шпигун-віртуоз",  "Шпигун назвав локацію правильно", "🕵", 20),
        new("spy-catch",    "Контррозвідка",   "Висунув підозру, яка спіймала шпигуна", "🔦", 15),
        // хвиля 2: bluff
        new("bluff-fox",  "Хитрий лис",     "Одна брехня в «Байкарях» надурила двох і більше", "🦊", 20),
        new("bluff-nose", "Нюх на правду",  "Вгадав правду в кожному питанні партії «Байкарів»", "👃", 25),
    ];

    static readonly Dictionary<string, Achievement> ByKey = All.ToDictionary(a => a.Key, StringComparer.Ordinal);

    public static Achievement? Get(string key) => ByKey.GetValueOrDefault(key);
}

/// <summary>
/// Видача ачівок. Перевірки — на подіях, які приносить <see cref="Rewards"/> (партія, соло, нагорода),
/// плюс онлайн-хвилини з тікера і зміна балансу з <see cref="Economy"/>. Кожна ачівка видається один раз:
/// сторож — <c>INSERT OR IGNORE</c> у таблицю, а не перевірка перед вставкою.
/// </summary>
public sealed class Achievements
{
    /// <summary>«Виграй N партій у грі X» — усе, що рахується однаково.</summary>
    static readonly (string Key, string Game, int Wins)[] WinRules =
    [
        ("chess-5", "chess", 5),
        ("checkers-5", "checkers", 5),
        ("duel-10", "duel", 10),
    ];

    readonly EconomyStore _store;
    readonly Economy _economy;
    readonly GameNames _names;
    readonly IClock _clock;
    readonly IOutbox _outbox;
    /// <summary>Що вже видано цьому серверу: інакше «Сотня» і «Меломан» довбали б базу на кожен рух грошей
    /// і на кожну хвилину онлайну — довічно, бо давно видана ачівка все одно щоразу йшла б у INSERT.</summary>
    readonly ConcurrentDictionary<string, byte> _granted = new(StringComparer.Ordinal);

    public Achievements(EconomyStore store, Economy economy, GameNames names, IClock clock, IOutbox outbox)
    {
        _store = store; _economy = economy; _names = names; _clock = clock; _outbox = outbox;
        _economy.Changed += OnBalance;
    }

    /// <summary>Видати ачівку, якщо її ще не було. true — щойно видали.</summary>
    public bool Unlock(string nick, string key)
    {
        var a = AchievementCatalog.Get(key);
        if (a is null) return false;
        var nickKey = Economy.Key(nick);
        if (nickKey.Length == 0) return false;
        // у пам'яті — і ті, що ми щойно видали, і ті, які база відбила як наявні: обидва випадки означають
        // «більше сюди не ходимо». А от якщо база впала — мітку знімаємо, щоб наступна подія спробувала ще раз
        var mark = $"{nickKey}|{key}";
        if (!_granted.TryAdd(mark, 0)) return false;
        bool granted;
        try { granted = _store.Unlock(nickKey, nick, key, _clock.UtcNow); }
        catch (Exception) { _granted.TryRemove(mark, out _); throw; }
        if (!granted) return false;

        if (a.Reward > 0)
            _economy.Grant(nick, a.Reward, $"ach:{key}", $"ach:{key}:{nickKey}",
                $"+{a.Reward} {Economy.Shards(a.Reward)}: ачівка «{a.Title}»");
        _outbox.Post(new AchievementUnlocked(nick, a.Key, a.Title, a.Text, a.Icon, a.Reward));
        _outbox.Post(new Journal($"🏅 {nick} хапає ачівку «{a.Title}»" +
            (a.Reward > 0 ? $" (+{a.Reward} {Economy.Shards(a.Reward)})" : "")));
        return true;
    }

    public bool Has(string nick, string key) => _store.HasAchievement(Economy.Key(nick), key);

    /// <summary>Що людина вже здобула — для профілю.</summary>
    public List<(Achievement Info, DateTimeOffset At)> Of(string nick) =>
        _store.AchievementsOf(Economy.Key(nick))
            .Select(u => (Info: AchievementCatalog.Get(u.Key), u.At))
            .Where(x => x.Info is not null)
            .Select(x => (x.Info!, x.At))
            .ToList();

    // ---------- перевірки на подіях ----------

    /// <summary>Кличе <see cref="Rewards"/> уже після запису результатів у базу.</summary>
    public void OnRoomFinished(RoomFinishedEvent e, string nick, string outcome)
    {
        var nickKey = Economy.Key(nick);
        var all = DateTimeOffset.MinValue;

        Unlock(nick, "first-game");
        if (outcome != "win") return;

        var wins = _store.CountResults(nickKey, null, "win", all);
        if (wins >= 1) Unlock(nick, "first-win");
        if (wins >= 10) Unlock(nick, "ten-wins");

        foreach (var (key, game, need) in WinRules)
            if (game == e.GameId && _store.CountResults(nickKey, game, "win", all) >= need)
                Unlock(nick, key);

        // «Настільний» — перемога в КОЖНІЙ настільній грі, яку знає платформа (реєстр збірки, ARCHITECTURE §8):
        // планка однакова для всіх і не залежить від того, у що встигли пограти інші. Приховані настільні
        // (морський бій, доміно, дурень) — теж настільні. На порожньому списку ігор (ранні гілки, тести)
        // ачівку не даємо: інакше перша ж перемога зачиняла б її за 50 черепків.
        var boards = _names.All.Where(i => i.Group == GameGroup.Board && !i.Solo).Select(i => i.Id).ToList();
        if (boards.Count >= 3)
        {
            var won = _store.GamesWon(nickKey);
            if (boards.All(won.Contains)) Unlock(nick, "all-boards");
        }

        var recent = _store.RecentResults(nickKey, 3, multiplayerOnly: true);
        if (recent.Count >= 3 && recent.All(r => r.Outcome == "win")) Unlock(nick, "streak-3");

        if (e.Stake >= 25) Unlock(nick, "high-roller");
    }

    /// <summary>
    /// Соло-результат: гончарне коло рахує глеки за весь час, а дуель кладе сюди найкращу реакцію в
    /// мілісекундах (specs/duel.md — «Швидка рука» перевіряється саме тут, а не в самій грі).
    /// </summary>
    public void OnSolo(SoloScoreEvent e)
    {
        if (e.GameId == "clicker")
        {
            if (e.Score >= 1_000) Unlock(e.Nick, "potter-1k");
            if (e.Score >= 100_000) Unlock(e.Nick, "potter-100k");
            if (e.Score >= 1_000_000) Unlock(e.Nick, "potter-1m");
            if (e.Score >= 1_000_000_000) Unlock(e.Nick, "potter-1b");
            if (e.Score >= 1_000_000_000_000) Unlock(e.Nick, "potter-1t");
            // Десяте оновлення: гривня (10¹⁵ за весь час) і червоний золотий (10²⁷).
            if (e.Score >= 1e15) Unlock(e.Nick, "potter-hryvnia");
            if (e.Score >= 1e27) Unlock(e.Nick, "potter-gold");
        }
        if (e.GameId == "duel" && e.Score is > 0 and < 200) Unlock(e.Nick, "duel-fast");
    }

    /// <summary>Результат щоденної головоломки (уже записаний), <paramref name="streak"/> — серія разом із сьогодні.</summary>
    public void OnDaily(string nick, DailyRow row, int streak)
    {
        if (!row.Solved) return;
        if (row.Game == "wordle")
        {
            if (row.Attempts is > 0 and <= 2) Unlock(nick, "wordle-2");
            if (streak >= 7) Unlock(nick, "wordle-7");
        }
        if (row.Game.StartsWith("mines", StringComparison.Ordinal) && row.Ms is > 0 and < 60_000)
            Unlock(nick, "mines-fast");
    }

    /// <summary>
    /// Позастандартна нагорода. Гра, якій треба ачівка, якої платформа сама не побачить (роль у мафії,
    /// слово на 30 очок, реакція швидша за 200 мс), кличе <c>Ctx.Award(seat, 0, "ach:&lt;key&gt;")</c>.
    /// </summary>
    public void OnAward(AwardEvent e)
    {
        if (e.Reason.StartsWith("ach:", StringComparison.Ordinal))
            Unlock(e.Nick, e.Reason[4..]);
    }

    /// <summary>Хвилини на сайті всього (не за день) — кличе EconomyTicker.</summary>
    public void OnOnlineMinutes(string nick, int totalMinutes)
    {
        if (totalMinutes >= 600) Unlock(nick, "listener-10h");
        if (totalMinutes >= 6_000) Unlock(nick, "listener-100h");
    }

    void OnBalance(string nick, int balance)
    {
        if (balance >= 100) Unlock(nick, "rich-100");
    }
}
