namespace Hlechyky.Games.Impl;

/// <summary>
/// Чи зараховується написане в «Мемології». Спершу — як усюди (<see cref="SvoyaAnswer.Hits"/>: ціла назва чи фраза,
/// одна помилка на п'ять літер). Зверху — «досить головних слів» (рішення 08.11.2026: довгий мем не встигаєш
/// дописати, хоч і знаєш): досить двох головних слів відповіді (в однослівній — одного) у будь-якому порядку й з
/// будь-якими закінченнями. «ляшко ліфт» влучає в «Це Ляшко, добрий день, ми в ліфті застрягли».
/// <para>
/// Головне слово — від трьох літер і не службове («це», «ми», «the»). Хоч одне з влучених мусить бути лише цього мему
/// в банку: «губка боб» на «Губка Боб, що передражнює» не влучає (Губок Бобів два), «кіт кивав» на «Кіт, що кивав у
/// такт» — так («кіт» є в багатьох, «кивав» — лише тут). Так назва одного мему не зараховується іншому.
/// </para>
/// </summary>
public static class MemyAnswer
{
    public const int MinWord = 3;
    /// <summary>Скільки головних слів досить (в однослівній відповіді — одного).</summary>
    public const int Need = 2;
    /// <summary>Скільки головних слів можна написати понад потрібні: перелік навздогад — не відповідь.</summary>
    public const int MaxExtra = 2;
    /// <summary>Основа слова для лічби по банку: перші п'ять літер латиницею («януковича» й «Yanukovych» — одне).</summary>
    const int StemLen = 5;

    /// <summary>Службові слова (після <see cref="MelodyAnswer.Key"/>: малі літери, без апострофів).</summary>
    static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        // українські
        "що", "щоб", "як", "але", "або", "аби", "для", "про", "при", "над", "під", "без", "між", "через", "біля", "після",
        "перед", "ось", "вже", "уже", "ще", "тут", "там", "так", "нє", "це", "цей", "ця", "ці", "цього", "той", "тої", "ті",
        "він", "вона", "воно", "вони", "мене", "мені", "тебе", "тобі", "нас", "вас", "нам", "вам", "їх", "його", "її",
        "мій", "моя", "моє", "мої", "твій", "твоя", "свій", "своя", "всі", "усі", "все", "усе", "весь", "вся", "бути",
        "був", "була", "було", "буде", "дуже", "теж", "також", "тоді", "коли", "лише", "тільки", "навіть", "знову",
        "чи", "хоч", "хай", "нехай", "де", "куди", "чому", "аж", "мем", "мему",
        // суржик і російське в цитатах
        "это", "что", "как", "все", "всё", "вот", "еще", "ещё", "тоже", "меня", "мне", "тебя", "она", "они",
        // англійські
        "the", "and", "but", "are", "was", "were", "been", "its", "this", "that", "these", "those", "you", "your",
        "our", "she", "they", "them", "his", "her", "for", "with", "from", "all", "not", "dont", "does", "did", "just",
        "again", "once", "what", "who", "youre", "have", "has",
    };

    /// <summary>Основа слова для лічби по банку: латиницею (кирилиця й латиниця зустрічаються), перші п'ять літер.</summary>
    public static string Stem(string word)
    {
        var l = MelodyAnswer.Latin(word);
        return l.Length <= StemLen ? l : l[..StemLen];
    }

    static IEnumerable<string> Content(string key)
    {
        foreach (var w in key.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (w.Length >= MinWord && !Stop.Contains(w)) yield return w;
    }

    /// <summary>Для <see cref="MemyBank.Words"/>: основа слова → у відповідях скількох мемів вона є (у межах мему — раз).</summary>
    public static IReadOnlyDictionary<string, int> Count(IEnumerable<MemyMeme> memes)
    {
        var count = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var m in memes)
        {
            var mine = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in m.NameAnswers)
                foreach (var w in Content(MelodyAnswer.Key(a))) mine.Add(Stem(w));
            foreach (var s in mine) count[s] = count.GetValueOrDefault(s) + 1;
        }
        return count;
    }

    public static bool Hits(string? text, IEnumerable<string> answers, MemyBank bank) => Hits(text, answers, bank.Words);

    public static bool Hits(string? text, IEnumerable<string> answers, IReadOnlyDictionary<string, int> words)
    {
        var list = answers as IReadOnlyCollection<string> ?? [.. answers];
        if (SvoyaAnswer.Hits(text, list)) return true;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var said = Content(MelodyAnswer.Key(text)).Distinct(StringComparer.Ordinal).ToList();
        if (said.Count == 0) return false;
        foreach (var a in list)
        {
            var keys = Content(MelodyAnswer.Key(a)).Distinct(StringComparer.Ordinal).ToList();
            if (keys.Count == 0) continue;
            var need = Math.Min(keys.Count, Need);
            if (said.Count > Math.Max(need + MaxExtra, keys.Count)) continue;
            // кожне написане слово закриває щонайбільше одне головне
            var used = new bool[said.Count];
            var matched = 0;
            var own = false;
            foreach (var k in keys)
            {
                for (var i = 0; i < said.Count; i++)
                {
                    if (used[i] || !Same(said[i], k, loose: need > 1)) continue;
                    used[i] = true;
                    matched++;
                    own |= words.GetValueOrDefault(Stem(k)) <= 1;
                    break;
                }
            }
            if (matched >= need && own) return true;
        }
        return false;
    }

    /// <summary>
    /// Те саме слово: однакові; закінчення (коротше — початок довшого, хвіст довшого до трьох літер: «ліфт» — «ліфті»,
    /// «застряг» — «застрягли»); одна помилка на п'ять літер. <paramref name="loose"/> (відповідь із двох і більше
    /// головних слів) — ще й інша остання літера коротшого: «хата» — «хату». В однослівній так не можна: «петро» не «петя»,
    /// «танк» не «танос». Латиницею порівнюємо, лише коли письмо різне («zelensky» — «зеленський»): зведення до
    /// латиниці зливає і/и й скорочує слова, тож між двома кириличними воно дає хибні збіги («малий» — «палій»).
    /// </summary>
    public static bool Same(string said, string key, bool loose = true)
    {
        if (said == key || Near(said, key, loose)) return true;
        if (Cyrillic(said) == Cyrillic(key)) return false;
        var (ls, lk) = (MelodyAnswer.Latin(said), MelodyAnswer.Latin(key));
        return ls == lk || Typo(ls, lk);
    }

    static bool Cyrillic(string w)
    {
        foreach (var ch in w) if (ch is >= '\u0400' and <= '\u04FF') return true;
        return false;
    }

    static bool Near(string a, string b, bool loose)
    {
        var shorter = Math.Min(a.Length, b.Length);
        if (shorter >= 4)
        {
            var p = 0;
            while (p < shorter && a[p] == b[p]) p++;
            if (p >= (loose ? shorter - 1 : shorter) && Math.Max(a.Length, b.Length) - p <= 3) return true;
        }
        return Typo(a, b);
    }

    /// <summary>Одна помилка на п'ять літер (як у <see cref="MelodyAnswer"/>).</summary>
    static bool Typo(string a, string b)
    {
        var max = b.Length / 5;
        return max > 0 && Math.Abs(a.Length - b.Length) <= max && MelodyAnswer.Distance(a, b) <= max;
    }
}
