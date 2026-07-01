namespace Base.It.Core.Models;

public enum SqlObjectType
{
    Unknown,
    Table,
    View,
    StoredProcedure,
    ScalarFunction,
    InlineTableFunction,
    TableValuedFunction,
    Trigger,

    /// <summary>
    /// User-defined table type (<c>CREATE TYPE [x] AS TABLE (...)</c>).
    /// Very common as a TVP for stored-proc parameters. Discovered from
    /// <c>sys.objects</c> with type = 'TT'. Cannot be ALTER'd — changing
    /// one requires dropping every dependent, dropping the type, and
    /// recreating both.
    /// </summary>
    TableType,

    /// <summary>
    /// User-defined data type / alias type
    /// (<c>CREATE TYPE [Money2] FROM DECIMAL(19,4)</c>). Lives in
    /// <c>sys.types</c> with <c>is_user_defined = 1 AND is_table_type = 0</c>
    /// — not in <c>sys.objects</c>, so discovery needs a separate catalog
    /// read. Cannot be ALTER'd either (same reason as <see cref="TableType"/>).
    /// </summary>
    UserDefinedDataType
}
