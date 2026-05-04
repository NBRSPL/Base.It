using Base.It.Core.Backup;
using Base.It.Core.Models;
using Xunit;

namespace Base.It.Core.Tests;

public class FileBackupStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        $"baseit_backup_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* test cleanup is best-effort */ }
    }

    [Fact]
    public void WriteObject_uses_DATE_RunRoleEnv_ObjectType_layout()
    {
        var store = new FileBackupStore(_root);
        var id    = new ObjectIdentifier("dbo", "usp_Foo");

        var path = store.WriteObject(
            runStamp: "143022456",
            role:     BackupRole.Source,
            environment: "DEV",
            type:     SqlObjectType.StoredProcedure,
            id:       id,
            definition: "CREATE PROC x AS SELECT 1");

        var rel = Path.GetRelativePath(_root, path).Replace('\\', '/');
        var parts = rel.Split('/');
        Assert.Equal(4, parts.Length);                                  // DATE / RUN_ROLE_ENV / TYPE / file
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$",   parts[0]);              // yyyy-MM-dd
        Assert.Equal("143022456_source_DEV",     parts[1]);              // run / role / env folder
        Assert.Equal("StoredProcedure",          parts[2]);              // type folder
        Assert.Equal("usp_Foo.sql",              parts[3]);              // bare name, no timestamp suffix
    }

    [Fact]
    public void WriteObject_role_target_lands_in_target_subfolder()
    {
        var store = new FileBackupStore(_root);
        var id    = new ObjectIdentifier("dbo", "usp_Foo");

        var path = store.WriteObject(
            runStamp: "143022456",
            role:     BackupRole.Target,
            environment: "PROD",
            type:     SqlObjectType.StoredProcedure,
            id:       id,
            definition: "CREATE PROC x AS SELECT 1");

        var rel = Path.GetRelativePath(_root, path).Replace('\\', '/');
        Assert.Contains("/143022456_target_PROD/", "/" + rel + "/");
    }

    [Fact]
    public void WriteObject_keeps_non_dbo_schema_in_filename()
    {
        var store = new FileBackupStore(_root);
        var id    = new ObjectIdentifier("sales", "Orders");

        var path = store.WriteObject(
            runStamp: "143022456",
            role:     BackupRole.Source,
            environment: "DEV",
            type:     SqlObjectType.Table,
            id:       id,
            definition: "col1");

        Assert.Equal("sales.Orders.sql", Path.GetFileName(path));
    }

    [Fact]
    public void Same_run_same_object_collision_is_resolved_with_counter_suffix()
    {
        var store = new FileBackupStore(_root);
        var id    = new ObjectIdentifier("dbo", "usp_Foo");

        var p1 = store.WriteObject("143022456", BackupRole.Source, "DEV", SqlObjectType.StoredProcedure, id, "v1");
        var p2 = store.WriteObject("143022456", BackupRole.Source, "DEV", SqlObjectType.StoredProcedure, id, "v2");

        Assert.True(File.Exists(p1));
        Assert.True(File.Exists(p2));
        Assert.NotEqual(p1, p2);
        Assert.Equal("usp_Foo.sql",   Path.GetFileName(p1));
        Assert.Equal("usp_Foo_1.sql", Path.GetFileName(p2));
        Assert.Equal("v1", File.ReadAllText(p1));
        Assert.Equal("v2", File.ReadAllText(p2));
    }

    [Fact]
    public void Different_runs_get_different_folders_no_collision()
    {
        var store = new FileBackupStore(_root);
        var id    = new ObjectIdentifier("dbo", "usp_Foo");

        var p1 = store.WriteObject("143022456", BackupRole.Source, "DEV", SqlObjectType.StoredProcedure, id, "v1");
        var p2 = store.WriteObject("143510999", BackupRole.Source, "DEV", SqlObjectType.StoredProcedure, id, "v2");

        Assert.NotEqual(Path.GetDirectoryName(p1), Path.GetDirectoryName(p2));
        // Both files are clean "usp_Foo.sql" — no counter suffix because each is in its own run folder.
        Assert.Equal("usp_Foo.sql", Path.GetFileName(p1));
        Assert.Equal("usp_Foo.sql", Path.GetFileName(p2));
    }

    [Fact]
    public void Filename_sanitizes_path_unsafe_characters()
    {
        var store = new FileBackupStore(_root);
        var id    = new ObjectIdentifier("dbo", "Foo/Bar");      // slash is illegal
        var p     = store.WriteObject("143022456", BackupRole.Manual, "DEV", SqlObjectType.StoredProcedure, id, "x");
        var folder = Path.GetFileName(Path.GetDirectoryName(p)!);
        Assert.DoesNotContain("/", folder);
        Assert.DoesNotContain("\\", folder);
    }

    [Fact]
    public void ZipFiles_never_overwrites_an_existing_zip()
    {
        var store = new FileBackupStore(_root);
        var id    = new ObjectIdentifier("dbo", "usp_Foo");
        var p1    = store.WriteObject("143022456", BackupRole.Source, "DEV",  SqlObjectType.StoredProcedure, id, "src");
        var p2    = store.WriteObject("143022456", BackupRole.Target, "TEST", SqlObjectType.StoredProcedure, id, "tgt");

        var z1 = store.ZipFiles("Pair.zip", p1, p2);
        var z2 = store.ZipFiles("Pair.zip", p1, p2);

        Assert.True(File.Exists(z1));
        Assert.True(File.Exists(z2));
        Assert.NotEqual(z1, z2);
    }

    [Fact]
    public void Manual_role_uses_manual_slug_in_folder()
    {
        var store = new FileBackupStore(_root);
        var id    = new ObjectIdentifier("dbo", "usp_Foo");

        var path = store.WriteObject(
            runStamp: "143022456",
            role:     BackupRole.Manual,
            environment: "PROD",
            type:     SqlObjectType.StoredProcedure,
            id:       id,
            definition: "CREATE PROC x AS SELECT 1");

        var rel = Path.GetRelativePath(_root, path).Replace('\\', '/');
        Assert.Contains("/143022456_manual_PROD/", "/" + rel + "/");
    }
}
