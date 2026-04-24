namespace Base.It.Core.Config;

/// <summary>
/// How a connection's effective connection string is produced.
/// </summary>
public enum AuthMode
{
    /// <summary>User pasted a full connection string. Use it as-is.</summary>
    RawConnectionString,
    /// <summary>Build from Server + Database + User + Password.</summary>
    SqlAuth,
    /// <summary>Build from Server + Database; rely on Windows integrated auth.</summary>
    WindowsIntegrated
}
