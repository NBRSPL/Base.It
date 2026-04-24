using System.Runtime.Versioning;
using Base.It.Core.Config;
using Xunit;

namespace Base.It.Core.Tests;

[SupportedOSPlatform("windows")]
public class DpapiConnectionStoreTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(),
        $"baseit_dpapi_{Guid.NewGuid():N}.bin");

    public void Dispose() { if (File.Exists(_tmp)) File.Delete(_tmp); }

    private static bool OnWindows => OperatingSystem.IsWindows();

    [Fact]
    public void Save_produces_ciphertext_not_plaintext()
    {
        if (!OnWindows) return;
        var store = new DpapiConnectionStore(_tmp);
        store.Save(new[]
        {
            new EnvironmentConfig("DEV", "Portal", "Server=secret;User=u;Password=p")
        });

        Assert.True(File.Exists(_tmp));
        var raw = File.ReadAllBytes(_tmp);
        var text = System.Text.Encoding.UTF8.GetString(raw);

        Assert.DoesNotContain("Server=secret", text);
        Assert.DoesNotContain("Password", text);
    }

    [Fact]
    public void Roundtrips_through_encryption()
    {
        if (!OnWindows) return;
        var store = new DpapiConnectionStore(_tmp);
        var input = new[]
        {
            new EnvironmentConfig("DEV",  "Portal",     "Server=a"),
            new EnvironmentConfig("PROD", "Production", "Server=b;Password=secret")
        };
        store.Save(input);

        var reloaded = new DpapiConnectionStore(_tmp).Load();
        Assert.Equal(2, reloaded.Count);
        Assert.Equal("Server=a",                 reloaded.First(e => e.Key == "DEV_Portal").ConnectionString);
        Assert.Equal("Server=b;Password=secret", reloaded.First(e => e.Key == "PROD_Production").ConnectionString);
    }

    [Fact]
    public void Missing_file_loads_empty()
    {
        if (!OnWindows) return;
        var store = new DpapiConnectionStore(_tmp);
        Assert.Empty(store.Load());
    }

    [Fact]
    public void Default_path_is_under_LocalAppData_BaseIt()
    {
        if (!OnWindows) return;
        var p = DpapiConnectionStore.DefaultPath();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.StartsWith(local, p);
        Assert.Contains("Base.It", p);
    }
}
