namespace Base.It.App.ViewModels;

/// <summary>
/// Flat (env, database) endpoint shown by the searchable AutoCompleteBox in
/// the Sync / Batch panes. <see cref="Label"/> is the primary text — the
/// user's DisplayName when set, otherwise <see cref="SubLabel"/>'s
/// "ENV / Database" form. <see cref="SubLabel"/> is always the env/db pair
/// and is shown as a subtitle in the dropdown so the user can still find
/// connections by environment or database name even when a custom display
/// name has been configured.
/// </summary>
public sealed record EndpointPick(
    string  Environment,
    string  Database,
    string  Label,
    string  SubLabel,
    string? Color)
{
    public string Key => $"{Environment.ToUpperInvariant()}|{Database.ToUpperInvariant()}";

    /// <summary>True only when DisplayName differs from the env/db pair — controls subtitle visibility in the dropdown row.</summary>
    public bool ShowSubLabel => !string.Equals(Label, SubLabel, StringComparison.Ordinal);

    public override string ToString() => Label;
}
