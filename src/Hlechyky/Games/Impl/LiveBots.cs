using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «🤖 + бот» у живих іграх (аерохокей, змійка, юрма…): господар, що сидить за столом сам, кличе бота-суперника,
/// а рівень бота — опція столу. Спільні тут лише правила виклику, текст і рівні; як бот грає — справа кожної гри.
///
/// Бот не сидить у каркасі: місце порожнє, тому нагород, рейтингу й ачівок партія з ботом не дає (Rewards рахує лише
/// людей, від двох різних). Гра, що покликала бота, сама пам'ятає, де він, і каже про це в <see cref="Game.SeatBot"/>.
/// Кнопку малює core.js поруч із «Почати», коли у виді гри є <c>botOffer: true</c> (див. <see cref="SoloBot.Offer"/>).
/// </summary>
public static class LiveBots
{
    /// <summary>Дія лобі: покликати чи прогнати бота (payload <c>{on: bool}</c>; без нього — перемкнути).</summary>
    public const string Toggle = "bot";

    public const string Name = "🤖 бот";

    public const string AloneText = "Сам на сам не пограєш: тисни «🤖 + бот» — або зачекай друга";

    public enum Level { Easy, Normal, Hard }

    public static readonly GameOption LevelOption = new("botlvl", "🤖 Бот, коли граєш сам",
        [("easy", "легкий"), ("normal", "звичайний"), ("hard", "сильний")], "normal");

    /// <summary>Та сама опція для ігор, де боти доповнюють стіл (<see cref="SoloBot.Most"/> ≥ 2): ключ і значення ті самі.</summary>
    public static readonly GameOption LevelsOption = new("botlvl", "🤖 Рівень ботів",
        [("easy", "легкий"), ("normal", "звичайний"), ("hard", "сильний")], "normal");

    /// <summary>Опція рівня під гру: один бот — «Бот, коли граєш сам», кілька — «Рівень ботів».</summary>
    public static GameOption LevelFor(int most) => most >= 2 ? LevelsOption : LevelOption;

    public static Level Read(IReadOnlyDictionary<string, string> options) =>
        options.TryGetValue(LevelOption.Key, out var v) ? v switch
        {
            "easy" => Level.Easy,
            "hard" => Level.Hard,
            _ => Level.Normal,
        } : Level.Normal;

    public static string Key(Level level) => level switch { Level.Easy => "easy", Level.Hard => "hard", _ => "normal" };

    /// <summary>Рівень у родовому для підсумків: «перемога над легким ботом».</summary>
    public static string Of(Level level) => level switch { Level.Easy => "легким", Level.Hard => "сильним", _ => "звичайним" };

    /// <summary>Рівень числом 0/1/2 — зручно для таблиць похибок і реакцій у ботах ігор.</summary>
    public static int Index(Level level) => (int)level;
}

/// <summary>
/// Стан «🤖 + бот» одного столу: чи кликали ботів і якого рівня. Гра тримає екземпляр у полі, кличе
/// <see cref="Configure"/> з <c>Configure(options)</c>, <see cref="Switch"/> — з <c>Act</c> у лобі (і між партіями),
/// <see cref="CanStart"/> — з <c>CanStart()</c>, а <see cref="Offer"/>/<see cref="Fill"/> кладе у свій вид.
///
/// Боти доповнюють стіл до <c>1 + Most</c> — стільки, скільки сідало б із самотньою людиною: сам — <see cref="Most"/>
/// ботів, удвох — на одного менше, і так до повного. Підсів друг у лобі — ботів менше саме собою (<see cref="Count"/>
/// рахується щоразу); посеред партії склад тримає гра (запам'ятовує місця ботів на старті).
/// </summary>
public sealed class SoloBot(int most = 1)
{
    /// <summary>
    /// Скільки ботів сідає максимум (із самотньою людиною). 1 — класичне «сам на сам із ботом». Гра з режимами може
    /// міняти його в <c>Configure</c> (Замри: естафета — 1, кожен за себе — 2); «кликали» від цього не скидається.
    /// </summary>
    public int Most { get => _most; set => _most = Math.Max(1, value); }
    int _most = Math.Max(1, most);

