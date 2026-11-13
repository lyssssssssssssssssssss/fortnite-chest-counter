using System.Text;

namespace ChestCounter.Core;

/// <summary>Tiny append-only log file (rotated at 2 MB). Never throws.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    public static void Initialize(string path) => _path = path;

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        var path = _path;
        if (path is null)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                {
                    var old = path + ".1";
                    File.Delete(old);
                    File.Move(path, old);
                }
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
            // logging must never break the app
        }
    }
}
