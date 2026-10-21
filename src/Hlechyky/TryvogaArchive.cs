using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Hlechyky;

/// <summary>
/// Архів «🚨 Тривоги» — окремий файл (<see cref="TryvogaOptions.ArchivePath"/>, типово <c>data/tryvoga.db</c>): який
/// дрон куди і як летів, і де коли була тривога. Для аналітики потім (приклади запитів — docs/tryvoga.md), тож нічого
/// не стираємо. Окремо від основної бази — щоб не роздувати її і щоб файл можна було просто забрати.
/// <para>
/// Час — UTC рядком <c>yyyy-MM-dd HH:mm:ss.fff</c>: сортується як рядок і його розуміють дати SQLite. Пише лише фоновий
/// опитувач (<see cref="Tryvoga"/>), по одній транзакції на знімок.
/// </para>
/// </summary>
public sealed class TryvogaArchive(IOptionsMonitor<TryvogaOptions> options, ILogger<TryvogaArchive> log)
{
    const string Schema = """
        PRAGMA journal_mode=WAL;
        CREATE TABLE IF NOT EXISTS threats(
            uid TEXT PRIMARY KEY, epoch TEXT NOT NULL, id TEXT NOT NULL, type TEXT NOT NULL, title TEXT,
            first_at TEXT NOT NULL, last_at TEXT NOT NULL, gone_at TEXT,
            first_region TEXT, last_region TEXT, last_locality TEXT,
            max_count INTEGER, max_sources INTEGER, confidence TEXT, text TEXT, json TEXT);
        CREATE INDEX IF NOT EXISTS ix_threats_first ON threats(first_at);
        CREATE INDEX IF NOT EXISTS ix_threats_open ON threats(gone_at) WHERE gone_at IS NULL;
        CREATE TABLE IF NOT EXISTS threat_points(
            uid TEXT NOT NULL, t TEXT NOT NULL, lat REAL NOT NULL, lon REAL NOT NULL,
            heading REAL, quality TEXT, unc_km REAL, region TEXT, locality TEXT, src TEXT NOT NULL,
            PRIMARY KEY(uid, t, lat, lon)) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS ix_points_t ON threat_points(t);
        CREATE TABLE IF NOT EXISTS alerts(
            key TEXT NOT NULL, since TEXT NOT NULL, level TEXT NOT NULL,
            name TEXT, oblast TEXT, whole INTEGER NOT NULL, reasons TEXT,
            seen_at TEXT NOT NULL, until TEXT,
            PRIMARY KEY(key, since, level));
        CREATE INDEX IF NOT EXISTS ix_alerts_open ON alerts(until) WHERE until IS NULL;
        """;

    /// <summary>Причини тривоги в базі — кирилицею, а не escape-кодами: її читатимуть очима.</summary>
    static readonly JsonSerializerOptions Js = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    readonly Lock _gate = new();
    string? _cs;
    /// <summary>
    /// Де ціль лежала востаннє. NEPTUN оновлює час і на кожне нове підтвердження тієї самої точки (КАБ над тим самим
    /// селом — 8 разів за 9 хв): у шлях кладемо лише нове місце, а час підтвердження й так є в <c>threats.last_at</c>.
    /// </summary>
    readonly Dictionary<string, (double Lat, double Lon)> _lastAt = [];

    public static string T(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    SqliteConnection Open()
    {
        var path = Paths.Resolve(options.CurrentValue.ArchivePath);
        if (_cs is null || !_cs.Contains(path, StringComparison.Ordinal))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _cs = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
            using var init = new SqliteConnection(_cs);
            init.Open();
            Exec(init, Schema);
        }
        var c = new SqliteConnection(_cs);
        c.Open();
        Exec(c, "PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;");
        return c;
    }

    static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static object Db(object? v) => v ?? DBNull.Value;

