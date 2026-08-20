using System.IO;
using System.Security.Cryptography;
using System.Text;
using Base.It.Core.Parsing;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Base.It.Core.Hashing;

/// <summary>
/// Canonical hashing of SQL definitions. Two servers holding the same
/// LOGICAL definition produce the same hash even when the stored text
/// differs in formatting — indentation, keyword casing, blank lines,
/// spaces vs tabs, CREATE-vs-ALTER layout, even comment alignment. This is
/// the foundation of drift / in-sync detection: a false "different" here is
/// exactly the "the preview shows no change but the object is still flagged
/// for execution" bug.
///
/// <para><b>Whitespace-insensitive by design.</b> <see cref="Hash"/> walks
/// the ScriptDom TOKEN stream and drops every whitespace token, folds
/// keyword casing, and collapses whitespace inside comments — so two
/// definitions that differ ONLY in spaces / tabs / newlines / indentation
/// hash equal. String literals and quoted identifiers are kept verbatim, so
/// a genuine content difference (<c>'a b'</c> vs <c>'a  b'</c>, or a column
/// literally named <c>[My Col]</c>) is still detected. Only tokenization is
/// required, not a full parse — so objects SqlFormatter can't fully parse
/// (and would otherwise fall back to formatting-sensitive raw text) still
/// get a whitespace-insensitive hash.</para>
///
/// <para><see cref="Normalize"/> is the separate, formatting-PRESERVING
/// canonical form used by the visual diff (it keeps comments and line
/// structure). The hash is deliberately more aggressive than the diff's
/// default view — it matches the diff with "Ignore spaces &amp; tabs"
/// turned on, which is the whole point of "hide in-sync".</para>
/// </summary>
public static class DefinitionHasher
{
    public static string Hash(string definition)
    {
        if (string.IsNullOrEmpty(definition)) return string.Empty;
        var canonical = Canonicalize(definition);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // Separator emitted between tokens so two adjacent tokens can never
    // fuse into a third meaning (e.g. `SELECT a` must not collide with a
    // single identifier `SELECTa`). U+0001 never appears in real T-SQL.
    private const char TokenSeparator = '\u0001';

    /// <summary>
    /// Whitespace-insensitive canonical form. Falls back to a
    /// collapse-all-whitespace pass when the text can't be tokenised, so
    /// even unparseable input still hashes deterministically and ignores
    /// layout noise.
    /// </summary>
    private static string Canonicalize(string definition)
        => TryTokenCanonical(definition, out var canon)
            ? canon
            : CollapseWhitespace(definition);

    private static bool TryTokenCanonical(string sql, out string result)
    {
        result = string.Empty;
        try
        {
            var parser = new TSql160Parser(initialQuotedIdentifiers: true);

            // Tokenise only — no full parse. GetTokenStream returns tokens
            // even for input the full parser would reject, so partially
            // valid / unusual constructs still canonicalise (and so ignore
            // whitespace) instead of dropping to the raw fallback. Lexer
            // errors are ignored: same input -> same tokens -> stable hash.
            IList<TSqlParserToken> tokens;
            using (var reader = new StringReader(sql))
                tokens = parser.GetTokenStream(reader, out _);

            if (tokens is null || tokens.Count == 0) return false;

            var sb = new StringBuilder(sql.Length);
            foreach (var t in tokens)
            {
                switch (t.TokenType)
                {
                    case TSqlTokenType.WhiteSpace:
                    case TSqlTokenType.EndOfFile:
                        continue; // layout — irrelevant to logical identity

                    case TSqlTokenType.SingleLineComment:
                    case TSqlTokenType.MultilineComment:
                        // Comment TEXT is content and stays significant, but
                        // the whitespace inside it (alignment, trailing
                        // spaces, wrapped lines) is formatting — collapse it
                        // so alignment-only comment edits don't read as drift.
                        sb.Append(CollapseWhitespace(t.Text));
                        sb.Append(TokenSeparator);
                        break;

                    default:
                        // Keywords are case-insensitive in T-SQL -> fold to
                        // upper. Identifiers / literals kept verbatim so real
                        // content (and data inside quotes) stays significant.
                        sb.Append(IsKeywordLike(t) ? t.Text.ToUpperInvariant() : t.Text);
                        sb.Append(TokenSeparator);
                        break;
                }
            }

            result = sb.ToString();
            return result.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Collapse every run of whitespace (spaces, tabs, CR, LF, …) to a
    /// single space and trim the ends. Used for comment interiors and as
    /// the unparseable-input fallback.
    /// </summary>
    private static string CollapseWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool inWhitespace = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch)) { inWhitespace = true; continue; }
            if (inWhitespace && sb.Length > 0) sb.Append(' ');
            inWhitespace = false;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Keyword detection mirroring <see cref="SqlFormatter"/>: a token is
    /// keyword-like when it isn't one of the well-known non-keyword token
    /// types (identifier, literal, comment, whitespace, variable, label)
    /// and its text starts with a letter.
    /// </summary>
    private static bool IsKeywordLike(TSqlParserToken t) => t.TokenType switch
    {
        TSqlTokenType.WhiteSpace           or
        TSqlTokenType.SingleLineComment    or
        TSqlTokenType.MultilineComment     or
        TSqlTokenType.Identifier           or
        TSqlTokenType.QuotedIdentifier     or
        TSqlTokenType.AsciiStringLiteral   or
        TSqlTokenType.UnicodeStringLiteral or
        TSqlTokenType.HexLiteral           or
        TSqlTokenType.Integer              or
        TSqlTokenType.Numeric              or
        TSqlTokenType.Real                 or
        TSqlTokenType.Money                or
        TSqlTokenType.Variable             or
        TSqlTokenType.Label                or
        TSqlTokenType.EndOfFile            => false,
        _ => t.Text is { Length: > 0 } && char.IsLetter(t.Text[0])
    };

    /// <summary>
    /// Formatting-PRESERVING canonical form used by the visual diff (keeps
    /// comments and line structure via <see cref="SqlFormatter"/>). Distinct
    /// from <see cref="Hash"/>, which is whitespace-insensitive. Retained as
    /// the diff's canonicaliser and for callers that want the pretty form.
    /// </summary>
    public static string Normalize(string definition)
    {
        if (string.IsNullOrEmpty(definition)) return string.Empty;

        // Token-canonicalise first: uppercased keywords, deterministic
        // whitespace, comments preserved verbatim, string literals + real
        // identifiers left untouched.
        var formatted = SqlFormatter.Format(definition);

        // Safety tidy for the fallback (unparseable) path: unify line
        // endings, drop trailing whitespace per line, single trailing
        // newline. On the formatted path this is a near no-op.
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
