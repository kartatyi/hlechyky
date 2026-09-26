using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Заморські гості — «Гостинний двір» (десяте оновлення, docs/games/specs/clicker-v10.md §7). Шість щаблів нової
/// драбини приводять своїх гостей (царградські й кантонські купці, діаспора з Канади, лондонські торговці, паризькі
/// колекціонери, гончарі з усього світу): вони замовляють вироби з горна й мають свою шану з рівнями.
///
/// Поки що це заготовка з усіма гачками, які кличе ядро: гостей нема, усі множники нейтральні. Пакет C замінює
/// тіло цього файла — сигнатури лишаються ті самі.
/// </summary>
public sealed partial class Clicker
{
    /// <summary>Щаблі драбини, що приводять гостей, — у порядку прибуття.</summary>
    public static readonly string[] GuestTiers = ["port", "voyage", "ocean", "exchange", "expo", "opishnia"];

    // ---------- що гості дають ядру (нейтральні, доки гостей нема) ----------

    /// <summary>+3 % до всього за кожен рівень шани будь-яких гостей.</summary>
    double GuestsAllMult => 1;
    /// <summary>Гончарі з усього світу: додаток до глека з полиці (поруч із кошиком).</summary>
    double GuestsFallBonus => 0;
    /// <summary>Кантонські купці: множник чекання розписного глека (≤ 1).</summary>
    double GuestsGoldenWait => 1;
    /// <summary>Діаспора: додаток до плати купців хати й замовлень сіл.</summary>
    double GuestsPayBonus => 0;
    /// <summary>Паризькі колекціонери: коло крутиться без тебе довше (загальна стеля — 24 год, її ставить ядро).</summary>
    TimeSpan GuestsOfflineExtra => TimeSpan.Zero;
    /// <summary>Царградські купці: додаток до ціни виробів на базарі й у замовленнях.</summary>
    double GuestsValueBonus => 0;
    /// <summary>Лондонські торговці: додаток до множника ярмарку розписного глека.</summary>
    double GuestsFairBonus => 0;

    // ---------- гачки життя партії ----------

    /// <summary>Стан пакета в збереженні. Усе необов'язкове: старе збереження — «гостей ще не було».</summary>
    sealed record GuestsRow;

    void ResetGuests(DateTimeOffset now) { }

    /// <summary>Той самий оплачений проміжок, що й у пасиву: розклад замовлень, прибуття нових гостей.</summary>
    void SyncGuests(DateTimeOffset now, TimeSpan paid) { }

    /// <summary>Дія «guests» (віддати замовлення, відпустити гостя). null — дія не наша.</summary>
    ActResult? ActGuests(string action, JsonElement payload) => null;

    /// <summary>Вид «guests»: null, поки гостей нема.</summary>
    object? ViewGuests(DateTimeOffset now) => null;

    /// <summary>Тексти гостей (хто, звідки, що любить, пільги) — у каталог, а не щопачки.</summary>
    object? CatalogGuests() => null;

    GuestsRow? SaveGuests() => null;

    void LoadGuests(GuestsRow? row) { }

    /// <summary>Обпал гостей не проганяє: шана й замовлення лишаються.</summary>
    void FireGuests(DateTimeOffset now) { }
}
