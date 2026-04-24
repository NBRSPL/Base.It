namespace Base.It.Core.Config;

/// <summary>
/// Storage abstraction for environment connection profiles.
/// Implementations decide where and how to persist.
/// </summary>
public interface IConnectionStore
{
    /// <summary>Human-readable location, e.g. file path. For UI status only.</summary>
    string Location { get; }

    IReadOnlyList<EnvironmentConfig> Load();
    void Save(IEnumerable<EnvironmentConfig> entries);

    /// <summary>Returns the *effective* connection string (honours auth mode).</summary>
    string? Get(string environment, string database);

    /// <summary>Returns the full profile including display/auth metadata.</summary>
    EnvironmentConfig? GetProfile(string environment, string database);
}
