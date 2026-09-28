using System.Text.Json;

namespace Hlechyky.Games;

/// <summary>
/// Коли гра вперше з'явилась на сервері (прохід №3, п. 249) — для «🆕 нова гра» в лобі. Раніше позначка трималась
/// лише на <c>added</c> у модулі, і 15 ігор 26–27.09 вийшли без нього: автор забув — гра не нова. Тепер сервер сам
/// запам'ятовує день, коли вперше побачив Id у реєстрі. Перший запуск з цим кодом бачить уже весь наявний набір —
/// усім їм пише <see cref="Old"/> (вони не нові; свіжим лишається <c>added</c> з модуля), а все, що прийде пізніше, —
/// дату першого старту з ним. Сховище — рядок <c>games:added</c> у game_state, без нової таблиці.
/// </summary>
public sealed class GameAdded(IGameStore store, IClock clock)
{
    public const string Key = "games:added";
    public const string Old = "2000-01-01";
    readonly Lock _gate = new();
    IReadOnlyDictionary<string, string>? _map;
    int _seen = -1;

    /// <summary>Id → «рррр-мм-дд». Під каталог кличуть на кожен запит — читаємо базу раз, далі з пам'яті.</summary>
    public IReadOnlyDictionary<string, string> Map(IReadOnlyList<CatalogGame> games)
    {
        lock (_gate)
        {
            if (_map is not null && _seen == games.Count) return _map;
            Dictionary<string, string> map;
            var json = store.LoadState(Key);
            var first = string.IsNullOrEmpty(json);
            try { map = first ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(json!) ?? []; }
            catch (JsonException) { map = []; first = true; }
            var today = Days.Today(clock);
            var dirty = false;
            foreach (var g in games)
            {
                if (map.ContainsKey(g.Id)) continue;
                map[g.Id] = first ? Old : today;
                dirty = true;
            }
            if (dirty)
            {
                try { store.SaveState(Key, JsonSerializer.Serialize(map)); }
                catch { /* база зайнята — наступний запит спробує ще; у пам'яті дата вже є */ }
            }
            _seen = games.Count;
            // Старим іграм дати не шлемо: клієнтові вона ні до чого, а каталог — на кожне відкриття лобі.
            return _map = map.Where(p => p.Value != Old).ToDictionary(p => p.Key, p => p.Value);
        }
    }
}
