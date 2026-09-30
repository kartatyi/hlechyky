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
/// Стан «🤖 + бот» одного столу: чи кликали бота і якого рівня. Гра тримає екземпляр у полі, кличе
/// <see cref="Configure"/> з <c>Configure(options)</c>, <see cref="Switch"/> — з <c>Act</c> у лобі (і між партіями),
/// <see cref="CanStart"/> — з <c>CanStart()</c>, а <see cref="Offer"/> кладе у свій вид.
/// </summary>
public sealed class SoloBot
{
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

    /// <summary>Бот грає, якщо його кликали й людина за столом одна. Підсів друг — бот іде сам, без кнопок.</summary>
    public bool Active(IRoomContext ctx, int seats) => Wanted && Humans(ctx, seats) == 1;

    public string? CanStart(IRoomContext ctx, int seats) => Humans(ctx, seats) == 1 && !Wanted ? LiveBots.AloneText : null;

    /// <summary>Дія <see cref="LiveBots.Toggle"/>: кличе лише господар і лише сам за столом (прогнати можна завжди).</summary>
    public ActResult Switch(IRoomContext ctx, int seat, JsonElement payload, int seats)
    {
        if (seat != ctx.HostSeat) return ActResult.Fail("Бота кличе господар столу");
        var on = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("on", out var p)
            ? p.ValueKind == JsonValueKind.True : !Wanted;
        if (on && Humans(ctx, seats) != 1) return ActResult.Fail("Бот грає лише з тим, хто сам за столом");
        Wanted = on;
        return ActResult.Accept(on ? "🤖 Бот сів навпроти" : "Бот пішов");
    }

    /// <summary>
    /// Поле виду <c>botOffer</c>: core.js тоді показує господареві кнопку «🤖 + бот» (чи «Прогнати бота», коли
    /// <c>botWanted = Wanted</c>). Гра кладе у вид ще <c>botLvl = LevelKey</c> — для підписів — і <c>bot</c>: місце
    /// (чи масив місць) бота в цій партії. Під час партії core.js кнопку не показує, тож тут фаз не питаємо.
    /// </summary>
    public bool Offer(IRoomContext ctx, int seats) => Wanted || Humans(ctx, seats) == 1;

    public string LevelKey => LiveBots.Key(Level);
}
