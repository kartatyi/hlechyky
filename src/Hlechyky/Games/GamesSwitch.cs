namespace Hlechyky.Games;

/// <summary>
/// Секція <c>Games</c> конфігу — вимикач ігор (09.10.2026). Підхоплюється наживо. Вимкнене не відкривається: ні нового
/// столу, ні соло, ні «сісти», «почати», «ще раз», ні турніру з цією грою (<see cref="Rooms.Closed"/>), у лобі її нема
/// (каталог), а без ігор узагалі нема й вкладки «🎮 Ігри» (/api/me → games). Партії, що вже йдуть, дограють.
/// </summary>
public sealed class GamesOptions
{
    /// <summary>Розділ «Ігри» загалом. false — жодної гри.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// Окремі вимкнені ігри — id через кому («clicker, poker»). Рядком, а не списком: список прив'язка конфігурації
    /// дописала б до типового, а не замінила б його (те саме в Curfew:Games).
    /// </summary>
    public string Off { get; set; } = "";

    public bool Plays(string? gameId) =>
        Enabled && !string.IsNullOrEmpty(gameId)
        && !Off.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(gameId, StringComparer.OrdinalIgnoreCase);

    /// <summary>Текст відмови; null — гра відкрита.</summary>
    public string? Refusal(string? gameId) =>
        !Enabled ? "Ігри на цьому сайті вимкнено" : Plays(gameId) ? null : "Цю гру на сайті вимкнено";
}
