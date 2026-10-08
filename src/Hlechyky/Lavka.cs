using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Serialization;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Microsoft.Data.Sqlite;

namespace Hlechyky;

// =====================================================================================================================
// «Лавка Дядька Глека»: кастомізація профілю за черепки. Рішення власника (26.09.2026): ціни фіксовані, усе куплене —
// назавжди (жодної оренди й жодних витрат «за раз»: присвята й феєрверк — вічні вміння з перервою), купують і вдягають
// лише акаунти, дарувати можна — акаунту й лише те, чого в нього ще нема. Контракт — _tools/lavka-contract.md.
// =====================================================================================================================

/// <summary>Слоти профілю, вони ж види речей. <see cref="Perk"/> — вміння: їх не вдягають, ними користуються.</summary>
public static class LavkaKind
{
    public const string Icon = "icon";
    public const string Frame = "frame";
    public const string Color = "color";
    public const string Title = "title";
    public const string Bg = "bg";
    /// <summary>Гімн переможця (docs/games/specs/anthem.md): звучить за столом, коли людина виграє партію на кількох.</summary>
    public const string Anthem = "anthem";
    /// <summary>Святкування перемоги (docs/games/specs/flair.md §3): анімація на столі переможця. Вдягається, як гімн.</summary>
    public const string Fx = "fx";
    /// <summary>
    /// Прокльон (docs/games/specs/flair.md §1): не вдягають і не мають назавжди — його насилають на іншого, і він звучить
    /// на трьох його програшах. Звичайним «Купити»/«Подарувати» не продається.
    /// </summary>
    public const string Curse = "curse";
    public const string Perk = "perk";
}

/// <summary>
/// Коли сезонну річ можна купити: київські дні «MM-dd» від і до включно. <see cref="From"/> пізніше за <see cref="To"/> —
/// сезон іде через Новий рік (ялинка: 12-15 … 01-15). Куплене поза сезоном нікуди не дівається — не купиш лише нове.
/// </summary>
public sealed record LavkaSeason(string From, string To)
{
    public bool Open(string monthDay) => string.CompareOrdinal(From, To) <= 0
        ? string.CompareOrdinal(monthDay, From) >= 0 && string.CompareOrdinal(monthDay, To) <= 0
        : string.CompareOrdinal(monthDay, From) >= 0 || string.CompareOrdinal(monthDay, To) <= 0;

    /// <summary>«15.12» — з якого дня річ знову в Лавці.</summary>
    public string Returns => $"{From[3..]}.{From[..2]}";
}

/// <summary>
/// Річ із Лавки. <paramref name="Art"/> — те, чим її малює браузер: емодзі значка, id рамки й тла, відтінок кольору
/// (число) або «rainbow», текст титулу. <paramref name="Ach"/> — титул за ачівку: не купується, з'являється в шафі сам.
/// </summary>
public sealed record LavkaItem(string Id, string Kind, string Title, int Price, object Art, int Tier = 0,
    LavkaSeason? Season = null, string? Ach = null);

/// <summary>
/// Що вдягнуто, готове до малювання (<c>color</c> — відтінок числом або «rainbow»). Так його бачать усі.
/// <paramref name="Photo"/> — адреса своєї фотки з версією (<c>/api/lavka/photo/…</c>); нема фото — поля в JSON нема
/// зовсім: старий клієнт і старі перевірки бачать вигляд рівно таким, яким він був до фоток.
/// </summary>
public sealed record LavkaLook(string? Icon, string? Frame, object? Color, string? Title, string? Bg,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Photo = null);

