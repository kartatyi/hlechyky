namespace Hlechyky.Tests.Support;

/// <summary>
/// «Хто скільки» показує лише людей з акаунтом (гість і бот туди не потрапляють, <see cref="Bots"/>). Тести статистики
/// заводять своїх гравців акаунтами цим помічником; нік, що вже має акаунт, пропускається.
/// </summary>
public static class People
{
    public static void Accounts(this Db db, params string[] nicks)
    {
        foreach (var nick in nicks)
            if (db.FindAccount(nick) is null) db.AddAccount(nick, "h", "s");
    }
}
