using System.Globalization;
using System.Text;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Перевірка тексту «Байкарів»: чи брехня часом не правда (<see cref="LooksTrue"/>) і чи дві брехні — одна й та
/// сама (<see cref="LooksSame"/>). Чиста, без стану: на вході рядки, на виході так/ні. Евристика свідомо груба:
/// відмінки й одруківки ловимо, синоніми — ні (за них відповідає поле <c>accept</c> банку). Хибна відмова чесній
/// брехні («гасло» при правді «гасі») дешева — людина напише іншу; прийнята правда зіпсувала б питання всім.
/// </summary>
public static class BluffText
{
    /// <summary>
    /// Для порівнянь: NFC, нижній регістр, усі апострофи — один «'», латинські двійники кириличних літер — кирилицею,
    /// невидимі знаки й наголоси геть, решта не-літер — пробіл, пробіли злиті. «Гасі!» → «гасі», «Пд. Буг» → «пд буг».
    /// </summary>
    public static string Norm(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        var sb = new StringBuilder(s.Length);
        var gap = false;
        foreach (var raw in s)
        {
            var ch = Fold(raw);
            var cat = char.GetUnicodeCategory(ch);
            // Невидимі (нульовий пробіл, м'який перенос) і наголоси (гá-сі): інакше «га​сі» на картці читалась би
            // як «гасі», а для перевірки була б двома словами «га» і «сі» — правда проскочила б непоміченою.
            if (cat is UnicodeCategory.Format or UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark) continue;
            if (char.IsLetterOrDigit(ch) || ch == '\'' || ch == '-')
            {
                if (gap && sb.Length > 0) sb.Append(' ');
                gap = false;
                sb.Append(ch);
            }
            else gap = true;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Апострофи до одного вигляду, а латинські двійники (a, c, e, i, o, p, x, y, k) — до кирилиці: «гaсi» з двома
    /// латинськими літерами на екрані не відрізнити від «гасі», тож і перевірка не має.
    /// </summary>
    static char Fold(char ch) => ch switch
    {
        '’' or 'ʼ' or '‘' or '`' or '´' or 'ʻ' or '′' => '\'',
        'a' => 'а', 'c' => 'с', 'e' => 'е', 'i' => 'і', 'o' => 'о', 'p' => 'р', 'x' => 'х', 'y' => 'у', 'k' => 'к',
        _ => ch,
    };

    /// <summary>Слова: <see cref="Norm"/>, поділене пробілами й дефісами, без порожніх і без апострофів по краях.</summary>
    public static List<string> Tokens(string? s)
    {
        var list = new List<string>();
        foreach (var part in Norm(s).Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries))
        {
            var t = part.Trim('\'');
            if (t.Length > 0) list.Add(t);
        }
        return list;
    }

    /// <summary>Відстань Левенштейна (вставка, видалення, заміна — по одиниці; без перестановок).</summary>
    public static int Lev(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    static int CommonPrefix(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && a[i] == b[i]) i++;
        return i;
    }

    static bool HasDigit(string s)
    {
        foreach (var ch in s) if (char.IsDigit(ch)) return true;
        return false;
    }

    /// <summary>
    /// Одне слово — те саме? Точно; одруківка (від чотирьох літер, одна правка); відмінок (спільний корінь із трьох
    /// літер, дві правки); довгий відмінок (спільний початок майже на все коротше слово, а різниця — не довша за
    /// закінчення: «кроликів»/«кролики», але не «hollywood»/«hollywoodhills»). Числа — лише точно: 1855 при правді
    /// 1854 — чесна брехня, а не одруківка.
    /// </summary>
    public static bool TokenMatch(string a, string b)
    {
        if (a == b) return true;
        if (HasDigit(a) || HasDigit(b)) return false;
        var min = Math.Min(a.Length, b.Length);
        if (min < 4) return false;
        var lev = Lev(a, b);
        if (lev <= 1) return true;
        var pre = CommonPrefix(a, b);
        if (pre >= 3 && lev <= 2) return true;
        return min >= 5 && pre >= Math.Max(4, min - 2) && Math.Abs(a.Length - b.Length) <= 3;
    }

    /// <summary>
    /// Кожне слово <paramref name="need"/> має свою пару в <paramref name="have"/> (кожне слово <paramref name="have"/>
    /// — щонайбільше для одного). Жадібно за порядком: для коротких фраз цього досить.
    /// </summary>
    public static bool AllIn(IReadOnlyList<string> need, IReadOnlyList<string> have)
    {
        if (need.Count == 0 || need.Count > have.Count) return false;
        var used = new bool[have.Count];
        foreach (var n in need)
        {
            var found = false;
            for (var j = 0; j < have.Count; j++)
            {
                if (used[j] || !TokenMatch(n, have[j])) continue;
                used[j] = found = true;
                break;
            }
            if (!found) return false;
        }
        return true;
    }

    /// <summary>Скільки літер і цифр у рядку (після <see cref="Norm"/>).</summary>
    public static int Letters(string? s)
    {
        var n = 0;
        foreach (var ch in Norm(s)) if (char.IsLetterOrDigit(ch)) n++;
        return n;
    }

    /// <summary>Чи ця «брехня» насправді правда цього питання (відповідь або будь-яка форма з <c>accept</c>).</summary>
    public static bool LooksTrue(string? lie, BluffQuestion question) => LooksTrue(lie, question.Forms);

    public static bool LooksTrue(string? lie, IEnumerable<string> forms)
    {
        var norm = Norm(lie);
        var tokens = Tokens(lie);
        var letters = Letters(lie);
        foreach (var f in forms)
        {
            if (string.IsNullOrWhiteSpace(f)) continue;
            if (norm.Length > 0 && norm == Norm(f)) return true;
            var ft = Tokens(f);
            if (AllIn(ft, tokens)) return true;                     // правда всередині фрази: «звісно ж, на гасі»
            // Шматок правди: «Буг» для «Південний Буг». Шматок має бути щонайменше половиною змістовних слів форми:
            // інакше «фрукт» ставав би правдою через форму банку «овоч, а не фрукт».
            if (letters >= 3 && AllIn(tokens, ft) && tokens.Count * 2 >= Meaningful(ft).Count) return true;
        }
        return false;
    }

    /// <summary>
    /// Службові слова, що не міняють брехню: «у хвості» і «хвості» — одна картка (у питанні й так стоїть «в ___»).
    /// Лише ті, що не несуть напрямку: «до сонця» і «від сонця», «під столом» і «над столом» — різні брехні.
    /// </summary>
    static readonly HashSet<string> Small = new(StringComparer.Ordinal)
    {
        "в", "у", "на", "з", "із", "зі", "зо", "і", "й", "та", "а", "ж", "же",
    };

    /// <summary>Слова без службових; якщо нічого не лишилось — як були.</summary>
    static List<string> Meaningful(List<string> tokens)
    {
        var list = tokens.FindAll(t => !Small.Contains(t));
        return list.Count > 0 ? list : tokens;
    }

    /// <summary>
    /// Дві брехні — одна картка? Однакові після <see cref="Norm"/> або слово в слово з відмінками й одруківками, у тому
    /// самому порядку, без службових слів («у хвості» = «хвості»). Брехня з самих смайликів нормалізується в порожнечу —
    /// такі порівнюємо як написано.
    /// </summary>
    public static bool LooksSame(string? a, string? b)
    {
        var na = Norm(a);
        var nb = Norm(b);
        if (na.Length == 0 || nb.Length == 0)
            return string.Equals(Clean(a), Clean(b), StringComparison.OrdinalIgnoreCase);
        if (na == nb) return true;
        var ta = Meaningful(Tokens(a));
        var tb = Meaningful(Tokens(b));
        if (ta.Count != tb.Count || ta.Count == 0) return false;
        for (var i = 0; i < ta.Count; i++)
            if (!TokenMatch(ta[i], tb[i])) return false;
        return true;
    }

    /// <summary>
    /// Текст для картки: керівні знаки й переноси — пробілом, пробіли (зокрема нерозривні) злиті, краї обрізані, невидимі
    /// знаки прибрані (крім ZWJ — на ньому тримаються складені смайлики). Регістр і пунктуацію автора не чіпаємо.
    /// </summary>
    public static string Clean(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Normalize(NormalizationForm.FormC);
        var sb = new StringBuilder(s.Length);
        var gap = false;
        foreach (var ch in s)
        {
            var cat = char.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.Format && ch != '‍') continue;
            if (char.IsControl(ch) || char.IsWhiteSpace(ch) || cat is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                gap = true;
                continue;
            }
            if (gap && sb.Length > 0) sb.Append(' ');
            gap = false;
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