/// <summary>
/// <c>Art</c> гімну: готовий — <c>{ emoji, url }</c> (файл у <c>web/static/anthems</c>), свій трек — <c>{ emoji, own: true }</c>
/// (адреса в кожного своя, її несе <c>ownAnthem</c> вітрини). Порожніх полів у JSON нема зовсім — браузер розрізняє за ними.
/// </summary>
public sealed record LavkaAnthemArt(
    [property: JsonPropertyName("emoji")] string Emoji,
    [property: JsonPropertyName("url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Url = null,
    [property: JsonPropertyName("own"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Own = null);

/// <summary>
/// Що зіграти за столом, коли людина виграла: назва, емодзі й адреса mp3 (<see cref="Lavka.AnthemOf"/>).
/// <paramref name="Len"/> — скільки звучить, секунди (null — невідомо): з ним святкування триває стільки ж у кожній вкладці.
/// </summary>
public sealed record AnthemPlay(string Title, string Emoji, string Url, double? Len = null);

/// <summary><c>Art</c> святкування перемоги: <c>{ emoji }</c> — саму анімацію малює браузер за id речі.</summary>
public sealed record LavkaFxArt([property: JsonPropertyName("emoji")] string Emoji);

/// <summary>Прокльон спрацював на програші: що зіграти за столом і скільки програшів йому ще лишилось.</summary>
public sealed record CurseHit(long Id, string Title, string Emoji, string Url, int Left);

/// <summary>
/// Каталог Лавки. Живе в коді, а не в базі: ціни й асортимент — рішення власника, і нова річ приходить разом із
/// клієнтом, який уміє її намалювати. Id — латиницею й наскрізно унікальні (на них тримаються покупки в базі), тому
/// там, де контракт дав одне слово двом речам, одна з них має інше: колір «Мідь» — <c>cuprum</c> (бо <c>copper</c> —
/// мідна рамка), титул «Нічна сова» — <c>nightowl</c> (бо <c>owl</c> — значок 🦉).
/// </summary>
public static class LavkaCatalog
{
    public const string Dedication = "dedication";
    public const string Fireworks = "fireworks";
    /// <summary>Вміння «Своя фотка»: ставити своє фото замість літери чи значка (LavkaPhotos.cs).</summary>
    public const string Photo = "photo";
    /// <summary>
    /// «Свій трек»: гімн із власного уривка (LavkaAnthems.cs). Річ полиці гімнів, а не вміння — вдягається й знімається
    /// звичайним слотом, але мовчить, доки людина не поставила уривок. Єдиний id із дефісом — так його назвав контракт.
    /// </summary>
    public const string OwnAnthem = "own-anthem";
    public const string AnthemUrlPrefix = "/static/anthems/";
    public const string CurseUrlPrefix = "/static/curses/";
    /// <summary>
    /// Вміння «Свій дзвінок» (docs/games/specs/flair.md §2): свій гімн дзвенить у того, кого особисто кличеш за стіл.
    /// Котрий — лежить у <c>lavka_worn</c> під слотом <see cref="RingSlot"/> (це не вид речі, а місце для id гімну).
    /// </summary>
    public const string Ring = "ring";
    public const string RingSlot = "ring";
    /// <summary>Скільки програшів «важить» один прокльон.</summary>
    public const int CurseHits = 3;
    public const int CursePrice = 500;

    /// <summary>
    /// Слоти, які вдягають (усе, крім вмінь і прокльонів), — у тому порядку, як їх показує профіль. Гімн і святкування —
    /// останні й не частина <see cref="LavkaLook"/>: вони живуть лише за столом переможця, і розсилати їх усім на кожне
    /// вдягання ні до чого.
    /// </summary>
    public static readonly IReadOnlyList<string> Slots =
        [LavkaKind.Icon, LavkaKind.Frame, LavkaKind.Color, LavkaKind.Title, LavkaKind.Bg, LavkaKind.Anthem, LavkaKind.Fx];

    static LavkaItem Icon(string id, string title, string art, int price, LavkaSeason? season = null) =>
        new(id, LavkaKind.Icon, title, price, art, price switch { <= 150 => 1, <= 250 => 2, _ => 3 }, season);

    static LavkaItem Frame(string id, string title, int price) => new(id, LavkaKind.Frame, title, price, id);
    static LavkaItem Color(string id, string title, int hue) => new(id, LavkaKind.Color, title, 250, hue);
    static LavkaItem Title(string id, string title) => new(id, LavkaKind.Title, title, 500, title);
    static LavkaItem Earned(string id, string title, string ach) => new(id, LavkaKind.Title, title, 0, title, Ach: ach);
    static LavkaItem Bg(string id, string title, int price) => new(id, LavkaKind.Bg, title, price, id);
    // Рівень гімну — з ціни, як у значків: 300 — звичайний, 600 — рідкісний, дорожче — особливий
    static LavkaItem Anthem(string id, string title, string emoji, int price) =>
        new(id, LavkaKind.Anthem, title, price, new LavkaAnthemArt(emoji, AnthemUrlPrefix + id + ".mp3"), AnthemTier(price));
    static int AnthemTier(int price) => price switch { <= 300 => 1, <= 600 => 2, _ => 3 };
    // Прокльон грає готовий файл: сумний тромбон — той самий, що й гімн, решта — свої в web/static/curses
    static LavkaItem Curse(string id, string title, string emoji, string? url = null) =>
        new(id, LavkaKind.Curse, title, CursePrice, new LavkaAnthemArt(emoji, url ?? CurseUrlPrefix + id + ".mp3"));
    static LavkaItem Fx(string id, string title, string emoji, int price) =>
        new(id, LavkaKind.Fx, title, price, new LavkaFxArt(emoji), price switch { <= 400 => 1, <= 600 => 2, _ => 3 });

    public static readonly IReadOnlyList<LavkaItem> All =
    [
        // значки: звичайні, рідкісні, особливі
        Icon("pot", "Глечик", "🏺", 150), Icon("sunflower", "Соняшник", "🌻", 150), Icon("cat", "Кіт", "🐈", 150),
        Icon("dog", "Пес", "🐕", 150), Icon("headphones", "Навушники", "🎧", 150), Icon("guitar", "Гітара", "🎸", 150),
        Icon("mug", "Кухоль", "🍺", 150), Icon("ball", "М'яч", "⚽", 150),
        Icon("owl", "Сова", "🦉", 250), Icon("fox", "Лис", "🦊", 250), Icon("frog", "Жабка", "🐸", 250),
        Icon("melon", "Кавун", "🍉", 250), Icon("pepper", "Перчик", "🌶", 250), Icon("dice", "Кубик", "🎲", 250),
        Icon("rocket", "Ракета", "🚀", 250), Icon("alien", "Прибулець", "👾", 250),
        Icon("fire", "Вогонь", "🔥", 300), Icon("gem", "Діамант", "💎", 300), Icon("dragon", "Дракон", "🐉", 300),
        Icon("moon", "Місяць", "🌙", 300), Icon("bolt", "Блискавка", "⚡", 300), Icon("unicorn", "Єдиноріг", "🦄", 300),
        Icon("mushroom", "Мухомор", "🍄", 300), Icon("violin", "Скрипка", "🎻", 300),
        // сезонні: ялинка на свята, писанка на Великдень, прапор до Дня Незалежності, гарбуз на осінь (записка Mariana, 28.09)
        Icon("tree", "Ялинка", "🎄", 300, new("12-15", "01-15")),
        Icon("egg", "Писанка", "🥚", 300, new("04-01", "05-10")),
        Icon("flag", "Прапор", "🇺🇦", 300, new("08-18", "08-31")),
        Icon("pumpkin", "Гарбуз", "🎃", 300, new("09-15", "11-30")),
        // Календар на весь рік (28.09): щодня в Лавці є хоч один сезонний значок. Свята — за новим календарем
        // (Миколая 6.12, Різдво 25.12, Сорок святих 9.03, Купала 24.06, Спаси 1/6/16.08, Покрова 1.10).
        Icon("snowman", "Сніговик", "⛄", 300, new("12-01", "02-29")),
        Icon("stnick", "Подарунок від Миколая", "🎁", 300, new("12-01", "12-10")),
        Icon("xstar", "Різдвяна зірка", "⭐", 300, new("12-20", "01-10")),
        Icon("valentine", "Валентинка", "💘", 300, new("02-07", "02-21")),
        Icon("skylark", "Жайворонок", "🐦", 300, new("03-01", "03-31")),
        Icon("tulip", "Тюльпан", "🌷", 300, new("03-01", "03-15")),
        Icon("chick", "Курчатко", "🐣", 300, new("04-01", "05-10")),
        Icon("chestnut", "Каштан", "🌰", 300, new("05-01", "05-31")),
        Icon("thread", "Нитка на вишиванку", "🧵", 300, new("05-10", "05-31")),
        Icon("strawberry", "Полуниця", "🍓", 300, new("06-01", "06-30")),
        Icon("fern", "Цвіт папороті", "🌿", 300, new("06-20", "07-10")),
        Icon("beach", "Відпустка", "⛱️", 300, new("07-01", "08-15")),
        Icon("apple", "Спасове яблуко", "🍎", 300, new("08-01", "08-20")),
        Icon("satchel", "Портфель", "🎒", 300, new("08-25", "09-15")),
        Icon("shield", "Щит захисника", "🛡️", 300, new("09-25", "10-10")),
        Icon("maple", "Кленовий лист", "🍁", 300, new("10-01", "11-15")),
        Icon("quill", "Перо", "✒️", 300, new("11-01", "11-15")),

        Frame("copper", "Мідна", 400), Frame("silver", "Срібна", 1000), Frame("gold", "Золота", 2500), Frame("alive", "Жива", 5000),

        Color("ember", "Полум'я", 0), Color("cuprum", "Мідь", 18), Color("amber", "Бурштин", 32), Color("linden", "Липа", 62),
        Color("grass", "Трава", 85), Color("emerald", "Смарагд", 110), Color("mint", "М'ята", 135), Color("wave", "Морська хвиля", 158),
        Color("turquoise", "Бірюза", 178), Color("sky", "Небо", 195), Color("cornflower", "Волошка", 212), Color("sapphire", "Сапфір", 228),
        Color("lilac", "Бузок", 248), Color("violet", "Фіалка", 270), Color("fuchsia", "Фуксія", 292), Color("raspberry", "Малина", 318),
        new("rainbow", LavkaKind.Color, "Веселка", 1500, "rainbow"),

        Title("nightowl", "Нічна сова"), Title("soul", "Душа компанії"), Title("quiet", "Тихий слухач"),
        Title("chatking", "Король балачок"), Title("halfdj", "Диджей на півставки"), Title("amateur", "Гончар-аматор"),
        Title("lark", "Ранкова пташка"), Title("vet", "Бувалий гравець"),
        Earned("meloman", "Меломан", "listener-100h"), Earned("tsar", "Цар-гончар", "potter-1t"), Earned("sage", "Знавець", "svoya-win"),
        Earned("commissar", "Комісар", "sheriff"), Earned("cowboy", "Ковбой", "duel-10"),

        Bg("stars", "Зоряна ніч", 800), Bg("stripes", "Смуги", 800), Bg("vyshyvanka", "Вишиванка", 1200),
        Bg("petrykivka", "Петриківка", 1200), Bg("trypillia", "Трипілля", 1500),

        // гімни переможця: свої мелодії, синтезовані docs/games/dev/anthems-make.py, — без чужих прав
        Anthem("fanfare", "Фанфари", "🎺", 300), Anthem("drumroll", "Барабанний дріб", "🥁", 300),
        Anthem("chiptune", "Вісім біт", "👾", 300), Anthem("trombone", "Сумний тромбон", "🎷", 300),
        Anthem("dzen", "Дзень-дзелень", "🏺", 300),
        Anthem("trembita", "Трембіта", "📯", 600), Anthem("bayan", "Баян-туш", "🪗", 600),
        Anthem("bells", "Дзвони", "🔔", 600), Anthem("applause", "Оплески", "👏", 600),
        Anthem("hopak", "Гопак", "💃", 1000), Anthem("cosmos", "Космос", "🚀", 1000), Anthem("solemn", "Урочисто", "🎻", 1000),
        // свій уривок: дорожче за «Свою фотку» — це звук для всіх за столом, а не картинка
        new(OwnAnthem, LavkaKind.Anthem, "Свій трек", 3000, new LavkaAnthemArt("🎤", Own: true), AnthemTier(3000)),

        // святкування перемоги: анімація на столі переможця. «Глек танцює гопак» — glekhopak, бо hopak — уже гімн
        Fx("confetti", "Конфеті", "🎊", 400), Fx("shards", "Дощ черепків", "🏺", 600), Fx("sunflowers", "Соняшники", "🌻", 600),
        Fx("salute", "Салют", "🎆", 1000), Fx("glekhopak", "Глек танцює гопак", "💃", 1500),

        // прокльони: синтезовані docs/games/dev/curses-make.py, як і гімни, — без чужих прав
        Curse("sadtrombone", "Сумний тромбон", "🎷", AnthemUrlPrefix + "trombone.mp3"), Curse("boo", "Бу-у-у", "👎"),
        Curse("crickets", "Цвіркуни", "🦗"), Curse("funeral", "Похоронний марш", "⚰️"), Curse("goat", "Цап", "🐐"),
        Curse("clown", "Клоун", "🤡"),

        new(Dedication, LavkaKind.Perk, "Присвята в ефір", 1500, "🎙"),
        new(Fireworks, LavkaKind.Perk, "Феєрверк", 600, "🎆"),
        // своє фото на аватарку (записка Назара, 28.09): куплене вміння назавжди, саме фото міняється раз на добу
        new(Photo, LavkaKind.Perk, "Своя фотка", 2000, "📷"),
        // свій гімн дзвінком особистого заклику за стіл (docs/games/specs/flair.md §2)
        new(Ring, LavkaKind.Perk, "Свій дзвінок", 500, "🔔"),
    ];

    // ToDictionary падає на однакових id — і тоді падає все, що торкнеться каталогу: дубль не проскочить непомітно
    static readonly Dictionary<string, LavkaItem> ById = All.ToDictionary(i => i.Id, StringComparer.Ordinal);

    public static LavkaItem? Get(string? id) => string.IsNullOrWhiteSpace(id) ? null : ById.GetValueOrDefault(id.Trim().ToLowerInvariant());

    /// <summary>З чим присвятити пісню: ключ приходить від браузера, текст звучить в ефірі.</summary>
    public static readonly IReadOnlyList<(string Key, string Text)> Phrases =
    [
        ("love", "з любов'ю"), ("luck", "на удачу"), ("miss", "бо сумує"),
        ("just", "просто так"), ("night", "на добраніч"), ("thanks", "за вчорашнє"),
    ];

    public static string? Phrase(string? key)
    {
        var k = (key ?? "").Trim().ToLowerInvariant();
        foreach (var p in Phrases)
            if (p.Key == k) return p.Text;
        return null;
    }

    /// <summary>
    /// Назва речі в реченні, без зовнішніх лапок: «значок «Лис» 🦊», «Срібна рамка», «колір ніка «Небо»», «Феєрверк».
    /// Нею підписано рух гаманця («Лавка — Срібна рамка»).
    /// </summary>
    public static string Label(LavkaItem i) => i.Kind switch
    {
        LavkaKind.Icon => $"значок «{i.Title}» {i.Art}",
        LavkaKind.Frame => $"{i.Title} рамка",
        LavkaKind.Color => $"колір ніка «{i.Title}»",
        LavkaKind.Title => $"титул «{i.Title}»",
        LavkaKind.Bg => $"тло «{i.Title}»",
        LavkaKind.Anthem => $"гімн «{i.Title}»",
        LavkaKind.Fx => $"святкування «{i.Title}»",
        LavkaKind.Curse => $"прокльон «{i.Title}»",
        _ => i.Title,
    };

    /// <summary>Те саме за id; невідомий id — як є: річ, якої вже нема в каталозі, хай краще видна кодом.</summary>
    public static string Label(string id) => Get(id) is { } i ? Label(i) : id;

    /// <summary>Назва в лапках там, де своїх лапок нема: «дарує Петрові «Срібна рамка»», але «дарує Петрові значок «Лис» 🦊».</summary>
    public static string Quoted(LavkaItem i) => i.Kind is LavkaKind.Frame or LavkaKind.Perk ? $"«{Label(i)}»" : Label(i);

    static readonly ConcurrentDictionary<string, double?> Lens = new(StringComparer.Ordinal);

    /// <summary>
    /// Скільки секунд звучить готовий файл каталогу (<c>/static/…mp3</c> у <c>web/</c>), з точністю до десятої: міряється
    /// один раз за кадрами mp3 (<see cref="Mp3Duration"/>, без ffprobe) і пам'ятається до перезапуску. null — файла
    /// нема чи він не розібрався: тоді святкування триває свої 6 с.
    /// </summary>
    public static double? ClipSeconds(string? url) => url is null || !url.StartsWith("/static/", StringComparison.Ordinal) || url.Contains("..")
        ? null
        : Lens.GetOrAdd(url, u =>
        {
            try
            {
                var path = Path.Combine(Paths.Root, "web", u.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                var sec = File.Exists(path) ? Mp3Duration.Seconds(File.ReadAllBytes(path)) : 0;
                return sec > 0 ? Math.Round(sec, 1) : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        });
}

// =====================================================================================================================
// Сховище
// =====================================================================================================================

/// <summary>Рядок «хто що вдягнув»: <paramref name="Nick"/> — як нік пишеться (з акаунта), ним підписано вигляд.</summary>
public sealed record LavkaWornRow(string NickKey, string Nick, string Slot, string Item);

/// <summary>
/// Своя фотка, що стоїть зараз: <paramref name="File"/> — ім'я в <c>data/avatars</c> (хеш ніка + версія вмісту),
/// <paramref name="At"/> — коли поставлено. <see cref="Url"/> змінюється разом із фото — кеш браузера не заважає.
/// </summary>
public sealed record LavkaPhotoRow(string NickKey, string Nick, string File, int Bytes, DateTimeOffset At)
{
    public string Url => LavkaPhotos.UrlPrefix + File;
}

/// <summary>
/// Свій трек, що стоїть зараз: <paramref name="File"/> — вирізаний mp3 у <c>data/anthems</c> (нове ім'я на кожен уривок),
/// <paramref name="Title"/> — як людина його назвала (null — без назви), звідки й скільки грає — у мілісекундах.
/// </summary>
public sealed record LavkaAnthemRow(string NickKey, string Nick, string File, string? Title, int StartMs, int LenMs, int Bytes,
    DateTimeOffset At)
{
    public string Url => LavkaAnthems.UrlPrefix + File;
}

/// <summary>
/// Прокльон, насланий на акаунт (docs/games/specs/flair.md §1): хто й на кого (ключ і написання ніка), що звучить,
/// скільки програшів лишилось. Живий — <see cref="Live"/>; розряджений має <paramref name="DoneAt"/>, відкуплений —
/// <paramref name="RansomedAt"/> (і <c>left = 0</c>), розкритий — <paramref name="RevealedAt"/>.
/// </summary>
public sealed record LavkaCurseRow(long Id, string FromKey, string FromNick, string ToKey, string ToNick, string Item, int Left,
    int Price, DateTimeOffset At, DateTimeOffset? RevealedAt, DateTimeOffset? RansomedAt, DateTimeOffset? DoneAt)
{
    public bool Live => Left > 0 && RansomedAt is null && DoneAt is null;
    /// <summary>Коли перестав діяти; null — ще діє.</summary>
    public DateTimeOffset? EndedAt => DoneAt ?? RansomedAt;
    /// <summary>live, done чи ransomed — так його бачить вітрина.</summary>
    public string State => RansomedAt is not null ? "ransomed" : Live ? "live" : "done";
}

/// <summary>
/// Таблиці Лавки: що в кого є (назавжди), що вдягнуто і коли востаннє користувались вмінням (перерва переживає рестарт).
/// DDL і SQL живуть тут, від <see cref="Db"/> — лише з'єднання на одну коротку операцію.
/// </summary>
public sealed class LavkaStore
{
    const string Schema = """
        CREATE TABLE IF NOT EXISTS lavka_owned(
            nick_key TEXT NOT NULL, item TEXT NOT NULL, source TEXT NOT NULL, from_nick TEXT, price INTEGER NOT NULL,
            at TEXT NOT NULL, PRIMARY KEY(nick_key, item));
        CREATE TABLE IF NOT EXISTS lavka_worn(
            nick_key TEXT NOT NULL, nick TEXT NOT NULL, slot TEXT NOT NULL, item TEXT NOT NULL, PRIMARY KEY(nick_key, slot));
        CREATE TABLE IF NOT EXISTS lavka_perk(
            nick_key TEXT NOT NULL, perk TEXT NOT NULL, used_at TEXT NOT NULL, PRIMARY KEY(nick_key, perk));
        CREATE TABLE IF NOT EXISTS lavka_photo(
            nick_key TEXT NOT NULL PRIMARY KEY, nick TEXT NOT NULL, file TEXT NOT NULL, bytes INTEGER NOT NULL, at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS lavka_anthem(
            nick_key TEXT NOT NULL PRIMARY KEY, nick TEXT NOT NULL, file TEXT NOT NULL, title TEXT,
            start_ms INTEGER NOT NULL, len_ms INTEGER NOT NULL, bytes INTEGER NOT NULL, at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS lavka_curse(
            id INTEGER PRIMARY KEY AUTOINCREMENT, from_key TEXT NOT NULL, from_nick TEXT NOT NULL, to_key TEXT NOT NULL,
            to_nick TEXT NOT NULL, item TEXT NOT NULL, "left" INTEGER NOT NULL, price INTEGER NOT NULL, at TEXT NOT NULL,
            revealed_at TEXT, ransomed_at TEXT, done_at TEXT);
        CREATE INDEX IF NOT EXISTS lavka_curse_to ON lavka_curse(to_key, id);
        CREATE INDEX IF NOT EXISTS lavka_curse_from ON lavka_curse(from_key, id);
        """;

    readonly Db _db;

    public LavkaStore(Db db)
    {
        _db = db;
        _db.With(c => { Exec(c, Schema); return 0; });
    }

    static string Key(string nick) => Auth.NickKey(nick);

    /// <summary>Записати річ за ніком. <paramref name="source"/> — buy чи gift; false — вона в нього вже була.</summary>
    public bool Own(string nick, string item, string source, string? from, int price, DateTimeOffset at) => _db.With(c =>
        Exec(c, "INSERT OR IGNORE INTO lavka_owned(nick_key, item, source, from_nick, price, at) VALUES($k, $i, $s, $f, $p, $at)",
            ("$k", Key(nick)), ("$i", item), ("$s", source), ("$f", from), ("$p", price), ("$at", Iso(at))) > 0);

    public bool Owns(string nick, string item) => _db.With(c =>
    {
        using var cmd = Cmd(c, "SELECT 1 FROM lavka_owned WHERE nick_key = $k AND item = $i", ("$k", Key(nick)), ("$i", item));
        return cmd.ExecuteScalar() is not null;
    });

    public HashSet<string> OwnedBy(string nick) => _db.With(c =>
    {
        using var cmd = Cmd(c, "SELECT item FROM lavka_owned WHERE nick_key = $k", ("$k", Key(nick)));
        using var r = cmd.ExecuteReader();
        var set = new HashSet<string>(StringComparer.Ordinal);
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    });

    /// <summary>
    /// Вдягнути річ у слот (те, що там було, знімається). Написання ніка оновлюється в усіх його рядках — щоб вигляд
    /// був підписаний тим ніком, під яким людина зараз.
    /// </summary>
    public void Wear(string nick, string slot, string item) => _db.With(c =>
    {
        Exec(c, """
            INSERT INTO lavka_worn(nick_key, nick, slot, item) VALUES($k, $n, $s, $i)
            ON CONFLICT(nick_key, slot) DO UPDATE SET item = excluded.item, nick = excluded.nick
            """, ("$k", Key(nick)), ("$n", nick), ("$s", slot), ("$i", item));
        Exec(c, "UPDATE lavka_worn SET nick = $n WHERE nick_key = $k", ("$k", Key(nick)), ("$n", nick));
    });

    /// <summary>Зняти те, що в слоті. Сама річ лишається в шафі — рядок «вдягнуто» просто зникає.</summary>
    public void Unwear(string nick, string slot) => _db.With(c =>
        Exec(c, "DELETE FROM lavka_worn WHERE nick_key = $k AND slot = $s", ("$k", Key(nick)), ("$s", slot)));

    public string? Worn(string nick, string slot) => _db.With(c =>
    {
        using var cmd = Cmd(c, "SELECT item FROM lavka_worn WHERE nick_key = $k AND slot = $s", ("$k", Key(nick)), ("$s", slot));
        return cmd.ExecuteScalar() as string;
    });

    /// <summary>Слот → id речі, що в ньому.</summary>
    public Dictionary<string, string> WornBy(string nick) => _db.With(c =>
    {
        using var cmd = Cmd(c, "SELECT slot, item FROM lavka_worn WHERE nick_key = $k", ("$k", Key(nick)));
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        while (r.Read()) map[r.GetString(0)] = r.GetString(1);
        return map;
    });

    public List<LavkaWornRow> AllWorn() => _db.With(c =>
    {
        using var cmd = Cmd(c, "SELECT nick_key, nick, slot, item FROM lavka_worn ORDER BY nick_key, slot");
        using var r = cmd.ExecuteReader();
        var list = new List<LavkaWornRow>();
        while (r.Read()) list.Add(new LavkaWornRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        return list;
    });

    /// <summary>Коли нік востаннє користувався вмінням; null — ще ні разу.</summary>
    public DateTimeOffset? PerkUsed(string nick, string perk) => _db.With(c =>
    {
        using var cmd = Cmd(c, "SELECT used_at FROM lavka_perk WHERE nick_key = $k AND perk = $p", ("$k", Key(nick)), ("$p", perk));
        return cmd.ExecuteScalar() is string s ? Ts(s) : (DateTimeOffset?)null;
    });

    public void UsePerk(string nick, string perk, DateTimeOffset at) => _db.With(c =>
        Exec(c, """
            INSERT INTO lavka_perk(nick_key, perk, used_at) VALUES($k, $p, $at)
            ON CONFLICT(nick_key, perk) DO UPDATE SET used_at = excluded.used_at
            """, ("$k", Key(nick)), ("$p", perk), ("$at", Iso(at))));

    /// <summary>Забути, коли користувався вмінням: перерва скидається (адмін зняв фото — нове можна одразу).</summary>
    public void ForgetPerk(string nick, string perk) => _db.With(c =>
        Exec(c, "DELETE FROM lavka_perk WHERE nick_key = $k AND perk = $p", ("$k", Key(nick)), ("$p", perk)));

    // ---------- своя фотка: у базі лише ім'я файла (з версією), саме фото — у data/avatars ----------

    const string PhotoCols = "nick_key, nick, file, bytes, at";

    public LavkaPhotoRow? Photo(string nick) => _db.With(c =>
        Photos(c, $"SELECT {PhotoCols} FROM lavka_photo WHERE nick_key = $k", ("$k", Key(nick))).FirstOrDefault());

    /// <summary>Свіжі згори — так їх переглядає адмін.</summary>
    public List<LavkaPhotoRow> AllPhotos() => _db.With(c => Photos(c, $"SELECT {PhotoCols} FROM lavka_photo ORDER BY at DESC, nick_key"));

    /// <summary>Поставити фото; повертає ім'я файла, що стояв досі (його треба прибрати з диска), або null.</summary>
    public string? SetPhoto(string nick, string file, int bytes, DateTimeOffset at) => _db.With(c =>
    {
        using var old = Cmd(c, "SELECT file FROM lavka_photo WHERE nick_key = $k", ("$k", Key(nick)));
        var was = old.ExecuteScalar() as string;
        Exec(c, """
            INSERT INTO lavka_photo(nick_key, nick, file, bytes, at) VALUES($k, $n, $f, $b, $at)
            ON CONFLICT(nick_key) DO UPDATE SET nick = excluded.nick, file = excluded.file, bytes = excluded.bytes, at = excluded.at
            """, ("$k", Key(nick)), ("$n", nick), ("$f", file), ("$b", bytes), ("$at", Iso(at)));
        return was;
    });

    /// <summary>Прибрати фото; повертає ім'я файла, що стояв (null — фото й не було).</summary>
    public string? DropPhoto(string nick) => _db.With(c =>
    {
        using var old = Cmd(c, "SELECT file FROM lavka_photo WHERE nick_key = $k", ("$k", Key(nick)));
        var was = old.ExecuteScalar() as string;
        if (was is not null) Exec(c, "DELETE FROM lavka_photo WHERE nick_key = $k", ("$k", Key(nick)));
        return was;
    });

    static List<LavkaPhotoRow> Photos(SqliteConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = Cmd(c, sql, ps);
        using var r = cmd.ExecuteReader();
        var list = new List<LavkaPhotoRow>();
        while (r.Read()) list.Add(new LavkaPhotoRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), Ts(r.GetString(4))));
        return list;
    }

    // ---------- свій трек: у базі ім'я вирізаного mp3 і як його назвали, сам файл — у data/anthems ----------

    const string AnthemCols = "nick_key, nick, file, title, start_ms, len_ms, bytes, at";

    public LavkaAnthemRow? Anthem(string nick) => _db.With(c =>
        Anthems(c, $"SELECT {AnthemCols} FROM lavka_anthem WHERE nick_key = $k", ("$k", Key(nick))).FirstOrDefault());

    /// <summary>Свіжі згори — так їх переглядає адмін.</summary>
    public List<LavkaAnthemRow> AllAnthems() => _db.With(c => Anthems(c, $"SELECT {AnthemCols} FROM lavka_anthem ORDER BY at DESC, nick_key"));

    /// <summary>Поставити уривок; повертає ім'я файла, що стояв досі (його треба прибрати з диска), або null.</summary>
    public string? SetAnthem(string nick, string file, string? title, int startMs, int lenMs, int bytes, DateTimeOffset at) => _db.With(c =>
    {
        using var old = Cmd(c, "SELECT file FROM lavka_anthem WHERE nick_key = $k", ("$k", Key(nick)));
        var was = old.ExecuteScalar() as string;
        Exec(c, """
            INSERT INTO lavka_anthem(nick_key, nick, file, title, start_ms, len_ms, bytes, at) VALUES($k, $n, $f, $t, $s, $l, $b, $at)
            ON CONFLICT(nick_key) DO UPDATE SET nick = excluded.nick, file = excluded.file, title = excluded.title,
                start_ms = excluded.start_ms, len_ms = excluded.len_ms, bytes = excluded.bytes, at = excluded.at
            """, ("$k", Key(nick)), ("$n", nick), ("$f", file), ("$t", title), ("$s", startMs), ("$l", lenMs), ("$b", bytes), ("$at", Iso(at)));
        return was;
    });

    /// <summary>Прибрати уривок; повертає ім'я файла, що стояв (null — уривка й не було).</summary>
    public string? DropAnthem(string nick) => _db.With(c =>
    {
        using var old = Cmd(c, "SELECT file FROM lavka_anthem WHERE nick_key = $k", ("$k", Key(nick)));
        var was = old.ExecuteScalar() as string;
        if (was is not null) Exec(c, "DELETE FROM lavka_anthem WHERE nick_key = $k", ("$k", Key(nick)));
        return was;
    });

    static List<LavkaAnthemRow> Anthems(SqliteConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = Cmd(c, sql, ps);
        using var r = cmd.ExecuteReader();
        var list = new List<LavkaAnthemRow>();
        while (r.Read())
            list.Add(new LavkaAnthemRow(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), Ts(r.GetString(7))));
        return list;
    }

    // ---------- прокльони: живі, розряджені й відкуплені — один рядок на кожне насилання ----------

    const string CurseCols = "id, from_key, from_nick, to_key, to_nick, item, \"left\", price, at, revealed_at, ransomed_at, done_at";
    const string LiveSql = "\"left\" > 0 AND ransomed_at IS NULL AND done_at IS NULL";

    /// <summary>Записати насланий прокльон; повертає id рядка (на ньому тримається ref оплати).</summary>
    public long AddCurse(string from, string to, string item, int left, int price, DateTimeOffset at) => _db.With(c =>
    {
        using var cmd = Cmd(c, """
            INSERT INTO lavka_curse(from_key, from_nick, to_key, to_nick, item, "left", price, at) VALUES($fk, $fn, $tk, $tn, $i, $l, $p, $at);
            SELECT last_insert_rowid();
            """, ("$fk", Key(from)), ("$fn", from), ("$tk", Key(to)), ("$tn", to), ("$i", item), ("$l", left), ("$p", price), ("$at", Iso(at)));
        return (long)cmd.ExecuteScalar()!;
    });

    /// <summary>Прибрати щойно записаний прокльон, за який так і не заплатили.</summary>
    public void DropCurse(long id) => _db.With(c => Exec(c, "DELETE FROM lavka_curse WHERE id = $id", ("$id", id)));

    public LavkaCurseRow? Curse(long id) => _db.With(c =>
        Curses(c, $"SELECT {CurseCols} FROM lavka_curse WHERE id = $id", ("$id", id)).FirstOrDefault());

    /// <summary>Живі прокльони на ніку, найстаріші першими (FIFO).</summary>
    public List<LavkaCurseRow> LiveOn(string nick) => _db.With(c =>
        Curses(c, $"SELECT {CurseCols} FROM lavka_curse WHERE to_key = $k AND {LiveSql} ORDER BY id", ("$k", Key(nick))));

    /// <summary>Чи є на ніку хоч один живий прокльон — для кожного програшу, тож без вибірки рядків.</summary>
    public bool Cursed(string nick) => _db.With(c =>
    {
        using var cmd = Cmd(c, $"SELECT 1 FROM lavka_curse WHERE to_key = $k AND {LiveSql} LIMIT 1", ("$k", Key(nick)));
        return cmd.ExecuteScalar() is not null;
    });

    /// <summary>Прокльони на ніку (<paramref name="on"/>) чи від ніка: живі й ті, що скінчились не раніше за <paramref name="since"/>.</summary>
    public List<LavkaCurseRow> CursesOf(string nick, bool on, DateTimeOffset since) => _db.With(c =>
        Curses(c, $"""
            SELECT {CurseCols} FROM lavka_curse WHERE {(on ? "to_key" : "from_key")} = $k
                AND (({LiveSql}) OR COALESCE(done_at, ransomed_at) >= $since)
            ORDER BY id
            """, ("$k", Key(nick)), ("$since", Iso(since))));

    /// <summary>Мінус один програш; на нулі — розряджений. Повертає, скільки лишилось, або null — прокльон уже не живий.</summary>
    public int? HitCurse(long id, DateTimeOffset at) => _db.With(c =>
    {
        var hit = Exec(c, $"""
            UPDATE lavka_curse SET "left" = "left" - 1, done_at = CASE WHEN "left" - 1 <= 0 THEN $at ELSE done_at END
            WHERE id = $id AND {LiveSql}
            """, ("$id", id), ("$at", Iso(at)));
        if (hit == 0) return (int?)null;
        using var cmd = Cmd(c, "SELECT \"left\" FROM lavka_curse WHERE id = $id", ("$id", id));
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    /// <summary>Відкуп: прокльон знято (left = 0). false — він уже не живий.</summary>
    public bool RansomCurse(long id, DateTimeOffset at) => _db.With(c =>
        Exec(c, $"UPDATE lavka_curse SET \"left\" = 0, ransomed_at = $at WHERE id = $id AND {LiveSql}", ("$id", id), ("$at", Iso(at))) > 0);

    /// <summary>Розкриття: тепер ціль знає, від кого. false — уже розкритий.</summary>
    public bool RevealCurse(long id, DateTimeOffset at) => _db.With(c =>
        Exec(c, "UPDATE lavka_curse SET revealed_at = $at WHERE id = $id AND revealed_at IS NULL", ("$id", id), ("$at", Iso(at))) > 0);

    static List<LavkaCurseRow> Curses(SqliteConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = Cmd(c, sql, ps);
        using var r = cmd.ExecuteReader();
        var list = new List<LavkaCurseRow>();
        DateTimeOffset? At(int i) => r.IsDBNull(i) ? null : Ts(r.GetString(i));
        while (r.Read())
            list.Add(new LavkaCurseRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
                r.GetInt32(6), r.GetInt32(7), Ts(r.GetString(8)), At(9), At(10), At(11)));
        return list;
    }

    static string Iso(DateTimeOffset t) => t.ToString("O", CultureInfo.InvariantCulture);
    static DateTimeOffset Ts(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    static SqliteCommand Cmd(SqliteConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    static int Exec(SqliteConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = Cmd(c, sql, ps);
        return cmd.ExecuteNonQuery();
    }
}

// =====================================================================================================================
// Те, що Лавці треба ззовні. Інтерфейси — щоб тести перевіряли правила без liquidsoap, edge-tts і SignalR.
// =====================================================================================================================

/// <summary>Трек у черзі ефіру так, як його бачить присвята. <paramref name="Status"/> — як у стані сайту: queued … dispatched.</summary>
public sealed record LavkaQueued(string ItemId, TrackInfo Track, string RequestedBy, string Status);

/// <summary>Черга ефіру: подивитись і поставити готове голосове перед потрібним треком.</summary>
public interface ILavkaAir
{
    /// <summary>Черга по порядку (без того, що вже грає).</summary>
    IReadOnlyList<LavkaQueued> Queue();

    /// <summary>Готове голосове — у чергу просто перед <paramref name="beforeItemId"/>.</summary>
    (bool Ok, string Message) AddVoiceBefore(TrackInfo track, string filePath, string nick, string beforeItemId);
}

/// <summary>Присвята, озвучена й покладена готовим голосовим у кеш ефіру.</summary>
public sealed record LavkaVoiceClip(TrackInfo Track, string FilePath);

/// <summary>Голос Глека для присвяти.</summary>
public interface ILavkaVoice
{
    /// <summary>Озвучити <paramref name="text"/>; null — TTS вимкнений, не встиг за розумний час або впав.</summary>
    Task<LavkaVoiceClip?> SpeakAsync(string text, string title, string artist, CancellationToken ct);
}

/// <summary>Розсилка подій Лавки: вигляд і феєрверк — усім, рядок у балачки — усім, тост — лише тому, кому подарували.</summary>
public interface ILavkaWire
{
    void Look(string nick, LavkaLook? look);
    void Fireworks(string nick);
    void Chat(object line);
    void Toast(string nick, string text);
    /// <summary>Рядок у Журнал під фільтром «🎮 Ігри» (від імені сайту, як рядки ігор). Типово — нікуди.</summary>
    void Journal(string text) { }
}

/// <summary>Відповідь Лавки: чи вдалось, що сказати людині і (для купівлі) скільки черепків лишилось.</summary>
public sealed record LavkaReply(bool Ok, string Message, int? Balance = null);

// =====================================================================================================================
// Правила
// =====================================================================================================================

/// <summary>
/// Правила Лавки: хто що може купити, подарувати, вдягнути і коли знову скористатись вмінням. Гроші — лише через
/// <see cref="Economy.TrySpend"/> з ref-ключем «lavka:&lt;власник&gt;:&lt;річ&gt;»: річ у власника буває раз назавжди,
/// тож і оплата за неї — теж раз, хоч би скільки разів клацнули і хоч би що впало посередині.
/// </summary>
public sealed class Lavka(LavkaStore store, Economy economy, EconomyStore econ, Db db, Presence presence,
    ILavkaAir air, ILavkaVoice voice, ILavkaWire wire, IClock clock, ILogger<Lavka> log) : IRings
{
    public const string NotAccount = "Закріпи нік — тоді Лавка твоя";
    public const string NoItem = "Такої речі в Лавці нема";
    public const string Mine = "Уже твоє";
    public const string SelfGift = "Собі не дарують — просто купи";
    public const string NoSlot = "Нема такого місця";
    public const string WrongSlot = "Ця річ не для цього місця";
    public const string NotOwned = "Спершу купи — тоді вдягнеш";
    public const string NoSong = "Спершу закинь пісню — присвята прозвучить перед нею";
    public const string Mute = "Глек зараз мовчить";
    public const string Fx = "🎆 бахає феєрверк!";

    public static readonly TimeSpan DedicationGap = TimeSpan.FromHours(3);
    public static readonly TimeSpan FireworksGap = TimeSpan.FromMinutes(10);
    /// <summary>Фото міняють безкоштовно, але не частіше разу на добу: аватарка — обличчя, а не слайд-шоу.</summary>
    public static readonly TimeSpan PhotoGap = TimeSpan.FromDays(1);
    /// <summary>Новий уривок свого треку — не частіше разу на 2 хвилини: щоб підправити, але не спамити ffmpeg.</summary>
    public static readonly TimeSpan AnthemGap = TimeSpan.FromMinutes(2);
    public const string NoGiftAnthem = "Свій трек дарувати не можна — його ставлять собі";
    public const string NoGiftRing = "Свій дзвінок дарувати не можна — він дзвонить твоїм гімном";
    public const string CurseNotSold = "Прокльони не купують і не дарують — їх насилають: «😈 Наслати…» на полиці прокльонів";
    public const string NoRing = "Спершу купи «Свій дзвінок»";
    public const string CurseToGuest = "Прокльон — лише на того, хто з акаунтом";
    public const string CurseSelf = "На себе не можна — хіба що з горя";
    public const string NotYourCurse = "Це не твій прокльон";
    public const string CurseGone = "Цей прокльон уже не діє";
    public const string CurseAgain = "Твій прокльон на цю людину ще діє — хай спершу розрядиться";
    public const string CurseFull = "На цій людині вже три прокльони — більше не влізе";
    /// <summary>Скільки живих прокльонів від різних людей влазить на одну ціль.</summary>
    public const int CursesPerTarget = 3;
    /// <summary>Відкупитись — удвічі дорожче за прокльон: черепки згорають, тому, хто наслав, нічого не йде.</summary>
    public const int RansomPrice = 1000;
    public const int RevealPrice = 300;
    /// <summary>Скільки днів розряджений чи відкуплений прокльон видно на вітрині (і його ще можна розкрити).</summary>
    public static readonly TimeSpan CurseMemory = TimeSpan.FromDays(7);

    /// <summary>«Чи вже є», списання і запис — одним шматком: інакше подарунок і купівля тієї самої речі разом заплатили б двічі.</summary>
    readonly object _gate = new();
    /// <summary>Хто зараз присвячує: поки Глек озвучує, другий клік не має запустити другу присвяту.</summary>
    readonly ConcurrentDictionary<string, byte> _dedicating = new(StringComparer.Ordinal);

    string MonthDay => Days.Of(clock.UtcNow)[5..];

    static TimeSpan Gap(string perk) => perk switch
    {
        LavkaCatalog.Dedication => DedicationGap,
        LavkaCatalog.Photo => PhotoGap,
        LavkaCatalog.OwnAnthem => AnthemGap,
        _ => FireworksGap,
    };

    static string AchTitle(string ach) => AchievementCatalog.Get(ach)?.Title ?? ach;

    /// <summary>Коли вміння знову готове; null — готове вже (або ним ще не користувались).</summary>
    public DateTimeOffset? ReadyAt(string nick, string perk) =>
        store.PerkUsed(nick, perk) is { } at && at + Gap(perk) > clock.UtcNow ? at + Gap(perk) : null;

    bool Has(string nick, LavkaItem item) =>
        item.Ach is { } ach ? econ.HasAchievement(EconomyStore.Key(nick), ach) : store.Owns(nick, item.Id);

    // ---------- що бачить людина ----------

    /// <summary>Вітрина (<c>GET /api/lavka</c>): увесь каталог зі станом «моє / вдягнуто», вміння з перервами і фрази присвяти.</summary>
    public object View(string nick, bool account)
    {
        var md = MonthDay;
        var mine = account ? store.OwnedBy(nick) : [];
        var achs = account ? econ.AchievementsOf(EconomyStore.Key(nick)).Select(a => a.Key).ToHashSet(StringComparer.Ordinal) : [];
        var wearing = account ? store.WornBy(nick) : new Dictionary<string, string>();
        var own = account ? store.Anthem(nick) : null;
        var since = clock.UtcNow - CurseMemory;
        var onMe = account ? store.CursesOf(nick, on: true, since) : [];
        var fromMe = account ? store.CursesOf(nick, on: false, since) : [];
        object Perk(string id)
        {
            var has = mine.Contains(id);
            return new { owned = has, readyAt = has ? ReadyAt(nick, id) : null };
        }
        return new
        {
            account,
            balance = economy.Balance(nick),
            items = LavkaCatalog.All.Select(i => new
            {
                id = i.Id, kind = i.Kind, title = i.Title, price = i.Price, art = i.Art, tier = i.Tier,
                season = i.Season is { } s ? new { from = s.From, to = s.To, open = s.Open(md) } : null,
                earned = i.Ach is { } a ? new { ach = a, achTitle = AchTitle(a) } : null,
                owned = i.Ach is { } got ? achs.Contains(got) : mine.Contains(i.Id),
                worn = wearing.TryGetValue(i.Kind, out var w) && w == i.Id,
            }),
            worn = new
            {
                icon = wearing.GetValueOrDefault(LavkaKind.Icon), frame = wearing.GetValueOrDefault(LavkaKind.Frame),
                color = wearing.GetValueOrDefault(LavkaKind.Color), title = wearing.GetValueOrDefault(LavkaKind.Title),
                bg = wearing.GetValueOrDefault(LavkaKind.Bg), anthem = wearing.GetValueOrDefault(LavkaKind.Anthem),
                fx = wearing.GetValueOrDefault(LavkaKind.Fx), ring = wearing.GetValueOrDefault(LavkaCatalog.RingSlot),
            },
            perks = new
            {
                fireworks = Perk(LavkaCatalog.Fireworks), dedication = Perk(LavkaCatalog.Dedication),
                // readyAt — коли можна поставити нове фото; url — те, що стоїть зараз (null — фото нема)
                photo = new
                {
                    owned = mine.Contains(LavkaCatalog.Photo),
                    readyAt = mine.Contains(LavkaCatalog.Photo) ? ReadyAt(nick, LavkaCatalog.Photo) : null,
                    url = account ? store.Photo(nick)?.Url : null,
                },
            },
            phrases = LavkaCatalog.Phrases.Select(p => new { key = p.Key, text = p.Text }),
            // свій трек: url — уривок, що стоїть зараз (null — ще нема), readyAt — коли можна поставити новий
            ownAnthem = new { url = own?.Url, title = own?.Title, readyAt = mine.Contains(LavkaCatalog.OwnAnthem) ? ReadyAt(nick, LavkaCatalog.OwnAnthem) : null },
            // прокльони: на мені (від кого — лише розкриті) і від мене; розряджені й відкуплені видно ще 7 днів
            curses = new
            {
                onMe = onMe.Select(c => new CurseOnMe(c.Id, c.Item, CurseTitle(c.Item), CurseEmoji(c.Item), c.Left, c.At,
                    c.RevealedAt is null ? null : c.FromNick, c.State)),
                mine = fromMe.Select(c => new
                {
                    id = c.Id, to = c.ToNick, item = c.Item, title = CurseTitle(c.Item), emoji = CurseEmoji(c.Item), left = c.Left,
                    at = c.At, revealed = c.RevealedAt is not null, state = c.State,
                }),
            },
        };
    }

    /// <summary>Рядок «На тобі»: <c>from</c> у JSON є лише тоді, коли ціль заплатила за розкриття.</summary>
    sealed record CurseOnMe(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("item")] string Item,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("emoji")] string Emoji,
        [property: JsonPropertyName("left")] int Left,
        [property: JsonPropertyName("at")] DateTimeOffset At,
        [property: JsonPropertyName("from"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? From,
        [property: JsonPropertyName("state")] string State);

    static string CurseTitle(string item) => LavkaCatalog.Get(item)?.Title ?? item;
    static string CurseEmoji(string item) => LavkaCatalog.Get(item)?.Art is LavkaAnthemArt a ? a.Emoji : "😈";

    /// <summary>Вигляд усіх, у кого щось вдягнуто (<c>GET /api/lavka/looks</c>): нік → що малювати.</summary>
    public Dictionary<string, LavkaLook> Looks()
    {
        var looks = new Dictionary<string, LavkaLook>(StringComparer.Ordinal);
        var photos = store.AllPhotos().ToDictionary(p => p.NickKey, StringComparer.Ordinal);
        foreach (var person in store.AllWorn().GroupBy(r => r.NickKey))
        {
            photos.Remove(person.Key, out var photo);
            if (Look(person.ToDictionary(r => r.Slot, r => r.Item, StringComparer.Ordinal), photo?.Url) is { } look)
                looks[person.First().Nick] = look;
        }
        // фото без жодної вдягнутої речі — теж вигляд
        foreach (var photo in photos.Values)
            if (Look(new Dictionary<string, string>(), photo.Url) is { } look) looks[photo.Nick] = look;
        return looks;
    }

    /// <summary>Вигляд одного ніка; null — нічого не вдягнуто й фото нема.</summary>
    public LavkaLook? LookOf(string nick) => Look(store.WornBy(nick), store.Photo(nick)?.Url);

    /// <summary>
    /// Гімн, який зазвучить, коли <paramref name="nick"/> виграє: вдягнутий готовий — його файл; вдягнутий свій трек —
    /// поставлений уривок (без уривка чи знятий адміном — тиша); нічого не вдягнуто або річ уже не його — null.
    /// </summary>
    public AnthemPlay? AnthemOf(string nick) => store.Worn(nick, LavkaKind.Anthem) is { } id ? Playable(nick, id) : null;

    /// <summary>
    /// Гімн <paramref name="id"/> так, як він зазвучить у <paramref name="nick"/>: річ — гімн, вона його, а свій трек — з
    /// поставленим уривком. null — зіграти нічого (не гімн, не куплений, уривка нема).
    /// </summary>
    AnthemPlay? Playable(string nick, string id)
    {
        if (LavkaCatalog.Get(id) is not { Kind: LavkaKind.Anthem, Art: LavkaAnthemArt art } item) return null;
        if (!store.Owns(nick, item.Id)) return null;
        if (item.Id != LavkaCatalog.OwnAnthem)
            return art.Url is { } url ? new AnthemPlay(item.Title, art.Emoji, url, LavkaCatalog.ClipSeconds(url)) : null;
        return store.Anthem(nick) is { } own
            ? new AnthemPlay(string.IsNullOrWhiteSpace(own.Title) ? item.Title : own.Title, art.Emoji, own.Url, own.LenMs > 0 ? own.LenMs / 1000.0 : null)
            : null;
    }

    /// <summary>Святкування, яке злетить над столом, коли <paramref name="nick"/> виграє: id вдягнутого й свого; інакше null.</summary>
    public string? FxOf(string nick) =>
        store.Worn(nick, LavkaKind.Fx) is { } id && LavkaCatalog.Get(id) is { Kind: LavkaKind.Fx } item && store.Owns(nick, item.Id) ? item.Id : null;

    /// <summary>
    /// Дзвінок <paramref name="nick"/> для особистого заклику (docs/games/specs/flair.md §2): є вміння, обрано гімн, і він
    /// зазвучить (свій, а свій трек — з уривком). Інакше null — заклик летить без дзвінка.
    /// </summary>
    public AnthemPlay? RingOf(string nick) =>
        store.Owns(nick, LavkaCatalog.Ring) && store.Worn(nick, LavkaCatalog.RingSlot) is { } id ? Playable(nick, id) : null;

    InviteRing? IRings.RingOf(string nick) => RingOf(nick) is { } p ? new InviteRing(p.Url, p.Title, p.Emoji) : null;

    /// <summary>
    /// Обрати дзвінок (<c>POST /api/lavka/ring</c>): <paramref name="itemId"/> — свій гімн (готовий куплений або «Свій трек»
    /// з уривком); порожньо — без дзвінка.
    /// </summary>
    public LavkaReply SetRing(string nick, bool account, string? itemId)
    {
        if (!account) return new(false, NotAccount);
        if (!store.Owns(nick, LavkaCatalog.Ring)) return new(false, NoRing);
        if (string.IsNullOrWhiteSpace(itemId))
        {
            lock (_gate) store.Unwear(nick, LavkaCatalog.RingSlot);
            return new(true, "Дзвінок вимкнено — кличеш без звуку");
        }
        if (LavkaCatalog.Get(itemId) is not { } item) return new(false, NoItem);
        if (item.Kind != LavkaKind.Anthem) return new(false, "Дзвінком стає лише гімн");
        if (!store.Owns(nick, item.Id)) return new(false, "Дзвінком стає лише свій гімн — спершу купи його");
        if (Playable(nick, item.Id) is not { } play) return new(false, "Спершу постав уривок «Свого треку»");
        lock (_gate) store.Wear(nick, LavkaCatalog.RingSlot, item.Id);
        return new(true, $"🔔 Тепер твій дзвінок — «{play.Title}»");
    }

    /// <summary>Річ, якої вже нема в каталозі (або не свого слота), не малюється — і не ламає решту вигляду.</summary>
    static LavkaLook? Look(IReadOnlyDictionary<string, string> worn, string? photo)
    {
        object? Art(string slot) => worn.TryGetValue(slot, out var id) && LavkaCatalog.Get(id) is { } i && i.Kind == slot ? i.Art : null;
        var look = new LavkaLook(Art(LavkaKind.Icon) as string, Art(LavkaKind.Frame) as string, Art(LavkaKind.Color),
            Art(LavkaKind.Title) as string, Art(LavkaKind.Bg) as string, photo);
        return look is { Icon: null, Frame: null, Color: null, Title: null, Bg: null, Photo: null } ? null : look;
    }

    // ---------- купити й подарувати ----------

    /// <summary>
    /// Купити річ собі (одразу вдягається в свій слот, крім вмінь) або подарувати акаунту <paramref name="forNick"/>
    /// (вдягається йому, лише якщо слот порожній). Відповідь завжди несе свіжий баланс.
    /// </summary>
    public LavkaReply Buy(string nick, bool account, string? itemId, string? forNick)
    {
        LavkaReply No(string message) => new(false, message, economy.Balance(nick));

        if (!account) return No(NotAccount);
        if (LavkaCatalog.Get(itemId) is not { } item) return No(NoItem);
        Account? to = null;
        var name = Clean(forNick);
        if (name.Length > 0)
        {
            if (Auth.NickKey(name) == Auth.NickKey(nick)) return No(SelfGift);
            to = db.FindAccount(name);
            if (to is null) return No($"«{name}» — не акаунт: дарувати можна лише тим, хто закріпив нік");
        }
        if (item.Ach is { } ach) return No($"Цей титул не купується — його дає ачівка «{AchTitle(ach)}»");
        if (item.Kind == LavkaKind.Curse) return No(CurseNotSold);
        if (to is not null && item.Id == LavkaCatalog.OwnAnthem) return No(NoGiftAnthem);
        if (to is not null && item.Id == LavkaCatalog.Ring) return No(NoGiftRing);

        var owner = to?.Nick ?? nick;
        lock (_gate)
        {
            if (store.Owns(owner, item.Id))
            {
                if (to is null) return No(Mine);
                var of = NickCases.Genitive(owner);
                return No($"{NickCases.AtStart(of)} {of} це вже є");
            }
            if (item.Season is { } season && !season.Open(MonthDay)) return No($"Повернеться {season.Returns}");
            var reason = (to is null ? "shop:" : "gift:") + item.Id;
            if (!economy.TrySpend(nick, item.Price, reason, $"lavka:{Auth.NickKey(owner)}:{item.Id}"))
                return No($"Бракує {Math.Max(1, item.Price - economy.Balance(nick))} 🏺");
            store.Own(owner, item.Id, to is null ? "buy" : "gift", to is null ? null : nick, item.Price, clock.UtcNow);
            if (item.Kind != LavkaKind.Perk && (to is null || store.Worn(owner, item.Kind) is null)) store.Wear(owner, item.Kind, item.Id);
        }

        // Черепки вже списано: розсилка може спіткнутись, але зробити купівлю невдалою вона не має права.
        try
        {
            wire.Look(owner, LookOf(owner));
            if (to is not null)
            {
                var line = db.AddChat(nick, $"дарує {NickCases.Dative(owner)} {LavkaCatalog.Quoted(item)}", "gift");
                wire.Chat(ChatLine(line, owner));
                wire.Toast(owner, $"🎁 {nick} дарує тобі {LavkaCatalog.Quoted(item)}");
            }
        }
        catch (Exception ex) { log.LogWarning(ex, "Лавка не розповіла про {Item} для {Owner}", item.Id, owner); }

        var said = to is null ? Bought(item) : $"Подаровано {NickCases.Dative(owner)}: {LavkaCatalog.Quoted(item)} — назавжди";
        return new LavkaReply(true, said, economy.Balance(nick));
    }

    static string Bought(LavkaItem i) => i.Kind switch
    {
        LavkaKind.Icon => $"Значок «{i.Title}» {i.Art} тепер твій назавжди — уже на аватарці",
        LavkaKind.Frame => $"«{i.Title} рамка» тепер твоя назавжди — уже на аватарці",
        LavkaKind.Color => $"Колір ніка «{i.Title}» тепер твій назавжди — уже світиться",
        LavkaKind.Title => $"Титул «{i.Title}» тепер твій назавжди — уже біля ніка",
        LavkaKind.Bg => $"Тло «{i.Title}» тепер твоє назавжди — уже в профілі",
        LavkaKind.Anthem when i.Id == LavkaCatalog.OwnAnthem => "«Свій трек» тепер твій назавжди — обери пісню й уривок, і він зазвучить, коли виграєш",
        LavkaKind.Anthem => $"Гімн «{i.Title}» тепер твій назавжди — зазвучить за столом, коли виграєш",
        LavkaKind.Fx => $"Святкування «{i.Title}» тепер твоє назавжди — злетить над столом, коли виграєш",
        _ when i.Id == LavkaCatalog.Ring => "«Свій дзвінок» тепер твій назавжди — обери гімн, і він задзвенить тому, кого кличеш за стіл",
        _ when i.Id == LavkaCatalog.Dedication => "«Присвята в ефір» тепер твоя назавжди — закинь пісню й присвяти її комусь",
        _ when i.Id == LavkaCatalog.Photo => "«Своя фотка» тепер твоя назавжди — обери фото, і воно стане аватаркою",
        _ => $"«{i.Title}» тепер твій назавжди — бахай!",
    };

    // ---------- прокльони (docs/games/specs/flair.md §1) ----------

    /// <summary>
    /// Наслати прокльон <paramref name="itemId"/> на акаунт <paramref name="toNick"/>: 500 🏺, звучить на трьох його програшах.
    /// Від однієї людини на одну ціль — один живий, на ціль — щонайбільше три від різних людей. Ціль дістає тост без
    /// автора, Журнал — рядок «😈 Петро тепер під прокльоном. Хто наслав — секрет»; хто наслав — ніде не видно.
    /// Нік — лише в називному («Ціль: Тест Оля»): відмінки ламаються на ніках із кількох слів, а рід угадувати нема з чого.
    /// </summary>
    public LavkaReply Curse(string nick, bool account, string? toNick, string? itemId)
    {
        LavkaReply No(string message) => new(false, message, economy.Balance(nick));

        if (!account) return No(NotAccount);
        var name = Clean(toNick);
        if (name.Length == 0) return No("Кого проклясти?");
        if (Auth.NickKey(name) == Auth.NickKey(nick)) return No(CurseSelf);
        if (db.FindAccount(name) is not { } to) return No(CurseToGuest);
        if (LavkaCatalog.Get(itemId) is not { Kind: LavkaKind.Curse } item) return No("Такого прокльону в Лавці нема");

        long id;
        lock (_gate)
        {
            var live = store.LiveOn(to.Nick);
            if (live.Any(c => c.FromKey == Auth.NickKey(nick))) return No(CurseAgain);
            if (live.Select(c => c.FromKey).Distinct(StringComparer.Ordinal).Count() >= CursesPerTarget) return No(CurseFull);
            if (economy.Balance(nick) < item.Price) return No($"Бракує {Math.Max(1, item.Price - economy.Balance(nick))} 🏺");
            // ref тримається на id рядка — тож спершу рядок, потім гроші; не списалось (чи впало) — рядка й не було
            id = store.AddCurse(nick, to.Nick, item.Id, LavkaCatalog.CurseHits, item.Price, clock.UtcNow);
            bool paid;
            try
            {
                // у тості гаманця — і ціль; в історії лишається «Лавка — прокльон «Цап»» (Reason)
                paid = economy.TrySpend(nick, item.Price, "curse:" + item.Id, $"curse:{id}",
                    economy.Text(-item.Price, "curse:" + item.Id) + " · ціль: " + to.Nick);
            }
            catch
            {
                store.DropCurse(id);   // не заплачений прокльон не має лишитись живим
                throw;
            }
            if (!paid)
            {
                store.DropCurse(id);
                return No($"Бракує {Math.Max(1, item.Price - economy.Balance(nick))} 🏺");
            }
        }

        // Черепки вже списано: розсилка може спіткнутись, але зробити прокльон невдалим вона не має права.
        try
        {
            wire.Toast(to.Nick, $"😈 На тебе наклали {LavkaCatalog.Label(item)} на {LavkaCatalog.CurseHits} програші. Від кого — секрет. Відкупитись — у Лавці");
            wire.Journal($"😈 {to.Nick} тепер під прокльоном. Хто наслав — секрет");
        }
        catch (Exception ex) { log.LogWarning(ex, "Лавка не розповіла про прокльон {Id}", id); }
        return new(true, $"😈 Прокльон «{item.Title}» наслано. Ціль: {to.Nick} — почує його на трьох своїх програшах. Від кого — секрет",
            economy.Balance(nick));
    }

    /// <summary>Відкупитись від живого прокльону на собі: 1000 🏺 згорають (тому, хто наслав, нічого), прокльон знято.</summary>
    public LavkaReply Ransom(string nick, bool account, long? id)
    {
        LavkaReply No(string message) => new(false, message, economy.Balance(nick));

        if (!account) return No(NotAccount);
        LavkaCurseRow curse;
        lock (_gate)
        {
            if (CurseOn(nick, id) is not { } c) return No(NotYourCurse);
            if (!c.Live) return No(CurseGone);
            if (!economy.TrySpend(nick, RansomPrice, "curse-ransom:" + c.Item, $"curse-ransom:{c.Id}"))
                return No($"Бракує {Math.Max(1, RansomPrice - economy.Balance(nick))} 🏺");
            store.RansomCurse(c.Id, clock.UtcNow);
            curse = c;
        }
        try { wire.Toast(curse.FromNick, $"😇 Від твого прокльону «{CurseTitle(curse.Item)}» відкупились. Ціль: {curse.ToNick}"); }
        catch (Exception ex) { log.LogWarning(ex, "тост про відкуп {Id} не полетів", curse.Id); }
        return new(true, $"😇 {Capital(LavkaCatalog.Label(curse.Item))} знято — більше не звучить", economy.Balance(nick));
    }

    /// <summary>Дізнатись, від кого прокльон на собі: 300 🏺, живий чи той, що скінчився за останні 7 днів.</summary>
    public LavkaReply Reveal(string nick, bool account, long? id)
    {
        LavkaReply No(string message) => new(false, message, economy.Balance(nick));

        if (!account) return No(NotAccount);
        LavkaCurseRow curse;
        lock (_gate)
        {
            if (CurseOn(nick, id) is not { } c) return No(NotYourCurse);
            if (c.RevealedAt is not null) return No($"Ти вже знаєш — це {c.FromNick}");
            if (c.EndedAt is { } ended && ended < clock.UtcNow - CurseMemory) return No(CurseGone);
            if (!economy.TrySpend(nick, RevealPrice, "curse-reveal:" + c.Item, $"curse-reveal:{c.Id}"))
                return No($"Бракує {Math.Max(1, RevealPrice - economy.Balance(nick))} 🏺");
            store.RevealCurse(c.Id, clock.UtcNow);
            curse = c;
        }
        try { wire.Toast(curse.FromNick, $"🕵️ Твій прокльон «{CurseTitle(curse.Item)}» розкрито: {curse.ToNick} знає, що він від тебе"); }
        catch (Exception ex) { log.LogWarning(ex, "тост про розкриття {Id} не полетів", curse.Id); }
        return new(true, $"🕵️ Хто наслав «{CurseTitle(curse.Item)}»: {curse.FromNick}", economy.Balance(nick));
    }

    /// <summary>Прокльон <paramref name="id"/>, що лежить саме на <paramref name="nick"/>; чужий чи неіснуючий — null.</summary>
    LavkaCurseRow? CurseOn(string nick, long? id) =>
        id is { } n && store.Curse(n) is { } c && c.ToKey == Auth.NickKey(nick) ? c : null;

    /// <summary>Чи є на ніку живий прокльон — кандидат у прокляті програші.</summary>
    public bool Cursed(string nick) => store.Cursed(nick);

    /// <summary>
    /// <paramref name="nick"/> програв за столом: найстаріший живий прокльон на ньому (FIFO) втрачає один програш, тому,
    /// хто наслав, — тост. null — живих прокльонів нема. Кличе <see cref="AnthemPlayer"/>.
    /// </summary>
    public CurseHit? HitCurse(string nick)
    {
        LavkaCurseRow? curse = null;
        int left = 0;
        lock (_gate)
        {
            foreach (var c in store.LiveOn(nick))
                if (store.HitCurse(c.Id, clock.UtcNow) is { } l) { (curse, left) = (c, l); break; }
        }
        if (curse is null) return null;
        var item = LavkaCatalog.Get(curse.Item);
        var art = item?.Art as LavkaAnthemArt;
        try
        {
            var what = $"😈 Твій прокльон «{CurseTitle(curse.Item)}» спрацював";
            wire.Toast(curse.FromNick, left > 0 ? $"{what} (лишилось {left}). Ціль: {curse.ToNick}" : $"{what} і розрядився. Ціль: {curse.ToNick}");
        }
        catch (Exception ex) { log.LogWarning(ex, "тост про прокльон {Id} не полетів", curse.Id); }
        return art?.Url is { } url ? new CurseHit(curse.Id, item!.Title, art.Emoji, url, left) : null;
    }

    static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // ---------- вдягнути ----------

    /// <summary>Вдягнути своє в слот (<paramref name="itemId"/> порожній — зняти). Слот — це вид речі; вміння не вдягають.</summary>
    public LavkaReply Wear(string nick, bool account, string? slot, string? itemId)
    {
        if (!account) return new(false, NotAccount);
        var s = (slot ?? "").Trim().ToLowerInvariant();
        if (!LavkaCatalog.Slots.Contains(s)) return new(false, NoSlot);
        // Гімн і святкування у вигляді не малюються — розсилати всім той самий вигляд через них ні до чого
        var seen = s is not (LavkaKind.Anthem or LavkaKind.Fx);
        if (string.IsNullOrWhiteSpace(itemId))
        {
            lock (_gate) store.Unwear(nick, s);
            if (seen) Announce(nick);
            return new(true, "Знято");
        }
        if (LavkaCatalog.Get(itemId) is not { } item) return new(false, NoItem);
        if (item.Kind != s) return new(false, WrongSlot);
        if (!Has(nick, item)) return new(false, item.Ach is { } ach ? $"Цей титул дає ачівка «{AchTitle(ach)}»" : NotOwned);
        lock (_gate) store.Wear(nick, s, item.Id);
        if (seen) Announce(nick);
        return new(true, "Вдягнуто");
    }

    /// <summary>Розіслати всім свіжий вигляд ніка (вдягнув, зняв, поставив чи прибрав фото).</summary>
    public void Announce(string nick)
    {
        try { wire.Look(nick, LookOf(nick)); }
        catch (Exception ex) { log.LogWarning(ex, "Лавка не розіслала вигляд {Nick}", nick); }
    }

    // ---------- вміння ----------

    /// <summary>🎆 Феєрверк: усім подія <c>fireworks</c> і рядок у балачки (у базу не лягає). Помилка — текстом, успіх — null.</summary>
    public string? Fireworks(string nick, bool account)
    {
        if (!account) return NotAccount;
        var now = clock.UtcNow;
        lock (_gate)
        {
            if (!store.Owns(nick, LavkaCatalog.Fireworks)) return "Феєрверк — вміння з Лавки: спершу купи його";
            if (ReadyAt(nick, LavkaCatalog.Fireworks) is { } ready) return $"Новий феєрверк — через {Left(ready - now)}";
            store.UsePerk(nick, LavkaCatalog.Fireworks, now);
        }
        try
        {
            wire.Fireworks(nick);
            wire.Chat(new { id = 0L, kind = "fx", nick, text = Fx, at = now });
        }
        catch (Exception ex) { log.LogWarning(ex, "феєрверк {Nick} не розіслався", nick); }
        return null;
    }

    /// <summary>
    /// Присвята в ефір: Глек (той самий голос, що веде «Свою гру») каже «Цю пісню Оля присвячує Петрові — на удачу»
    /// просто перед наступним треком того, хто присвячує, який ще не пішов у liquidsoap. <paramref name="to"/> — нік або
    /// «*» (усім). Рядок у балачки лягає завжди; нема голосу (TTS вимкнений, не встиг, впав) — присвята однаково
    /// відбулась, і відповідь каже, що без голосу. Перерва — <see cref="DedicationGap"/> від кожної присвяти.
    /// </summary>
    public async Task<LavkaReply> DedicateAsync(string nick, bool account, string? to, string? phrase)
    {
        if (!account) return new(false, NotAccount);
        if (!store.Owns(nick, LavkaCatalog.Dedication)) return new(false, "Присвяти — вміння з Лавки: спершу купи «Присвята в ефір»");
        if (LavkaCatalog.Phrase(phrase) is not { } words) return new(false, "Обери, з чим присвятити");
        string? whom = null;   // як пишеться нік того, кому присвячують; null — усім
        if (!Everyone(to))
        {
            var name = Clean(to);
            if (name.Length == 0) return new(false, "Кому присвятити?");
            if (Auth.NickKey(name) == Auth.NickKey(nick)) return new(false, "Собі — то вже не присвята. Обери когось або всіх");
            whom = Spelling(name);
        }

        var key = Auth.NickKey(nick);
        if (!_dedicating.TryAdd(key, 0)) return new(false, "Присвята вже летить — ще мить");
        try
        {
            if (ReadyAt(nick, LavkaCatalog.Dedication) is { } ready) return new(false, $"Наступна присвята — через {Left(ready - clock.UtcNow)}");
            if (NextSong(nick) is not { } first) return new(false, NoSong);

            var toWhom = whom is null ? "всім, хто слухає" : NickCases.Dative(whom);
            // Людина вже натиснула — присвята має відбутись, навіть якщо вкладку закрили, поки Глек озвучував.
            var clip = await voice.SpeakAsync($"Цю пісню {nick} присвячує {toWhom} — {words}",
                whom is null ? "Присвята всім" : $"Присвята {toWhom}", nick, CancellationToken.None);
            // Поки Глек озвучував, черга могла зрушити: трек беремо свіжий
            var song = NextSong(nick);
            string? mute = null;
            if (clip is null) mute = Mute;
            else if (song is null) mute = "пісня вже пішла в ефір";
            else if (!air.AddVoiceBefore(clip.Track, clip.FilePath, nick, song.ItemId).Ok) mute = "ефір зараз не бере голосових";

            lock (_gate) store.UsePerk(nick, LavkaCatalog.Dedication, clock.UtcNow);
            var about = SongName((song ?? first).Track);
            try
            {
                var line = db.AddChat(nick, $"присвячує «{about}» {toWhom} — {words}", "dedication");
                wire.Chat(ChatLine(line, whom ?? "*"));
            }
            catch (Exception ex) { log.LogWarning(ex, "рядок присвяти від {Nick} не ліг у балачки", nick); }
            return mute is null
                ? new(true, $"Є! Глек скаже присвяту перед «{about}»")
                : new(true, $"Присвята лягла в балачки — без голосу: {mute}");
        }
        finally
        {
            _dedicating.TryRemove(key, out _);
        }
    }

    /// <summary>Наступний трек ніка, який ще не пішов у liquidsoap: не відправлений, не зламаний і не голосове.</summary>
    LavkaQueued? NextSong(string nick) => air.Queue().FirstOrDefault(q =>
        q.Status is not ("dispatched" or "failed") && !VoiceService.IsVoice(q.Track.Id) && Auth.NickKey(q.RequestedBy) == Auth.NickKey(nick));

    /// <summary>«Назва — Виконавець», як у контракті присвяти.</summary>
    static string SongName(TrackInfo t) => string.IsNullOrWhiteSpace(t.Artist) ? t.Title : $"{t.Title} — {t.Artist}";

    /// <summary>
    /// Як пишеться той, кому присвячують: акаунт — як зареєстрований, хтось на сайті — як його видно («оля» → «Оля»,
    /// «Вася» → «гість Вася»), решта — як написали: присвятити можна й тому, хто слухає десь у дорозі.
    /// </summary>
    string Spelling(string name)
    {
        if (db.FindAccount(name) is { } a) return a.Nick;
        var online = presence.Online;
        return online.FirstOrDefault(n => Auth.NickKey(n) == Auth.NickKey(name))
            ?? online.FirstOrDefault(n => Auth.NickKey(n) == Auth.NickKey(Auth.GuestPrefix + name))
            ?? name;
    }

    // ---------- дрібниці ----------

    /// <summary>«*» — усім; «всім» чи «усім», набране руками, — те саме, а не нік, який Глек просклоняв би в «всімові».</summary>
    static bool Everyone(string? to) => (to ?? "").Trim().ToLowerInvariant() is "*" or "всім" or "усім";

    /// <summary>Нік із запиту: без «@» попереду (так людей згадують у балачках) і без керівних символів.</summary>
    static string Clean(string? raw) => Auth.CleanNick((raw ?? "").Trim().TrimStart('@'));

    /// <summary>Рядок балачок так, як його шле сайт (<see cref="ChatMessage"/>), плюс <c>to</c> — кому.</summary>
    static object ChatLine(ChatMessage m, string to) => new
    {
        id = m.Id, nick = m.Nick, text = m.Text, at = m.At, kind = m.Kind, roomId = m.RoomId, replyTo = m.ReplyTo,
        replyNick = m.ReplyNick, replyText = m.ReplyText, likes = m.Likes, topic = m.Topic, to,
    };

    /// <summary>«2 год 14 хв», «7 хв», «40 с» — скільки ще чекати, вгору: «через 0 хв» ніхто не прочитає як «ще трохи».</summary>
    public static string Left(TimeSpan t)
    {
        if (t.TotalSeconds < 60) return $"{Math.Max(1, (int)Math.Ceiling(t.TotalSeconds))} с";
        var min = (int)Math.Ceiling(t.TotalMinutes);
        if (min < 60) return $"{min} хв";
        return min % 60 == 0 ? $"{min / 60} год" : $"{min / 60} год {min % 60} хв";
    }
}
