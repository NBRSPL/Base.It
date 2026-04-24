namespace Base.It.Core.Logging;

/// <summary>
/// Append-only file logger. One log per day. Thread-safe.
/// Replaces the old Logger.cs — same behaviour, same folder, no lock contention.
/// </summary>
public sealed class FileLogger
{
    private readonly string _folder;
    private readonly object _gate = new();

    public FileLogger(string folder = "Logs")
    {
        _folder = folder;
        Directory.CreateDirectory(_folder);
    }

    public void Log(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
        var path = Path.Combine(_folder, $"log_{DateTime.Now:yyyyMMdd}.txt");
        lock (_gate) File.AppendAllText(path, line);
    }
}
