namespace Base.It.Core.Config;

/// <summary>
/// A lightweight (environment, database) address used wherever we need to
/// serialise a pointer to a connection without duplicating the auth /
/// display metadata on <see cref="EnvironmentConfig"/>. Watch groups carry
/// a list of these to describe their target endpoints; ViewModels resolve
/// each one against the connection store to build a live conn string.
/// </summary>
public sealed record TargetRoute(string Environment, string Database)
{
    /// <summary>
    /// Short, stable key used for lookups and deduping. Case-insensitive on
    /// both sides, so pickers can compare without worrying about casing drift.
    /// </summary>
    public string Key =>
        $"{Environment?.ToUpperInvariant()}|{Database?.ToUpperInvariant()}";

    /// <summary>Plain one-line display, used as a fallback when no <see cref="EnvironmentConfig.DisplayName"/> is available.</summary>
    public string Display => $"{Environment} · {Database}";
}
