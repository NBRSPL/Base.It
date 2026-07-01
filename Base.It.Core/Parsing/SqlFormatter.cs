using System.IO;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Base.It.Core.Parsing;

/// <summary>
/// Reformats T-SQL into a canonical shape for diffing:
///   - Reserved keywords UPPERCASE
///   - Comments preserved verbatim (both -- line comments and /* block */)
///   - Deterministic whitespace: source layout is discarded and rebuilt
///     so cosmetic differences (indent, casing, extra blank lines) don't
///     pollute the diff — but real content and comments stay.
///
/// Why not ScriptDom's Sql160ScriptGenerator: it parses to an AST first,
/// and comments aren't AST nodes, so it silently discards them. That's
/// the bug that landed with the 1.3.0 formatter. We walk the token
/// stream ourselves so comment tokens are just another kind of thing we
/// emit — no splicing, no positional guessing.
///
/// If the input can't be tokenized we fall back to the raw string so
/// the caller can still diff something meaningful.
/// </summary>
public static class SqlFormatter
{
    public static bool TryFormat(string? sql, out string formatted)
    {
        formatted = sql ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sql)) return false;

        try
        {
            var parser = new TSql160Parser(initialQuotedIdentifiers: true);

            // Tokenize once for the formatter walker …
            IList<TSqlParserToken> tokens;
            using (var reader = new StringReader(sql))
                tokens = parser.GetTokenStream(reader, out _);

            // … and parse once to confirm the SQL is structurally valid.
            // Reformatting a partial fragment would produce a misleading
            // diff (both sides mangled the same wrong way), so we bail
            // out and echo the original for unparseable input.
            using (var reader = new StringReader(sql))
            {
                var fragment = parser.Parse(reader, out var parseErrors);
                if (fragment is null || parseErrors is { Count: > 0 }) return false;
            }

            if (tokens is null || tokens.Count == 0) return false;

            var raw = new TokenFormatter().Format(tokens);

            // Diff engine splits on \n; strip \r variants and enforce a trailing newline.
            formatted = raw.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd() + "\n";
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string Format(string? sql) => TryFormat(sql, out var f) ? f : (sql ?? string.Empty);
}

/// <summary>
/// Stateful token walker. Consumes a ScriptDom token stream and emits
/// canonical text: uppercase keywords, block-level indent inside
/// BEGIN/END, one clause per line for SELECT/FROM/WHERE/etc.
/// </summary>
internal sealed class TokenFormatter
{
    private readonly StringBuilder _sb = new(capacity: 1024);
    private int  _indent;
    private bool _lineHasContent;
    private bool _needsSpace;

