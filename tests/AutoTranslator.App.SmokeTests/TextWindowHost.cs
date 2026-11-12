using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace AutoTranslator.SmokeTests;

/// <summary>
/// Helper process ("--textwindow x y w h"): a plain top-most WPF window that shows known text at
/// known positions. It has to be a separate process because the app never reads its own windows.
/// Commands arrive on stdin, layout reports and clicks leave on stdout (see <see cref="TextWindow"/>).
/// </summary>
internal static class TextWindowHost
{
    public const string Title = "SmokeTest Text Window";
    public const string FontName = "Microsoft YaHei";

    private static readonly object OutGate = new object();
    private static Window? _window;
    private static Canvas? _canvas;

    public static int Run(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        double x = double.Parse(args[1], CultureInfo.InvariantCulture);
        double y = double.Parse(args[2], CultureInfo.InvariantCulture);
        double width = double.Parse(args[3], CultureInfo.InvariantCulture);
        double height = double.Parse(args[4], CultureInfo.InvariantCulture);

        var app = new Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _canvas = new Canvas();
        _canvas.Background = Brushes.White;
        _canvas.PreviewMouseLeftButtonDown += (sender, e) =>
        {
            Point point = _canvas.PointToScreen(e.GetPosition(_canvas));
            Out("CLICK " + Round(point.X) + " " + Round(point.Y));
        };

        _window = new Window();
        _window.Title = Title;
        _window.WindowStartupLocation = WindowStartupLocation.Manual;
        _window.Left = x;
        _window.Top = y;
        _window.Width = width;
        _window.Height = height;
        _window.ResizeMode = ResizeMode.NoResize;
        _window.Topmost = true;
        _window.Background = Brushes.White;
        _window.UseLayoutRounding = true;
        _window.Content = _canvas;
        _window.Loaded += (sender, e) =>
        {
            ForceForeground();
            EmitLater(0);
        };

        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        var reader = new Thread(() =>
        {
            try
            {
                string? line;
                while ((line = Console.In.ReadLine()) != null)
                {
                    string command = line;
                    dispatcher.BeginInvoke(new Action(() => Handle(command)));
                }
            }
            catch
            {
                // stdin closed
            }

            // The parent is gone: do not linger on the desktop.
            dispatcher.BeginInvoke(new Action(() => app.Shutdown()));
        });
        reader.IsBackground = true;
        reader.Start();

        _window.Show();
        app.Run();
        return 0;
    }

    private static void Handle(string line)
    {
        try
        {
            string[] parts = line.Split(new[] { ' ' }, 3);
            string command = parts[0];
            int seq = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;

            switch (command)
            {
                case "SET":
                    SetItems(parts.Length > 2 ? parts[2] : "-");
                    EmitLater(seq);
                    break;
                case "ACTIVATE":
                    ForceForeground();
                    EmitLater(seq);
                    break;
                case "QUIT":
                    Application.Current.Shutdown();
                    break;
            }
        }
        catch (Exception ex)
        {
            Out("ERROR " + ex.GetType().Name + ": " + ex.Message.Replace('\n', ' ').Replace('\r', ' '));
        }
    }

    private static void SetItems(string spec)
    {
        _canvas!.Children.Clear();
        if (spec == "-" || spec.Length == 0)
        {
            return;
        }

        foreach (string item in spec.Split(';'))
        {
            string[] fields = item.Split(',');
            var block = new TextBlock();
            block.Text = Encoding.UTF8.GetString(Convert.FromBase64String(fields[3]));
            block.FontSize = double.Parse(fields[2], CultureInfo.InvariantCulture);
            block.FontFamily = new FontFamily(FontName);
            block.Foreground = Brushes.Black;
            Canvas.SetLeft(block, double.Parse(fields[0], CultureInfo.InvariantCulture));
            Canvas.SetTop(block, double.Parse(fields[1], CultureInfo.InvariantCulture));
            _canvas.Children.Add(block);
        }

        _canvas.UpdateLayout();
    }

    /// <summary>Reports the layout once WPF has rendered it.</summary>
    private static void EmitLater(int seq)
    {
        _window!.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => Emit(seq)));
    }

    private static void Emit(int seq)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(_window!).Handle;
            Point topLeft = _canvas!.PointToScreen(new Point(0, 0));
            Point bottomRight = _canvas.PointToScreen(new Point(_canvas.ActualWidth, _canvas.ActualHeight));
            Native.RECT rect;
            Native.GetWindowRect(hwnd, out rect);

            var lines = new List<string>();
            lines.Add("LAYOUT " + hwnd.ToInt64().ToString(CultureInfo.InvariantCulture)
                      + " " + (Native.GetForegroundWindow() == hwnd ? "1" : "0")
                      + " " + Rect(topLeft, bottomRight)
                      + " " + rect.Left + "," + rect.Top + "," + (rect.Right - rect.Left) + "," + (rect.Bottom - rect.Top));

            int index = 0;
            foreach (UIElement child in _canvas.Children)
            {
                var block = child as TextBlock;
                if (block == null)
                {
                    continue;
                }

                Point a = block.PointToScreen(new Point(0, 0));
                Point b = block.PointToScreen(new Point(block.ActualWidth, block.ActualHeight));
                lines.Add("ITEM " + index + " " + Rect(a, b) + " "
                          + Convert.ToBase64String(Encoding.UTF8.GetBytes(block.Text)));
                index++;
            }

            lines.Add("DONE " + seq);
            lock (OutGate)
            {
                foreach (string line in lines)
                {
                    Console.Out.WriteLine(line);
                }

                Console.Out.Flush();
            }
        }
        catch (Exception ex)
        {
            Out("ERROR " + ex.GetType().Name + ": " + ex.Message.Replace('\n', ' ').Replace('\r', ' '));
        }
    }

    /// <summary>
    /// Makes this window the foreground window. A background process normally may not do that, so
    /// the usual workarounds are tried in turn: plain call, attaching to the foreground thread's
    /// input queue, and finally a synthetic ALT key press.
    /// </summary>
    private static void ForceForeground()
    {
        IntPtr hwnd = new WindowInteropHelper(_window!).Handle;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            if (Native.GetForegroundWindow() == hwnd)
            {
                return;
            }

            if (attempt >= 2)
            {
                Native.keybd_event(Native.VK_MENU, 0, 0, UIntPtr.Zero);
                Native.keybd_event(Native.VK_MENU, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
            }

            IntPtr foreground = Native.GetForegroundWindow();
            uint me = Native.GetCurrentThreadId();
            uint other = 0;
            if (foreground != IntPtr.Zero)
            {
                uint pid;
                other = Native.GetWindowThreadProcessId(foreground, out pid);
            }

            bool attached = other != 0 && other != me && Native.AttachThreadInput(me, other, true);
            Native.ShowWindow(hwnd, Native.SW_SHOW);
            Native.BringWindowToTop(hwnd);
            Native.SetForegroundWindow(hwnd);
            if (attached)
            {
                Native.AttachThreadInput(me, other, false);
            }

            _window!.Activate();
            Thread.Sleep(120);
        }
    }

    private static string Rect(Point topLeft, Point bottomRight)
    {
        int x = Round(topLeft.X);
        int y = Round(topLeft.Y);
        return x + "," + y + "," + (Round(bottomRight.X) - x) + "," + (Round(bottomRight.Y) - y);
    }

    private static int Round(double value)
    {
        return (int)Math.Round(value);
    }

    private static void Out(string line)
    {
        lock (OutGate)
        {
            Console.Out.WriteLine(line);
            Console.Out.Flush();
        }
    }
}
