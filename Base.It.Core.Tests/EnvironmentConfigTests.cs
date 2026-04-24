using Base.It.Core.Config;
using Xunit;

namespace Base.It.Core.Tests;

public class EnvironmentConfigTests
{
    [Fact]
    public void Raw_mode_returns_connection_string_unchanged()
    {
        var c = new EnvironmentConfig("DEV", "Portal", "Server=dev;Database=P") {
            Auth = AuthMode.RawConnectionString
        };
        Assert.Equal("Server=dev;Database=P", c.BuildConnectionString());
    }

    [Fact]
    public void SqlAuth_builds_full_connection_string()
    {
        var c = new EnvironmentConfig("TEST", "Portal", "") {
            Auth = AuthMode.SqlAuth,
            Server = "testsql", DatabaseName = "Portal",
            Username = "u", Password = "p"
        };
        var s = c.BuildConnectionString();
        Assert.Contains("Server=testsql",    s);
        Assert.Contains("Database=Portal",   s);
        Assert.Contains("User Id=u",         s);
        Assert.Contains("Password=p",        s);
        Assert.Contains("TrustServerCertificate=true", s);
    }

    [Fact]
    public void WindowsAuth_has_no_credentials()
    {
        var c = new EnvironmentConfig("PROD", "Production", "") {
            Auth = AuthMode.WindowsIntegrated,
            Server = "prodsql"
        };
        var s = c.BuildConnectionString();
        Assert.Contains("Server=prodsql", s);
        Assert.Contains("Integrated Security=true", s);
        Assert.DoesNotContain("User Id",  s);
        Assert.DoesNotContain("Password", s);
    }

    [Fact]
    public void Label_never_exposes_environment_name()
    {
        // Security: without a DisplayName, fall back to Database only (not "DEV_...").
        var noName = new EnvironmentConfig("DEV", "Portal", "x");
        Assert.Equal("Portal", noName.Label);
        Assert.DoesNotContain("DEV", noName.Label);

        Assert.Equal("Dev Primary",
            new EnvironmentConfig("DEV", "Portal", "x") { DisplayName = "Dev Primary" }.Label);
    }

    [Fact]
    public void SqlAuth_uses_logical_Database_when_DatabaseName_missing()
    {
        var c = new EnvironmentConfig("DEV", "Portal", "") {
            Auth = AuthMode.SqlAuth, Server = "s", Username = "u", Password = "p"
        };
        Assert.Contains("Database=Portal", c.BuildConnectionString());
    }
}
