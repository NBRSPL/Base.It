using Base.It.Core.Dacpac;
using Base.It.Core.Models;
using Xunit;

namespace Base.It.Core.Tests;

public class DacpacExporterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"baseit_dacpac_{Guid.NewGuid():N}");

    public DacpacExporterTests() { Directory.CreateDirectory(_root); }
    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { } }

    private DacpacExporter MakeExporter(bool enabled = true) =>
        new(new DacpacExportOptions(Enabled: enabled, RootFolder: _root, StageInGit: false, BranchPrefix: "drift/"));

    [Fact]
    public void New_stored_procedure_goes_to_Procs2_folder()
    {
        var exp = MakeExporter();
        var path = exp.Export(
            new ObjectIdentifier("dbo", "usp_Foo"),
            SqlObjectType.StoredProcedure,
            "CREATE PROCEDURE dbo.usp_Foo AS SELECT 1;");

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        var rel = Path.GetRelativePath(_root, path!).Replace('\\', '/');
        Assert.Equal("dbo/Procs2/usp_Foo.sql", rel);
    }

    [Theory]
    [InlineData(SqlObjectType.Table,               "Tables")]
    [InlineData(SqlObjectType.View,                "Views")]
    [InlineData(SqlObjectType.StoredProcedure,     "Stored Procedures")]
    [InlineData(SqlObjectType.ScalarFunction,      "Functions")]
    [InlineData(SqlObjectType.InlineTableFunction, "Functions")]
    [InlineData(SqlObjectType.TableValuedFunction, "Functions")]
    [InlineData(SqlObjectType.Trigger,             "Triggers")]
    public void Type_folder_follows_SSDT_convention(SqlObjectType type, string expected)
        => Assert.Equal(expected, DacpacExporter.TypeFolder(type));

    [Theory]
    [InlineData(SqlObjectType.Table,               "Tables2")]
    [InlineData(SqlObjectType.View,                "Views2")]
    [InlineData(SqlObjectType.StoredProcedure,     "Procs2")]
    [InlineData(SqlObjectType.ScalarFunction,      "Functions2")]
    [InlineData(SqlObjectType.InlineTableFunction, "Functions2")]
    [InlineData(SqlObjectType.TableValuedFunction, "Functions2")]
    [InlineData(SqlObjectType.Trigger,             "Triggers2")]
    public void New_type_folder_uses_suffixed_names(SqlObjectType type, string expected)
        => Assert.Equal(expected, DacpacExporter.NewTypeFolder(type));

    [Fact]
    public void Existing_file_is_updated_in_place_regardless_of_folder()
    {
        // The team's SSDT project uses a "Procs" folder (not "Stored Procedures").
        var existingFolder = Path.Combine(_root, "dbo", "Procs");
        Directory.CreateDirectory(existingFolder);
        var existingPath = Path.Combine(existingFolder, "usp_Foo.sql");
        File.WriteAllText(existingPath, "-- old");

        var exp  = MakeExporter();
        var path = exp.Export(
            new ObjectIdentifier("dbo", "usp_Foo"),
            SqlObjectType.StoredProcedure,
            "CREATE PROCEDURE dbo.usp_Foo AS SELECT 2;");

        // Should overwrite the existing file, not create a Procs2 entry.
        Assert.Equal(existingPath, path);
        Assert.Contains("SELECT 2", File.ReadAllText(existingPath));
        Assert.False(Directory.Exists(Path.Combine(_root, "dbo", "Procs2")));
    }

    [Fact]
    public void Existing_file_search_prefers_schema_scoped_match()
    {
        // Same filename exists under two schema folders — export for 'hr'
        // must update the hr copy, not the sales copy.
        var salesPath = Path.Combine(_root, "sales", "Tables", "Orders.sql");
        var hrPath    = Path.Combine(_root, "hr",    "Tables", "Orders.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(salesPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(hrPath)!);
        File.WriteAllText(salesPath, "-- sales");
        File.WriteAllText(hrPath,    "-- hr old");

        var exp  = MakeExporter();
        var path = exp.Export(
            new ObjectIdentifier("hr", "Orders"),
            SqlObjectType.Table,
            "CREATE TABLE hr.Orders(Id INT);");

        Assert.Equal(hrPath, path);
        Assert.Contains("CREATE TABLE hr.Orders", File.ReadAllText(hrPath));
        Assert.Equal("-- sales", File.ReadAllText(salesPath));
    }

    [Fact]
    public void Existing_file_at_flat_layout_is_updated_when_no_schema_folder()
    {
        // Flat SSDT layout: {Root}/Procs/Name.sql, no schema subfolder.
        var flatFolder = Path.Combine(_root, "Procs");
        Directory.CreateDirectory(flatFolder);
        var existingPath = Path.Combine(flatFolder, "usp_Bar.sql");
        File.WriteAllText(existingPath, "-- old");

        var exp  = MakeExporter();
        var path = exp.Export(
            new ObjectIdentifier("dbo", "usp_Bar"),
            SqlObjectType.StoredProcedure,
            "CREATE PROCEDURE dbo.usp_Bar AS SELECT 1;");

        Assert.Equal(existingPath, path);
    }

    [Fact]
    public void Disabled_exporter_returns_null_and_writes_nothing()
    {
        var exp  = MakeExporter(enabled: false);
        var path = exp.Export(new ObjectIdentifier("dbo", "X"), SqlObjectType.View, "CREATE VIEW X AS SELECT 1");
        Assert.Null(path);
        Assert.Empty(Directory.GetFiles(_root, "*.sql", SearchOption.AllDirectories));
    }

    [Fact]
    public void Unusable_root_folder_returns_null()
    {
        var bogusRoot = Path.Combine(_root, "does-not-exist");
        var exp = new DacpacExporter(new DacpacExportOptions(true, bogusRoot, false, "drift/"));
        Assert.Null(exp.Export(new ObjectIdentifier("dbo", "X"), SqlObjectType.View, "CREATE VIEW X AS SELECT 1"));
    }

    [Fact]
    public void Definition_is_normalised_to_crlf_line_endings()
    {
        var exp = MakeExporter();
        var path = exp.Export(
            new ObjectIdentifier("dbo", "V"), SqlObjectType.View,
            "line1\nline2\r\nline3"); // mixed endings
        var bytes = File.ReadAllBytes(path!);
        var text  = System.Text.Encoding.UTF8.GetString(bytes);
        // After normalization every newline is \r\n.
        Assert.Contains("line1\r\nline2\r\nline3", text);
        Assert.DoesNotContain("line1\nline2", text.Replace("\r\n", "<crlf>"));
    }

    [Fact]
    public void New_objects_in_different_schemas_use_per_schema_folders()
    {
        var exp = MakeExporter();
        exp.Export(new ObjectIdentifier("sales", "Orders"), SqlObjectType.Table, "CREATE TABLE sales.Orders(Id INT)");
        exp.Export(new ObjectIdentifier("hr",    "People"), SqlObjectType.Table, "CREATE TABLE hr.People(Id INT)");
        Assert.True(Directory.Exists(Path.Combine(_root, "sales", "Tables2")));
        Assert.True(Directory.Exists(Path.Combine(_root, "hr",    "Tables2")));
    }

    [Fact]
    public void Relative_path_helper_matches_exported_path()
    {
        var exp = MakeExporter();
        var id  = new ObjectIdentifier("dbo", "usp_Foo");
        var rel = exp.RelativePathFor(id, SqlObjectType.StoredProcedure);
        var path = exp.Export(id, SqlObjectType.StoredProcedure, "CREATE PROC usp_Foo AS SELECT 1");
        var actualRel = Path.GetRelativePath(_root, path!);
        Assert.Equal(rel, actualRel);
    }

    [Fact]
    public void Relative_path_helper_reports_existing_path_when_file_already_exists()
    {
        var existingFolder = Path.Combine(_root, "dbo", "Procs");
        Directory.CreateDirectory(existingFolder);
        File.WriteAllText(Path.Combine(existingFolder, "usp_Foo.sql"), "-- old");

        var exp = MakeExporter();
        var rel = exp.RelativePathFor(new ObjectIdentifier("dbo", "usp_Foo"), SqlObjectType.StoredProcedure)
            .Replace('\\', '/');
        Assert.Equal("dbo/Procs/usp_Foo.sql", rel);
    }
}
