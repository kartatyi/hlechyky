using System.Text;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Текст Клавоперегонів: як звести уривок до того, що можна надрукувати (<see cref="Normalize"/>), і які знаки
/// вважаються «тим самим» під пальцями (<see cref="Same"/>). Те саме правило живе в <c>web/games/typerace.js</c>
/// (функція <c>same</c>) і в <c>data/typerace/check.py</c> — міняти разом (spec §7.3).
/// </summary>
public static class TyperaceText
{
    /// <summary>Апостроф, яким банк пише «м’ята»: усі інші варіанти зводяться до нього.</summary>
    public const char Apostrophe = '’';
    public const char Dash = '—';

    /// <summary>
    /// Звести сирий текст (з Вікіджерел, з руки) до друкованого вигляду: один пробіл, один перенос рядка, апостроф
    /// ’, тире — між пробілами, дефіс — усередині слова, лапки «», три крапки замість «…», без знаків наголосу.
    /// </summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        // Наголоси (Глібов 1918 друкує «що́») і невидимі знаки — геть ще до зведення в NFC: «г» з наголосом інакше стала б «ѓ».
        var pre = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch is '́' or '̀' or '­' or '​' or '‌' or '‍' or '⁠' or '﻿') continue;
            pre.Append(ch);
        }
        var s = pre.ToString().Normalize(NormalizationForm.FormC);

        // 1) переноси, пробіли, апострофи, три крапки — посимвольно
        var a = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (ch == '\r') { a.Append('\n'); if (i + 1 < s.Length && s[i + 1] == '\n') i++; continue; }
            if (ch == '\n') { a.Append('\n'); continue; }
            if (ch == '…') { a.Append("..."); continue; }
            if (IsApostrophe(ch)) { a.Append(Apostrophe); continue; }
            if (char.IsWhiteSpace(ch)) { a.Append(' '); continue; }
            a.Append(ch);
        }

        // 2) пробіли: кілька — один, навколо \n — жодного, кілька \n — один
        var b = new StringBuilder(a.Length);
        var pendingSpace = false;
        var pendingBreak = false;
        foreach (var ch in a.ToString())
        {
            if (ch == ' ') { pendingSpace = true; continue; }
            if (ch == '\n') { pendingBreak = true; continue; }
            if (b.Length > 0)
            {
                if (pendingBreak) b.Append('\n');
                else if (pendingSpace) b.Append(' ');
            }
            pendingSpace = pendingBreak = false;
            b.Append(ch);
        }
        var t = b.ToString();

        // 3) тире й дефіси, лапки — за сусідами
        var c = new StringBuilder(t.Length + 16);
        for (var i = 0; i < t.Length; i++)
        {
            var ch = t[i];
            var prev = c.Length > 0 ? c[^1] : '\n';
            var next = i + 1 < t.Length ? t[i + 1] : '\n';
            if (IsHyphen(ch) || IsLongDash(ch))
            {
                // дефіс усередині слова («де-не-де», «забули-б») лишається дефісом; решта — тире
                if (IsHyphen(ch) && char.IsLetterOrDigit(prev) && char.IsLetterOrDigit(next)) { c.Append('-'); continue; }
                // тире: пробіл ліворуч (якщо це не початок рядка й не дужка/лапка) і праворуч (якщо далі не кінець рядка)
                if (prev is not ' ' and not '\n' and not '(' and not '«') c.Append(' ');
                c.Append(Dash);
                if (next is not ' ' and not '\n' and not ',' and not '.' and not ')' and not '»' and not '!' and not '?' and not ';' and not ':')
                    c.Append(' ');
                continue;
            }
            if (IsQuote(ch))
            {
                var opening = prev is ' ' or '\n' or '(' or '—' or '«' || c.Length == 0;
                // лапка, що стоїть впритул до слова праворуч і має перед собою розділовий знак чи тире, — теж відкриває
                if (!opening && char.IsLetter(next) && prev is ':' or ',') opening = true;
                c.Append(opening ? '«' : '»');
                continue;
            }
            c.Append(ch);
        }

        // 4) ще раз прибрати подвійні пробіли, які могли виникнути довкола тире
        var d = new StringBuilder(c.Length);
        foreach (var ch in c.ToString())
        {
            if (ch == ' ' && d.Length > 0 && (d[^1] == ' ' || d[^1] == '\n')) continue;
            if (ch == '\n' && d.Length > 0 && d[^1] == ' ') d.Length--;
            d.Append(ch);
        }
        return d.ToString().Trim(' ', '\n');
    }

    static bool IsApostrophe(char ch) => ch is '\'' or '’' or 'ʼ' or '‘' or '`' or '´' or '′';
    static bool IsHyphen(char ch) => ch is '-' or '‐' or '‑';
    static bool IsLongDash(char ch) => ch is '‒' or '–' or '—' or '―' or '−';
    static bool IsQuote(char ch) => ch is '"' or '„' or '“' or '”' or '«' or '»' or '‟';

    /// <summary>
    /// Клас рівності знака при друці: 1 — апострофи, 2 — тире й дефіси, 3 — лапки, 4 — пробіл і кінець рядка;
    /// 0 — знак дорівнює лише собі.
    /// </summary>
    public static int Class(char ch) => ch switch
    {
        '’' or '\'' or 'ʼ' or '‘' or '`' or '´' => 1,
        '—' or '–' or '‒' or '-' or '―' or '‐' or '‑' => 2,
        '«' or '»' or '"' or '„' or '“' or '”' => 3,
        '\n' or ' ' => 4,
        _ => 0,
    };

    /// <summary>
    /// Чи годиться набраний знак <paramref name="typed"/> там, де в тексті <paramref name="expected"/>. Регістр важить;
    /// апостроф ’ можна набрати ', тире — дефісом, «» — ", кінець рядка — пробілом.
    /// </summary>
    public static bool Same(char expected, char typed)
    {
        if (expected == typed) return true;
        var k = Class(expected);
        return k != 0 && k == Class(typed);
    }

    /// <summary>Чи в дозволеній абетці знак (§7.3): українські літери, цифри, пробіл, \n і розділові.</summary>
    public static bool Typeable(char ch) =>
        ch is >= 'а' and <= 'щ' or 'ь' or 'ю' or 'я' or 'є' or 'і' or 'ї' or 'ґ'
        or >= 'А' and <= 'Щ' or 'Ь' or 'Ю' or 'Я' or 'Є' or 'І' or 'Ї' or 'Ґ'
        or >= '0' and <= '9'
        or ' ' or '\n' or '.' or ',' or ';' or ':' or '!' or '?' or '-' or '—' or '’' or '(' or ')' or '«' or '»' or '"';

    /// <summary>
    /// Текст можна давати в гру: не порожній, лише дозволена абетка (без «ё ъ ы э», латиниці й сурогатних пар),
    /// без подвійних пробілів і пробілів на краях.
    /// </summary>
    public static bool IsTypeable(string? text)
    {
        if (string.IsNullOrEmpty(text) || text[0] is ' ' or '\n' || text[^1] is ' ' or '\n') return false;
        for (var i = 0; i < text.Length; i++)
        {
            if (!Typeable(text[i])) return false;
            if (i > 0 && text[i] is ' ' or '\n' && text[i - 1] is ' ' or '\n') return false;
        }
        return true;
    }

    /// <summary>
    /// Слово-пастка: слово, на якому спіткнулось найбільше гонщиків (<paramref name="misses"/> — по масиву на
    /// журнал: де висів червоний). Роздільник після слова (пробіл, кінець рядка) належить слову перед ним: хто
    /// натиснув літеру замість пробілу, не дописав саме його. Щонайменше двоє спіткнулись — інакше пастки нема. Рівно —
    /// довше слово, далі раніше в тексті.
    /// </summary>
    public static TyperaceTrap? Trap(string text, IReadOnlyList<bool[]> misses)
    {
        if (misses.Count < 2 || string.IsNullOrEmpty(text)) return null;
        var wordOf = new int[text.Length];
        var starts = new List<int>();
        var ends = new List<int>();
        var w = -1;
        var inWord = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is ' ' or '\n') { inWord = false; wordOf[i] = Math.Max(0, w); continue; }
            if (!inWord) { w++; starts.Add(i); ends.Add(i); inWord = true; }
            ends[w] = i + 1;
            wordOf[i] = w;
        }
        if (w < 0) return null;
        var count = new int[w + 1];
        var hit = new bool[w + 1];
        foreach (var miss in misses)
        {
            Array.Clear(hit);
            for (var i = 0; i < miss.Length && i < text.Length; i++) if (miss[i]) hit[wordOf[i]] = true;
            for (var j = 0; j <= w; j++) if (hit[j]) count[j]++;
        }
        string? best = null;
        var bestN = 0;
        for (var j = 0; j <= w; j++)
        {
            if (count[j] < 2 || count[j] < bestN) continue;
            var word = Bare(text[starts[j]..ends[j]]);
            if (word.Length == 0) continue;
            if (count[j] > bestN || word.Length > best!.Length) { best = word; bestN = count[j]; }
        }
        return best is null ? null : new TyperaceTrap(best, bestN, misses.Count);
    }

    /// <summary>Слово без розділових на краях: «вишнями,» → «вишнями», «(ну» → «ну»; апостроф і дефіс усередині лишаються.</summary>
    static string Bare(string word)
    {
        int a = 0, b = word.Length;
        while (a < b && !char.IsLetterOrDigit(word[a])) a++;
        while (b > a && !char.IsLetterOrDigit(word[b - 1])) b--;
        return word[a..b];
    }
}

/// <summary>Слово-пастка партії: на ньому спіткнулись <see cref="N"/> гонщиків із <see cref="Of"/>, чиї журнали бачив суддя.</summary>
public sealed record TyperaceTrap(string Word, int N, int Of);
