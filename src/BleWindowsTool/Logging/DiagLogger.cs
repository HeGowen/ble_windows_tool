using System.Text;

namespace BleWindowsTool.Logging;

public sealed class DiagLogger : IDisposable
{
    private readonly object _lock = new();
    private readonly StreamWriter _writer;

    public string LogFile { get; }

    public DiagLogger(string logFile)
    {
        LogFile = logFile;
        Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
        _writer = new StreamWriter(new FileStream(logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8)
        {
            AutoFlush = true
        };
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    public void Exception(string context, Exception ex)
    {
        Write("ERROR", $"{context}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
    }

    private void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}";
        lock (_lock)
        {
            Console.WriteLine(line);
            _writer.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer.Dispose();
        }
    }
}
