namespace Base.It.Core.Config;

/// <summary>Volatile store. Used by tests and by the CLI --dry-run mode.</summary>
public sealed class InMemoryConnectionStore : IConnectionStore
{
    private readonly List<EnvironmentConfig> _entries = new();
    public string Location => "memory://";

    public InMemoryConnectionStore(IEnumerable<EnvironmentConfig>? seed = null)
    {
        if (seed is not null) _entries.AddRange(seed);
    }

    public IReadOnlyList<EnvironmentConfig> Load() => _entries.ToArray();

    public void Save(IEnumerable<EnvironmentConfig> entries)
    {
        _entries.Clear();
        _entries.AddRange(entries);
    }

    public string? Get(string environment, string database) =>
        GetProfile(environment, database)?.BuildConnectionString();

    public EnvironmentConfig? GetProfile(string environment, string database) =>
        _entries.FirstOrDefault(e =>
            string.Equals(e.Environment, environment, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.Database,    database,    StringComparison.OrdinalIgnoreCase));
}