    /// <summary>
    /// Keywords that begin a new line when encountered. Deliberately
    /// conservative — includes only unambiguous statement / clause
    /// starters. JOIN variants, BY, ON, WHEN etc. stay inline. SET is
    /// omitted because it's ambiguous (statement SET vs UPDATE ... SET).
    /// </summary>
    private static readonly HashSet<string> ClauseStarters = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "GROUP", "ORDER", "HAVING",
        "UNION", "INTERSECT", "EXCEPT",
        "INSERT", "UPDATE", "DELETE", "MERGE",
        "IF", "ELSE", "WHILE", "RETURN", "DECLARE",
        "EXEC", "EXECUTE"
    };

    public string Format(IList<TSqlParserToken> tokens)
    {
        foreach (var t in tokens)
        {
            if (t.TokenType == TSqlTokenType.EndOfFile) continue;
            if (t.TokenType == TSqlTokenType.WhiteSpace) continue; // we build our own layout
            Emit(t);
        }
        return _sb.ToString();
    }

    private void Emit(TSqlParserToken t)
    {
        // ── Comments ─────────────────────────────────────────────────────
        // A comment is "inline" when the current output line already has
        // content — i.e. some significant token was just emitted here.
        // Otherwise it's a block comment on its own line. This mirrors
        // what a developer would expect: `-- trailing comment` stays
        // trailing, `-- section header` gets its own line.
        if (t.TokenType == TSqlTokenType.SingleLineComment)
        {
            if (_lineHasContent) _sb.Append(' ');
            else                 AppendIndent();
            _sb.Append(t.Text.TrimEnd('\r', '\n'));
            _lineHasContent = true;
            NewLine();
            return;
        }
        if (t.TokenType == TSqlTokenType.MultilineComment)
        {
            if (_lineHasContent) _sb.Append(' ');
            else                 AppendIndent();
            _sb.Append(t.Text.TrimEnd('\r', '\n'));
            _lineHasContent = true;
            NewLine();
            return;
        }

        var text = t.Text;

        // ── Statement terminator ─────────────────────────────────────────
        if (text == ";")
        {
            _sb.Append(';');
            _lineHasContent = true;
            NewLine();
            return;
        }

        // ── Punctuation that binds tightly to its neighbour ──────────────
        if (text == "." )       { _sb.Append('.');  _needsSpace = false; _lineHasContent = true; return; }
        if (text == "," )       { _sb.Append(',');  _needsSpace = true;  _lineHasContent = true; return; }
        if (text == ")" )       { _sb.Append(')');  _needsSpace = true;  _lineHasContent = true; return; }
        if (text == "]" )       { _sb.Append(']');  _needsSpace = true;  _lineHasContent = true; return; }
        if (text == "(" )       { if (_needsSpace && _lineHasContent) _sb.Append(' '); _sb.Append('('); _needsSpace = false; _lineHasContent = true; return; }
        if (text == "[" )       { if (_needsSpace && _lineHasContent) _sb.Append(' '); _sb.Append('['); _needsSpace = false; _lineHasContent = true; return; }

        // ── Keyword casing decision ──────────────────────────────────────
        var isKeyword = IsKeywordLike(t);
        var emit      = isKeyword ? text.ToUpperInvariant() : text;

        // ── Structural keywords with their own layout ────────────────────
        if (isKeyword && emit == "BEGIN")
        {
            if (_lineHasContent) NewLine();
            AppendIndent();
            _sb.Append("BEGIN");
            _lineHasContent = true;
            NewLine();
            _indent++;
            return;
        }
        if (isKeyword && emit == "END")
        {
            if (_lineHasContent) NewLine();
            _indent = Math.Max(0, _indent - 1);
            AppendIndent();
            _sb.Append("END");
            _lineHasContent = true;
            _needsSpace = true;
            return;
        }
        if (isKeyword && emit == "GO")
        {
            // Batch separator: always on its own line, no indent, resets
            // indent for the next batch (`GO` inside BEGIN/END is illegal
            // T-SQL so the reset is safe).
            if (_lineHasContent) NewLine();
            _sb.Append("GO");
            _lineHasContent = true;
            NewLine();
            _indent = 0;
            return;
        }

        // Clause starters: newline before, then indent to current block level.
        if (isKeyword && ClauseStarters.Contains(emit))
        {
            if (_lineHasContent) NewLine();
            AppendIndent();
            _sb.Append(emit);
            _lineHasContent = true;
            _needsSpace = true;
            return;
        }

        // Default: single space between tokens, indent on first-of-line.
        if (_lineHasContent && _needsSpace) _sb.Append(' ');
        else if (!_lineHasContent)          AppendIndent();

        _sb.Append(emit);
        _lineHasContent = true;
        _needsSpace = true;
    }

    private void NewLine()
    {
        if (!_lineHasContent) return; // Never emit blank lines
        _sb.Append('\n');
        _lineHasContent = false;
        _needsSpace = false;
    }

    private void AppendIndent()
    {
        if (_indent > 0) _sb.Append(new string(' ', _indent * 4));
    }

    /// <summary>
    /// A token is a keyword-like symbol iff it isn't one of the well-known
    /// non-keyword TokenTypes AND its text starts with a letter. Excludes
    /// identifiers, literals, variables, labels, comments, whitespace.
    /// Anything else that the parser labelled with a keyword-specific
    /// TokenType shows up here with a letter-leading Text and gets uppercased.
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
}
