using System.Text.Json;
using System.Text.Json.Nodes;

namespace Base.It.Core.Config;

/// <summary>
/// Reads/writes a plain JSON file matching the old DB_Sync appsettings.json layout:
///   { "ConnectionStrings": { "DEV_Portal": "Server=...;...", ... } }
/// Retained for one-time migration/import of legacy config into the secure
/// DPAPI store. Not used as the primary store anymore.
/// </summary>
public sealed class ConnectionConfigStore : IConnectionStore
{
    private readonly string _path;
    public string Location => _path;

    public ConnectionConfigStore(string path) => _path = path;

    public IReadOnlyList<EnvironmentConfig> Load()
    {
        if (!File.Exists(_path)) return Array.Empty<EnvironmentConfig>();
        var json = File.ReadAllText(_path);
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<EnvironmentConfig>();

        var root = JsonNode.Parse(json) as JsonObject;
        var cs = root?["ConnectionStrings"] as JsonObject;
        if (cs is null) return Array.Empty<EnvironmentConfig>();

        var list = new List<EnvironmentConfig>();
        foreach (var kv in cs)
        {
            var key = kv.Key;
            var value = kv.Value?.GetValue<string>() ?? string.Empty;
            var (env, db) = SplitKey(key);
            list.Add(new EnvironmentConfig(env, db, Clean(value)));
        }
        return list;
    }

    public void Save(IEnumerable<EnvironmentConfig> entries)
    {
        var root = File.Exists(_path)
            ? (JsonNode.Parse(File.ReadAllText(_path)) as JsonObject ?? new JsonObject())
            : new JsonObject();

        var cs = new JsonObject();
        foreach (var e in entries)
            cs[e.Key] = e.ConnectionString;
        root["ConnectionStrings"] = cs;

        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public string? Get(string environment, string database) =>
        GetProfile(environment, database)?.BuildConnectionString();

    public EnvironmentConfig? GetProfile(string environment, string database) =>
        Load().FirstOrDefault(e =>
            string.Equals(e.Environment, environment, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.Database,    database,    StringComparison.OrdinalIgnoreCase));

    public static string Clean(string? s) =>
        string.IsNullOrEmpty(s) ? string.Empty : s.Trim().Trim('"');

    private static (string env, string db) SplitKey(string key)
    {
        var i = key.IndexOf('_');
        return i < 0 ? (key, "") : (key[..i], key[(i + 1)..]);
    }
}
