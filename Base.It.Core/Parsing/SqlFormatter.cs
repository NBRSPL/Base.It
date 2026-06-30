using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Base.It.Core.Parsing;

/// <summary>
/// Pretty-prints T-SQL into a single canonical form using ScriptDom's parser +
/// script generator. The point is comparison: when both the source and the
/// target are formatted the same way first, the line/char diff highlights only
/// the *real* changes instead of being swamped by cosmetic differences in
/// whitespace, indentation, casing of keywords, or line breaks.
///
/// Formatting is best-effort and never throws: if the text can't be parsed
/// (partial fragment, dialect ScriptDom rejects, etc.) the original string is
/// returned unchanged so the caller can still diff the raw text.
/// </summary>
public static class SqlFormatter
{
    // SQL 2022 (160) parser/generator — matches TSqlValidator's parser version.
    private static SqlScriptGeneratorOptions BuildOptions() => new()
    {
        KeywordCasing            = KeywordCasing.Uppercase,
        IncludeSemicolons        = true,
        IndentationSize          = 4,
        AlignClauseBodies        = false,
        AsKeywordOnOwnLine       = false,
        NewLineBeforeFromClause    = true,
        NewLineBeforeWhereClause   = true,
        NewLineBeforeGroupByClause = true,
        NewLineBeforeHavingClause  = true,
        NewLineBeforeJoinClause    = true,
        NewLineBeforeOrderByClause = true,
        SqlEngineType            = SqlEngineType.All,
    };

    /// <summary>
    /// Try to reformat <paramref name="sql"/> into canonical form.
    /// Returns false (and echoes the input) when the text can't be parsed.
    /// </summary>
    public static bool TryFormat(string? sql, out string formatted)
    {
        formatted = sql ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sql)) return false;

        try
        {
            var parser = new TSql160Parser(initialQuotedIdentifiers: true);
            using var reader = new StringReader(sql);
            var fragment = parser.Parse(reader, out IList<ParseError> errors);

            // Don't reformat something that doesn't fully parse — a partial
            // regeneration would produce a misleading diff.
            if (fragment is null || errors is { Count: > 0 }) return false;

            var generator = new Sql160ScriptGenerator(BuildOptions());
            generator.GenerateScript(fragment, out var outSql);
            if (string.IsNullOrEmpty(outSql)) return false;

            // Canonicalise line endings to LF; the diff engine splits on \n.
            formatted = outSql.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd() + "\n";
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reformat <paramref name="sql"/>, or return it unchanged if it can't be
    /// parsed. Convenience wrapper over <see cref="TryFormat"/>.
    /// </summary>
    public static string Format(string? sql) => TryFormat(sql, out var f) ? f : (sql ?? string.Empty);
}