    /// <summary>
    /// Знімок цілей: ціль — рядок у <c>threats</c> (id NEPTUN унікальні в межах <c>epoch</c>, тож uid = epoch/id), кожна
    /// нова точка шляху — у <c>threat_points</c> (поточна — <c>now</c>, точки сліду — <c>trail</c>; повтори відкидає
    /// ключ). Ціль, якої в знімку вже нема, дістає <c>gone_at</c>; повернулась — знову відкрита.
    /// </summary>
    public void SaveThreats(ThreatSet set, DateTimeOffset now)
    {
        lock (_gate)
        {
            try { WriteThreats(set, now); }
            catch (Exception ex) { log.LogWarning(ex, "архів тривоги: цілі не записались"); }
        }
    }

    void WriteThreats(ThreatSet set, DateTimeOffset now)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var nowS = T(now);
        using var up = c.CreateCommand();
        up.Transaction = tx;
        up.CommandText = """
            INSERT INTO threats(uid, epoch, id, type, title, first_at, last_at, gone_at, first_region, last_region, last_locality,
                                max_count, max_sources, confidence, text, json)
            VALUES($uid, $epoch, $id, $type, $title, $first, $last, NULL, $region, $region, $locality, $count, $sources, $conf, $text, $json)
            ON CONFLICT(uid) DO UPDATE SET
                type = excluded.type, title = excluded.title, last_at = MAX(threats.last_at, excluded.last_at), gone_at = NULL,
                first_at = MIN(threats.first_at, excluded.first_at),
                first_region = COALESCE(threats.first_region, excluded.first_region),
                last_region = COALESCE(excluded.last_region, threats.last_region),
                last_locality = COALESCE(excluded.last_locality, threats.last_locality),
                max_count = MAX(COALESCE(threats.max_count, 0), COALESCE(excluded.max_count, 0)),
                max_sources = MAX(COALESCE(threats.max_sources, 0), COALESCE(excluded.max_sources, 0)),
                confidence = excluded.confidence, text = excluded.text, json = excluded.json
            WHERE threats.json IS NOT excluded.json OR threats.gone_at IS NOT NULL
            """;
        foreach (var n in new[] { "$uid", "$epoch", "$id", "$type", "$title", "$first", "$last", "$region", "$locality", "$count", "$sources", "$conf", "$text", "$json" })
            up.Parameters.Add(new SqliteParameter(n, DBNull.Value));

        using var pt = c.CreateCommand();
        pt.Transaction = tx;
        pt.CommandText = """
            INSERT OR IGNORE INTO threat_points(uid, t, lat, lon, heading, quality, unc_km, region, locality, src)
            VALUES($uid, $t, $lat, $lon, $heading, $quality, $unc, $region, $locality, $src)
            """;
        foreach (var n in new[] { "$uid", "$t", "$lat", "$lon", "$heading", "$quality", "$unc", "$region", "$locality", "$src" })
            pt.Parameters.Add(new SqliteParameter(n, DBNull.Value));

        void Point(string uid, DateTimeOffset t, double lat, double lon, double? heading, string? quality, double? unc, string? region, string? locality, string src)
        {
            pt.Parameters["$uid"].Value = uid;
            pt.Parameters["$t"].Value = T(t);
            pt.Parameters["$lat"].Value = Math.Round(lat, 5);
            pt.Parameters["$lon"].Value = Math.Round(lon, 5);
            pt.Parameters["$heading"].Value = Db(heading);
            pt.Parameters["$quality"].Value = Db(quality);
            pt.Parameters["$unc"].Value = Db(unc);
            pt.Parameters["$region"].Value = Db(region);
            pt.Parameters["$locality"].Value = Db(locality);
            pt.Parameters["$src"].Value = src;
            pt.ExecuteNonQuery();
        }

