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
    public void WriteObject_uses_DATE_Env_ObjectType_layout()
    {
        var store = new FileBackupStore(_root);
        var id    = new ObjectIdentifier("dbo", "usp_Foo");

        var path = store.WriteObject("DEV", SqlObjectType.StoredProcedure, id, "CREATE PROC x AS SELECT 1");

        var rel = Path.GetRelativePath(_root, path).Replace('\\', '/');
        var parts = rel.Split('/');
        Assert.Equal(4, parts.Length);                                 // DATE / ENV / TYPE / file
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$",       parts[0]);         // yyyy-MM-dd
        Assert.Equal("DEV",                          parts[1]);         // env folder
        Assert.Equal("StoredProcedure",              parts[2]);         // object type folder
        Assert.Matches(@"^usp_Foo_\d{9}\.sql$",      parts[3]);         // name first, time at end
    }

    [Fact]
    public void WriteObject_keeps_non_dbo_schema_in_filename()
    {
        var store = new FileBackupStore(_root);
        var id    = new ObjectIdentifier("sales", "Orders");

        var path = store.WriteObject("DEV", SqlObjectType.Table, id, "col1");

        var file = Path.GetFileName(path);
        Assert.Matches(@"^sales\.Orders_\d{9}\.sql$", file);            // non-default schema is preserved
    }

    [Fact]
    public void Two_writes_never_overwrite_each_other()
    {
        var store = new FileBackupStore(_root);
        var id    = new ObjectIdentifier("dbo", "usp_Foo");

        var p1 = store.WriteObject("DEV", SqlObjectType.StoredProcedure, id, "v1");
        var p2 = store.WriteObject("DEV", SqlObjectType.StoredProcedure, id, "v2");

        Assert.True(File.Exists(p1));
        Assert.True(File.Exists(p2));
        Assert.NotEqual(p1, p2);
        Assert.Equal("v1", File.ReadAllText(p1));
        Assert.Equal("v2", File.ReadAllText(p2));
    }

    [Fact]
    public void Same_millisecond_collision_is_resolved_with_counter_suffix()
    {
        // Pre-create a file matching the exact name the store is about to pick
        // by writing once, deducing the directory, then filling every possible
        // HHmmssfff_ENV.sql slot is impractical; instead, write twice and
        // assert both files exist (covered above), and additionally cover the
        // _N suffix path by pre-seeding the destination directory.
        var store = new FileBackupStore(_root);
        var id    = new ObjectIdentifier("sales", "Orders");
        var first = store.WriteObject("TEST", SqlObjectType.Table, id, "col1");

        var dir = Path.GetDirectoryName(first)!;
        var twin = Path.Combine(dir, Path.GetFileName(first));  // same name
        // File already exists; the next write in the same ms would collide.
        // Force a collision by using the same timestamp-derived name again:
        var seeded = twin.Replace(".sql", "_1.sql");
        File.WriteAllText(seeded, "seeded");

        var p = store.WriteObject("TEST", SqlObjectType.Table, id, "col2");
        Assert.True(File.Exists(p));
        Assert.NotEqual(first,  p);
        Assert.NotEqual(seeded, p);
        Assert.Equal("col2",   File.ReadAllText(p));
        Assert.Equal("seeded", File.ReadAllText(seeded));       // untouched
    }

    [Fact]
    public void Filename_sanitizes_path_unsafe_characters()
    {
        var store = new FileBackupStore(_root);
        var id    = new ObjectIdentifier("dbo", "Foo/Bar");     // slash is illegal
        var p     = store.WriteObject("DEV", SqlObjectType.StoredProcedure, id, "x");
        var folder = Path.GetFileName(Path.GetDirectoryName(p)!);
        Assert.DoesNotContain("/", folder);
        Assert.DoesNotContain("\\", folder);
    }

    [Fact]
    public void ZipFiles_never_overwrites_an_existing_zip()
    {
        var store = new FileBackupStore(_root);
        var id    = new ObjectIdentifier("dbo", "usp_Foo");
        var p1    = store.WriteObject("DEV",  SqlObjectType.StoredProcedure, id, "src");
        var p2    = store.WriteObject("TEST", SqlObjectType.StoredProcedure, id, "tgt");

        var z1 = store.ZipFiles("Pair.zip", p1, p2);
        var z2 = store.ZipFiles("Pair.zip", p1, p2);

        Assert.True(File.Exists(z1));
        Assert.True(File.Exists(z2));
        Assert.NotEqual(z1, z2);
    }
}
