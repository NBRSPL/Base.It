using System.Text.RegularExpressions;
using Base.It.Core.Models;

namespace Base.It.Core.Sync;

/// <summary>
/// Rewrites the leading CREATE keyword of a module definition to ALTER.
/// Handles whitespace and casing, ignores leading comments.
/// Tables return the original text unchanged — schema change for tables is
/// handled at a higher level in later stages (migrations / SqlPackage).
/// </summary>
public static class CreateToAlterRewriter
{
    private static readonly RegexOptions Opts =
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled;

    private static readonly Regex ProcRx    = new(@"\bCREATE\s+PROCEDURE\b",      Opts);
    private static readonly Regex FuncRx    = new(@"\bCREATE\s+FUNCTION\b",       Opts);
    private static readonly Regex ViewRx    = new(@"\bCREATE\s+VIEW\b",           Opts);
    private static readonly Regex TriggerRx = new(@"\bCREATE\s+TRIGGER\b",        Opts);

    public static string Rewrite(string definition, SqlObjectType type)
    {
        if (string.IsNullOrWhiteSpace(definition)) return definition;
        return type switch
        {
            SqlObjectType.StoredProcedure      => ProcRx.Replace(definition, "ALTER PROCEDURE", 1),
            SqlObjectType.ScalarFunction or
            SqlObjectType.InlineTableFunction or
            SqlObjectType.TableValuedFunction  => FuncRx.Replace(definition, "ALTER FUNCTION", 1),
            SqlObjectType.View                 => ViewRx.Replace(definition, "ALTER VIEW", 1),
            SqlObjectType.Trigger              => TriggerRx.Replace(definition, "ALTER TRIGGER", 1),
            _                                  => definition
        };
    }
}
