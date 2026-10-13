using Microsoft.Extensions.Options;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Розділ <c>Clicker</c> конфігу (на проді — appsettings.Local.json). Поки що лише подарунки гончарям від майстерні.
/// </summary>
public sealed class ClickerOptions
{
    public List<ClickerGift> Gifts { get; set; } = [];
}

/// <summary>
/// Подарунок гончареві: компенсація за те, що коло само не полагодить (13.10 — серії smaug і Назара, що їх порвали
/// вихід з кола й поганий зв'язок, ClickerLate.cs). Видається раз на <see cref="Id"/>.
/// </summary>
public sealed class ClickerGift
{
    /// <summary>Унікальний ключ: той самий подарунок удруге не видається (і черепки йдуть із ним як ref леджера).</summary>
    public string Id { get; set; } = "";
    /// <summary>Кому — нік акаунта. Регістр не має значення.</summary>
    public string Nick { get; set; } = "";
    /// <summary>Скільки додати до серії глеків з полиці.</summary>
    public int Streak { get; set; }
    /// <summary>Скільки черепків у гаманець — нагородою, без денної стелі.</summary>
    public int Shards { get; set; }
    /// <summary>Що сказати гончареві в тості. Порожньо — «🎁 Подарунок від майстерні».</summary>
    public string Note { get; set; } = "";
}

public sealed partial class Clicker
{
    /// <summary>Причина в леджері: категорія «нагороди» в гаманці.</summary>
    public const string GiftReason = "award:🎁 подарунок від Гончарного кола";

    /// <summary>Id виданих подарунків. Зберігається: подарунок не повториться ні після перезапуску, ні з іншого пристрою.</summary>
    readonly HashSet<string> _takenGifts = new(StringComparer.Ordinal);

    /// <summary>
    /// Видати подарунки, що чекають цього гончаря (з конфігу, <see cref="ClickerOptions.Gifts"/>). Кличеться після
    /// вдалої справжньої дії: гончар тут і побачить тост. Повертає, що сказати, або порожньо.
    /// </summary>
    string TakeGifts()
    {
        var gifts = Ctx.Services.GetService<IOptionsMonitor<ClickerOptions>>()?.CurrentValue.Gifts;
        if (gifts is not { Count: > 0 } || Ctx.NickOf(0) is not { Length: > 0 } nick) return "";
        var said = new List<string>();
        foreach (var gift in gifts)
        {
            if (gift.Id.Length == 0 || !string.Equals(gift.Nick.Trim(), nick, StringComparison.OrdinalIgnoreCase)
                || _takenGifts.Contains(gift.Id))
                continue;
            _takenGifts.Add(gift.Id);
            var what = new List<string>();
            if (gift.Streak > 0)
            {
                _fallStreak += gift.Streak;
                what.Add($"+{gift.Streak} до серії (тепер {_fallStreak})");
            }
            if (gift.Shards > 0 && Ctx.Services.GetService<IStakes>() is { } stakes)
            {
                stakes.Grant(nick, gift.Shards, GiftReason, $"clicker-gift:{gift.Id}");
                what.Add($"+{gift.Shards} черепків");
            }
            var note = gift.Note.Trim().Length > 0 ? gift.Note.Trim() : "🎁 Подарунок від майстерні";
            said.Add(what.Count > 0 ? $"{note}: {string.Join(", ", what)}" : note);
        }
        return string.Join(" · ", said);
    }
}
