using Base.It.Core.Config;

namespace Base.It.App.ViewModels;

/// <summary>
/// Item in the title-bar's searchable connection-group picker. Wraps a
/// <see cref="ConnectionGroup"/> alongside a synthetic "All connections"
/// entry where <see cref="Group"/> is null — picking that one drops the
/// active-group filter so every configured connection becomes visible.
/// <see cref="ToString"/> returns the name so AutoCompleteBox renders +
/// filters against the human-readable label out of the box.
/// </summary>
public sealed record ConnectionGroupOption(string Name, ConnectionGroup? Group)
{
    /// <summary>Synthetic "All connections" entry pinned at the top of the picker.</summary>
    public static readonly ConnectionGroupOption All = new("All connections", null);

    public override string ToString() => Name;
}
