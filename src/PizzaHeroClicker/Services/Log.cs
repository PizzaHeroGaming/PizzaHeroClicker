using System.IO;

namespace PizzaHeroClicker.Services;

/// <summary>
/// Thread-safe rolling log: app.log rolls to app.1.log ... app.3.log at about 1 MB.
/// Logging never throws; if the folder is unwritable the app simply runs without a log.
/// </summary>
public static class Log
{
    private const long MaxBytes = 1_000_000;
    private const int KeepFiles = 3;

    private static readonly object Gate = new();
    private static string? _path;

    public static string? Folder { get; private set; }

    public static void Init(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Folder = folder;
            _path = Path.Combine(folder, "app.log");
        }
        catch
        {
            _path = null;
        }
    }

    public static void Info(string message) => Write("INFO ", message, null);
    public static void Warn(string message) => Write("WARN ", message, null);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        if (_path is null) return;
        lock (Gate)
        {
            try
            {
                Roll();
                string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}";
                if (ex is not null) line += Environment.NewLine + ex;
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch
            {
                // A failed log write must never take the app down.
            }
        }
    }

    private static void Roll()
    {
        var info = new FileInfo(_path!);
        if (!info.Exists || info.Length < MaxBytes) return;

        string Numbered(int n) => Path.Combine(Folder!, $"app.{n}.log");
        for (int i = KeepFiles - 1; i >= 1; i--)
        {
            if (File.Exists(Numbered(i))) File.Move(Numbered(i), Numbered(i + 1), overwrite: true);
        }
        File.Move(_path!, Numbered(1), overwrite: true);
    }
}
