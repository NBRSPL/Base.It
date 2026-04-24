using Base.It.Core.Config;
using Xunit;

namespace Base.It.Core.Tests;

public class ConnectionImporterTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(),
        $"baseit_legacy_{Guid.NewGuid():N}.json");

    public void Dispose() { if (File.Exists(_tmp)) File.Delete(_tmp); }

    [Fact]
    public void Imports_entries_into_target_store()
    {
        File.WriteAllText(_tmp, """
        { "ConnectionStrings": {
            "DEV_Portal":  "Server=dev",
            "TEST_Portal": "Server=test",
            "PROD_Portal": ""
        }}
        """);
        var target = new InMemoryConnectionStore();

        var r = ConnectionImporter.ImportFromLegacy(_tmp, target);

        Assert.Equal(2, r.Imported);              // empty value skipped
        Assert.Equal(1, r.Skipped);
        Assert.Equal("Server=dev",  target.Get("DEV",  "Portal"));
        Assert.Equal("Server=test", target.Get("TEST", "Portal"));
    }

    [Fact]
    public void Does_not_overwrite_existing_entries()
    {
        File.WriteAllText(_tmp, """
        { "ConnectionStrings": { "DEV_Portal": "legacy-value" } }
        """);
        var target = new InMemoryConnectionStore(new[]
        {
            new EnvironmentConfig("DEV", "Portal", "kept-value")
        });

        var r = ConnectionImporter.ImportFromLegacy(_tmp, target);

        Assert.Equal(0, r.Imported);
        Assert.Equal(1, r.Skipped);
        Assert.Equal("kept-value", target.Get("DEV", "Portal"));
    }

    [Fact]
    public void Missing_legacy_file_is_a_noop()
    {
        var target = new InMemoryConnectionStore();
        var r = ConnectionImporter.ImportFromLegacy(_tmp + ".nope", target);
        Assert.Equal(0, r.Imported);
        Assert.Empty(target.Load());
    }
}
