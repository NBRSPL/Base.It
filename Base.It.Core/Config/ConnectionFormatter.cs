using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;

namespace Base.It.Core.Config;

/// <summary>
/// The different shapes a connection can be copied to / pasted from the
/// clipboard in. JSON is the lossless default (round-trips every field);
/// the others are conveniences for sharing with tooling that expects a
/// raw SQL connection string or a flat key=value listing.
/// </summary>
public enum ConnectionFormat
{
    /// <summary>Full structured copy — every field. Round-trippable.</summary>
    Json,
    /// <summary>Raw ADO.NET connection string (the result of <see cref="EnvironmentConfig.BuildConnectionString"/>).</summary>
    ConnectionString,
    /// <summary>One-key-per-line listing — handy for sharing in chat / docs.</summary>
    KeyValue,
}

/// <summary>
/// Two-way clipboard formatter for <see cref="EnvironmentConfig"/>.
///
/// <see cref="Serialize"/> turns a connection profile into the chosen
/// textual format (JSON / connection-string / key=value).
///
/// <see cref="TryParse"/> sniffs an arbitrary clipboard blob, detects
/// which format it's in, and reconstructs an <see cref="EnvironmentConfig"/>
/// — used by the Settings pane's "smart paste" so a user can paste any of
/// the three formats into one textbox and have every field auto-filled
/// without picking a format first.
///
/// All formats include the password when present. The Settings UI is
/// responsible for warning the user before copying so a sensitive value
/// doesn't end up on the clipboard by accident.
/// </summary>
public static class ConnectionFormatter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented    = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serialize <paramref name="cfg"/> to <paramref name="format"/>.</summary>
    public static string Serialize(EnvironmentConfig cfg, ConnectionFormat format)
    {
        return format switch
        {
            ConnectionFormat.Json             => SerializeJson(cfg),
            ConnectionFormat.ConnectionString => cfg.BuildConnectionString(),
            ConnectionFormat.KeyValue         => SerializeKeyValue(cfg),
            _                                  => SerializeJson(cfg),
        };
    }

    private static string SerializeJson(EnvironmentConfig cfg)
    {
        // Build a plain DTO so the wire format isn't tied to the record's
        // primary constructor ordering. Stable, human-editable, lossless.
        var dto = new
        {
            cfg.Environment,
            cfg.Database,
            DisplayName  = cfg.DisplayName,
            Color        = cfg.Color,
            Auth         = cfg.Auth.ToString(),
            Server       = cfg.Server,
            DatabaseName = cfg.DatabaseName,
            Username     = cfg.Username,
            Password     = cfg.Password,
            ConnectionString = string.IsNullOrWhiteSpace(cfg.ConnectionString) ? null : cfg.ConnectionString,
        };
        return JsonSerializer.Serialize(dto, JsonOpts);
    }

    private static string SerializeKeyValue(EnvironmentConfig cfg)
    {
        var sb = new StringBuilder();
        void Add(string k, string? v)
        {
            if (string.IsNullOrEmpty(v)) return;
            sb.Append(k).Append('=').Append(v).Append('\n');
        }
        Add("Environment", cfg.Environment);
        Add("Database",    cfg.Database);
        Add("DisplayName", cfg.DisplayName);
        Add("Color",       cfg.Color);
        Add("Auth",        cfg.Auth.ToString());
        Add("Server",      cfg.Server);
        Add("SqlDatabase", cfg.DatabaseName);
        Add("Username",    cfg.Username);
        Add("Password",    cfg.Password);
        if (cfg.Auth == AuthMode.RawConnectionString)
            Add("ConnectionString", cfg.ConnectionString);
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Sniff the format of an arbitrary string and reconstruct an
    /// <see cref="EnvironmentConfig"/>. Returns null when the blob doesn't
    /// look like any known format. Heuristic:
    ///   - Trimmed text starts with <c>{</c> → JSON
    ///   - Contains <c>Server=</c> and a semicolon → ADO.NET connection string
    ///   - Has <c>Environment=</c> or <c>Database=</c> on its own line → key=value
    ///
    /// Missing fields default to empty strings; the caller (Settings paste
    /// handler) decides whether to surface a warning. Auth is inferred from
    /// the presence of <c>User Id</c> / <c>Username</c> vs
    /// <c>Integrated Security</c>.
    /// </summary>
    public static EnvironmentConfig? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();

        if (t.StartsWith("{"))
            return TryParseJson(t);

        // Connection string sniff: at least one semicolon AND a key like
        // Server / Data Source / Initial Catalog. Avoids false positives
        // on URL-like content.
        if (t.Contains(';') && (LooksLikeConnString(t)))
            return ParseConnectionString(t);

        // Key=value fallback: must have at least Environment= or Database=
        // on a line so we don't claim arbitrary newline-delimited input.
        if (LooksLikeKeyValue(t))
            return ParseKeyValue(t);

        // Final fallback: maybe a bare connection string without Server=
        // shape. Let the SqlConnectionStringBuilder try anyway.
        if (t.Contains('='))
            return ParseConnectionString(t);

        return null;
    }

    private static bool LooksLikeConnString(string t)
        => t.IndexOf("Server=",         StringComparison.OrdinalIgnoreCase) >= 0
        || t.IndexOf("Data Source=",    StringComparison.OrdinalIgnoreCase) >= 0
        || t.IndexOf("Initial Catalog=", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool LooksLikeKeyValue(string t)
    {
        // Cheap check: at least one of the canonical keys on its own line.
        foreach (var line in t.Split('\n'))
        {
            var l = line.TrimStart();
            if (l.StartsWith("Environment=", StringComparison.OrdinalIgnoreCase)) return true;
            if (l.StartsWith("Database=",    StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static EnvironmentConfig? TryParseJson(string t)
    {
        try
        {
            using var doc = JsonDocument.Parse(t);
            var root = doc.RootElement;
            string Get(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() ?? "" : "";

            var env  = Get("Environment");
            var db   = Get("Database");
            var auth = Get("Auth");
            var conn = Get("ConnectionString");

            var parsedAuth = Enum.TryParse<AuthMode>(auth, ignoreCase: true, out var a)
                ? a : AuthMode.RawConnectionString;

            return new EnvironmentConfig(env, db, conn)
            {
                DisplayName  = NullIfEmpty(Get("DisplayName")),
                Color        = NullIfEmpty(Get("Color")),
                Auth         = parsedAuth,
                Server       = NullIfEmpty(Get("Server")),
                DatabaseName = NullIfEmpty(Get("DatabaseName")),
                Username     = NullIfEmpty(Get("Username")),
                Password     = NullIfEmpty(Get("Password")),
            };
        }
        catch { return null; }
    }

    private static EnvironmentConfig ParseConnectionString(string t)
    {
        // SqlConnectionStringBuilder happily accepts partial / non-canonical
        // strings; any unknown keys land in its base dictionary. We map
        // the well-known keys back onto our config fields and infer the
        // auth mode from what's actually present.
        var b = new SqlConnectionStringBuilder();
        try { b.ConnectionString = t; } catch { /* swallow — keep what parsed */ }

        var server  = b.DataSource ?? "";
        var db      = b.InitialCatalog ?? "";
        var user    = b.UserID ?? "";
        var pwd     = b.Password ?? "";
        var integ   = b.IntegratedSecurity;

        var auth = integ
            ? AuthMode.WindowsIntegrated
            : (string.IsNullOrWhiteSpace(user) ? AuthMode.RawConnectionString : AuthMode.SqlAuth);

        // We don't know the logical Environment from a connection string —
        // the user fills it in. Default the logical "Database" to whatever
        // the connection string named so the UI shows something useful.
        return new EnvironmentConfig(Environment: "", Database: db, ConnectionString: t)
        {
            Server       = NullIfEmpty(server),
            DatabaseName = NullIfEmpty(db),
            Username     = NullIfEmpty(user),
            Password     = NullIfEmpty(pwd),
            Auth         = auth,
        };
    }

    private static EnvironmentConfig ParseKeyValue(string t)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in t.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var k = line.Substring(0, eq).Trim();
            var v = line.Substring(eq + 1).Trim();
            map[k] = v;
        }
        string V(params string[] keys)
        {
            foreach (var k in keys)
                if (map.TryGetValue(k, out var v)) return v;
            return "";
        }

        var auth = V("Auth");
        var parsedAuth = Enum.TryParse<AuthMode>(auth, ignoreCase: true, out var a)
            ? a : AuthMode.RawConnectionString;

        return new EnvironmentConfig(V("Environment", "Env"), V("Database", "Db"), V("ConnectionString", "ConnStr"))
        {
            DisplayName  = NullIfEmpty(V("DisplayName", "Display")),
            Color        = NullIfEmpty(V("Color")),
            Auth         = parsedAuth,
            Server       = NullIfEmpty(V("Server", "Host", "DataSource")),
            DatabaseName = NullIfEmpty(V("SqlDatabase", "InitialCatalog")),
            Username     = NullIfEmpty(V("Username", "User", "UserId")),
            Password     = NullIfEmpty(V("Password", "Pwd")),
        };
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
