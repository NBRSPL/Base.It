using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Base.It.Core.Config;

/// <summary>
/// Per-user encrypted connection store. Uses Windows DPAPI with CurrentUser scope.
///
/// Properties:
///  - Payload is UTF-8 JSON, then DPAPI-encrypted with an optional entropy.
///  - Only the same Windows user on the same machine can decrypt it.
///    Copying the file to another user/machine produces an opaque blob.
///  - No master password to remember; the OS binds the key to the user session.
///  - Default location: %LOCALAPPDATA%\Base.It\connections.bin
///
/// On non-Windows platforms this type throws a clear PlatformNotSupportedException
/// on first access so callers can fall back to another IConnectionStore.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiConnectionStore : IConnectionStore
{
    private const string EntropyMagic = "BaseIt.v1.connections";

    private readonly string _path;
    private readonly byte[] _entropy;

    public string Location => _path;

    public DpapiConnectionStore(string? path = null)
    {
        _path    = path ?? DefaultPath();
        _entropy = Encoding.UTF8.GetBytes(EntropyMagic);
        EnsureWindows();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public static string DefaultPath()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(baseDir, "Base.It", "connections.bin");
    }

    public IReadOnlyList<EnvironmentConfig> Load()
    {
        if (!File.Exists(_path)) return Array.Empty<EnvironmentConfig>();
        try
        {
            var encrypted = File.ReadAllBytes(_path);
            if (encrypted.Length == 0) return Array.Empty<EnvironmentConfig>();

            var plain = ProtectedData.Unprotect(encrypted, _entropy, DataProtectionScope.CurrentUser);
            var json  = Encoding.UTF8.GetString(plain);
            var list  = JsonSerializer.Deserialize<List<EnvironmentConfig>>(json);
            return list ?? (IReadOnlyList<EnvironmentConfig>)Array.Empty<EnvironmentConfig>();
        }
        catch (CryptographicException)
        {
            // Blob can't be decrypted by this user (copied from another machine/user).
            // Treat as empty — we'll never silently surface ciphertext as plaintext.
            return Array.Empty<EnvironmentConfig>();
        }
    }

    public void Save(IEnumerable<EnvironmentConfig> entries)
    {
        var list  = entries.ToList();
        var json  = JsonSerializer.Serialize(list);
        var plain = Encoding.UTF8.GetBytes(json);
        var encrypted = ProtectedData.Protect(plain, _entropy, DataProtectionScope.CurrentUser);

        // Atomic write so partial writes never corrupt the store.
        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, encrypted);
        if (File.Exists(_path)) File.Replace(tmp, _path, null, ignoreMetadataErrors: true);
        else                    File.Move(tmp, _path);
    }

    public string? Get(string environment, string database) =>
        GetProfile(environment, database)?.BuildConnectionString();

    public EnvironmentConfig? GetProfile(string environment, string database) =>
        Load().FirstOrDefault(e =>
            string.Equals(e.Environment, environment, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.Database,    database,    StringComparison.OrdinalIgnoreCase));

    private static void EnsureWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException(
                "DpapiConnectionStore is Windows-only. Use an alternative IConnectionStore.");
    }
}
