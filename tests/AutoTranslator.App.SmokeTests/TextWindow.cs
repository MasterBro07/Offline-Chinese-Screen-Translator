using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutoTranslator.Core;

namespace AutoTranslator.SmokeTests;

/// <summary>One text to show: position (window client coordinates) and font size in WPF units.</summary>
internal sealed class TextItem
{
    public TextItem(double x, double y, double size, string text)
    {
        X = x;
        Y = y;
        Size = size;
        Text = text;
    }

    public double X { get; }

    public double Y { get; }

    public double Size { get; }

    public string Text { get; }
}

/// <summary>A text as the helper window really placed it, in physical screen pixels.</summary>
internal sealed class PlacedText
{
    public PlacedText(string text, ScreenRect rect)
    {
        Text = text;
        Rect = rect;
    }

    public string Text { get; }

    public ScreenRect Rect { get; }
}

internal sealed class TextLayout
{
    public IntPtr Hwnd { get; set; }

    /// <summary>True when the helper window saw itself as the foreground window.</summary>
    public bool Foreground { get; set; }

    public ScreenRect Client { get; set; }

    public ScreenRect Window { get; set; }

    public List<PlacedText> Items { get; } = new List<PlacedText>();

    public DateTime ReportedUtc { get; set; }
}

/// <summary>Parent-side handle of the helper process started with "--textwindow".</summary>
internal sealed class TextWindow : IDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);

    private readonly Process _process;
    private readonly object _gate = new object();
    private readonly Dictionary<int, TaskCompletionSource<TextLayout>> _waiters =
        new Dictionary<int, TaskCompletionSource<TextLayout>>();
    private readonly List<ScreenRect> _clicks = new List<ScreenRect>();
    private TextLayout? _building;
    private int _nextSeq = 1;

    private TextWindow(Process process)
    {
        _process = process;
    }

    public TextLayout Layout { get; private set; } = new TextLayout();

    public int ProcessId
    {
        get { return _process.Id; }
    }

    public bool HasExited
    {
        get { return _process.HasExited; }
    }

    public static async Task<TextWindow> StartAsync(int x, int y, int width, int height)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!);
        info.ArgumentList.Add("--textwindow");
        info.ArgumentList.Add(x.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add(y.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add(width.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add(height.ToString(CultureInfo.InvariantCulture));
        info.UseShellExecute = false;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardOutputEncoding = Encoding.UTF8;
        info.StandardErrorEncoding = Encoding.UTF8;

        Process process = Process.Start(info)!;
        Native.AllowSetForegroundWindow(process.Id);

        var window = new TextWindow(process);
        Task<TextLayout> first = window.Register(0);
        process.OutputDataReceived += (sender, e) => window.OnLine(e.Data);
        process.ErrorDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                Report.Info("[text window stderr] " + e.Data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await first.WaitAsync(CommandTimeout).ConfigureAwait(true);
        return window;
    }

    /// <summary>Replaces the shown texts and returns once the helper has rendered them.</summary>
    public Task<TextLayout> SetAsync(params TextItem[] items)
    {
        string spec = items.Length == 0
            ? "-"
            : string.Join(";", items.Select(item =>
                item.X.ToString(CultureInfo.InvariantCulture) + ","
                + item.Y.ToString(CultureInfo.InvariantCulture) + ","
                + item.Size.ToString(CultureInfo.InvariantCulture) + ","
                + Convert.ToBase64String(Encoding.UTF8.GetBytes(item.Text))));
        return SendAsync("SET", spec);
    }

    /// <summary>Asks the helper to make itself the foreground window.</summary>
    public Task<TextLayout> ActivateAsync()
    {
        Native.AllowSetForegroundWindow(_process.Id);
        return SendAsync("ACTIVATE", null);
    }

    public void ClearClicks()
    {
        lock (_gate)
        {
            _clicks.Clear();
        }
    }

    /// <summary>Waits for a mouse click on the helper window; returns its screen position (1x1 rect) or null.</summary>
    public async Task<ScreenRect?> WaitForClickAsync(TimeSpan timeout)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            lock (_gate)
            {
                if (_clicks.Count > 0)
                {
                    return _clicks[0];
                }
            }

            await Task.Delay(50).ConfigureAwait(true);
        }

        return null;
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                try
                {
                    _process.StandardInput.WriteLine("QUIT");
                    _process.StandardInput.Flush();
                }
                catch
                {
                    // pipe already closed
                }

                if (!_process.WaitForExit(3000))
                {
                    _process.Kill(true);
                }
            }
        }
        catch
        {
            // best effort
        }

        _process.Dispose();
    }

    private async Task<TextLayout> SendAsync(string command, string? argument)
    {
        int seq;
        Task<TextLayout> done;
        lock (_gate)
        {
            seq = _nextSeq++;
        }

        done = Register(seq);
        string line = command + " " + seq.ToString(CultureInfo.InvariantCulture) + (argument != null ? " " + argument : string.Empty);
        _process.StandardInput.WriteLine(line);
        _process.StandardInput.Flush();
        return await done.WaitAsync(CommandTimeout).ConfigureAwait(true);
    }

    private Task<TextLayout> Register(int seq)
    {
        var waiter = new TaskCompletionSource<TextLayout>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _waiters[seq] = waiter;
        }

        return waiter.Task;
    }

    private void OnLine(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        try
        {
            string[] parts = line.Split(' ');
            switch (parts[0])
            {
                case "LAYOUT":
                    _building = new TextLayout
                    {
                        Hwnd = new IntPtr(long.Parse(parts[1], CultureInfo.InvariantCulture)),
                        Foreground = parts[2] == "1",
                        Client = ParseRect(parts[3]),
                        Window = ParseRect(parts[4]),
                    };
                    break;

                case "ITEM":
                    if (_building != null)
                    {
                        _building.Items.Add(new PlacedText(
                            Encoding.UTF8.GetString(Convert.FromBase64String(parts[3])), ParseRect(parts[2])));
                    }

                    break;

                case "DONE":
                    TextLayout? layout = _building;
                    _building = null;
                    if (layout == null)
                    {
                        break;
                    }

                    layout.ReportedUtc = DateTime.UtcNow;
                    int seq = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    TaskCompletionSource<TextLayout>? waiter;
                    lock (_gate)
                    {
                        Layout = layout;
                        if (_waiters.TryGetValue(seq, out waiter))
                        {
                            _waiters.Remove(seq);
                        }
                    }

                    if (waiter != null)
                    {
                        waiter.TrySetResult(layout);
                    }

                    break;

                case "CLICK":
                    lock (_gate)
                    {
                        _clicks.Add(new ScreenRect(
                            int.Parse(parts[1], CultureInfo.InvariantCulture),
                            int.Parse(parts[2], CultureInfo.InvariantCulture), 1, 1));
                    }

                    break;

                default:
                    Report.Info("[text window] " + line);
                    break;
            }
        }
        catch (Exception ex)
        {
            Report.Info("[text window] unparsable line \"" + line + "\": " + ex.Message);
        }
    }

    private static ScreenRect ParseRect(string text)
    {
        string[] fields = text.Split(',');
        return new ScreenRect(
            int.Parse(fields[0], CultureInfo.InvariantCulture),
            int.Parse(fields[1], CultureInfo.InvariantCulture),
            int.Parse(fields[2], CultureInfo.InvariantCulture),
            int.Parse(fields[3], CultureInfo.InvariantCulture));
    }
}
