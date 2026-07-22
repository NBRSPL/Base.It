using System.Security.Cryptography;
using System.Text;
using Base.It.Core.Parsing;

namespace Base.It.Core.Hashing;

/// <summary>
/// Canonical hashing of SQL definitions. Two servers holding the same
/// LOGICAL definition produce the same hash even when the stored text
/// differs in formatting — indentation, keyword casing, blank lines,
/// CREATE-vs-ALTER layout. This is the foundation of drift / in-sync
/// detection: a false "different" here is exactly the "the preview shows
/// no change but the object is still flagged for execution" bug.
///
/// The normaliser runs the definition through <see cref="SqlFormatter"/> —
/// the SAME token-based canonicaliser the visual diff (Compare / Sync /
/// Batch preview) uses. Using one formatter for both guarantees the hash
/// and the diff can never disagree. When the SQL can't be parsed,
/// SqlFormatter echoes the raw text, so unparseable definitions still get
/// a stable hash (just formatting-sensitive, which is the safe fallback).
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
        if (string.IsNullOrEmpty(definition)) return string.Empty;

        // Token-canonicalise first: uppercased keywords, deterministic
        // whitespace, comments preserved verbatim, string literals + real
        // identifiers left untouched. This is what collapses cosmetic
        // differences so they don't register as drift.
        var formatted = SqlFormatter.Format(definition);

        // Safety tidy for the fallback (unparseable) path: unify line
        // endings, drop trailing whitespace per line, single trailing
        // newline. On the formatted path this is a near no-op (SqlFormatter
        // already normalises those), so Normalize stays idempotent.
        var unified = formatted.Replace("\r\n", "\n").Replace("\r", "\n");
        var sb = new StringBuilder(unified.Length);
        foreach (var line in unified.Split('\n'))
        {
            sb.Append(line.TrimEnd());
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n') + "\n";
    }
}
