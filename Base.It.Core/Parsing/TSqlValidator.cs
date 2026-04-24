using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Base.It.Core.Parsing;

/// <summary>
/// Wraps Microsoft's official ScriptDom parser. Used to validate syntax
/// before we commit a definition to git or execute it on a target.
/// Replaces regex-based rewriting in later stages.
/// </summary>
public static class TSqlValidator
{
    public sealed record ValidationResult(bool IsValid, IReadOnlyList<string> Errors);

    public static ValidationResult Validate(string script)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(script ?? string.Empty);
        parser.Parse(reader, out var errors);

        if (errors is { Count: > 0 })
        {
            var messages = errors
                .Select(e => $"Line {e.Line}, Col {e.Column}: {e.Message}")
                .ToArray();
            return new ValidationResult(false, messages);
        }
        return new ValidationResult(true, Array.Empty<string>());
    }
}
