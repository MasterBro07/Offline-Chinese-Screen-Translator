using System;
using System.IO;
using AutoTranslator.Core;

namespace AutoTranslator.Desktop;

/// <summary>Minimal append-only log file in %AppData%/AutoTranslator/log.txt. Never throws.</summary>
internal static class Log
{
    private static readonly object Gate = new object();
    private const long MaxBytes = 1024 * 1024;

    public static string FilePath
    {
        get { return Path.Combine(AppSettings.DefaultDirectory, "log.txt"); }
    }

    public static void Info(string message)
    {
        Write("INFO", message);
    }

    public static void Error(string message, Exception? ex = null)
    {
        Write("ERROR", ex == null ? message : message + Environment.NewLine + ex);
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                string path = FilePath;
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    info.Delete();
                }

                File.AppendAllText(
                    path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + level + "] " + message + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}
