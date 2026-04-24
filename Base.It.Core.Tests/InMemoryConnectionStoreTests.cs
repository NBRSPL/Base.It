using Base.It.Core.Config;
using Xunit;

namespace Base.It.Core.Tests;

public class InMemoryConnectionStoreTests
{
    [Fact]
    public void Roundtrips_entries()
    {
        var store = new InMemoryConnectionStore();
        store.Save(new[]
        {
            new EnvironmentConfig("DEV",  "Portal",     "Server=a"),
            new EnvironmentConfig("PROD", "Production", "Server=b"),
        });

        Assert.Equal("Server=a", store.Get("DEV",  "Portal"));
        Assert.Equal("Server=b", store.Get("prod", "production"));
        Assert.Null(store.Get("TEST", "Portal"));
    }

    [Fact]
    public void Save_replaces_all_entries()
    {
        var store = new InMemoryConnectionStore(new[]
        {
            new EnvironmentConfig("DEV", "Portal", "a")
        });
        store.Save(new[] { new EnvironmentConfig("TEST", "Portal", "b") });

        Assert.Null(store.Get("DEV", "Portal"));
        Assert.Equal("b", store.Get("TEST", "Portal"));
    }
}
