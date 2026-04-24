namespace Base.It.Core.Config;

/// <summary>
/// A named bundle of connection endpoints. Only one group is "active" at a
/// time — the UI filters every connection dropdown by the active group so
/// the user only sees relevant environments for the project they're
/// currently working on. The same <see cref="EnvironmentConfig"/> can
/// appear in multiple groups; membership is by (env, database) key, not
/// by identity, so groups compose rather than copy.
/// </summary>
public sealed record ConnectionGroup(
    Guid                  Id,
    string                Name,
    IReadOnlyList<string> ConnectionKeys)
{
    /// <summary>Stable casing-insensitive key for an (env, database) pair.</summary>
    public static string KeyFor(string? env, string? database) =>
        $"{(env ?? "").ToUpperInvariant()}|{(database ?? "").ToUpperInvariant()}";

    /// <summary>True when this group contains the given connection.</summary>
    public bool Contains(string? env, string? database) =>
        ConnectionKeys.Any(k => string.Equals(k, KeyFor(env, database), StringComparison.Ordinal));

    public static ConnectionGroup Create(string name, IEnumerable<string> keys)
    {
        var cleaned = (keys ?? Array.Empty<string>())
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k!.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return new ConnectionGroup(Guid.NewGuid(), (name ?? "").Trim(), cleaned);
    }
}
