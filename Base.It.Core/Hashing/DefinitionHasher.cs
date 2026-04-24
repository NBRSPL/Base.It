using System.Security.Cryptography;
using System.Text;

namespace Base.It.Core.Hashing;

/// <summary>
/// Canonical hashing of SQL definitions. Two servers that hold the same logical
/// definition — modulo line-ending and trailing-whitespace differences —
/// produce the same hash. This is the foundation of drift detection.
/// </summary>
public static class DefinitionHasher
{
    public static string Hash(string definition)
    {
        if (string.IsNullOrEmpty(definition)) return string.Empty;
        var normalized = Normalize(definition);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string Normalize(string definition)
    {
        var unified = definition.Replace("\r\n", "\n").Replace("\r", "\n");
        var sb = new StringBuilder(unified.Length);
        foreach (var line in unified.Split('\n'))
        {
            sb.Append(line.TrimEnd());
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n') + "\n";
    }
}
