namespace Hlechyky.Games.Impl;

/// <summary>
/// Слово Дядька Глека на розкриття раунду — у виді (<c>reveal.say</c>), під таблицею, а не в Балачках (урок
/// «Скільки?»: за вечір там були б сотні рядків). <c>{0}</c> — нік у називному (відмінювати нема як, тож
/// фрази складені так, щоб нік стояв у називному й без роду), <c>{1}</c> — відстань (<see cref="GeoText.Km"/>).
/// Вибір — <c>Ctx.Rng</c>: той самий сід — ті самі фрази.
/// <para>
/// Похвала — за відстанню, а не лише за очками: «майже в ціль» за 400 км звучало б як знущання. До
/// <see cref="CloseKm"/> — «гостре око», до <see cref="GoodKm"/> — «знає ці краї», далі — рівно, без похвали,
/// а коли найближчий узяв менше за <see cref="FarPoints"/> очок (≈ 415 км) — «усі далеко».
/// </para>
/// </summary>
public static class GeoLines
{
    /// <summary>В яблучко (до кілометра).</summary>
    public static readonly string[] Bull =
    [
        "Точнісінько — {0}! Наче звідти й фотографували.",
        "{0} — у самісіньку правду.",
        "В яблучко: {0}, {1} від місця.",
    ];

    /// <summary>Компанія, найближчий — до <see cref="CloseKm"/>.</summary>
    public static readonly string[] Close =
    [
        "Гостре око в цьому раунді — {0}, лише {1}.",
        "{0} — майже в ціль: {1}.",
        "{0} тримає марку: {1} до цілі.",
        "Ще трохи — і в яблучко: {0}, {1}.",
    ];

    /// <summary>Компанія, найближчий — до <see cref="GoodKm"/>.</summary>
    public static readonly string[] Good =
    [
        "{0} знає ці краї: {1} убік.",
        "Найкраще чуття — {0}, різниця {1}.",
        "Непогано, {0}: {1} від правди.",
    ];

    /// <summary>Компанія, найближчий далі за <see cref="GoodKm"/>, але ще з очками: рівно, без похвали.</summary>
    public static readonly string[] Mid =
    [
        "Найближче — {0}: {1} від правди.",
        "Перше місце — {0}, промах {1}.",
        "Ближче за всіх — {0}, {1}.",
        "Тут виграє {0} — {1} повз.",
        "{0} на першому місці, {1} убік.",
    ];

    /// <summary>Компанія, і всі далеко: у найближчого менше <see cref="FarPoints"/> очок.</summary>
    public static readonly string[] AllFar =
    [
        "Ех, ніхто навіть близько. Найменший промах — {0}: {1}.",
        "Мимо всі. {0} хоч дивиться в той бік: {1}.",
        "Ця Україна сьогодні велика. Найближче — {0}, {1}.",
    ];

    /// <summary>Сам за столом і близько: «найближче» й «перше місце» самому звучали б смішно.</summary>
    public static readonly string[] SoloClose =
    [
        "Гостре око, {0}: лише {1}.",
        "{0} — майже в ціль: {1}.",
        "Ще трохи — і в яблучко, {0}: {1}.",
    ];

    /// <summary>Сам за столом, до <see cref="GoodKm"/>.</summary>
    public static readonly string[] SoloGood =
    [
        "{0} знає ці краї: {1} убік.",
        "Непогано, {0}: {1} від правди.",
        "{1} від місця — так тримати, {0}.",
    ];

    /// <summary>Сам за столом, далі за <see cref="GoodKm"/>, але ще з очками.</summary>
    public static readonly string[] SoloMid =
    [
        "{0}: {1} убік. Буває й ближче.",
        "{1} повз, {0}. Непогано, але можна краще.",
        "{0}, {1} від правди. Наступне — точніше.",
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

    /// <summary>Нижче цього в найближчого — «усі далеко» (≈ 415 км).</summary>
    public const int FarPoints = 500;
    /// <summary>До стількох кілометрів — «гостре око».</summary>
    public const double CloseKm = 30;
    /// <summary>До стількох — «знає ці краї»; далі похвали нема.</summary>
    public const double GoodKm = 150;

    /// <summary>
    /// Фраза раунду. <paramref name="nick"/> і <paramref name="km"/> — найближчий (null — ніхто не ставив);
    /// <paramref name="alone"/> — за столом один гравець.
    /// </summary>
    public static string Pick(Random rng, string? nick, double? km, int points, bool alone)
    {
        if (nick is null || km is not { } d) return None[rng.Next(None.Length)];
        var bank = d <= GeoScore.BullKm ? Bull
            : points < FarPoints ? (alone ? SoloFar : AllFar)
            : d <= CloseKm ? (alone ? SoloClose : Close)
            : d <= GoodKm ? (alone ? SoloGood : Good)
            : alone ? SoloMid : Mid;
        return string.Format(bank[rng.Next(bank.Length)], nick, GeoText.Km(d));
    }
}