        var uids = new List<string>();
        foreach (var t in set.Items)
        {
            var uid = set.Epoch + "/" + t.Id;
            uids.Add(uid);
            var last = t.UpdatedAt ?? now;
            var first = t.Trail.Count > 0 ? t.Trail.Min(p => p.T) : last;
            if (last < first) first = last;
            up.Parameters["$uid"].Value = uid;
            up.Parameters["$epoch"].Value = set.Epoch;
            up.Parameters["$id"].Value = t.Id;
            up.Parameters["$type"].Value = t.Type;
            up.Parameters["$title"].Value = Db(t.Title);
            up.Parameters["$first"].Value = T(first);
            up.Parameters["$last"].Value = T(last);
            up.Parameters["$region"].Value = Db(t.Region);
            up.Parameters["$locality"].Value = Db(t.Locality);
            up.Parameters["$count"].Value = Db(t.Count);
            up.Parameters["$sources"].Value = Db(t.Sources);
            up.Parameters["$conf"].Value = Db(t.Confidence);
            up.Parameters["$text"].Value = Db(t.Text);
            up.Parameters["$json"].Value = t.Raw;
            up.ExecuteNonQuery();
            foreach (var p in t.Trail) Point(uid, p.T, p.Lat, p.Lon, null, null, null, null, null, "trail");
            // «лише область» — це центр області, а не місце: у шлях не кладемо, щоб не малювати стрибків
            var at = (Math.Round(t.Lat, 5), Math.Round(t.Lon, 5));
            if (!t.AreaOnly && (!_lastAt.TryGetValue(uid, out var was) || was != at))
            {
                Point(uid, last, t.Lat, t.Lon, t.Heading, t.Quality, t.UncertaintyKm, t.Region, t.Locality, "now");
                _lastAt[uid] = at;
            }
        }
        foreach (var uid in _lastAt.Keys.Except(uids).ToList()) _lastAt.Remove(uid);

        using var gone = c.CreateCommand();
        gone.Transaction = tx;
        gone.CommandText = "UPDATE threats SET gone_at = $now WHERE gone_at IS NULL AND uid NOT IN (SELECT value FROM json_each($ids))";
        gone.Parameters.AddWithValue("$now", nowS);
        gone.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(uids));
        gone.ExecuteNonQuery();
        tx.Commit();
    }

    /// <summary>
    /// Знімок тривог: рядок на (ключ, since, рівень) — тож зміна жовтої на червону посеред тривоги лишає обидва рядки.
    /// Зникла з переліку — <c>until</c> = час першого опитування без неї.
    /// </summary>
    public void SaveAlerts(AlertSet set, DateTimeOffset now)
    {
        lock (_gate)
        {
            try { WriteAlerts(set, now); }
            catch (Exception ex) { log.LogWarning(ex, "архів тривоги: тривоги не записались"); }
        }
    }

    void WriteAlerts(AlertSet set, DateTimeOffset now)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var nowS = T(now);
        using var ins = c.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO alerts(key, since, level, name, oblast, whole, reasons, seen_at, until)
            VALUES($key, $since, $level, $name, $oblast, $whole, $reasons, $now, NULL)
            ON CONFLICT(key, since, level) DO UPDATE SET until = NULL, reasons = excluded.reasons WHERE alerts.until IS NOT NULL OR alerts.reasons IS NOT excluded.reasons
            """;
        foreach (var n in new[] { "$key", "$since", "$level", "$name", "$oblast", "$whole", "$reasons" })
            ins.Parameters.Add(new SqliteParameter(n, DBNull.Value));
        ins.Parameters.AddWithValue("$now", nowS);
        var ids = new List<string>();
        foreach (var r in set.Rows)
        {
            var since = T(r.Since);
            ids.Add(r.Key + "|" + since + "|" + r.Level);
            ins.Parameters["$key"].Value = r.Key;
            ins.Parameters["$since"].Value = since;
            ins.Parameters["$level"].Value = r.Level;
            ins.Parameters["$name"].Value = r.Name;
            ins.Parameters["$oblast"].Value = r.Oblast;
            ins.Parameters["$whole"].Value = r.Whole ? 1 : 0;
            ins.Parameters["$reasons"].Value = r.Reasons.Count == 0 ? DBNull.Value : JsonSerializer.Serialize(r.Reasons, Js);
            ins.ExecuteNonQuery();
        }
        using var end = c.CreateCommand();
        end.Transaction = tx;
        end.CommandText = "UPDATE alerts SET until = $now WHERE until IS NULL AND key || '|' || since || '|' || level NOT IN (SELECT value FROM json_each($ids))";
        end.Parameters.AddWithValue("$now", nowS);
        end.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(ids));
        end.ExecuteNonQuery();
        tx.Commit();
    }
}
