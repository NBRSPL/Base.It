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
    Trigger
}
