namespace Base.It.App.ViewModels;

/// <summary>
/// User-saved source/target preset. Picking one in the Sync / Batch profile
/// bar restores <see cref="SourceEnv"/> + <see cref="SourceDatabase"/> and
/// re-checks every target chip whose key appears in <see cref="TargetKeys"/>.
/// Keys are upper-cased "ENV|DATABASE" — case-insensitive matching of the
/// connection list is the chip-rebuild responsibility.
/// </summary>
public sealed class EndpointProfile
{
    public string Id   { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";

    public string SourceEnv      { get; set; } = "";
    public string SourceDatabase { get; set; } = "";

    public List<string> TargetKeys { get; set; } = new();
}
