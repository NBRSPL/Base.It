using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Base.It.Core.Config;

/// <summary>
/// File-backed store for <see cref="WatchGroup"/>s. JSON-on-disk so the user
/// can hand-edit or check in a config file if they want. Atomic writes via
/// temp-file-then-rename so an interrupted save can never corrupt the file.
///
/// Schema: the new shape uses <c>sourceDatabase</c> + <c>targets[]</c>. Older
/// files used <c>targetEnv</c> + <c>database</c> (single-target). The
/// loader detects the legacy shape and converts transparently — users keep
/// their groups, writes end up in the new shape.
/// </summary>
public sealed class WatchGroupStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() } // ObjectTypes[] as names, not ints
    };

    private readonly string _path;
    private readonly object _gate = new();
    private List<WatchGroup> _groups = new();

    public WatchGroupStore(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public string Path => _path;

    public IReadOnlyList<WatchGroup> All
    {
        get { lock (_gate) return _groups.ToList(); }
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
        {
            lock (_gate) _groups = new List<WatchGroup>();
            return;
        }

        var json = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            lock (_gate) _groups = new List<WatchGroup>();
            return;
        }

        // Parse to nodes first so we can cope with both the legacy
        // single-target shape and the new multi-target shape without
        // crashing on whichever one is on disk.
        var loaded = new List<WatchGroup>();
        var root   = JsonNode.Parse(json) as JsonArray;
        if (root is not null)
        {
            foreach (var node in root)
            {
                if (node is not JsonObject obj) continue;
                var g = Parse(obj);
                if (g is not null) loaded.Add(g);
            }
        }
        lock (_gate) _groups = loaded;
    }

    /// <summary>
    /// Reads a single group from a <see cref="JsonObject"/>, honouring both
    /// the legacy single-target shape and the new shape. Unknown properties
    /// are ignored.
    /// </summary>
    private static WatchGroup? Parse(JsonObject obj)
    {
        var id       = TryGuid(obj["id"]) ?? Guid.NewGuid();
        var name     = TryString(obj["name"]) ?? "";
        var sourceEnv = TryString(obj["sourceEnv"]) ?? "";
        var interval = TryInt(obj["intervalSeconds"]) ?? 30;
        var enabled  = TryBool(obj["enabled"]) ?? true;

        // Objects[]
        var objects = new List<string>();
        if (obj["objects"] is JsonArray objs)
            foreach (var o in objs)
                if (o is not null)
                {
                    var s = o.AsValue().ToString();
                    if (!string.IsNullOrWhiteSpace(s)) objects.Add(s);
                }

        // Prefer new shape: sourceDatabase + targets[].
        var sourceDb = TryString(obj["sourceDatabase"]);
        var targets  = new List<TargetRoute>();

        if (obj["targets"] is JsonArray tgtArr)
        {
            foreach (var t in tgtArr)
            {
                if (t is not JsonObject to) continue;
                var env = TryString(to["environment"]);
                var db  = TryString(to["database"]);
                if (!string.IsNullOrWhiteSpace(env) && !string.IsNullOrWhiteSpace(db))
                    targets.Add(new TargetRoute(env!, db!));
            }
        }

        // Legacy fallback: targetEnv + database.
        if (string.IsNullOrWhiteSpace(sourceDb) || targets.Count == 0)
        {
            var legacyTargetEnv = TryString(obj["targetEnv"]);
            var legacyDatabase  = TryString(obj["database"]);
            if (!string.IsNullOrWhiteSpace(legacyDatabase))
            {
                if (string.IsNullOrWhiteSpace(sourceDb)) sourceDb = legacyDatabase;
                if (targets.Count == 0 && !string.IsNullOrWhiteSpace(legacyTargetEnv))
                    targets.Add(new TargetRoute(legacyTargetEnv!, legacyDatabase!));
            }
        }

        if (string.IsNullOrWhiteSpace(sourceEnv) || string.IsNullOrWhiteSpace(sourceDb))
            return null;

        // ObjectTypes[] — JSON array of enum names (case-insensitive).
        // Missing in legacy files → null, which the record treats as
        // "all types" at runtime.
        List<Models.SqlObjectType>? types = null;
        if (obj["objectTypes"] is JsonArray typeArr)
        {
            types = new List<Models.SqlObjectType>();
            foreach (var t in typeArr)
            {
                var s = TryString(t);
                if (!string.IsNullOrWhiteSpace(s) &&
                    Enum.TryParse<Models.SqlObjectType>(s, ignoreCase: true, out var val))
                    types.Add(val);
            }
        }

        return new WatchGroup(
            Id: id,
            Name: name,
            SourceEnv: sourceEnv,
            SourceDatabase: sourceDb!,
            Targets: targets,
            Objects: objects,
            IntervalSeconds: Math.Max(5, interval),
            Enabled: enabled,
            ObjectTypes: types);
    }

    // Defensive primitive extractors — JsonNode throws if a property is the
    // wrong kind (e.g. a number where a string is expected), so wrap.
    private static string? TryString(JsonNode? n)
    { try { return n?.GetValue<string>(); } catch { return n?.ToString(); } }
    private static Guid?   TryGuid  (JsonNode? n)
    { try { var s = TryString(n); return Guid.TryParse(s, out var g) ? g : null; } catch { return null; } }
    private static int?    TryInt   (JsonNode? n)
    { try { return n?.GetValue<int>(); } catch { return int.TryParse(TryString(n), out var i) ? i : null; } }
    private static bool?   TryBool  (JsonNode? n)
    { try { return n?.GetValue<bool>(); } catch { return bool.TryParse(TryString(n), out var b) ? b : null; } }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        List<WatchGroup> snapshot;
        lock (_gate) snapshot = _groups.ToList();

        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = _path + ".tmp";
        await using (var fs = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(fs, snapshot, JsonOpts, ct);
        }
        File.Move(tmp, _path, overwrite: true);
    }

    public void Upsert(WatchGroup group)
    {
        if (group is null) throw new ArgumentNullException(nameof(group));
        lock (_gate)
        {
            var idx = _groups.FindIndex(g => g.Id == group.Id);
            if (idx >= 0) _groups[idx] = group;
            else _groups.Add(group);
        }
    }

    public bool Remove(Guid id)
    {
        lock (_gate)
        {
            var idx = _groups.FindIndex(g => g.Id == id);
            if (idx < 0) return false;
            _groups.RemoveAt(idx);
            return true;
        }
    }

    public WatchGroup? Get(Guid id)
    {
        lock (_gate) return _groups.FirstOrDefault(g => g.Id == id);
    }
}
