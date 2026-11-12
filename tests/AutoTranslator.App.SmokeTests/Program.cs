using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace AutoTranslator.SmokeTests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length >= 5 && args[0] == "--textwindow")
        {
            return TextWindowHost.Run(args);
        }

        Console.OutputEncoding = new UTF8Encoding(false);

        string outDir = Path.Combine(Environment.CurrentDirectory, "smoke-out");
        string? exePath = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--out")
            {
                outDir = Path.GetFullPath(args[i + 1]);
            }
            else if (args[i] == "--exe")
            {
                exePath = Path.GetFullPath(args[i + 1]);
            }
        }

        Report.Open(outDir);

        // Nothing here may hang a CI job for long.
        var watchdog = new Timer(
            state =>
            {
                Report.Fail("suite", "watchdog: the smoke tests did not finish within 14 minutes");
                Report.Summary();
                Environment.Exit(3);
            },
            null, TimeSpan.FromMinutes(14), Timeout.InfiniteTimeSpan);

        // One STA thread with a WPF dispatcher, like the app itself: the overlay window lives here.
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        dispatcher.UnhandledException += (sender, e) =>
        {
            Report.Fail("suite", "unhandled exception on the UI thread: " + e.Exception);
            e.Handled = true;
        };

        var suite = new Suite(outDir, exePath);
        dispatcher.BeginInvoke(new Func<Task>(async () =>
        {
            try
            {
                await suite.RunAsync();
            }
            catch (Exception ex)
            {
                Report.Fail("suite", "aborted by an unexpected exception: " + ex);
            }
            finally
            {
                suite.Dispose();
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
            }
        }));
        Dispatcher.Run();

        watchdog.Dispose();
        Report.Summary();
        return Report.Failed > 0 ? 1 : 0;
    }
}