    /// <summary>До скількох доповнюють стіл: <c>1 + Most</c>. Поле виду <c>botFill</c> (core.js — «🤖 + боти до N»).</summary>
    public int Fill => 1 + Most;

    public bool Wanted { get; private set; }
    public LiveBots.Level Level { get; private set; } = LiveBots.Level.Normal;

    public void Configure(IReadOnlyDictionary<string, string> options) => Level = LiveBots.Read(options);

    /// <summary>Скільки людей сидить за столом на <paramref name="seats"/> місць.</summary>
    public static int Humans(IRoomContext ctx, int seats)
    {
        var n = 0;
        for (var s = 0; s < seats; s++) if (ctx.Seated(s)) n++;
        return n;
    }

    /// <summary>
    /// Скільки ботів сідає в партію зараз: кликали й є хоч одна людина — <c>1 + Most − людей</c> (від 0 до
    /// <see cref="Most"/>), і не більше вільних місць. Для <c>Most = 1</c> — один бот лише з самотньою людиною.
    /// </summary>
    public int Count(IRoomContext ctx, int seats)
    {
        if (!Wanted) return 0;
        var humans = Humans(ctx, seats);
        if (humans < 1) return 0;
        return Math.Min(Math.Clamp(1 + Most - humans, 0, Most), seats - humans);
    }

    /// <summary>Чи грають боти в цій партії. Підсіли друзі до повного «1 + Most» — боти йдуть самі, без кнопок.</summary>
    public bool Active(IRoomContext ctx, int seats) => Count(ctx, seats) > 0;

    public string? CanStart(IRoomContext ctx, int seats) => Humans(ctx, seats) == 1 && !Wanted ? LiveBots.AloneText : null;

    /// <summary>
    /// Дія <see cref="LiveBots.Toggle"/>: кличе лише господар і лише коли людей від одного до <see cref="Most"/> — є місце
    /// хоч на одного бота (прогнати можна завжди).
    /// </summary>
    public ActResult Switch(IRoomContext ctx, int seat, JsonElement payload, int seats)
    {
        if (seat != ctx.HostSeat) return ActResult.Fail(Most == 1 ? "Бота кличе господар столу" : "Ботів кличе господар столу");
        var on = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("on", out var p)
            ? p.ValueKind == JsonValueKind.True : !Wanted;
        var humans = Humans(ctx, seats);
        if (on && (humans < 1 || humans > Most))
            return ActResult.Fail(Most == 1 ? "Бот грає лише з тим, хто сам за столом" : $"Боти доповнюють стіл лише до {Fill}");
        Wanted = on;
        if (Most == 1) return ActResult.Accept(on ? "🤖 Бот сів навпроти" : "Бот пішов");
        if (!on) return ActResult.Accept("Боти пішли");
        return ActResult.Accept(humans == 1 ? $"🤖 Боти сіли: {Count(ctx, seats)}" : $"🤖 Боти доповнять стіл до {Fill}");
    }

    /// <summary>
    /// Поле виду <c>botOffer</c>: core.js тоді показує господареві кнопку «🤖 + бот» (чи «Прогнати бота», коли
    /// <c>botWanted = Wanted</c>); коли людей двоє й більше — «🤖 + боти до N» з <c>botFill = Fill</c>. Гра кладе у вид
    /// ще <c>botLvl = LevelKey</c> — для підписів — і <c>bot</c>: місце (чи масив місць) бота в цій партії.
    /// Під час партії core.js кнопку не показує, тож тут фаз не питаємо.
    /// </summary>
    public bool Offer(IRoomContext ctx, int seats) => Wanted || Humans(ctx, seats) is var h && h >= 1 && h <= Most;

    public string LevelKey => LiveBots.Key(Level);
}
