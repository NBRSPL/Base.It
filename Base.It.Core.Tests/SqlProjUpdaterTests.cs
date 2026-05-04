using Base.It.Core.Dacpac;
using Xunit;

namespace Base.It.Core.Tests;

public class SqlProjUpdaterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"baseit_sqlproj_{Guid.NewGuid():N}");

    public SqlProjUpdaterTests() { Directory.CreateDirectory(_root); }
    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>
    /// Drops a minimal SSDT-shaped .sqlproj at the given path. Mirrors the
    /// shape Visual Studio emits: msbuild namespace, one ItemGroup with
    /// Build entries.
    /// </summary>
    private static string WriteSqlProj(string folder, string name = "Db.sqlproj", string? extraBuild = null)
    {
        Directory.CreateDirectory(folder);
        var existing = extraBuild is null ? "" : $"    <Build Include=\"{extraBuild}\" />\r\n";
        var xml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Project DefaultTargets=""Build"" ToolsVersion=""4.0"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <ItemGroup>
{existing}  </ItemGroup>
</Project>";
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, xml);
        return path;
    }

    [Fact]
    public void Adds_new_Build_entry_when_sql_file_was_just_created()
    {
        var sqlproj = WriteSqlProj(_root);
        var sql = Path.Combine(_root, "dbo", "Procs2", "usp_Foo.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(sql)!);
        File.WriteAllText(sql, "CREATE PROCEDURE dbo.usp_Foo AS SELECT 1;");

        var modified = SqlProjUpdater.EnsureBuildIncludes(_root, sql);

        Assert.True(modified);
        var contents = File.ReadAllText(sqlproj);
        Assert.Contains(@"Include=""dbo\Procs2\usp_Foo.sql""", contents);
    }

    [Fact]
    public void Skips_when_Build_entry_already_present()
    {
        var sqlproj = WriteSqlProj(_root, extraBuild: @"dbo\Procs\usp_Existing.sql");
        var sql = Path.Combine(_root, "dbo", "Procs", "usp_Existing.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(sql)!);
        File.WriteAllText(sql, "CREATE PROCEDURE dbo.usp_Existing AS SELECT 1;");

        var modified = SqlProjUpdater.EnsureBuildIncludes(_root, sql);

        Assert.False(modified);
    }

    [Fact]
    public void Treats_forward_and_back_slashes_as_equivalent()
    {
        var sqlproj = WriteSqlProj(_root, extraBuild: @"dbo/Procs/usp_X.sql"); // forward
        var sql = Path.Combine(_root, "dbo", "Procs", "usp_X.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(sql)!);
        File.WriteAllText(sql, "x");

        var modified = SqlProjUpdater.EnsureBuildIncludes(_root, sql);

        Assert.False(modified);
    }

    [Fact]
    public void Finds_sqlproj_in_first_level_subdirectory()
    {
        var sub = Path.Combine(_root, "src");
        var sqlproj = WriteSqlProj(sub);
        var sql = Path.Combine(sub, "dbo", "Tables2", "Customers.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(sql)!);
        File.WriteAllText(sql, "CREATE TABLE dbo.Customers (Id INT)");

        var modified = SqlProjUpdater.EnsureBuildIncludes(_root, sql);

        Assert.True(modified);
        var contents = File.ReadAllText(sqlproj);
        Assert.Contains(@"Include=""dbo\Tables2\Customers.sql""", contents);
    }

    [Fact]
    public void Returns_false_when_no_sqlproj_present()
    {
        var sql = Path.Combine(_root, "x.sql");
        File.WriteAllText(sql, "x");
        Assert.False(SqlProjUpdater.EnsureBuildIncludes(_root, sql));
    }

    [Fact]
    public void Batch_adds_multiple_entries_in_one_write()
    {
        var sqlproj = WriteSqlProj(_root);
        var a = Path.Combine(_root, "dbo", "Procs2", "A.sql");
        var b = Path.Combine(_root, "dbo", "Procs2", "B.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(a)!);
        File.WriteAllText(a, "x");
        File.WriteAllText(b, "x");

        var modified = SqlProjUpdater.EnsureBuildIncludes(_root, new[] { a, b });

        Assert.True(modified);
        var contents = File.ReadAllText(sqlproj);
        Assert.Contains(@"Include=""dbo\Procs2\A.sql""", contents);
        Assert.Contains(@"Include=""dbo\Procs2\B.sql""", contents);
    }
}
