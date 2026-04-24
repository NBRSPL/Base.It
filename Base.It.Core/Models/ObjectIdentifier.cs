namespace Base.It.Core.Models;

/// <summary>
/// Schema-qualified identity of a database object. Default schema is 'dbo' when omitted.
/// Accepts forms: "Foo", "dbo.Foo", "[dbo].[Foo]".
/// </summary>
public readonly record struct ObjectIdentifier(string Schema, string Name)
{
    public static ObjectIdentifier Parse(string qualifiedName)
    {
        if (string.IsNullOrWhiteSpace(qualifiedName))
            throw new ArgumentException("Object name required", nameof(qualifiedName));

        var parts = qualifiedName.Split('.', 2);
        return parts.Length == 1
            ? new ObjectIdentifier("dbo", Unbracket(parts[0]))
            : new ObjectIdentifier(Unbracket(parts[0]), Unbracket(parts[1]));
    }

    private static string Unbracket(string s) => s.Trim().Trim('[', ']');

    public override string ToString() => $"[{Schema}].[{Name}]";
}
