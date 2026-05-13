using Base.It.Core.Config;
using Xunit;

namespace Base.It.Core.Tests;

public class ConnectionFormatterTests
{
    private static EnvironmentConfig MakeSqlAuth() =>
        new("DEV", "Portal", "")
        {
            Auth         = AuthMode.SqlAuth,
            Server       = "sql01.local",
            DatabaseName = "Portal",
            Username     = "appuser",
            Password     = "s3cret!",
        };

    [Fact]
    public void Json_round_trips_every_field()
    {
        var cfg  = MakeSqlAuth();
        var text = ConnectionFormatter.Serialize(cfg, ConnectionFormat.Json);

        var back = ConnectionFormatter.TryParse(text);

        Assert.NotNull(back);
        Assert.Equal("DEV",       back!.Environment);
        Assert.Equal("Portal",    back.Database);
        Assert.Equal(AuthMode.SqlAuth, back.Auth);
        Assert.Equal("sql01.local",    back.Server);
        Assert.Equal("appuser",        back.Username);
        Assert.Equal("s3cret!",        back.Password);
    }

    [Fact]
    public void KeyValue_round_trips_and_drops_empties()
    {
        var cfg  = MakeSqlAuth();
        var text = ConnectionFormatter.Serialize(cfg, ConnectionFormat.KeyValue);
        Assert.Contains("Environment=DEV",    text);
        Assert.Contains("Database=Portal",    text);
        Assert.Contains("Server=sql01.local", text);

        var back = ConnectionFormatter.TryParse(text);
        Assert.NotNull(back);
        Assert.Equal("DEV",     back!.Environment);
        Assert.Equal("appuser", back.Username);
    }

    [Fact]
    public void ConnectionString_parse_extracts_server_db_username()
    {
        var cs   = "Server=sql01.local;Database=Portal;User Id=appuser;Password=hunter2;TrustServerCertificate=true;";
        var back = ConnectionFormatter.TryParse(cs);

        Assert.NotNull(back);
        Assert.Equal("sql01.local", back!.Server);
        Assert.Equal("Portal",      back.DatabaseName);
        Assert.Equal("appuser",     back.Username);
        Assert.Equal("hunter2",     back.Password);
        Assert.Equal(AuthMode.SqlAuth, back.Auth);
    }

    [Fact]
    public void ConnectionString_with_integrated_security_yields_WindowsIntegrated()
    {
        var cs   = "Server=sql01.local;Database=Portal;Integrated Security=true;";
        var back = ConnectionFormatter.TryParse(cs);

        Assert.NotNull(back);
        Assert.Equal(AuthMode.WindowsIntegrated, back!.Auth);
    }

    [Fact]
    public void TryParse_returns_null_for_unrecognisable_blob()
    {
        Assert.Null(ConnectionFormatter.TryParse("hello world"));
        Assert.Null(ConnectionFormatter.TryParse(""));
        Assert.Null(ConnectionFormatter.TryParse(null));
    }

    [Fact]
    public void Json_format_picks_JSON_even_with_leading_whitespace()
    {
        var json = "  \n  { \"Environment\": \"PROD\", \"Database\": \"Sales\", \"Auth\": \"WindowsIntegrated\" }";
        var back = ConnectionFormatter.TryParse(json);
        Assert.NotNull(back);
        Assert.Equal("PROD", back!.Environment);
        Assert.Equal(AuthMode.WindowsIntegrated, back.Auth);
    }
}
