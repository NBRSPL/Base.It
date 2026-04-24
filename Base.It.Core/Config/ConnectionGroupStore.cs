using System.Text.Json;
using System.Text.Json.Nodes;

namespace Base.It.Core.Config;

/// <summary>
/// File-backed store for <see cref="ConnectionGroup"/>s plus a persisted
/// pointer to the currently-active group. JSON-on-disk, atomic writes.
///
/// Schema:
/// <code>
/// {
///   "activeId": "{guid | null}",
///   "groups": [ { id, name, connectionKeys: [ "ENV|DB", ... ] } ]
/// }
/// </code>
///
/// The active pointer is stored in the same file so that a single atomic
/// write keeps the two in lockstep — there's no window where the pointer
/// references a deleted group.
/// </summary>
public sealed class ConnectionGroupStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;
    private readonly object _gate = new();
    private List<ConnectionGroup> _groups = new();
    private Guid? _activeId;

    public ConnectionGroupStore(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public string Path => _path;

    public IReadOnlyList<ConnectionGroup> All
    {
        get { lock (_gate) return _groups.ToList(); }
    }

    /// <summary>Currently-active group id, or <c>null</c> when no group is active (all connections visible).</summary>
    public Guid? ActiveGroupId
    {
        get { lock (_gate) return _activeId; }
    }

    /// <summary>Resolved active group — null when no group is active or the id points to a deleted group.</summary>
    public ConnectionGroup? ActiveGroup
    {
        get
        {
            lock (_gate)
            {
                if (_activeId is null) return null;
                return _groups.FirstOrDefault(g => g.Id == _activeId.Value);
            }
        }
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
        {
            lock (_gate) { _groups = new List<ConnectionGroup>(); _activeId = null; }
            return;
        }

        var json = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            lock (_gate) { _groups = new List<ConnectionGroup>(); _activeId = null; }
            return;
        }

        var loaded = new List<ConnectionGroup>();
        Guid? activeId = null;

        var root = JsonNode.Parse(json);
        if (root is JsonObject obj)
        {
            activeId = TryGuid(obj["activeId"]);
            if (obj["groups"] is JsonArray arr)
            {
                foreach (var node in arr)
                {
                    if (node is not JsonObject go) continue;
                    var id   = TryGuid(go["id"]) ?? Guid.NewGuid();
                    var name = TryString(go["name"]) ?? "";
                    var keys = new List<string>();
                    if (go["connectionKeys"] is JsonArray ka)
                    {
                        foreach (var k in ka)
                        {
                            var s = TryString(k);
                            if (!string.IsNullOrWhiteSpace(s)) keys.Add(s!.ToUpperInvariant());
                        }
                    }
                    loaded.Add(new ConnectionGroup(id, name, keys));
                }
            }
        }

        lock (_gate)
        {
            _groups = loaded;
            // Drop a stale active pointer rather than fail silently later.
            _activeId = activeId is not null && loaded.Any(g => g.Id == activeId.Value) ? activeId : null;
        }
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        object snapshot;
        lock (_gate)
        {
            snapshot = new
            {
                activeId = _activeId,
                groups   = _groups.Select(g => new
                {
                    id             = g.Id,
                    name           = g.Name,
                    connectionKeys = g.ConnectionKeys
                }).ToList()
            };
        }

        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = _path + ".tmp";
        await using (var fs = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(fs, snapshot, JsonOpts, ct);
        }
        File.Move(tmp, _path, overwrite: true);
    }

    public void Upsert(ConnectionGroup group)
    {
        if (group is null) throw new ArgumentNullException(nameof(group));
        lock (_gate)
        {
            var idx = _groups.FindIndex(g => g.Id == group.Id);
            if (idx >= 0) _groups[idx] = group;
            else          _groups.Add(group);
        }
    }

    public bool Remove(Guid id)
    {
        lock (_gate)
        {
            var idx = _groups.FindIndex(g => g.Id == id);
            if (idx < 0) return false;
            _groups.RemoveAt(idx);
            if (_activeId == id) _activeId = null;
            return true;
        }
    }

    public ConnectionGroup? Get(Guid id)
    {
        lock (_gate) return _groups.FirstOrDefault(g => g.Id == id);
    }

    /// <summary>Switch the active group — pass <c>null</c> to clear (show every connection).</summary>
    public void SetActive(Guid? id)
    {
        lock (_gate)
        {
            _activeId = id is not null && _groups.Any(g => g.Id == id.Value) ? id : null;
        }
    }

    private static string? TryString(JsonNode? n)
    { try { return n?.GetValue<string>(); } catch { return n?.ToString(); } }
    private static Guid?   TryGuid  (JsonNode? n)
    { try { var s = TryString(n); return Guid.TryParse(s, out var g) ? g : null; } catch { return null; } }
}
