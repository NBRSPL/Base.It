using Base.It.Core.Config;
using Xunit;

namespace Base.It.Core.Tests;

public class ConnectionConfigStoreTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(),
        $"baseit_{Guid.NewGuid():N}.json");

    public void Dispose() { if (File.Exists(_tmp)) File.Delete(_tmp); }

    [Fact]
    public void Load_returns_empty_when_file_missing()
    {
        Assert.Empty(new ConnectionConfigStore(_tmp).Load());
    }

    [Fact]
    public void Reads_legacy_DB_Sync_appsettings_shape()
    {
        File.WriteAllText(_tmp, """
        {
          "ConnectionStrings": {
            "DEV_Portal": "Server=devsql;Database=Portal",
            "TEST_Portal": "\"Server=testsql;Database=Portal\""
          }
        }
        """);
        var store = new ConnectionConfigStore(_tmp);
        var list = store.Load();

        Assert.Equal(2, list.Count);
        Assert.Contains(list, e => e.Environment == "DEV" && e.Database == "Portal");
        // Trimming of stray quotes matches old SqlSyncService.CleanConnectionString.
        Assert.Equal("Server=testsql;Database=Portal", store.Get("TEST", "Portal"));
    }

    [Fact]
    public void Save_roundtrips()
    {
        var store = new ConnectionConfigStore(_tmp);
        store.Save(new[]
        {
            new EnvironmentConfig("DEV",  "Portal",     "Server=a"),
            new EnvironmentConfig("PROD", "Production", "Server=b")
        });

        var reloaded = new ConnectionConfigStore(_tmp).Load();
        Assert.Equal(2, reloaded.Count);
        Assert.Equal("Server=a", reloaded.First(e => e.Key == "DEV_Portal").ConnectionString);
        Assert.Equal("Server=b", reloaded.First(e => e.Key == "PROD_Production").ConnectionString);
    }
}
