namespace Base.It.Core.Models;

/// <summary>
/// A captured SQL object: its identity, type, definition text, and stable content hash.
/// Identical definitions across servers produce identical hashes — this is the drift primitive.
/// </summary>
public sealed record SqlObject(
    ObjectIdentifier Id,
    SqlObjectType Type,
    string Definition,
    string Hash);
