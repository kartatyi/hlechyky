using System.Globalization;

namespace Hlechyky;

/// <summary>
/// Зведення «Хто скільки» v3: що одна частина «Літопису» підказує іншій. «🎯 Мої цілі» дізнаються місце в «🏁 Гонці
/// тижня» (LitopysCharts.cs), а «🔔 Нове» (LitopysNews.cs) — коли варто зазирнути в «🏺 Глека», «📈 Графіки» й «📖 Рекорди».
/// </summary>
public sealed partial class Litopys
{
    /// <summary>
    /// Ціль «🏁 Гонка тижня»: кого обженеш і скільки очок бракує, а лідерові — який у нього відрив. Перемога — 3 очки,
    /// тож Ease = бракує / 9 — «три перемоги» вважаємо далеким, одна — близьким.
    /// </summary>
    partial void MoreGoals(string key, List<Goal> goals)
    {
        var race = RaceTable("week", clock.UtcNow);
        var me = race.Find(r => r.Key == key);
        if (me is null || me.Points == 0 || race.Count < 2) return;
        if (me.Place == 1)
        {
            var next = race.FirstOrDefault(r => r.Place > 1);
            if (next is null) return;
            var lead = me.Points - next.Points;
            goals.Add(new Goal("race", "🏁", Parts("Ти ведеш у Гонці тижня, а §0 відстає лише на " + Count(lead, "очко", "очки", "очок"), next.Nick),
                lead, me.Points, me.Points, "#stats/charts", "До гонки", Math.Min(1, 0.3 + lead / 18.0)));
            return;
        }
        var ahead = race.Last(r => r.Place < me.Place);
        var gap = ahead.Points - me.Points + 1;
        goals.Add(new Goal("race", "🏁", Parts("Ще " + Count(gap, "очко", "очки", "очок") + " — і обженеш §0 у Гонці тижня (зараз ти " + me.Place + "-й)", ahead.Nick),
            gap, me.Points, ahead.Points + 1, "#games", "За стіл", gap / 9.0));
    }

    /// <summary>
    /// «🔔 Нове» для вкладок з інших пакетів. Сигнатури міняються лише тоді, коли людині справді є що глянути:
    /// <c>records</c> — хтось перебив рекорд; <c>charts</c> — моє місце в Гонці тижня чи її лідер; <c>glek</c> — Глек
    /// прожарив мене в ефірі.
    /// </summary>
    partial void MorePulse(string key, Dictionary<string, string> parts)
    {
        parts["records"] = RecordsSig();
        var race = RaceTable("week", clock.UtcNow);
        var leader = race.Count > 0 ? race[0].Key : "";
        var mine = key.Length > 0 ? race.Find(r => r.Key == key)?.Place ?? 0 : 0;
        parts["charts"] = Sig("lead:" + leader, "me:" + mine.ToString(CultureInfo.InvariantCulture));
        if (key.Length > 0) parts["glek"] = Sig("roast:" + LastRoastOf(key));
    }

    /// <summary>Остання прожарка людини, що пішла в ефір (id), — щоб «нове» на 🏺 Глеку з'являлось після неї.</summary>
    string LastRoastOf(string key) => Cached("wire:roast:" + key, () => db.With(c =>
    {
        if (!HasTable(c, "live_ads")) return "";   // таблицю заводить жива реклама — у тестовій базі її може не бути
        using var cmd = Cmd(c, "SELECT MAX(id) FROM live_ads WHERE kind = 'roast' AND status = 'aired' AND target_key = $k", ("$k", key));
        return cmd.ExecuteScalar() is long id ? id.ToString(CultureInfo.InvariantCulture) : "";
    }));
}
