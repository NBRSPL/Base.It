namespace Base.It.Core.Config;

/// <summary>
/// One named (environment, database) pair together with everything needed to
/// connect to it. The primary-constructor <see cref="ConnectionString"/> is the
/// legacy single-string form; richer fields (<see cref="Server"/>, etc.)
/// populate when <see cref="Auth"/> is SqlAuth or WindowsIntegrated.
/// Optional <see cref="DisplayName"/> / <see cref="Color"/> are purely for the UI.
/// </summary>
public sealed record EnvironmentConfig(string Environment, string Database, string ConnectionString)
{
    public string Key => $"{Environment}_{Database}";

    // ---- optional presentation ----
    public string? DisplayName { get; init; }
    public string? Color { get; init; }                 // hex "#RRGGBB"

    // ---- authentication ----
    public AuthMode Auth { get; init; } = AuthMode.RawConnectionString;
    public string? Server { get; init; }
    public string? DatabaseName { get; init; }          // actual SQL database name (if different from logical Database)
    public string? Username { get; init; }
    public string? Password { get; init; }

    /// <summary>
    /// Public-facing label. Never includes the environment name (security/shoulder-surfing).
    /// Prefers DisplayName, falls back to the logical Database only.
    /// </summary>
    public string Label =>
        !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName! : Database;

    /// <summary>
    /// Effective connection string used at runtime. Honours the selected auth
    /// mode rather than always returning the raw field.
    /// </summary>
    public string BuildConnectionString() => Auth switch
    {
        AuthMode.SqlAuth           => BuildSqlAuth(),
        AuthMode.WindowsIntegrated => BuildWindowsAuth(),
        _                          => ConnectionString ?? string.Empty
    };

    private string BuildSqlAuth()
    {
        var db = string.IsNullOrWhiteSpace(DatabaseName) ? Database : DatabaseName;
        return $"Server={Server};Database={db};User Id={Username};Password={Password};TrustServerCertificate=true;";
    }

    private string BuildWindowsAuth()
    {
        var db = string.IsNullOrWhiteSpace(DatabaseName) ? Database : DatabaseName;
        return $"Server={Server};Database={db};Integrated Security=true;TrustServerCertificate=true;";
    }
}
