namespace Hlechyky;

/// <summary>Repo root discovery: tools, cache, data and web are all addressed relative to it.</summary>
public static class Paths
{
    public static string Root { get; } = FindRoot();

    static string FindRoot()
    {
        var env = Environment.GetEnvironmentVariable("HLECHYKY_ROOT");
        if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "liquidsoap", "radio.liq"))) return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    public static string Resolve(string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(Root, path));
}

public sealed class SiteOptions
{
    public string Name { get; set; } = "Глечики";
    /// <summary>The auto-DJ's persona, nominative ("Дядько Глек радить").</summary>
    public string DjName { get; set; } = "Дядько Глек";
    /// <summary>Genitive form ("порада Дядька Глека").</summary>
    public string DjNameGen { get; set; } = "Дядька Глека";
    public string PublicStreamUrl { get; set; } = "";
    public int StreamDelaySeconds { get; set; } = 6;
    public int ListenPort { get; set; } = 8080;
}

public sealed class AuthOptions
{
    public string AdminKey { get; set; } = "";
}

public sealed class YtDlpOptions
{
    public string BinaryPath { get; set; } = "tools/yt-dlp/yt-dlp.exe";
    public string FfmpegDir { get; set; } = "tools/yt-dlp";
    public string CacheDir { get; set; } = "cache";
    /// <summary>Used only when YouTube demands a login (age-gated 18+ videos): a Netscape cookies.txt exported from a logged-in browser…</summary>
    public string CookiesFile { get; set; } = "";
    /// <summary>…or the browser to read cookies from (firefox works; chrome/edge lock their database while running).</summary>
    public string CookiesFromBrowser { get; set; } = "";
    public int MaxDurationSeconds { get; set; } = 900;
    public int TimeoutSeconds { get; set; } = 240;
}

public sealed class VoiceOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Довші записи ріжуться при перегонці; браузер зупиняє запис сам на цій межі.</summary>
    public int MaxSeconds { get; set; } = 120;
    public long MaxUploadBytes { get; set; } = 10 * 1024 * 1024;
    /// <summary>До якої гучності (LUFS) підтягнути голос, щоб він не тонув між треками.</summary>
    public double LoudnessLufs { get; set; } = -14;
}

public sealed class LiquidsoapOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 1234;
    public string ApiKey { get; set; } = "";
    public string CacheMount { get; set; } = "/cache";
}

public sealed class IcecastOptions
{
    public string StatusUrl { get; set; } = "http://127.0.0.1:8000/status-json.xsl";
    public string RadioMount { get; set; } = "/radio.mp3";
    public string SpotifyMount { get; set; } = "/spotify.mp3";
}

public sealed class LastFmOptions
{
    public string ApiKey { get; set; } = "";
    public string SharedSecret { get; set; } = "";
}

public sealed class AutoDjOptions
{
    public bool Enabled { get; set; } = true;
    public int RecentSeeds { get; set; } = 10;
    public int LikeSeeds { get; set; } = 4;
    public int NoRepeatHours { get; set; } = 6;
    public int MaxDurationSeconds { get; set; } = 720;
    public string SeedQuery { get; set; } = "";
}

public sealed class DjBotOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Ключ з console.anthropic.com. Порожній — Глек просто мовчить, сайт працює як і працював.</summary>
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "claude-haiku-4-5";
    /// <summary>Жорстка стеля на календарний місяць; коли впирається — Глек мовчить до першого числа.</summary>
    public double MonthlyBudgetUsd { get; set; } = 5;
    /// <summary>Скільки разів одна людина може смикнути Глека за годину.</summary>
    public int PerNickPerHour { get; set; } = 12;
    /// <summary>Спонтанні репліки: не частіше ніж раз на стільки хвилин.</summary>
    public int SpontaneousCooldownMinutes { get; set; } = 20;
    /// <summary>Шанс кинути слівце, коли заграв новий трек.</summary>
    public double ChanceOnTrackChange { get; set; } = 0.15;
    /// <summary>Шанс встряти в жваву балачку.</summary>
    public double ChanceOnLivelyChat { get; set; } = 0.25;
    /// <summary>Скільки повідомлень за 3 хвилини вважати жвавою балачкою.</summary>
    public int LivelyChatMessages { get; set; } = 4;
    public int MaxToolRounds { get; set; } = 4;
    public int MaxTokens { get; set; } = 400;
    public int MaxReplyChars { get; set; } = 400;
    public int TimeoutSeconds { get; set; } = 45;
    // Ціни Haiku 4.5 станом на вересень 2026, $ за мільйон токенів — лише щоб рахувати стелю
    public double InputUsdPerMTok { get; set; } = 1.0;
    public double OutputUsdPerMTok { get; set; } = 5.0;
    public double CacheReadUsdPerMTok { get; set; } = 0.10;
    public double CacheWriteUsdPerMTok { get; set; } = 1.25;
}

public sealed class DeployOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Спільний секрет із вебхуком на GitHub. Порожній — ендпоінт /api/github/deploy просто не існує.</summary>
    public string WebhookSecret { get; set; } = "";
    public string Branch { get; set; } = "main";
    /// <summary>Назва workflow в Actions, зеленої збірки якого чекаємо (порожньо — будь-якого).</summary>
    public string Workflow { get; set; } = "build";
    public string Script { get; set; } = "deploy.ps1";
}
