using System.Globalization;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «🎲 Мікс» (прохід №3, п. 19): пакет із тем усіх вбудованих пакетів — спершу тих, яких ніхто за цим столом ще не
/// бачив (пам'ять — <see cref="SvoyaSeen"/>), далі бачених найдавніше. Нового контенту не треба, а повторів менше.
/// <para>
/// Теми з медіа не беремо: файли медіа лежать у теці рідного пакета й роздаються за його id. Складність тримаємо:
/// теми першого раунду рідного пакета йдуть у перший раунд міксу, другого — у другий, решта — у третій; ціни
/// перераховуються під раунд міксу (<c>100 × раунд × (1..5)</c>).
/// </para>
/// </summary>
public static class SvoyaMix
{
    public const string Id = "x_mix";
    public const string Title = "🎲 Мікс";
    public const int Rounds = 3, ThemesPerRound = 5, FinalThemes = 5;
    public const string Note = "Теми з усіх пакетів Глечиків — спершу ті, яких ніхто за цим столом ще не бачив";

    static readonly string[] RoundNames = ["Перший раунд", "Другий раунд", "Третій раунд"];

    /// <summary>Ключ теми для пам'яті «бачили»: рідний пакет, номер раунду (фінал — «final») і назва.</summary>
    public static string Key(string packId, int round, SvoyaRound r, SvoyaTheme t) =>
        t.Origin ?? $"{packId}/{(r.IsFinal ? "final" : round.ToString(CultureInfo.InvariantCulture))}/{t.Name}";

    sealed record Pick(SvoyaTheme Theme, string Key, int Level);

    /// <summary>Зібрати мікс. <paramref name="fresh"/> — скільки обраних тем цей стіл ще не бачив.</summary>
    public static SvoyaPack? Build(IEnumerable<SvoyaPack> sources, IReadOnlyDictionary<string, DateTimeOffset> lastSeen, Random rng, out int fresh)
    {
        var normal = new List<Pick>();
        var final = new List<Pick>();
        foreach (var p in sources)
        {
            var level = 0;
            for (var ri = 0; ri < p.Rounds.Count; ri++)
            {
                var r = p.Rounds[ri];
                foreach (var t in r.Themes)
                    if (t.Questions.Count > 0 && t.Questions.All(q => q.Media is null && q.AnswerMedia is null))
                        (r.IsFinal ? final : normal).Add(new Pick(t, Key(p.Id, ri, r, t), Math.Min(level, Rounds - 1)));
                if (!r.IsFinal) level++;
            }
        }
        fresh = 0;
        if (normal.Count == 0) return null;

        // найсвіжіші: спершу небачені, далі бачені найдавніше; серед рівних — навмання; без двох тем з однаковою назвою
        var chosen = new List<Pick>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in Freshest(normal, lastSeen, rng))
        {
            if (chosen.Count >= Rounds * ThemesPerRound) break;
            if (names.Add(x.Theme.Name)) chosen.Add(x);
        }
        var finals = new List<Pick>();
        foreach (var x in Freshest(final, lastSeen, rng))
        {
            if (finals.Count >= FinalThemes) break;
            if (names.Add(x.Theme.Name)) finals.Add(x);
        }
        fresh = chosen.Concat(finals).Count(x => !lastSeen.ContainsKey(x.Key));

        var pack = new SvoyaPack
        {
            Id = Id, Title = Title, Author = SvoyaBuiltin.Author, AuthorKey = "", Public = true, Source = SvoyaPack.Builtin,
        };
        var ordered = chosen.OrderBy(x => x.Level).ToList();      // стабільно: легші — раніше
        var perRound = (int)Math.Ceiling(ordered.Count / (double)Rounds);
        for (var k = 0; k * perRound < ordered.Count; k++)
            pack.Rounds.Add(new SvoyaRound
            {
                Name = RoundNames[Math.Min(k, RoundNames.Length - 1)],
                Themes = [.. ordered.Skip(k * perRound).Take(perRound).Select(x => Copy(x, k))],
            });
        if (finals.Count >= SvoyaPack.MinFinalThemes)
            pack.Rounds.Add(new SvoyaRound
            {
                Name = "Фінал", Type = SvoyaRound.Final,
                Themes = [.. finals.Select(x => new SvoyaTheme { Name = x.Theme.Name, Origin = x.Key, Questions = [Copy(x.Theme.Questions[0], 0)] })],
            });
        pack.Description = Describe(chosen.Count + finals.Count, fresh);
        return pack;
    }

    static string Describe(int total, int fresh) =>
        fresh == total ? $"{Note}. Усі {total} тем — нові для вас" : $"{Note}. Нових для вас — {fresh} із {total}";

    /// <summary>
    /// Опис міксу, яким його гратимуть: «Один раунд і фінал» чи бліц лишають на полі менше тем, ніж зібрано, — тож
    /// рахуємо лише ті, що лишились (рецензія проходу №3: писало «Усі 20 тем» при полі 5+5).
    /// </summary>
    public static string Describe(SvoyaPack shaped, IReadOnlyDictionary<string, DateTimeOffset> lastSeen)
    {
        int total = 0, fresh = 0;
        foreach (var r in shaped.Rounds)
            foreach (var t in r.Themes)
            {
                total++;
                if (t.Origin is null || !lastSeen.ContainsKey(t.Origin)) fresh++;
            }
        return Describe(total, fresh);
    }

    static List<Pick> Freshest(List<Pick> all, IReadOnlyDictionary<string, DateTimeOffset> lastSeen, Random rng)
    {
        var list = new List<Pick>(all);
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return [.. list.OrderBy(x => lastSeen.TryGetValue(x.Key, out var at) ? at : DateTimeOffset.MinValue)];
    }

    static SvoyaTheme Copy(Pick x, int round) => new()
    {
        Name = x.Theme.Name,
        Origin = x.Key,
        Questions = [.. x.Theme.Questions.Take(SvoyaPack.MaxQuestions).Select((q, i) => Copy(q, (i + 1) * SvoyaPack.PriceStep * (round + 1)))],
    };

    static SvoyaQuestion Copy(SvoyaQuestion q, int price) => new()
    {
        Price = price, Type = q.Type, Text = q.Text, Media = q.Media, Answer = q.Answer, Accept = [.. q.Accept],
        AnswerMedia = q.AnswerMedia, Comment = q.Comment, CatPrice = q.CatPrice is null ? null : price,
    };
}
