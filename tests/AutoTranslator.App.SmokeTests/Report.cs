using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AutoTranslator.SmokeTests;

/// <summary>Collects [PASS]/[FAIL]/[SKIP] lines; writes them to the console and to smoke.log.</summary>
internal static class Report
{
    private static readonly object Gate = new object();
    private static readonly List<string> Results = new List<string>();
    private static readonly DateTime Start = DateTime.UtcNow;
    private static StreamWriter? _file;

    public static int Passed { get; private set; }

    public static int Failed { get; private set; }

    public static int Skipped { get; private set; }

    public static string OutputDirectory { get; private set; } = ".";

    public static void Open(string directory)
    {
        OutputDirectory = directory;
        Directory.CreateDirectory(directory);
        _file = new StreamWriter(Path.Combine(directory, "smoke.log"), false, new UTF8Encoding(false));
        _file.AutoFlush = true;
    }

    public static void Section(string title)
    {
        Write(string.Empty);
        Write("===== " + title + " =====");
    }

    public static void Info(string message)
    {
        Write("  " + message);
    }

    /// <summary>
    /// While set, results are printed as "[EXPERIMENT pass/fail]" and not counted: used to try
    /// a configuration the app does not use on this Windows version.
    /// </summary>
    public static bool Experiment { get; set; }

    public static void Pass(string name, string details)
    {
        if (Experiment)
        {
            Result("[EXPERIMENT pass]", name, details);
            return;
        }

        Result("[PASS]", name, details);
        Passed++;
    }

    public static void Fail(string name, string details)
    {
        if (Experiment)
        {
            Result("[EXPERIMENT fail]", name, details);
            return;
        }

        Result("[FAIL]", name, details);
        Failed++;
    }

    public static void Skip(string name, string reason)
    {
        Result("[SKIP]", name, reason);
        Skipped++;
    }

    /// <summary>PASS when the condition holds, else FAIL; the details are printed either way.</summary>
    public static bool Check(bool condition, string name, string details)
    {
        if (condition)
        {
            Pass(name, details);
        }
        else
        {
            Fail(name, details);
        }

        return condition;
    }

    public static void Summary()
    {
        Write(string.Empty);
        Write("===== SUMMARY =====");
        lock (Gate)
        {
            foreach (string line in Results)
            {
                Write(line);
            }
        }

        Write(string.Empty);
        lock (Gate)
        {
            foreach (string line in Results)
            {
                if (line.StartsWith("[FAIL]", StringComparison.Ordinal))
                {
                    Write("FAILED: " + line.Substring(7));
                }
            }
        }

        Write("TOTAL: " + Passed + " passed, " + Failed + " failed, " + Skipped + " skipped");
    }

    public static string F(double value)
    {
        return value.ToString("0.#", CultureInfo.InvariantCulture);
    }

    private static void Result(string tag, string name, string details)
    {
        string line = tag + " " + name + ": " + details;
        lock (Gate)
        {
            Results.Add(line);
        }

        Write(line);
    }

    private static void Write(string line)
    {
        lock (Gate)
        {
            string stamped = line.Length == 0
                ? line
                : (DateTime.UtcNow - Start).TotalSeconds.ToString("000.0", CultureInfo.InvariantCulture) + " " + line;
            Console.WriteLine(stamped);
            if (_file != null)
            {
                _file.WriteLine(stamped);
            }
        }
    }
}
