namespace Hlechyky.Games.Impl;

/// <summary>
/// Слово Дядька Глека на розкриття раунду — у виді (<c>reveal.say</c>), під таблицею, а не в Балачках (урок
/// «Скільки?»: за вечір там були б сотні рядків). <c>{0}</c> — нік у називному (відмінювати нема як, тож
/// фрази складені так, щоб нік стояв у називному й без роду), <c>{1}</c> — відстань (<see cref="GeoText.Km"/>).
/// Вибір — <c>Ctx.Rng</c>: той самий сід — ті самі фрази.
/// </summary>
public static class GeoLines
{
    /// <summary>Звичайний раунд у компанії: найближчий узяв щонайменше 500 очок.</summary>
    public static readonly string[] Normal =
    [
        "Найближче — {0}: {1} від правди.",
        "{0} знає ці краї: {1} убік.",
        "Око-алмаз у цьому раунді — {0}, лише {1}.",
        "Перше місце — {0}, промах {1}.",
        "{0} — майже в ціль: {1}.",
        "Ближче за всіх — {0}, {1}.",
        "{0} тримає марку: {1} до цілі.",
        "Тут виграє {0} — {1} повз.",
        "Найкраще чуття — {0}, різниця {1}.",
        "{0} на першому місці, {1} убік.",
    ];

    /// <summary>В яблучко (до кілометра).</summary>
    public static readonly string[] Bull =
    [
        "Точнісінько — {0}! Наче звідти й фотографували.",
        "{0} — у самісіньку правду.",
        "В яблучко: {0}, {1} від місця.",
    ];

    /// <summary>Компанія, і всі далеко: у найближчого менше 500 очок.</summary>
    public static readonly string[] AllFar =
    [
        "Ех, ніхто навіть близько. Найменший промах — {0}: {1}.",
        "Мимо всі. {0} хоч дивиться в той бік: {1}.",
        "Ця Україна сьогодні велика. Найближче — {0}, {1}.",
    ];

    /// <summary>Сам за столом і близько (≥ 500 очок): «найближче» й «перше місце» самому звучать смішно.</summary>
    public static readonly string[] SoloNear =
    [
        "{0} знає ці краї: {1} убік.",
        "Непогано, {0}: {1} від правди.",
        "{1} від місця — так тримати, {0}.",
        "{0} — майже в ціль: {1}.",
        "Око-алмаз, {0}: лише {1}.",
    ];

    /// <summary>Сам за столом і далеко.</summary>
    public static readonly string[] SoloFar =
    [
        "Далеченько — {1} убік. Наступне буде ближче, {0}.",
        "{0}, це не там: {1} повз.",
        "Мимо на {1}. Ну, тепер ти це знаєш, {0}.",
    ];

    /// <summary>Жодної шпильки на мапі.</summary>
    public static readonly string[] None =
    [
        "Тиша на мапі. Наступне.",
        "Жодної шпильки. Буває.",
    ];

    /// <summary>Нижче цього в найближчого — «усі далеко».</summary>
    public const int FarPoints = 500;

    /// <summary>
    /// Фраза раунду. <paramref name="nick"/> і <paramref name="km"/> — найближчий (null — ніхто не ставив);
    /// <paramref name="alone"/> — за столом один гравець.
    /// </summary>
    public static string Pick(Random rng, string? nick, double? km, int points, bool alone)
    {
        if (nick is null || km is not { } d) return None[rng.Next(None.Length)];
        var bank = d <= GeoScore.BullKm ? Bull
            : alone ? (points >= FarPoints ? SoloNear : SoloFar)
            : points >= FarPoints ? Normal : AllFar;
        return string.Format(bank[rng.Next(bank.Length)], nick, GeoText.Km(d));
    }
}
