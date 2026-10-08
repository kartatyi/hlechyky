namespace Hlechyky.Games.Impl;

/// <summary>
/// Лад у коморі (08.10, пакет clk12/lad). Раніше повна комора продавала на базар усе нове з горна — і дзвінке, і
/// розкішне, — а лежали в ній сотні простих: smaug 06.10 мав 800 з 800, з них 340 простих, і не міг зібрати 60
/// дзвінких барил на Пристань. Тепер новий виріб, що не влазить, міняється з найдешевшим незахищеним із тих, що
/// лежать, — якщо такий дешевший за нього. Захищене — те, чого просять толока (поточний і наступний етап, лише чого
/// ще бракує) і відкриті замовлення гостей, сіл та майстерштук (той самий резерв, що в «Усе на віз»).
/// </summary>
public sealed partial class Clicker
{
    /// <summary>
    /// Звільнити місце під <paramref name="over"/> нових виробів, продаючи дешевші незахищені. Працює ключами й
    /// кількостями (комора — до сотень ключів, пачка — хоч тисячі штук), не штуками. Повертає глеки за продане;
    /// <paramref name="over"/> зменшується на те, що лягло в комору.
    /// </summary>
    double LadSwap(string ware, string style, int quality, ref int over)
    {
        if (over <= 0 || _items.Count == 0) return 0;
        // ItemValue на кожен ключ комори — пасив і клік рахуємо раз (поза видом пам'ять вимкнена).
        var memo = _memoOn;
        if (!memo)
        {
            _memoPassive = PassiveBase;
            _memoClick = ClickBase;
            _memoOn = true;
        }
        try
        {
            var price = ItemValue(ware, style, quality);
            var cheaper = new List<(string Key, ItemInfo Item, int Count, double Value)>();
            foreach (var (key, count) in _items)
            {
                if (count <= 0 || ParseItem(key) is not { } it) continue;
                var v = ItemValue(it.Ware, it.Style, it.Quality);
                if (v < price) cheaper.Add((key, it, count, v));
            }
            if (cheaper.Count == 0) return 0;
            cheaper.Sort((a, b) => a.Value != b.Value ? a.Value.CompareTo(b.Value) : string.CompareOrdinal(a.Key, b.Key));
            var kept = LadKept(Ctx.Clock.UtcNow);
            var newKey = ItemKey(ware, style, quality);
            double pots = 0;
            foreach (var c in cheaper)
            {
                if (over <= 0) break;
                var take = Math.Min(over, c.Count - kept.GetValueOrDefault(c.Key));
                if (take <= 0) continue;
                if (take == c.Count) _items.Remove(c.Key);
                else _items[c.Key] = c.Count - take;
                _items[newKey] = _items.GetValueOrDefault(newKey) + take;
                var got = c.Value * take;
                Add(got);
                pots += got;
                over -= take;
                LadNote(c.Item, take, got);
            }
            return pots;
        }
        finally { if (!memo) _memoOn = false; }
    }

    /// <summary>
    /// Скільки штук кожного ключа комори захищено: кожна вимога тримає стільки, скільки просить, з найгіршої
    /// придатної якості — так само, як «Усе на віз» (<see cref="WagonPick"/>).
    /// </summary>
    internal Dictionary<string, int> LadKept(DateTimeOffset now)
    {
        var left = new Dictionary<ItemInfo, int>();
        foreach (var (key, count) in _items)
            if (count > 0 && ParseItem(key) is { } it) left[it] = count;
        var kept = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (match, need) in LadHolds(now))
        {
            var want = need;
            foreach (var it in left.Keys.Where(match).OrderBy(x => x.Quality).ThenBy(x => x.Ware, StringComparer.Ordinal)
                         .ThenBy(x => x.Style, StringComparer.Ordinal).ToList())
            {
                if (want <= 0) break;
                var take = Math.Min(want, left[it]);
                if (take <= 0) continue;
                left[it] -= take;
                want -= take;
                var key = ItemKey(it.Ware, it.Style, it.Quality);
                kept[key] = kept.GetValueOrDefault(key) + take;
            }
        }
        return kept;
    }

    /// <summary>Чого чекають: замовлення й майстерштук (як для воза) і толока — поточний етап (чого бракує) і наступний.</summary>
    IEnumerable<(Func<ItemInfo, bool> Match, int Need)> LadHolds(DateTimeOffset now)
    {
        foreach (var h in WagonHolds(now)) yield return h;
        if (TolokaCurrent() is not { } b) yield break;
        var s = Math.Clamp(_tStage, 0, b.Stages.Length - 1);
        var st = b.Stages[s];
        // Закладений етап свої вироби вже забрав — тримати нічого.
        if (_tLaidAt == default)
            for (var i = 0; i < st.Needs.Length; i++)
                if (TolokaLeft(st, i) is var left and > 0) yield return (TolokaMatch(st.Needs[i]), left);
        if (TolokaAfter(b, s, _tBuilt, _total) is { } nx)
            foreach (var n in nx.Building.Stages[nx.Stage].Needs)
                yield return (TolokaMatch(n), n.N);
    }

    // ---------- підсумок одним рядком ----------

    /// <summary>Що продав лад просто зараз (горно бере собі в рядок) і за весь простій (у «поки тебе не було»). Не зберігається.</summary>
    readonly Dictionary<(string Ware, int Q), int> _ladNow = [], _ladAway = [];
    double _ladNowPots, _ladAwayPots;

    void LadNote(ItemInfo it, int n, double pots)
    {
        _ladNow[(it.Ware, it.Quality)] = _ladNow.GetValueOrDefault((it.Ware, it.Quality)) + n;
        _ladNowPots += pots;
        if (!_awayOpen) return;
        _ladAway[(it.Ware, it.Quality)] = _ladAway.GetValueOrDefault((it.Ware, it.Quality)) + n;
        _ladAwayPots += pots;
    }

    void LadNowClear()
    {
        _ladNow.Clear();
        _ladNowPots = 0;
    }

    /// <summary>«звільнив місце: продав 12 дешевших — горщик звичайний ×8, макітра добра ×4». null — нічого не продавав.</summary>
    static string? LadLine(Dictionary<(string Ware, int Q), int> sold)
    {
        if (sold.Count == 0) return null;
        var total = sold.Values.Sum();
        var top = sold.OrderByDescending(x => x.Value).ThenBy(x => x.Key.Ware, StringComparer.Ordinal).ThenBy(x => x.Key.Q).ToList();
        var what = string.Join(", ", top.Take(3).Select(x =>
            $"{WareOf(x.Key.Ware)?.Name.ToLowerInvariant() ?? x.Key.Ware} {QualityWord(x.Key.Ware, x.Key.Q)} ×{x.Value}"));
        return $"звільнив місце: продав {total} {Plural(total, "дешевший", "дешевші", "дешевших")} — {what}{(top.Count > 3 ? "…" : "")}";
    }

    /// <summary>Рядок горна: що лад продав за цей обпал (і забути).</summary>
    string? LadTakeNow()
    {
        var line = LadLine(_ladNow);
        LadNowClear();
        return line;
    }

    /// <summary>У запис «поки тебе не було» — один рядок на весь простій.</summary>
    void LadAwayFlush(List<string> notes)
    {
        if (LadLine(_ladAway) is { } line && notes.Count < AwayNotesMax)
            notes.Add($"🧺 Комора повна — {line}: +{PotsShort(_ladAwayPots)}");
        LadAwayClear();
    }

    void LadAwayClear()
    {
        _ladAway.Clear();
        _ladAwayPots = 0;
    }
}
