namespace Base.It.Core.Config;

public sealed record ImportResult(int Imported, int Skipped, string? Source);

/// <summary>
/// One-time migration helper. Reads any legacy plaintext appsettings.json and
/// merges its non-empty entries into the secure store, without overwriting
/// existing secure entries. After migration the caller should delete or
/// quarantine the plaintext source.
/// </summary>
public static class ConnectionImporter
{
    public static ImportResult ImportFromLegacy(string legacyPath, IConnectionStore target)
    {
        if (!File.Exists(legacyPath)) return new ImportResult(0, 0, legacyPath);

        var legacy  = new ConnectionConfigStore(legacyPath).Load();
        var current = target.Load().ToDictionary(e => e.Key, StringComparer.OrdinalIgnoreCase);
        int imported = 0, skipped = 0;

        foreach (var entry in legacy)
        {
            if (string.IsNullOrWhiteSpace(entry.ConnectionString)) { skipped++; continue; }
            if (current.ContainsKey(entry.Key))                    { skipped++; continue; }
            current[entry.Key] = entry;
            imported++;
        }

        if (imported > 0) target.Save(current.Values);
        return new ImportResult(imported, skipped, legacyPath);
    }
}
