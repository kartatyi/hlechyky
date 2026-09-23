using System.Text.RegularExpressions;

namespace Hlechyky;

/// <summary>
/// Посилання в тому, що людина вставила. Їх буває кілька одним шматком: через пробіл, з нового рядка,
/// а то й зліплені докупи — друге вставили в поле, де ще стояло перше, поки сайт розбирав його.
/// Тоді кожне закидаємо окремо, по порядку, а не шукаємо на YouTube Music увесь той рядок.
/// </summary>
public static partial class Links
{
    public static List<string> Split(string? text)
    {
        var list = new List<string>();
        foreach (Match m in LinkRx().Matches(text ?? ""))
        {
            // хвіст речення («дивись https://youtu.be/…!») посиланню не належить
            var link = m.Value.TrimEnd('.', ',', ';', '!', '?', ')', ']', '»', '"', '\'');
            if (link.Length > 0) list.Add(link);
        }
        return list;
    }

    [GeneratedRegex(@"(?:https?://|spotify:(?:track|album|playlist):)\S+?(?=https?://|spotify:(?:track|album|playlist):|\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRx();
}
